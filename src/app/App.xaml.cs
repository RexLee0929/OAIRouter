using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace OAIRouter;

public partial class App : Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _wake;
    private Thread? _wakeThread;

    public static AppConfig Config { get; private set; } = new();
    public static ProxyService Proxy { get; } = new();
    public static MainWindow? Shell { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Accent.Init();
        Resources["AccentBrush"] = Accent.MainBrush;
        Resources["AccentDarkBrush"] = Accent.DarkBrush;
        Resources["AccentSoftBrush"] = Accent.SoftBrush;
        Resources["AccentHoverBrush"] = Accent.HoverBrush;

        Config = ConfigStore.Load();
        LogStore.Instance.Limit = Config.LogLimit;

        var name = InstanceName();
        _mutex = new Mutex(true, name + ".mutex", out var isFirst);
        if (!isFirst)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(name + ".wake", out var existing))
                {
                    existing.Set();
                    existing.Dispose();
                }
            }
            catch { }
            Shutdown();
            return;
        }

        _wake = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".wake");
        _wakeThread = new Thread(() =>
        {
            while (true)
            {
                if (!_wake.WaitOne()) continue;
                Dispatcher.BeginInvoke(() => Shell?.RestoreFromTray(silent: false));
            }
        }) { IsBackground = true, Name = "wake-listener" };
        _wakeThread.Start();

        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) WriteCrash(ex);
        };

        LogStore.Instance.Info("启动", $"{Brand.Name} {Brand.Version}");

        Shell = new MainWindow();
        var startHidden = e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase);
        // 标志必须在 Show() 之前设好：Loaded 是在 Show() 里同步触发的
        if (startHidden) Shell.StartHidden();
        if (e.Args.Contains("--start", StringComparer.OrdinalIgnoreCase)) Shell.StartServiceOnLoad();

        // 托盘模式也要 Show 一次：否则 Loaded 不触发，服务不会启动、窗口句柄也不存在
        Shell.Show();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrash(e.Exception);
        LogStore.Instance.Error("崩溃", e.Exception.Message);
        Shell?.ShowToast($"程序出错：{e.Exception.Message}");
        e.Handled = true;
    }

    private static void WriteCrash(Exception ex)
    {
        try
        {
            var path = Path.Combine(ConfigStore.Dir, "error.log");
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>按程序所在目录散列命名，免安装版与安装版可各跑一个。</summary>
    private static string InstanceName()
    {
        var dir = AppContext.BaseDirectory;
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(dir));
        return $"{Brand.Name}.{Convert.ToHexString(hash)[..8]}";
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Proxy.StopAsync().GetAwaiter().GetResult(); } catch { }
        _wake?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    public static void Quit()
    {
        Shell?.AllowClose();
        Current.Shutdown();
    }
}
