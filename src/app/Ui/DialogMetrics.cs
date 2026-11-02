using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace OAIRouter.Ui;

/// <summary>一行「不可断内容」：文案 + 它渲染时的字号字重 + 这一行前面还要让出的宽度。</summary>
public readonly record struct DialogLine(string Text, double Size, FontWeight Weight, double Indent = 0);

/// <summary>量出来的结果：多宽，以及是哪一行决定的（那行只用于自检日志，不参与排版）。</summary>
public readonly record struct MeasuredLine(double Width, string Text);

/// <summary>
/// 对话框宽度的量法（规范 §七.2 / §十三.4）。宽度不写死、也不从档位里挑：
/// 窗口量一遍「最长的一行不可断内容」，加上卡片左右留白，就是这一屏该有的宽。
/// 同一个程序里所有对话框共用这一套尺子，但各量自己的文案 —— 于是宽度天然不同；
/// 哪天文案改长了，宽度自己跟着变，不用谁想起来去重量。
///
/// 三条约定：
///   · 按「行」量，不按「段」量：一段里可能有多条硬换行（关于页的快捷键块就是），
///     段宽是几行拼起来的，不是「最长的一行」；
///   · 每个 Run 用自己的字号字重量：关于页里 19px 标题、12px 正文、11px 页脚混在一篇里；
///   · PixelsPerDip 用窗口实际 DPI，不给就按 96dpi 量 —— 150% 缩放下量出的宽偏小，正文被迫多折一行（§十）。
/// </summary>
public static class DialogMetrics
{
    /// <summary>
    /// 正文折行阈值：一行超过这个像素宽就让正文折行，窗口不再跟着撑宽。
    /// 依据：正文 12px 微软雅黑，一个汉字约 12px；一行 45 个汉字（≈540px）是一屏扫读
    /// 比较舒服的上限，再长眼睛回扫会累。本程序的文案都是短句 + 一段说明，取这个值。
    /// 它管的是「正文多宽折行」，不是窗口宽度的档位 —— 窗口宽 = 阈值 + 卡片左右留白。
    /// </summary>
    public const double ReadLimit = 540;

    /// <summary>量一行不换行时的自然宽（含行尾空格）。</summary>
    public static double TextWidth(string? text, double size, FontWeight weight,
                                   FontFamily family, double pixelsPerDip)
    {
        if (string.IsNullOrWhiteSpace(text) || size <= 0) return 0;

        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal),
            size, Brushes.Black, pixelsPerDip);

        return formatted.WidthIncludingTrailingWhitespace;
    }

    /// <summary>纯文字行里最宽的一行。</summary>
    public static MeasuredLine WidestText(IEnumerable<DialogLine> lines, FontFamily family, double pixelsPerDip)
    {
        var best = new MeasuredLine(0, "");

        foreach (var line in lines)
        {
            var width = TextWidth(line.Text, line.Size, line.Weight, family, pixelsPerDip) + line.Indent;
            if (width > best.Width) best = new MeasuredLine(width, line.Text.Trim());
        }

        return best;
    }

    /// <summary>
    /// 富文本文档里最宽的一行（关于框用）。逐段、逐硬换行、逐 Run 累加：
    /// 段里混着加粗与不同字号时，只有按 Run 累加才量得准。
    /// </summary>
    public static MeasuredLine WidestDocumentLine(FlowDocument doc, FontFamily family, double pixelsPerDip)
    {
        var best = new MeasuredLine(0, "");

        foreach (var paragraph in Paragraphs(doc.Blocks))
        {
            var size = paragraph.FontSize > 0 && !double.IsNaN(paragraph.FontSize)
                ? paragraph.FontSize
                : doc.FontSize;

            var indent = paragraph.Margin.Left;      // 列表项的悬挂缩进：圆点从 40.5 起
            var width = 0d;
            var text = new StringBuilder();

            void Flush()
            {
                if (width + indent > best.Width)
                    best = new MeasuredLine(width + indent, text.ToString().Trim());

                width = 0;
                text.Clear();
            }

            Walk(paragraph.Inlines, size, paragraph.FontWeight,
                (runText, runSize, runWeight) =>
                {
                    width += TextWidth(runText, runSize, runWeight, family, pixelsPerDip);
                    text.Append(runText);
                },
                Flush);

            Flush();
        }

        return best;
    }

    /// <summary>按硬换行拆行：\\n / \\r\\n 都认，返回的每一行才是「一行」。</summary>
    public static IEnumerable<string> HardLines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];

        return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }

    /* ----------------------------- 富文本遍历 ----------------------------- */

    private static IEnumerable<Paragraph> Paragraphs(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    yield return paragraph;
                    break;

                case List list:
                    foreach (var item in list.ListItems)
                        foreach (var paragraph in Paragraphs(item.Blocks))
                            yield return paragraph;
                    break;

                case Section section:
                    foreach (var paragraph in Paragraphs(section.Blocks))
                        yield return paragraph;
                    break;
            }
        }
    }

    private static void Walk(InlineCollection inlines, double size, FontWeight weight,
                             Action<string, double, FontWeight> onRun, Action onBreak)
    {
        foreach (var inline in inlines)
        {
            var ownSize = LocalSize(inline, size);
            var ownWeight = LocalWeight(inline, weight);

            switch (inline)
            {
                case Run run:
                    onRun(run.Text, ownSize, ownWeight);
                    break;

                case LineBreak:
                    onBreak();
                    break;

                case Span span:                       // Span / Bold / Hyperlink 都在这条树上
                    Walk(span.Inlines, ownSize, ownWeight, onRun, onBreak);
                    break;
            }
        }
    }

    /// <summary>字号只在「自己设过」时才算数，否则继承父级的（Run 的 FontSize 默认是 NaN）。</summary>
    private static double LocalSize(TextElement element, double inherited)
        => element.ReadLocalValue(TextElement.FontSizeProperty) is double value && value > 0 && !double.IsNaN(value)
            ? value
            : inherited;

    /// <summary>字重同理：段落设了 SemiBold、Run 没设时必须继承段落的，否则加粗行会量窄。</summary>
    private static FontWeight LocalWeight(TextElement element, FontWeight inherited)
        => element.ReadLocalValue(TextElement.FontWeightProperty) is FontWeight value ? value : inherited;
}
