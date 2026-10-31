using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace OAIRouter;

public partial class AboutDialog : Window
{
    private readonly DispatcherTimer _hintTimer = new() { Interval = TimeSpan.FromSeconds(1.6) };

    public AboutDialog()
    {
        InitializeComponent();
        BuildDocument();
        _hintTimer.Tick += (_, _) => { CopyHint.Text = ""; _hintTimer.Stop(); ResetPill(); };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowChrome.Apply(this);
    }

    private void BuildDocument()
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1B)),
            PagePadding = new Thickness(0),
        };

        var title = new Paragraph { Margin = new Thickness(0, 0, 0, 2) };
        title.Inlines.Add(new Run(Brand.Name) { FontSize = 19, FontWeight = FontWeights.SemiBold });
        title.Inlines.Add(new Run(" "));
        title.Inlines.Add(new Run(Brand.Version) { FontSize = 19, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("AccentBrush") });
        doc.Blocks.Add(title);

        doc.Blocks.Add(Para(Brand.Tagline, 12, 0x5C, 0x5C, 0x5C, 0));

        doc.Blocks.Add(Para(
            "把本机多个模型来源收拢到一个地址上：按路径前缀分流转发，按别名替换模型名，" +
            "并在需要时把 Chat Completions 翻译成 Responses API，让工具调用与思考档位可以同时使用。",
            12, 0x1B, 0x1B, 0x1B, 12));

        doc.Blocks.Add(Head("主要功能"));
        doc.Blocks.Add(Bullet("路径分流：/openai、/newapi 各自指向不同上游，互不干扰"));
        doc.Blocks.Add(Bullet("模型映射：客户端填 aa，转发时替换为真实模型名"));
        doc.Blocks.Add(Bullet("协议翻译：Chat Completions ⇄ Responses，工具与思考共存"));
        doc.Blocks.Add(Bullet("运行日志：只记时间、路由、模型、状态码与耗时，不含密钥与对话内容"));
        doc.Blocks.Add(Bullet("托盘常驻：可隐藏到托盘，关键事件静音通知"));

        doc.Blocks.Add(Head("快捷键"));
        doc.Blocks.Add(Para("Ctrl+C 复制监听地址　　Ctrl+L 清空日志　　Ctrl+Q 退出", 12, 0x1B, 0x1B, 0x1B, 12));

        var footer = new Paragraph { Margin = new Thickness(0, 12, 0, 0), FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)) };
        footer.Inlines.Add(new Run("免费软件，依据 MIT License 分发。"));
        footer.Inlines.Add(new LineBreak());
        footer.Inlines.Add(new Run($"© 2026 {Brand.Author}　保留所有权利。"));
        doc.Blocks.Add(footer);

        Body.Document = doc;
    }

    private static Paragraph Para(string text, double size, byte r, byte g, byte b, double top)
        => new(new Run(text))
        {
            FontSize = size,
            Foreground = new SolidColorBrush(Color.FromRgb(r, g, b)),
            Margin = new Thickness(0, top, 0, 0),
        };

    private static Paragraph Head(string text)
        => new(new Run(text))
        {
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 0),
        };

    private static Paragraph Bullet(string text)
    {
        var p = new Paragraph { Margin = new Thickness(31, 0, 0, 0), TextIndent = -9 };
        p.Inlines.Add(new Run("•  "));
        p.Inlines.Add(new Run(text));
        return p;
    }

    private void OnCopyEmail(object sender, MouseButtonEventArgs e)
    {
        try
        {
            Clipboard.SetText(Brand.Email);
            MailPill.Background = (Brush)FindResource("AccentSoftBrush");
            MailPill.BorderBrush = (Brush)FindResource("AccentBrush");
            MailText.Foreground = (Brush)FindResource("AccentBrush");
            CopyHint.Text = "已复制到剪贴板";
            _hintTimer.Stop();
            _hintTimer.Start();
        }
        catch
        {
            CopyHint.Text = "复制失败";
            _hintTimer.Stop();
            _hintTimer.Start();
        }
    }

    private void ResetPill()
    {
        MailPill.Background = Brushes.White;
        MailPill.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF1));
        MailText.Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1B));
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
