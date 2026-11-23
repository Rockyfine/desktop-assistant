using System.Diagnostics;

namespace DesktopAssistant.Desktop;

public partial class MainWindow
{
    private static readonly Uri Usage = new("https://chatgpt.com/settings/usage");
    private IReadOnlyList<SubscriptionAccount> _planAccounts = [];
    private readonly Dictionary<Guid, IReadOnlyList<SubscriptionOption>> _catalogs = [];
    private CancellationTokenSource? _planOperation;
    private ChatGptSignIn? _signIn;
    private bool _refreshingAccountPicker;

    private SubscriptionAccount? ChosenAccount => PlanAccounts.SelectedItem as SubscriptionAccount;
    private SubscriptionOption? ChosenModel => PlanModels.SelectedItem as SubscriptionOption;

    private void InitializeModelSettings()
    {
        ModelMode.SelectedIndex = _settings.Subscription is not null || _settings.Provider is null ? 0 : 1;
        LoadPlanAccounts(_settings.Subscription?.AccountId);
        ToggleModelPanel();
    }

    private void ModelModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready)
            return;
        ToggleModelPanel();
        TryLoadActivePlanModels();
    }

    private void ToggleModelPanel()
    {
        bool plan = ModelMode.SelectedIndex == 0;
        PlanModelPanel.Visibility = plan ? Visibility.Visible : Visibility.Collapsed;
        ApiModelPanel.Visibility = plan ? Visibility.Collapsed : Visibility.Visible;
    }

    private void LoadPlanAccounts(Guid? preferredId)
    {
        try
        {
            _planAccounts = _subscription.Accounts();
            Guid? selected = preferredId ?? ChosenAccount?.Id;
            _refreshingAccountPicker = true;
            PlanAccounts.ItemsSource = _planAccounts;
            PlanAccounts.SelectedItem = _planAccounts.FirstOrDefault(item => item.Id == selected)
                ?? _planAccounts.FirstOrDefault();
        }
        catch (Exception error) { PlanStatus.Text = error.Message; }
        finally
        {
            _refreshingAccountPicker = false;
            ShowPlanModels();
            RefreshChat();
        }
    }

    private void PlanAccountChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _refreshingAccountPicker)
            return;
        PlanStatus.Text = "";
        ShowPlanModels();
        TryLoadActivePlanModels();
    }

    private void PlanModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready)
            SetPlanButtons();
    }

    private void ShowPlanModels()
    {
        SubscriptionAccount? account = ChosenAccount;
        SubscriptionOption? previous = ChosenModel;
        SubscriptionModel? saved = _settings.Subscription;
        IReadOnlyList<SubscriptionOption> options = account is { PlanEnabled: true } && _catalogs.TryGetValue(account.Id, out var catalog)
            ? catalog : [];
        PlanModels.ItemsSource = options;
        PlanModels.SelectedItem = options.FirstOrDefault(model => model.Id == previous?.Id)
            ?? options.FirstOrDefault(model => saved?.AccountId == account?.Id && model.Id == saved?.Model)
            ?? options.FirstOrDefault();
        SetPlanButtons();
    }

    private void SetPlanButtons()
    {
        SubscriptionAccount? account = ChosenAccount;
        bool available = account?.PlanEnabled == true;
        bool busy = _planOperation is not null;
        ModelMode.IsEnabled = !busy;
        PlanAccounts.IsEnabled = !busy && _planAccounts.Count > 0;
        PlanModels.IsEnabled = !busy && available;
        PlanSignInButton.IsEnabled = !busy;
        PlanSignInButton.Content = account switch
        {
            { SignedIn: true, PlanEnabled: false } => "授权订阅用量",
            { SignedIn: true } => "重新登录",
            _ => "Continue with ChatGPT"
        };
        PlanAccountState.Text = account switch
        {
            { PlanEnabled: true } => "已连接 · ChatGPT 方案用量已授权",
            { SignedIn: true } => "已登录 · 尚未授权使用 ChatGPT 方案用量",
            _ => "尚未登录 ChatGPT"
        };
        PlanAddButton.IsEnabled = !busy;
        PlanAddButton.Visibility = _planAccounts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        PlanSignOutButton.IsEnabled = !busy && account?.SignedIn == true;
        PlanRefreshButton.IsEnabled = !busy && available;
        PlanTestButton.IsEnabled = PlanSaveButton.IsEnabled = !busy && available && ChosenModel is not null;
        PlanCancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TryLoadActivePlanModels()
    {
        if (_ready && Pages.SelectedIndex == 3 && ModelMode.SelectedIndex == 0 &&
            ChosenAccount is { PlanEnabled: true } account && !_catalogs.ContainsKey(account.Id) && _planOperation is null)
            _ = FetchPlanModelsAsync(account.Id);
    }

    private async void SignInPlan(object sender, RoutedEventArgs e) =>
        await LoginPlanAsync(ChosenAccount?.Id, ChosenAccount is { SignedIn: true, PlanEnabled: false });

    private async void AddPlanAccount(object sender, RoutedEventArgs e) => await LoginPlanAsync(null, false);

    private async Task LoginPlanAsync(Guid? id, bool askForConsent)
    {
        SubscriptionAccount? connection = null;
        await RunPlanOperationAsync("正在启动 ChatGPT 授权…", TimeSpan.FromMinutes(5), async token =>
        {
            connection = await _subscription.SignInAsync(id, _brand.Name, askForConsent, signIn =>
            {
                _signIn = signIn;
                PlanLoginPanel.Visibility = Visibility.Visible;
                PlanStatus.Text = "请在浏览器中确认账号和订阅权限。";
                OpenInBrowser(signIn.BrowserUrl);
            }, token);
            _catalogs.Remove(connection.Id);
            LoadPlanAccounts(connection.Id);
            PlanStatus.Text = connection.PlanEnabled
                ? "ChatGPT 订阅已连接。"
                : "ChatGPT 已登录。可授权订阅用量，或选择 API Key 接入。";
            if (connection.PlanEnabled && !_settings.PlanWelcomeShown)
            {
                MessageBox.Show(this, "选用订阅模型后，符合条件的请求会使用你的 ChatGPT 方案用量。"
                    + "可通过“查看 ChatGPT 用量”查看或管理限额。",
                    "已授权使用 ChatGPT 方案", MessageBoxButton.OK, MessageBoxImage.Information);
                WriteSettings(_settings with { PlanWelcomeShown = true });
            }
        });
        if (connection is { PlanEnabled: true })
            await FetchPlanModelsAsync(connection.Id);
    }

    private async void RefreshPlanModels(object sender, RoutedEventArgs e)
    {
        if (ChosenAccount is { } account)
            await FetchPlanModelsAsync(account.Id);
    }

    private Task FetchPlanModelsAsync(Guid id) =>
        RunPlanOperationAsync("正在读取此账号的订阅模型…", TimeSpan.FromSeconds(25), async token =>
        {
            _catalogs[id] = await _subscription.ListModelsAsync(id, token);
            ShowPlanModels();
            PlanStatus.Text = _catalogs[id].Count == 0
                ? "当前账号没有可列出的订阅模型。"
                : "模型列表已更新。选择模型后点击“使用该模型”。";
        });

    private async void TestPlanModel(object sender, RoutedEventArgs e)
    {
        if (GetPlanSelection() is not { } selection)
            return;
        await RunPlanOperationAsync("正在测试所选订阅模型…", TimeSpan.FromSeconds(45), async token =>
        {
            await _subscription.TestAsync(selection, token);
            PlanStatus.Text = "订阅模型测试成功。";
        });
    }

    private void SavePlanModel(object sender, RoutedEventArgs e)
    {
        if (GetPlanSelection() is not { } selection)
            return;
        try
        {
            WriteSettings(_settings with { Subscription = selection });
            RefreshChat();
            PlanStatus.Text = "已切换到所选 ChatGPT 订阅模型。";
        }
        catch (Exception error) { PlanStatus.Text = error.Message; }
    }

    private SubscriptionModel? GetPlanSelection() =>
        ChosenAccount is { PlanEnabled: true } account && ChosenModel is { } model
            ? new SubscriptionModel(account.Id, model.Id, model.Label)
            : null;

    private async void SignOutPlan(object sender, RoutedEventArgs e)
    {
        if (ChosenAccount is not { SignedIn: true } account)
            return;
        if (_settings.Subscription?.AccountId == account.Id)
            InterruptReply();
        await RunPlanOperationAsync("正在退出此账号…", TimeSpan.FromSeconds(18), async token =>
        {
            bool confirmed = await _subscription.SignOutAsync(account.Id, token);
            _catalogs.Remove(account.Id);
            LoadPlanAccounts(account.Id);
            PlanStatus.Text = confirmed
                ? "已退出此 ChatGPT 账号。"
                : "已退出本机账号。远程注销未获确认，可在 ChatGPT 设置中断开应用。";
        });
    }

    private async Task RunPlanOperationAsync(string status, TimeSpan timeout, Func<CancellationToken, Task> operation)
    {
        if (_planOperation is not null || _closing)
            return;
        using CancellationTokenSource deadline = new(timeout);
        _planOperation = deadline;
        PlanStatus.Text = status;
        SetPlanButtons();
        try { await operation(deadline.Token); }
        catch (OperationCanceledException)
        {
            if (!_closing && PlanStatus.Text != "已取消操作。")
                PlanStatus.Text = "操作超时，请重试。";
        }
        catch (Exception error)
        {
            if (!_closing)
            {
                LoadPlanAccounts(null);
                PlanStatus.Text = error.Message;
            }
        }
        finally
        {
            _planOperation = null;
            _signIn = null;
            PlanLoginPanel.Visibility = Visibility.Collapsed;
            PlanCallback.Clear();
            SetPlanButtons();
        }
    }

    private void CancelPlanOperation(object sender, RoutedEventArgs e)
    {
        PlanStatus.Text = "已取消操作。";
        _planOperation?.Cancel();
    }

    private void SubmitPlanCallback(object sender, RoutedEventArgs e)
    {
        if (_signIn is not { } signIn)
            return;
        try
        {
            signIn.SubmitCallback(PlanCallback.Password);
            PlanCallback.Clear();
            PlanStatus.Text = "正在核对 ChatGPT 授权…";
        }
        catch (ArgumentException error) { PlanStatus.Text = error.Message; }
    }

    private void ReopenPlanBrowser(object sender, RoutedEventArgs e)
    {
        if (_signIn is { } signIn)
            OpenInBrowser(signIn.BrowserUrl);
    }

    private void OpenPlanUsage(object sender, RoutedEventArgs e) => OpenInBrowser(Usage);

    private void OpenInBrowser(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            PlanStatus.Text = "无法打开默认浏览器。请检查浏览器设置后重试。";
        }
    }
}
