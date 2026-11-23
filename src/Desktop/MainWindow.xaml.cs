using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace DesktopAssistant.Desktop;

public partial class MainWindow : Window
{
    private readonly Branding _brand = Branding.Load();
    private readonly LocalStore _store;
    private readonly string _dataDirectory;
    private readonly SettingsFile _settingsFile;
    private readonly VoiceRouter _router = new(AppIdentity.ModelDirectory);
    private readonly VoiceSession _voice;
    private readonly ChatClient _client = new();
    private readonly ChatGptSubscription _subscription;
    private readonly DispatcherTimer _reminders;
    private readonly Forms.NotifyIcon _tray;
    private readonly Forms.ToolStripMenuItem _trayPause;
    private readonly System.Drawing.Icon _trayIcon;
    private AppSettings _settings;
    private bool _ready;
    private bool _closing;
    private bool _micPaused;
    private bool _changingCapture;
    private string _conversation = Guid.NewGuid().ToString("N");
    private CancellationTokenSource? _answer;
    private Appointment? _draft;

    private VoiceOptions VoiceSettings => _settings.Voice ?? new VoiceOptions();
    private string ActiveName => _settings.DisplayName ?? _brand.Name;

    public MainWindow(string dataDirectory)
    {
        _dataDirectory = dataDirectory;
        _settingsFile = new SettingsFile(dataDirectory);
        _subscription = new ChatGptSubscription(dataDirectory);
        _settings = _settingsFile.Read();
        _store = new LocalStore(dataDirectory);
        _voice = new VoiceSession(_router);
        InitializeComponent();

        _trayIcon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "assistant.ico"));
        _tray = new Forms.NotifyIcon { Icon = _trayIcon };
        Forms.ContextMenuStrip menu = new();
        menu.Items.Add("打开窗口", null, (_, _) => Dispatcher.BeginInvoke(ShowWindow));
        _trayPause = new Forms.ToolStripMenuItem("暂停麦克风");
        _trayPause.Click += (_, _) => Dispatcher.BeginInvoke(() => OnPauseMicAsync());
        menu.Items.Add(_trayPause);
        menu.Items.Add("应用更新", null, (_, _) => Dispatcher.BeginInvoke(() =>
        {
            ShowWindow();
            ShowPage(3);
            Dispatcher.BeginInvoke(UpdateCard.BringIntoView, DispatcherPriority.Loaded);
        }));
        menu.Items.Add("退出", null, (_, _) => Dispatcher.BeginInvoke(ExitAsync));
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowWindow);
        _tray.BalloonTipClicked += (_, _) => Dispatcher.BeginInvoke(() => { ShowWindow(); ShowPage(1); });
        ApplyName();
        _tray.Visible = true;
        _voice.Status += status => Dispatch(() => { MicState.Text = status; VoiceStatus.Text = status; });
        _voice.TextReady += text => Dispatch(() => ConsumeRecording(text));
        _voice.ManualChanged += manual => Dispatch(() => SetVoiceIndicator(manual));
        _router.Changed += state => Dispatch(() => RefreshSlot(state));
        _router.Notice += notice => Dispatch(() => PluginStatus.Text = notice);
        _reminders = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background,
            (_, _) => PollReminders(), Dispatcher);
        _ready = true;
        LoadSettings();
        LoadUpdateSettings();
        ScheduleDay.SelectedDate = DateTime.Today;
        ResetEditor();
        ShowPage(0);
        Loaded += FirstLoad;
    }

    private async void FirstLoad(object sender, RoutedEventArgs e)
    {
        Loaded -= FirstLoad;
        PollReminders();
        await RestoreVoiceAsync();
        Composer.Focus();
    }

    private void Dispatch(Action action)
    {
        if (!_closing)
            Dispatcher.BeginInvoke(() => { if (!_closing) action(); });
    }

    public void ShowWindow()
    {
        if (_closing)
            return;
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) && !SystemParameters.HighContrast)
        {
            const int useImmersiveDarkMode = 20;
            int enabled = 1;
            _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, useImmersiveDarkMode, ref enabled, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    private async void ExitAsync()
    {
        if (_closing)
            return;
        _closing = true;
        _reminders.Stop();
        InterruptReply();
        _planOperation?.Cancel();
        _updateOperation?.Cancel();
        try
        {
            await _voice.DisposeAsync();
            await _router.DisposeAsync();
        }
        finally
        {
            _tray.Visible = false;
            _tray.ContextMenuStrip?.Dispose();
            _tray.Dispose();
            _trayIcon.Dispose();
            _client.Dispose();
            _subscription.Dispose();
            Application.Current.Shutdown();
        }
    }

    private void ApplyName()
    {
        Title = ActiveName;
        BrandName.Text = ActiveName;
        BrandName.ToolTip = ActiveName;
        _tray.Text = ActiveName;
        RefreshChat();
    }

    private void WriteSettings(AppSettings settings)
    {
        _settingsFile.Write(settings);
        _settings = settings;
    }

    private void Navigate(object sender, RoutedEventArgs e) =>
        ShowPage(int.Parse((string)((Button)sender).Tag));

    private void OpenSettings(object sender, RoutedEventArgs e)
    {
        ShowPage(3);
        SettingsScroll.ScrollToTop();
    }

    private void ShowPage(int page)
    {
        Pages.SelectedIndex = page;
        Button[] buttons = [NavChat, NavSchedule, NavHistory, NavSettings];
        for (int i = 0; i < buttons.Length; ++i)
        {
            buttons[i].Background = i == page ? Brush("Raised") : Brushes.Transparent;
            buttons[i].Foreground = i == page ? Brush("Accent") : Brush("Muting");
            buttons[i].BorderBrush = i == page ? Brush("Accent") : Brushes.Transparent;
        }
        if (page == 1) RefreshAppointments();
        if (page == 2) RefreshHistory();
        if (page == 3) TryLoadActivePlanModels();
    }

    private Brush Brush(string key) => (Brush)FindResource(key);

    private void ShowError(string message) =>
        MessageBox.Show(this, message, ActiveName, MessageBoxButton.OK, MessageBoxImage.Error);

    private bool Confirm(string message) =>
        MessageBox.Show(this, message, ActiveName, MessageBoxButton.YesNo, MessageBoxImage.Question,
            MessageBoxResult.No) == MessageBoxResult.Yes;

    private void NewChat(object sender, RoutedEventArgs e)
    {
        InterruptReply();
        _conversation = Guid.NewGuid().ToString("N");
        _draft = null;
        ChatMessages.Children.Clear();
        Composer.Clear();
        ChatStatus.Text = "";
        RefreshChat();
        Composer.Focus();
    }

    private void ComposerKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            _ = SendAsync();
        }
    }

    private async void Send(object sender, RoutedEventArgs e) => await SendAsync();

    private async Task SendAsync()
    {
        string text = Composer.Text.Trim();
        if (text.Length == 0 || _answer is not null || _closing)
            return;
        SubscriptionModel? subscription = _settings.Subscription;
        ProviderSettings? provider = _settings.Provider;
        if (subscription is null && provider is null)
        {
            ChatStatus.Text = "请先在设置中配置模型。";
            return;
        }
        string id = _conversation;
        using var request = new CancellationTokenSource();
        _answer = request;
        _draft = null;
        Composer.Clear();
        ChatStatus.Text = "正在等待模型回复…";
        try
        {
            Message input = new("user", text, DateTimeOffset.Now);
            string? secret = subscription is null ? Credentials.Read(provider!) : null;
            _store.AddMessage(id, input);
            AddMessage(ChatMessages, input);
            RefreshChat();
            IReadOnlyList<Message> messages = _store.Messages(id);
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(request.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(90));
            ChatReply reply = subscription is null
                ? await _client.ReplyAsync(provider!, secret, messages, DateTimeOffset.Now, deadline.Token)
                : await _subscription.ReplyAsync(subscription, messages, DateTimeOffset.Now, deadline.Token);
            if (request.IsCancellationRequested || _conversation != id || _closing)
                return;
            Message output = new("assistant", reply.Text, DateTimeOffset.Now);
            _store.AddMessage(id, output);
            AddMessage(ChatMessages, output);
            _draft = reply.Draft;
            RefreshChat();
            ChatStatus.Text = "";
            if (VoiceSettings.SpeakReplies)
            {
                ChatStatus.Text = "正在朗读…";
                try { await _voice.SpeakAsync(output.Text, request.Token); }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    if (!_closing && _conversation == id)
                        ChatStatus.Text = "朗读失败。 " + error.Message;
                }
                if (ChatStatus.Text == "正在朗读…")
                    ChatStatus.Text = "";
            }
        }
        catch (OperationCanceledException) when (_conversation == id && !_closing)
        {
            ChatStatus.Text = request.IsCancellationRequested ? "已停止。" : "请求超时，请重试。";
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (_conversation == id && !_closing)
                ChatStatus.Text = error.Message;
        }
        finally
        {
            if (ReferenceEquals(_answer, request))
                _answer = null;
            if (!_closing)
                RefreshChat();
        }
    }

    private void AddMessage(StackPanel destination, Message message)
    {
        bool user = message.Role == "user";
        StackPanel messageContent = new();
        TextBlock meta = new()
        {
            Text = "你  ·  " + message.At.ToLocalTime().ToString("HH:mm"),
            Foreground = Brush(user ? "Accent" : "Muting"), FontSize = 12, Margin = new Thickness(0, 0, 0, 6)
        };
        if (!user)
            meta.SetBinding(TextBlock.TextProperty, new Binding(nameof(Title))
            {
                Source = this, StringFormat = "{0}  ·  " + message.At.ToLocalTime().ToString("HH:mm")
            });
        TextBox selectable = new()
        {
            Text = message.Text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            MinHeight = 0, IsTabStop = false
        };
        messageContent.Children.Add(meta);
        messageContent.Children.Add(selectable);
        Border card = new()
        {
            Background = Brush(user ? "Raised" : "Canvas"), CornerRadius = new CornerRadius(3),
            BorderBrush = Brush(user ? "Accent" : "Outline"),
            BorderThickness = user ? new Thickness(1, 1, 2, 1) : new Thickness(2, 1, 1, 1),
            Padding = new Thickness(13, 10, 13, 10),
            Margin = user ? new Thickness(35, 0, 0, 10) : new Thickness(0, 0, 35, 10), Child = messageContent
        };
        destination.Children.Add(card);
        if (destination == ChatMessages)
            Dispatcher.BeginInvoke(ChatScroll.ScrollToEnd, DispatcherPriority.Background);
    }

    private void RefreshChat()
    {
        string? model = _settings.Provider?.Model;
        if (_settings.Subscription is { } subscription)
        {
            SubscriptionAccount? account = _planAccounts.FirstOrDefault(item => item.Id == subscription.AccountId);
            model = $"ChatGPT 订阅 · {subscription.Label}";
            if (account is { PlanEnabled: true })
                model += $" · {account.Label}";
            else
                model += account?.SignedIn == true ? " · 需授权用量" : " · 需登录";
        }
        ProviderState.Text = model ?? "尚未配置模型";
        ActiveModelText.Text = model is null ? "尚未配置当前模型" : "当前模型 · " + model;
        ChatUsageButton.Visibility = _settings.Subscription is null ? Visibility.Collapsed : Visibility.Visible;
        ConnectButton.Content = model is null ? "配置模型" : "管理模型";
        Welcome.Visibility = ChatMessages.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ChatScroll.Visibility = ChatMessages.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DraftCard.Visibility = _draft is null ? Visibility.Collapsed : Visibility.Visible;
        if (_draft is { } draft)
            DraftLabel.Text = $"{draft.Day:yyyy-MM-dd} {draft.Start:HH\\:mm}  ·  {draft.Title}";
        StopButton.Visibility = _answer is null ? Visibility.Collapsed : Visibility.Visible;
        SendButton.IsEnabled = _answer is null;
    }

    private void ConsumeRecording(string text)
    {
        bool hasText = !string.IsNullOrWhiteSpace(Composer.Text);
        ShowPage(0);
        string recognized = hasText ? Composer.Text.TrimEnd() + "\n" + text : text;
        Composer.Text = recognized[..Math.Min(recognized.Length, Composer.MaxLength)];
        Composer.CaretIndex = Composer.Text.Length;
        if (_answer is null && !hasText && (_settings.Provider is not null || _settings.Subscription is not null))
            _ = SendAsync();
    }

    private void StopReply(object sender, RoutedEventArgs e) => InterruptReply();

    private void InterruptReply()
    {
        _answer?.Cancel();
        _voice.Interrupt();
    }

    private void SetVoiceIndicator(bool recording)
    {
        TalkButton.Content = recording ? "结束录音" : "说话";
        TalkButton.Background = TalkButton.BorderBrush = Brush(recording ? "Alert" : "Accent");
        CoreSignal.Stroke = CoreMarkers.Fill = Brush(recording ? "Alert" : _micPaused ? "Muting" : "Accent");
        MicIndicator.Fill = Brush(recording || _micPaused ? "Alert" : "Muting");
    }
    private async void Talk(object sender, RoutedEventArgs e)
    {
        if (_changingCapture)
            return;
        _changingCapture = true;
        try
        {
            InterruptReply();
            await _voice.ToggleTalkAsync();
        }
        catch (Exception error) { ChatStatus.Text = error.Message; }
        finally { _changingCapture = false; }
    }

    private async void PauseMic(object sender, RoutedEventArgs e) => await OnPauseMicAsync();

    private async Task OnPauseMicAsync()
    {
        if (_changingCapture)
            return;
        _changingCapture = true;
        try
        {
            bool paused = !_micPaused;
            await _voice.PauseAsync(paused);
            _micPaused = paused;
            TalkButton.IsEnabled = !paused;
            SetVoiceIndicator(recording: false);
            PauseButton.Content = _trayPause.Text = paused ? "恢复麦克风" : "暂停麦克风";
        }
        catch (Exception error) { MicState.Text = error.Message; }
        finally { _changingCapture = false; }
    }

    private void PollReminders()
    {
        try
        {
            IReadOnlyList<Appointment> due = _store.ClaimReminders(DateTimeOffset.Now);
            if (due.Count > 0)
                _tray.ShowBalloonTip(6000, ActiveName,
                    string.Join("\n", due.Select(item => $"{item.Start:HH\\:mm}  {item.Title}")), Forms.ToolTipIcon.Info);
        }
        catch (Exception error) { ScheduleStatus.Text = error.Message; }
    }
}
