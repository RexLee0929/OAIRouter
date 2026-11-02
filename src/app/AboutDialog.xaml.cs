using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OAIRouter.Ui;

namespace OAIRouter;

/// <summary>
/// 「帮助 → 关于」。三段式（内容框 / 联系条 / 按钮栏），规范 §八 的样板：
/// 原生标题栏 + 同一套配色与字体 + 可选中的富文本 + 高度随内容拟合。
/// 文案全部在 src/app/ABOUT.md，这里只做占位符替换与渲染。
/// </summary>
public partial class AboutDialog : DialogWindow
{
    private static string? _aboutMd;

    private readonly DispatcherTimer _hintTimer = new() { Interval = TimeSpan.FromSeconds(1.6) };

    public AboutDialog()
    {
        InitializeComponent();

        // 窗口标题写「关于 + 程序名」：程序名只有一个来源
        Title = $"关于 {Brand.Name}";

        // 高度上限放在窗口上，而不是给内容加 MaxHeight ——
        // 否则内容一长就变成「小框里滚动」，而不是整窗变高
        MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height - 60);

        BuildDocument();

        _hintTimer.Tick += (_, _) =>
        {
            CopyHint.Text = "";
            _hintTimer.Stop();
            ResetPill();
        };
    }

    /* ----------------------------- 内容 ----------------------------- */

    private void BuildDocument()
    {
        var md = ReadAbout()
            .Replace("{NAME}", Brand.Name)
            .Replace("{VERSION}", Brand.Version)
            .Replace("{AUTHOR}", Brand.Author)
            .Replace("{TAGLINE}", Brand.Tagline);

        Doc.Document = AboutDoc.Parse(md,
            FindBrush("AccentBrush"), FindBrush("TextBrush"),
            FindBrush("SubBrush"), FindBrush("HintBrush"));

        // RichTextBox 会在文档挂上去之后的布局里把 PagePadding 复位成模板值 5,0,5,0，
        // 正文于是整体右移 5px、和卡片左内边距对不齐。赋值时还没上屏，设一次不管用，
        // 必须在 Loaded 之后补压，之后每轮高度拟合也顺手压一次。
        Doc.Loaded += (_, _) => ZeroPadding();
        ZeroPadding();
    }

    private void ZeroPadding()
    {
        if (Doc.Document is not null) Doc.Document.PagePadding = new Thickness(0);
    }

    /// <summary>文案从 exe 内嵌的 ABOUT.md 读，不散落在 C# 里。</summary>
    private static string ReadAbout()
    {
        if (_aboutMd is not null) return _aboutMd;

        var asm = typeof(AboutDialog).Assembly;
        using var stream = asm.GetManifestResourceStream($"{Brand.Name}.ABOUT.md");
        if (stream is null) return _aboutMd = $"{Brand.Name} {Brand.Version}";

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return _aboutMd = reader.ReadToEnd();
    }

    /* ----------------------------- 宽度 ----------------------------- */

    /// <summary>
    /// 宽度按 ABOUT.md 里最宽的那一行算（规范 §七.2）。关于页是唯一一篇「一整篇文档」，
    /// 所以量的是文档本身，而不是谁手抄的一份文案。
    /// </summary>
    protected override MeasuredLine MeasureContent()
        => DialogMetrics.WidestDocumentLine(Doc.Document, Doc.FontFamily,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    /// <summary>外边距 20×2 + 卡片内边距 14×2 + 卡片边框 1×2（AboutDialog.xaml 的三段式布局）。</summary>
    protected override double HorizontalInset => 70;

    /* ----------------------------- 高度拟合 ----------------------------- */

    /// <summary>
    /// 在基类「按内容量高」之后补一次差值校正：RichTextBox 的 DesiredSize 会偏小，
    /// 溢出量只能从 ExtentHeight − ViewportHeight 读出来，把差值加回窗口高。
    /// 只用差值不行 —— 它读不出「内容比窗口矮」，窗口就永远缩不回去。
    /// </summary>
    protected override void OnFitPass()
    {
        ZeroPadding();      // 布局过程会把它复位，每轮都补一次

        if (Doc.ExtentHeight <= 0 || Doc.ViewportHeight <= 0) return;

        var delta = Doc.ExtentHeight - Doc.ViewportHeight;
        if (Math.Abs(delta) > 0.5) Apply(Math.Ceiling(ActualHeight + delta));
    }

    /* ----------------------------- 邮箱 ----------------------------- */

    private static Brush FindBrush(string key)
        => Application.Current.TryFindResource(key) as Brush ?? Brushes.DimGray;

    private void OnCopyEmail(object sender, MouseButtonEventArgs e)
    {
        try
        {
            Clipboard.SetText(Brand.Email);
            MailPill.Background = FindBrush("AccentSoftBrush");
            MailPill.BorderBrush = FindBrush("AccentBrush");
            MailText.Foreground = FindBrush("AccentBrush");
            CopyHint.Text = "已复制到剪贴板";
        }
        catch
        {
            CopyHint.Foreground = FindBrush("HintBrush");
            CopyHint.Text = "复制失败，请手动选中邮箱文字复制";
        }

        _hintTimer.Stop();
        _hintTimer.Start();
    }

    private void ResetPill()
    {
        MailPill.Background = FindBrush("CardBrush");
        MailPill.BorderBrush = FindBrush("LineBrush");
        MailText.Foreground = FindBrush("TextBrush");
        CopyHint.Foreground = FindBrush("AccentBrush");
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
