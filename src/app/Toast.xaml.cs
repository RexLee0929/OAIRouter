using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace OAIRouter;

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

    public Toast(string message, bool warn)
    {
        InitializeComponent();
        Message.Text = message;
        Glyph.Data = Geometry.Parse(warn
            ? "M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18zM12 8v5M12 16v.01"
            : "M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18zM8.5 12.5l2.5 2.5 4.5-5");
        Glyph.Stroke = warn ? new SolidColorBrush(Accent.Warn) : (Brush)FindResource("AccentBrush");

        _timer.Tick += (_, _) => Close();
    }

    private void Place()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 8;
        Top = area.Bottom - ActualHeight - 8;
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        Place();
        _timer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        if (ReferenceEquals(_current, this)) _current = null;
        base.OnClosed(e);
    }

    /// <summary>关键信息才通知，且限频去重，避免刷屏。</summary>
    public static void Show(string message, bool warn = false)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        if (message == _lastText && (DateTime.Now - _lastShown).TotalSeconds < 20) return;
        if ((DateTime.Now - _lastShown).TotalSeconds < 2) return;

        _lastText = message;
        _lastShown = DateTime.Now;

        Application.Current?.Dispatcher.Invoke(() =>
        {
            _current?.Close();
            var toast = new Toast(message, warn);
            _current = toast;
            toast.Show();
        });
    }
}
