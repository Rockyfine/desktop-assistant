using System.Diagnostics;
using Microsoft.Win32;

namespace DesktopAssistant.Desktop;

public partial class MainWindow
{
    private UpdateClient? _updates;
    private UpdateClient.Release? _availableUpdate;
    private CancellationTokenSource? _updateOperation;

    private void LoadUpdateSettings()
    {
        UpdateVersion.Text = $"当前版本  {_brand.Version}";
        if (_brand.ReleaseRepository is { Length: > 0 } repository)
            _updates = new UpdateClient(repository, _brand.InstallerName);
        UpdateStatus.Text = _updates is null ? "此构建未指定发布仓库。" : "点击检查更新获取最新版本。";
        RefreshUpdateButtons();
    }

    private void RefreshUpdateButtons()
    {
        CheckUpdateButton.IsEnabled = _updates is not null && _updateOperation is null;
        OpenReleasesButton.IsEnabled = _updates is not null;
        InstallUpdateButton.IsEnabled = _availableUpdate is not null && _updateOperation is null && IsInstalledCopy();
        CancelUpdateButton.Visibility = _updateOperation is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static bool IsInstalledCopy()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
            $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{AppIdentity.Id}");
        if (key?.GetValue("InstallLocation") is not string { Length: > 0 } directory)
            return false;

        string installed = Path.GetFullPath(Path.Combine(directory, "app"));
        return Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory).Equals(installed, StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(directory, "Uninstall.exe"));
    }

    private async void CheckUpdates(object sender, RoutedEventArgs e)
    {
        if (_updates is null || _updateOperation is not null)
            return;

        using var operation = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        _updateOperation = operation;
        _availableUpdate = null;
        UpdateStatus.Text = "正在检查 GitHub 发布版本…";
        RefreshUpdateButtons();
        try
        {
            _availableUpdate = await _updates.CheckAsync(_brand.Version, operation.Token);
            UpdateStatus.Text = _availableUpdate is { } release
                ? IsInstalledCopy() ? $"发现新版本  {release.Version}" : $"发现新版本  {release.Version}。可从发布页下载安装包。"
                : "当前已是最新版本。";
        }
        catch (OperationCanceledException) { UpdateStatus.Text = "检查已取消或超时。"; }
        catch (Exception error) { UpdateStatus.Text = $"检查更新失败。{error.Message}"; }
        finally
        {
            _updateOperation = null;
            RefreshUpdateButtons();
        }
    }

    private async void InstallUpdate(object sender, RoutedEventArgs e)
    {
        if (_updates is null || _availableUpdate is not { } release || _updateOperation is not null || !IsInstalledCopy())
            return;
        if (!Confirm($"更新到 {release.Version}？下载完成后，应用将退出并自动重启。正在录制的语音会结束。"))
            return;

        using var operation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        _updateOperation = operation;
        UpdateStatus.Text = $"正在下载 {release.Version}…";
        RefreshUpdateButtons();
        try
        {
            var progress = new Progress<long>(bytes =>
            {
                if (!_closing && _updateOperation == operation)
                    UpdateStatus.Text = $"正在下载 {release.Version} · {100 * bytes / release.Size}%";
            });
            string installer = await _updates.DownloadAsync(release, Path.Combine(_dataDirectory, "updates"), progress, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (_closing)
                return;
            if (!IsInstalledCopy())
                throw new InvalidOperationException("本机安装位置已改变，请重新打开已安装的桌面助手。");

            using Process? updater = Process.Start(new ProcessStartInfo(installer)
            {
                WorkingDirectory = Path.GetDirectoryName(installer)!,
                Arguments = "/UPDATE",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (updater is null)
                throw new InvalidOperationException("无法启动已下载的更新程序。");
            ExitAsync();
        }
        catch (OperationCanceledException) { UpdateStatus.Text = "下载已取消或超时。"; }
        catch (Exception error) { UpdateStatus.Text = $"下载或启动更新失败。{error.Message}"; }
        finally
        {
            _updateOperation = null;
            RefreshUpdateButtons();
        }
    }

    private void CancelUpdate(object sender, RoutedEventArgs e) => _updateOperation?.Cancel();

    private void OpenReleases(object sender, RoutedEventArgs e)
    {
        if (_updates is not null)
            OpenInBrowser(_updates.ReleasesPage);
    }
}
