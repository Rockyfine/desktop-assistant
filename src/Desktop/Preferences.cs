using System.Windows.Media;
using Microsoft.Win32;

namespace DesktopAssistant.Desktop;

public partial class MainWindow
{
    private readonly Dictionary<string, VoicePlugin> _plugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<VoiceSlot, ComboBox> _selectors = [];
    private readonly Dictionary<VoiceSlot, TextBlock> _slotLabels = [];
    private readonly Dictionary<VoiceSlot, SlotState> _slotStates = [];
    private static readonly IReadOnlyDictionary<VoiceSlot, string> SlotNames = new Dictionary<VoiceSlot, string>
    {
        [VoiceSlot.Aec] = "回声消除",
        [VoiceSlot.Wake] = "唤醒",
        [VoiceSlot.Vad] = "语音活动检测",
        [VoiceSlot.Asr] = "语音识别",
        [VoiceSlot.Tts] = "语音合成",
        [VoiceSlot.Fx] = "音效"
    };

    private void LoadSettings()
    {
        DisplayName.Text = ActiveName;
        Endpoint.Text = _settings.Provider?.Endpoint ?? "";
        ModelName.Text = _settings.Provider?.Model ?? "";
        InitializeModelSettings();
        Keyword.Text = VoiceSettings.Keyword;
        string[] languages = [VoiceSettings.Culture, "zh-CN", "en-US"];
        string[] speakers = [""];
        try { languages = [.. languages, .. VoiceRouter.KeywordLanguages()]; }
        catch (Exception error) { VoiceStatus.Text = error.Message; }
        try { speakers = [.. speakers, .. VoiceRouter.Speakers()]; }
        catch (Exception error) { VoiceStatus.Text = error.Message; }
        WakeLanguage.ItemsSource = languages.Distinct(StringComparer.OrdinalIgnoreCase);
        WakeLanguage.SelectedItem = VoiceSettings.Culture;
        Speaker.ItemsSource = speakers.Select(voice => new Choice(voice, voice.Length == 0 ? "系统默认（优先普通话）" : voice));
        Speaker.DisplayMemberPath = nameof(Choice.Label);
        Speaker.SelectedValuePath = nameof(Choice.Id);
        Speaker.SelectedValue = speakers.Contains(VoiceSettings.Speaker) ? VoiceSettings.Speaker : "";
        AlwaysListen.IsChecked = VoiceSettings.AlwaysListen;
        SpeakReplies.IsChecked = VoiceSettings.SpeakReplies;
        ListenWhileSpeaking.IsChecked = VoiceSettings.ListenWhileSpeaking;
        foreach ((string path, string fingerprint) in _settings.TrustedPlugins ?? [])
        {
            try
            {
                VoicePlugin plugin = VoicePlugin.Read(path);
                if (plugin.Fingerprint == fingerprint)
                    _plugins.TryAdd(plugin.Id, plugin);
                else
                    PluginStatus.Text = "有插件文件发生变化，请重新加载清单。";
            }
            catch (Exception) { PluginStatus.Text = "有插件文件无法读取，可重新加载清单。"; }
        }
        BuildSlotControls();
    }

    private void SaveName(object sender, RoutedEventArgs e)
    {
        try
        {
            string name = DisplayName.Text.Trim();
            if (name.Length is < 1 or > 32 || name.Any(char.IsControl))
                throw new ArgumentException("请输入 1–32 个字符的显示名称。");
            WriteSettings(_settings with { DisplayName = name });
            ApplyName();
            NameStatus.Text = "已更新显示名称。";
        }
        catch (Exception error) { NameStatus.Text = error.Message; }
    }

    private ProviderSettings ReadProvider()
    {
        ProviderSettings provider = new(Endpoint.Text.Trim(), ModelName.Text.Trim());
        _ = provider.ChatUri();
        if (provider.Model.Length == 0)
            throw new ArgumentException("请输入模型名称。");
        return provider;
    }

