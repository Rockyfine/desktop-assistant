using System.Security.Cryptography;
using System.Text;

namespace DesktopAssistant.Desktop;

public partial class App : Application
{
    private Mutex? _instance;
    private Mutex? _installationInUse;
    private EventWaitHandle? _show;
    private RegisteredWaitHandle? _listener;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            string directory = Path.GetFullPath(Environment.GetEnvironmentVariable("DESKTOP_ASSISTANT_DATA_DIR")
                ?? AppIdentity.DataDirectory);
            string key = "Local\\" + AppIdentity.Id + "." + Convert.ToHexString(
                SHA256.HashData(Encoding.Unicode.GetBytes(directory.ToUpperInvariant())))[..16];
            _show = new EventWaitHandle(false, EventResetMode.AutoReset, key + ".show");
            _instance = new Mutex(false, key, out bool first);
            if (!first)
            {
                _show.Set();
                Shutdown();
                return;
            }
            _installationInUse = new Mutex(false, "Local\\" + AppIdentity.Id + ".running");
            if (Mutex.TryOpenExisting("Local\\" + AppIdentity.Id + ".maintenance", out Mutex? maintenance))
            {
                maintenance.Dispose();
                Shutdown();
                return;
            }
            MainWindow window = new(directory);
            MainWindow = window;
            _listener = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) =>
                Dispatcher.BeginInvoke(window.ShowWindow), null, Timeout.Infinite, false);
            window.Show();
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "桌面助手无法启动", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _listener?.Unregister(null);
        _show?.Dispose();
        _installationInUse?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
