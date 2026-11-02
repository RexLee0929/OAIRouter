using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace OAIRouter;

/// <summary>提示条落点。程序看得见就贴在程序上，看不见才退回任务栏一侧。</summary>
public enum ToastAnchor
{
    /// <summary>程序窗口的正上方居中（默认）：用户视线在程序里，提示就得在程序里。</summary>
    ProgramTop,

    /// <summary>任务栏一侧（右下角）。只给「已最小化到托盘」这类主窗口已经藏起来的提示用。</summary>
    TrayCorner,
}

/// <summary>
/// 自绘通知小窗（规范 §七：不用托盘气泡、不用系统 Toast —— 那两者的提示音由系统决定，
/// 程序无权关掉）。这里全程静音、不抢焦点、不进通知中心。
/// </summary>
public partial class Toast : Window
{
    private static Toast? _current;
    private static DateTime _lastShown = DateTime.MinValue;
    private static string _lastText = "";

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3.2) };
    private readonly ToastAnchor _anchor;

    public Toast(string message, bool warn, ToastAnchor anchor)
    {
        InitializeComponent();

        _anchor = anchor;
        Message.Text = message;
        // 图标与提示文字同一个语义色：警告=Accent.Warn，其余=强调色（§四.1）
        Glyph.Content = Icons.Get(warn ? "warning" : "success",
            warn ? new SolidColorBrush(Accent.Warn) : (Brush)FindResource("AccentBrush"), 16);

        _timer.Tick += (_, _) => Close();
    }

    private void Place()
    {
        var owner = Application.Current?.MainWindow;
        var ownerUsable = owner is { IsVisible: true } && owner.WindowState != WindowState.Minimized;

        if (_anchor == ToastAnchor.TrayCorner || !ownerUsable)
        {
            // 主窗口看不见了（最小化 / 最小化到托盘 / 启动中）：贴任务栏一侧
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 8;
            Top = area.Bottom - ActualHeight - 8;
            return;
        }

        // 程序的中上方：横向以主窗口居中，纵向落在标题栏 + 菜单栏之下
        Left = owner!.Left + (owner.ActualWidth - ActualWidth) / 2;
        Top = owner.Top + 84;
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        Place();

        // 摆好位置才显形（XAML 里 Opacity=0），淡入一下，不闪
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        _timer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        if (ReferenceEquals(_current, this)) _current = null;
        base.OnClosed(e);
    }

    /// <summary>关键信息才通知，且限频去重，避免刷屏。</summary>
    public static void Show(string message, bool warn = false, ToastAnchor anchor = ToastAnchor.ProgramTop)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        if (message == _lastText && (DateTime.Now - _lastShown).TotalSeconds < 20) return;
        if ((DateTime.Now - _lastShown).TotalSeconds < 2) return;

        _lastText = message;
        _lastShown = DateTime.Now;

        Application.Current?.Dispatcher.Invoke(() =>
        {
            _current?.Close();
            var toast = new Toast(message, warn, anchor);
            _current = toast;
            toast.Show();
        });
    }
}
