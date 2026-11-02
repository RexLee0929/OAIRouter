using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using OAIRouter.Ui;

namespace OAIRouter;

/// <summary>
/// 自绘错误框 —— 「能不弹就不弹」阶梯里剩下的那一档（规范 §七.5 级 ⑤）。
/// 只有「必须让用户知道、而内容区已经表达不了」的场合才用，目前只有未捕获异常。
///
/// 三条硬规矩（§七.4）：
///   · 窗口标题只写程序名，事情本身写进卡片内标题；
///   · 同屏只允许一个，后来者只进 error.log（不叠弹窗）；
///   · 没有日志入口时退化为单个「关闭」。
/// </summary>
public partial class ErrorDialog : DialogWindow
{
    private static bool _showing;

    private readonly string? _logPath;

    private ErrorDialog(string headline, string body, string? logPath)
    {
        InitializeComponent();

        Title = Brand.Name;
        _logPath = logPath;

        Shell.Headline = string.IsNullOrWhiteSpace(headline) ? "操作没有完成" : headline;
        Shell.Message = body;

        if (string.IsNullOrEmpty(logPath))
        {
            // 没有日志可打开：只留「关闭」，Enter 与 Esc 都落到它上面
            Shell.PrimaryText = "";
            Shell.SecondaryText = "关闭";
        }

        Shell.PrimaryInvoked += (_, _) =>
        {
            OpenLog();
            Close();
        };
        Shell.SecondaryInvoked += (_, _) => Close();

        DialogHost.Setup(this, Shell);
    }

    /* ----------------------------- 宽度 ----------------------------- */

    /// <summary>
    /// 宽度按这个框自己的文案算（规范 §七.2）—— 报错文案每家都不同，
    /// 短的报错就该窄，长的那条自然会撑开，不用谁去挑档位。
    /// </summary>
    protected override MeasuredLine MeasureContent()
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // 标题和图标同处一行：文字前面要让出图标列
        var headline = DialogMetrics.WidestText(
            [new DialogLine(Shell.Headline, DialogShell.HeadlineSize, FontWeights.SemiBold, DialogShell.IconColumn)],
            FontFamily, dpi);

        var body = DialogMetrics.WidestText(
            DialogMetrics.HardLines(Shell.Message).Select(line => new DialogLine(line, 12, FontWeights.Normal)),
            FontFamily, dpi);

        var widest = headline.Width >= body.Width ? headline : body;

        // 文案再短，也不能窄到把「打开日志 / 关闭」挤出去
        return Shell.ButtonRowWidth > widest.Width
            ? new MeasuredLine(Shell.ButtonRowWidth, "按钮栏")
            : widest;
    }

    protected override double HorizontalInset => DialogShell.HorizontalInset;

    /// <summary>给界面抓图工具用的样例框（不走单实例守卫，只为看外观）。</summary>
    internal static ErrorDialog ForRender() => new(
        "刚才的操作没有完成",
        "端口 8787 被别的程序占用了，服务没能启动。\n\n换一个端口再试，或先关掉占用该端口的程序。",
        App.LogPath);

    /// <summary>
    /// 报一个「用户必须知道」的错误。调用方负责写 error.log 与状态栏红字，
    /// 这里只负责把话说清楚。已有对话框在屏幕上时直接返回 —— 一次只允许一个（§七.1）。
    /// </summary>
    public static void Report(Window? owner, string headline, string body, string? logPath)
    {
        if (_showing) return;

        // 别的对话框开着就不再叠一层：日志与状态栏已经把信息留下了
        if (Application.Current is { } app
            && app.Windows.OfType<Window>().Any(w => w != owner && w.IsVisible && w is DialogWindow))
            return;

        _showing = true;
        try
        {
            var dialog = new ErrorDialog(headline, body, logPath);
            if (owner is { IsVisible: true })
            {
                dialog.Owner = owner;
            }
            else
            {
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            dialog.ShowDialog();
        }
        finally
        {
            _showing = false;
        }
    }

    private void OpenLog()
    {
        try
        {
            if (string.IsNullOrEmpty(_logPath)) return;

            if (!File.Exists(_logPath)) File.WriteAllText(_logPath, "");

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{_logPath}\"",
                UseShellExecute = true,
            });
        }
        catch
        {
            // 打不开就算了：错误框本身不该再抛错
        }
    }
}
