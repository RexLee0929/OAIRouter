using System.Windows;
using System.Windows.Media;

namespace OAIRouter.Ui;

/// <summary>
/// 对话框基类：统一窗口级的两件事 —— 外观是主窗口的延续（原生标题栏 + 同一套 DWM 配色），
/// 以及尺寸按内容拟合（规范 §四 / §七.2 / §八）。
///
/// 尺寸怎么来：宽度由子类在 <see cref="MeasureContent"/> 里量出「最长的一行不可断内容」，
/// 基类加上卡片留白、夹到本程序的正文折行阈值写回 Width；高度按实际内容高校正 2~3 轮。
/// 这里不存任何「宽度档位」—— 谁的内容长谁就宽，改了文案窗口自己跟着变。
///
/// 不用 SizeToContent 量高：WPF 对富文本量出来偏小，会把底部按钮栏挤出窗口。
/// 宽度也不能交给它 —— 折行文本在无限宽下量出来是「一整行」，窗口会横到屏幕外。
/// </summary>
public class DialogWindow : Window
{
    private int _fitPass;

    protected DialogWindow()
    {
        WindowStyle = WindowStyle.SingleBorderWindow;   // 原生标题栏（§四）
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        Background = new SolidColorBrush(Color.FromRgb(0xF2, 0xF6, 0xFB));

        // Loaded 在首次上屏之前，先量一次，避免看到「小窗弹出来再变大」；
        // ContentRendered 之后再校一次，收掉布局回弹留下的误差。
        Loaded += (_, _) => FitToContent();
        ContentRendered += (_, _) => FitToContent();
    }

    /// <summary>
    /// 本框「最长的一行不可断内容」需要多宽（px，不含窗口边框、不含 <see cref="HorizontalInset"/>）。
    /// 子类量自己的文案；返回 Width = 0 表示这一屏不按内容定宽。
    /// </summary>
    protected virtual MeasuredLine MeasureContent() => default;

    /// <summary>
    /// 卡片与外边距的左右留白（px）：窗口宽 = 内容宽 + 这个值。
    /// 数值对应本框 XAML 里的 Margin + Padding + BorderThickness —— 它是留白，不是宽度档位，
    /// 改版式要连这里一起改。
    /// </summary>
    protected virtual double HorizontalInset => 0;

    /// <summary>最近一次量宽的结果说明（是哪一行把窗口撑到这个宽），只给自检日志用。</summary>
    internal string WidthEvidence { get; private set; } = "";

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowChrome.Apply(this);
    }

    /// <summary>
    /// 把宽高校正到刚好放下内容（规范 §七.2）。做法：按内容量宽，再按该宽度量高，取自然尺寸
    /// 加回非客户区。跑 2~3 轮收敛 —— 第一轮改尺寸之后布局会回弹一次，只量一轮会差几像素。
    /// </summary>
    protected virtual void FitToContent()
    {
        _fitPass = 0;
        FitPass();
    }

    /// <summary>
    /// 派生类可在这里补一次校正。关于框用它把富文本「量高偏小」的那部分补回来：
    /// 量高负责收敛到大致高度（能涨也能缩），差值校正负责兜住 RichTextBox 的误差。
    /// </summary>
    protected virtual void OnFitPass()
    {
    }

    private void FitPass()
    {
        FitByMeasure();
        OnFitPass();

        if (++_fitPass < 3)
            Dispatcher.BeginInvoke(FitPass, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void FitByMeasure()
    {
        if (Content is not FrameworkElement root) return;

        var m = root.Margin;

        // 非客户区 = 窗口 −（内容实际尺寸 + 它自己的外边距）。
        // ActualHeight/Width 不含 Margin，DesiredSize 含 Margin，两边口径不同，这里一次对齐。
        var chromeH = ActualHeight - root.ActualHeight - m.Top - m.Bottom;
        var chromeW = ActualWidth - root.ActualWidth - m.Left - m.Right;

        if (chromeH <= 0 || chromeW <= 0) return;

        var windowW = FitWidth(chromeW);
        var availW = windowW - chromeW - m.Left - m.Right;

        root.Measure(new Size(availW, double.PositiveInfinity));
        var natural = root.DesiredSize.Height;      // 含 Margin
        root.InvalidateMeasure();                   // 恢复常规约束，否则下一轮布局沿用无限高

        // 窗口高 = 非客户区 + 内容（含外边距）
        Apply(Math.Ceiling(chromeH + natural));
    }

    /// <summary>
    /// 按内容量宽：窗口宽 = 最长一行 + 卡片留白 + 系统边框，上限是本程序的正文折行阈值。
    /// 返回本次使用的窗口宽（含系统边框）。
    /// </summary>
    private double FitWidth(double chromeW)
    {
        var measured = MeasureContent();
        if (measured.Width <= 0) return ActualWidth;    // 不按内容定宽的窗口：宽归 XAML / 用户

        var cap = DialogMetrics.ReadLimit + HorizontalInset + chromeW;
        var want = Math.Ceiling(measured.Width) + HorizontalInset + chromeW;
        var target = Math.Max(MinWidth, Math.Min(cap, want));

        if (double.IsNaN(Width) || Math.Abs(target - Width) > 0.5) Width = target;

        WidthEvidence = want > cap
            ? $"{Math.Round(measured.Width)}px 的行「{Shorten(measured.Text)}」超折行阈值 {DialogMetrics.ReadLimit}，夹到上限"
            : $"{Math.Round(measured.Width)}px 的行「{Shorten(measured.Text)}」";

        return target;
    }

    private static string Shorten(string text)
        => text.Length <= 28 ? text : text[..28] + "…";

    /// <summary>写回高度，夹在 MinHeight、MaxHeight 与「一屏之内」之间。</summary>
    protected void Apply(double target)
    {
        var cap = Math.Min(SystemParameters.WorkArea.Height - 80, MaxHeight);
        target = Math.Max(MinHeight, Math.Min(cap, target));
        if (Math.Abs(target - Height) > 0.5) Height = target;
    }
}