    private async void TestProvider(object sender, RoutedEventArgs e)
    {
        TestProviderButton.IsEnabled = false;
        try
        {
            ProviderSettings provider = ReadProvider();
            string? key = ApiKey.Password.Trim();
            if (key.Length == 0)
                key = Credentials.Read(provider);
            ProviderStatus.Text = "正在测试模型…";
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
            await _client.TestAsync(provider, key, timeout.Token);
            ProviderStatus.Text = "模型连接成功。";
        }
        catch (OperationCanceledException) { ProviderStatus.Text = "连接测试超时。"; }
        catch (Exception error) { ProviderStatus.Text = error.Message; }
        finally { TestProviderButton.IsEnabled = true; }
    }

    private void SaveProvider(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(Endpoint.Text) && string.IsNullOrWhiteSpace(ModelName.Text))
            {
                WriteSettings(_settings with { Provider = null, Subscription = null });
                ProviderStatus.Text = "已断开模型。";
            }
            else
            {
                ProviderSettings provider = ReadProvider();
                if (!string.IsNullOrWhiteSpace(ApiKey.Password))
                    Credentials.Write(provider, ApiKey.Password.Trim());
                WriteSettings(_settings with { Provider = provider, Subscription = null });
                ProviderStatus.Text = "已保存模型连接。";
            }
            ApiKey.Clear();
            RefreshChat();
        }
        catch (Exception error) { ProviderStatus.Text = error.Message; }
    }

    private void ClearKey(object sender, RoutedEventArgs e)
    {
        try
        {
            Credentials.Remove(new ProviderSettings(Endpoint.Text.Trim()));
            ApiKey.Clear();
            ProviderStatus.Text = "此服务的密钥已清除。";
        }
        catch (Exception error) { ProviderStatus.Text = error.Message; }
    }

    private VoiceOptions ReadVoice()
    {
        string phrase = Keyword.Text.Trim();
        if (phrase.Length is < 1 or > 48 || phrase.Any(char.IsControl))
            throw new ArgumentException("请输入 1–48 个字符的唤醒词。");
        return new VoiceOptions(phrase, (string?)WakeLanguage.SelectedItem ?? "zh-CN", (string?)Speaker.SelectedValue ?? "",
            AlwaysListen.IsChecked == true, SpeakReplies.IsChecked == true, ListenWhileSpeaking.IsChecked == true);
    }

    private async void TryWake(object sender, RoutedEventArgs e)
    {
        if (_changingCapture)
            return;
        _changingCapture = true;
        try { await _voice.TestWakeAsync(ReadVoice()); }
        catch (Exception error) { VoiceStatus.Text = error.Message; }
        finally { _changingCapture = false; }
    }

    private async void TryVoice(object sender, RoutedEventArgs e)
    {
        Button button = (Button)sender;
        button.IsEnabled = false;
        try
        {
            var options = ReadVoice();
            await _voice.SpeakAsync($"你好，我是{ActiveName}。", options);
            VoiceStatus.Text = "已播放试听。";
        }
        catch (OperationCanceledException) { VoiceStatus.Text = "试听已停止。"; }
        catch (Exception error) { VoiceStatus.Text = error.Message; }
        finally { button.IsEnabled = true; }
    }

    private async void SaveVoice(object sender, RoutedEventArgs e)
    {
        if (_changingCapture)
            return;
        _changingCapture = true;
        try
        {
            VoiceOptions options = ReadVoice();
            await _voice.ConfigureAsync(options);
            WriteSettings(_settings with { Voice = options });
            VoiceStatus.Text = "已保存语音设置。";
        }
        catch (Exception error) { VoiceStatus.Text = error.Message; }
        finally { _changingCapture = false; }
    }

    private async Task RestoreVoiceAsync()
    {
        _changingCapture = true;
        try
        {
            foreach (VoiceSlot slot in Enum.GetValues<VoiceSlot>())
            {
                if (_settings.Engines?.TryGetValue(slot.ToString(), out string? id) != true || id == "builtin")
                    continue;
                try
                {
                    if (!_plugins.TryGetValue(id!, out VoicePlugin? plugin))
                        throw new InvalidOperationException("插件不可用。");
                    await Task.Run(() => _router.SelectAsync(slot, plugin, VoiceSettings));
                }
                catch (Exception error) { PluginStatus.Text = $"{SlotNames[slot]}已使用内置引擎。{error.Message}"; }
            }
            await _voice.ConfigureAsync(VoiceSettings);
        }
        catch (Exception error) { VoiceStatus.Text = MicState.Text = error.Message; }
        finally { _changingCapture = false; }
    }

    private void AddPlugin(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择本地语音插件清单", Filter = "插件清单 (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            VoicePlugin plugin = VoicePlugin.Read(dialog.FileName);
            if (!Confirm($"信任插件「{plugin.Name}」？选择该引擎后，它会以当前用户身份运行。"))
                return;
            if (_plugins.TryGetValue(plugin.Id, out VoicePlugin? old) &&
                !old.ManifestPath.Equals(plugin.ManifestPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("已有使用相同 ID 的插件。");
            Dictionary<string, string> trusted = new(_settings.TrustedPlugins ?? [], StringComparer.OrdinalIgnoreCase)
            {
                [plugin.ManifestPath] = plugin.Fingerprint
            };
            WriteSettings(_settings with { TrustedPlugins = trusted });
            _plugins[plugin.Id] = plugin;
            foreach (VoiceSlot slot in plugin.Slots)
                SetChoices(slot);
            PluginStatus.Text = $"已加载「{plugin.Name}」。在相应的语音环节选择它，再点击切换。";
        }
        catch (Exception error) { PluginStatus.Text = error.Message; }
    }

    private void BuildSlotControls()
    {
        foreach (VoiceSlot slot in Enum.GetValues<VoiceSlot>())
        {
            DockPanel line = new() { Margin = new Thickness(0, 8, 0, 0) };
            Button apply = new() { Content = "切换", Tag = slot, Margin = new Thickness(8, 0, 0, 0) };
            apply.Click += ChangeSlot;
            DockPanel.SetDock(apply, Dock.Right);
            line.Children.Add(apply);
            TextBlock label = new() { Text = SlotNames[slot], Width = 125, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(label, Dock.Left);
            line.Children.Add(label);
            ComboBox select = new() { DisplayMemberPath = nameof(Choice.Label), SelectedValuePath = nameof(Choice.Id) };
            System.Windows.Automation.AutomationProperties.SetName(select, SlotNames[slot] + "引擎");
            line.Children.Add(select);
            PluginRows.Children.Add(line);
            _slotLabels[slot] = label;
            _selectors[slot] = select;
            SetChoices(slot);
        }
    }

    private void SetChoices(VoiceSlot slot)
    {
        ComboBox combo = _selectors[slot];
        string current = _slotStates.TryGetValue(slot, out SlotState? state) ? state.Pending ?? state.Current : "builtin";
        combo.ItemsSource = new[] { new Choice("builtin", "内置") }.Concat(
            _plugins.Values.Where(plugin => plugin.Slots.Contains(slot)).Select(plugin => new Choice(plugin.Id, plugin.Name)));
        combo.SelectedValue = current;
    }

    private void RefreshSlot(SlotState state)
    {
        _slotStates[state.Slot] = state;
        SetChoices(state.Slot);
        _slotLabels[state.Slot].Foreground = (Brush)FindResource(state.Pending is null ? "Ink" : "Accent");
        _slotLabels[state.Slot].Text = SlotNames[state.Slot] + (state.Pending is null ? "" : " · 待切换");
    }

    private async void ChangeSlot(object sender, RoutedEventArgs e)
    {
        Button button = (Button)sender;
        VoiceSlot slot = (VoiceSlot)button.Tag;
        string id = (string?)_selectors[slot].SelectedValue ?? "builtin";
        button.IsEnabled = false;
        try
        {
            VoiceOptions options = VoiceSettings;
            VoicePlugin? plugin = id == "builtin" ? null : _plugins[id];
            SlotState state = await Task.Run(() => _router.SelectAsync(slot, plugin, options));
            Dictionary<string, string> engines = new(_settings.Engines ?? []) { [slot.ToString()] = id };
            WriteSettings(_settings with { Engines = engines });
            PluginStatus.Text = state.Pending is null ? $"{SlotNames[slot]}已切换。" : "将在当前语音处理结束后切换。";
        }
        catch (Exception error)
        {
            SetChoices(slot);
            PluginStatus.Text = error.Message;
        }
        finally { button.IsEnabled = true; }
    }

    public sealed record Choice(string Id, string Label);
}
