using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using OAIRouter.Ui;

namespace OAIRouter;

public sealed class ImportSelection
{
    public bool Routes { get; init; }
    public bool Maps { get; init; }
    public bool Pricing { get; init; }
    public bool Models { get; init; }
    public bool Settings { get; init; }
}

public partial class ImportWindow : DialogWindow
{
    /// <summary>勾选框本体 13px + 与文字的间距 4px（WPF 默认模板）。这段占位不随文案变，所以是常量。</summary>
    private const double CheckBoxGlyph = 17;

    public ImportSelection Selection { get; private set; } = new();

    public ImportWindow(AppConfig incoming)
    {
        InitializeComponent();

        // 只填数量，文案在 XAML 里
        ChkRoutes.Tag = incoming.Routes.Count;
        ChkMaps.Tag = incoming.ModelMap.Count;
        ChkPricing.Tag = incoming.Pricing.Count;
        ChkModels.Tag = incoming.Models.Count;

        Shell.PrimaryInvoked += (_, _) =>
        {
            Selection = new ImportSelection
            {
                Routes = ChkRoutes.IsChecked == true,
                Maps = ChkMaps.IsChecked == true,
                Pricing = ChkPricing.IsChecked == true,
                Models = ChkModels.IsChecked == true,
                Settings = ChkSettings.IsChecked == true,
            };
            DialogResult = true;
        };

        Shell.SecondaryInvoked += (_, _) => DialogResult = false;

        DialogHost.Setup(this, Shell);
    }

    /* ----------------------------- 宽度 ----------------------------- */

    /// <summary>
    /// 宽度按这个框自己的内容算（规范 §七.2）：标题、说明、五行勾选项里最宽的那一行。
    /// 勾选项的文案里带数量（「路由规则（3 条）」），数量变了宽度跟着变 —— 这正是量文案而不是量屏幕的意义。
    /// </summary>
    protected override MeasuredLine MeasureContent()
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var lines = new List<DialogLine>
        {
            new(Shell.Headline, DialogShell.HeadlineSize, FontWeights.SemiBold, DialogShell.IconColumn),
            new(Hint.Text, 11.5, FontWeights.Normal),
        };

        foreach (var box in new[] { ChkRoutes, ChkMaps, ChkPricing, ChkModels, ChkSettings })
            lines.Add(new DialogLine(LabelOf(box), 12, FontWeights.Normal, CheckBoxGlyph));

        var widest = DialogMetrics.WidestText(lines, FontFamily, dpi);

        // 勾选项都很短时，宽度由按钮栏兜底（不然「取消 / 导入」会被挤出卡片）
        return Shell.ButtonRowWidth > widest.Width
            ? new MeasuredLine(Shell.ButtonRowWidth, "按钮栏")
            : widest;
    }

    protected override double HorizontalInset => DialogShell.HorizontalInset;

    /// <summary>读勾选框上真正渲染出来的文案：前四项是 Run 拼的（数量走绑定），第五项是普通字符串。</summary>
    private static string LabelOf(CheckBox box) => box.Content switch
    {
        TextBlock block => new TextRange(block.ContentStart, block.ContentEnd).Text,
        string text => text,
        _ => "",
    };
}
