using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace OAIRouter;

public partial class App : Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _wake;
    private Thread? _wakeThread;

    public static bool IsDev { get; private set; }
    public static AppConfig Config { get; private set; } = new();
    public static ProxyService Proxy { get; } = new();
    public static MainWindow? Shell { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var processName = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "";
        IsDev = e.Args.Contains("--dev", StringComparer.OrdinalIgnoreCase)
            || string.Equals(processName, "OAIRouter.Dev", StringComparison.OrdinalIgnoreCase)
            || processName.StartsWith("OAIRouter.Dev.", StringComparison.OrdinalIgnoreCase);
        if (IsDev) Directory.CreateDirectory(ConfigStore.Dir);

        var iconTestExitCode = DebugTools.TryRunIconTest(e.Args);
        if (iconTestExitCode.HasValue)
        {
            Current.Shutdown(iconTestExitCode.Value);
            return;
        }

        Accent.Init();
        Resources["AccentBrush"] = Accent.MainBrush;
        Resources["AccentDarkBrush"] = Accent.DarkBrush;
        Resources["AccentSoftBrush"] = Accent.SoftBrush;
        // 悬停浅档：GhostButton / 菜单子项 / 下拉条目的 IsMouseOver 都吃这一支（§十三.2）
        Resources["AccentHoverBrush"] = Accent.HoverBrush;

        var firstDevRun = IsDev && !File.Exists(ConfigStore.Path);
        Config = ConfigStore.Load();
        if (firstDevRun)
        {
            Config.Port = 18787;
            ConfigStore.Save(Config);
        }
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
            if (args.ExceptionObject is not Exception ex) return;
            WriteCrash(ex);
            // 非 UI 线程上的崩溃：能派发就派发，派发不了至少日志已经落下
            try { Dispatcher.BeginInvoke(() => Shell?.ReportError(ex)); } catch { }
        };

        LogStore.Instance.Info("启动", $"{Brand.Name} {Brand.Version}");

        Shell = new MainWindow();
        var startHidden = e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase);
        // 标志必须在 Show() 之前设好：Loaded 是在 Show() 里同步触发的
        if (startHidden) Shell.StartHidden();
        if (e.Args.Contains("--start", StringComparer.OrdinalIgnoreCase)) Shell.StartServiceOnLoad();

        // 托盘模式也要 Show 一次：否则 Loaded 不触发，服务不会启动、窗口句柄也不存在
        Shell.Show();

        // 调试命令统一入口（--render / --shotreal / --shotwin / --dialogs），正常启动不会命中
        var debugExitCode = DebugTools.TryRun(e.Args);
        if (debugExitCode.HasValue)
        {
            Current.Shutdown(debugExitCode.Value);
            return;
        }


    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrash(e.Exception);                        // ① error.log
        LogStore.Instance.Error("崩溃", e.Exception.Message);
        Shell?.ReportError(e.Exception);                // ② 状态栏红字 ③ 自绘错误框
        e.Handled = true;
    }

    /// <summary>崩溃日志的唯一落点。错误框的「打开日志」也指这里。</summary>
    public static string LogPath => Path.Combine(ConfigStore.Dir, "error.log");

    private static void WriteCrash(Exception ex)
    {
        try
        {
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>
    /// 单实例锁名。原来是「程序名 + exe 目录散列」，免安装版与安装版各拿一把锁，
    /// 可以同时跑两个进程（端口还会冲突）——使用者把它当多开的 bug 报上来。
    /// 现在全局唯一：哪个副本先跑，后启动的都只负责把它的窗口唤到前台。
    /// </summary>
    private static string InstanceName() => IsDev ? Brand.Name + ".Dev" : Brand.Name;

    protected override void OnExit(ExitEventArgs e)
    {
        // 服务是在 UI 线程上启动的，Kestrel 停止链路里有续延要回到 UI 线程；
        // 在这里用 GetResult() 阻塞 UI 线程就是永久死锁——实测复现过（进程退不掉）。
        // 挪到后台线程等，且最多等 8 秒：超时就放行，进程退出时框架自会拆掉监听。
        try
        {
            var stop = Task.Run(() => Proxy.StopAsync());
            if (!stop.Wait(TimeSpan.FromSeconds(8)))
                LogStore.Instance.Warn("服务", "停止超时，跳过等待直接退出");
        }
        catch { }
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
