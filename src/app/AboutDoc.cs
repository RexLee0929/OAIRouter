using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace OAIRouter;

/// <summary>
/// 「帮助 → 关于」的内容源：src/app/ABOUT.md（EmbeddedResource）。
/// 文案在 Markdown 里维护，改一句话只改 md，不碰 C#、也不重算尺寸。
///
/// 排版参数逐项对齐规范 §八（源自 txtFormat 关于页的实测值，DIP @96dpi）：
///
///   ## **{NAME}** **{VERSION}**   标题：19px 半粗，程序名正文色，版本号强调色
///   紧随的一个段落                 副标题：12px 灰字，紧贴标题
///   **小节**                      小节标题：12px 半粗；若下一段是普通行（中间可隔一个空行），
///                                 与其合并为同一段（「快捷键」就是这种）
///   - 条目                        项目符号：圆点用强调色，
///                                 缩进 40.5 / 首行悬挂 −9.2，回行对齐条目文字
///   普通行                        同一空行块内的相邻行合并为一段（硬换行），行内支持 **加粗**
///   ©  开头的行                    所在段落判定为页脚：11px #8A8A8A
///
/// 间距：所有块只给「上边距 12」，列表项之间 0；行距一律用字体自然行距，
/// 不写死 LineHeight（写死会让整页比参照松 2~3px）。
/// 占位符 {NAME}/{VERSION}/{AUTHOR}/{TAGLINE} 由调用方用 Brand 替换。
/// </summary>
public static class AboutDoc
{
    // ---- 排版常量：规范 §八 的实测值 ----
    private const double BlockGap = 12;      // 块间额外留白
    private const double ListIndent = 40.5;  // 列表文字左边界（相对正文左边界）
    private const double BulletHang = 9.2;   // 圆点相对条目文字的悬挂量（圆点起点 = 40.5 − 9.2）
    private const double TitleSize = 19;
    private const double FooterSize = 11;

    private static Brush _accent = Brushes.DodgerBlue;
    private static Brush _ink = Brushes.Black;
    private static Brush _muted = Brushes.Gray;
    private static Brush _dim = Brushes.DimGray;

    public static FlowDocument Parse(string md, Brush accent, Brush ink, Brush muted, Brush dim)
    {
        _accent = accent;
        _ink = ink;
        _muted = muted;
        _dim = dim;

        var doc = new FlowDocument
        {
            // PagePadding 必须归零：模板值 5,0,5,0 会让正文整体右移 5px，和卡片内边距对不齐
            PagePadding = new Thickness(0),
            FontSize = 12,
            Foreground = ink,
        };

        var lines = md.Replace("\r\n", "\n").Split('\n');
        var pending = new List<string>();   // 当前空行块里攒的普通行
        var footerBlock = false;            // 当前块里出现过 © 行 → 页脚样式
        var subtitleDone = false;
        var listStarted = false;            // 上一块是列表项

        void Flush()
        {
            if (pending.Count == 0) return;

            var p = new Paragraph { Margin = new Thickness(0, BlockGap, 0, 0) };
            for (var i = 0; i < pending.Count; i++)
            {
                if (i > 0) p.Inlines.Add(new LineBreak());
                AddInline(p, pending[i]);
            }

            if (footerBlock)
            {
                p.FontSize = FooterSize;
                p.Foreground = _dim;
            }
            else if (!subtitleDone && doc.Blocks.Count > 0)
            {
                // 标题后的第一个段落固定当副标题：灰字、紧贴标题
                p.Foreground = _muted;
                p.Margin = new Thickness(0);
                subtitleDone = true;
            }

            doc.Blocks.Add(p);
            pending.Clear();
            footerBlock = false;
            listStarted = false;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();

            if (line.Length == 0)
            {
                Flush();
                continue;
            }

            // 标题：# 与 ## 都认，去掉 ** 加粗标记后交给 TitlePara 上色
            if (line.StartsWith('#') && line.TrimStart('#').Trim().Length > 0)
            {
                Flush();
                doc.Blocks.Add(TitlePara(line.TrimStart('#').Trim().Replace("**", "")));
                continue;
            }

            // 小节标题：整行被 ** 包住
            if (line.StartsWith("**") && line.EndsWith("**") && line.Length > 4)
            {
                Flush();
                var head = line[2..^2].Trim();

                // 小节标题后若跟着普通行（允许隔一个空行），合并进同一段 ——「快捷键」的样子；
                // 后面直接是列表/标题/另一个小节则各走各的 ——「主要功能」的样子。
                var j = i + 1;
                if (j < lines.Length && lines[j].TrimEnd().Length == 0) j++;

                var follow = new List<string>();
                while (j < lines.Length)
                {
                    var next = lines[j].TrimEnd();
                    if (next.Length == 0) break;
                    if (next.StartsWith('#') || next.StartsWith("- ")
                        || (next.StartsWith("**") && next.EndsWith("**") && next.Length > 4)) break;
                    follow.Add(next);
                    j++;
                }

                var p = new Paragraph { Margin = new Thickness(0, BlockGap, 0, 0) };
                p.Inlines.Add(new Run(head) { FontWeight = FontWeights.SemiBold });
                foreach (var extra in follow)
                {
                    p.Inlines.Add(new LineBreak());
                    AddInline(p, extra);
                }
                if (follow.Count > 0) i = j - 1;     // 被合并的行直接跳过

                doc.Blocks.Add(p);
                listStarted = false;
                continue;
            }

            // 项目符号：圆点强调色，悬挂缩进（回行对齐条目文字，不对齐圆点）
            if (line.StartsWith("- "))
            {
                Flush();
                var p = new Paragraph
                {
                    Margin = new Thickness(ListIndent, listStarted ? 0 : BlockGap, 0, 0),
                    TextIndent = -BulletHang,
                };
                p.Inlines.Add(new Run("• ") { Foreground = _accent });
                AddInline(p, line[2..].Trim());
                doc.Blocks.Add(p);
                listStarted = true;
                continue;
            }

            // © 行：所在段落按页脚处理
            if (line.StartsWith("© ")) footerBlock = true;
            pending.Add(line);
        }

        Flush();
        return doc;
    }

    /// <summary>行内 **加粗** 解析；不支持嵌套与斜体（关于页用不到）。</summary>
    private static void AddInline(Paragraph p, string text)
    {
        var parts = text.Split(["**"], StringSplitOptions.None);
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) continue;
            var run = new Run(parts[i]);
            if (i % 2 == 1) run.FontWeight = FontWeights.SemiBold;
            p.Inlines.Add(run);
        }
    }

    /// <summary>标题段：程序名正文色，版本号（最后一个空格后、数字开头的 token）用强调色。</summary>
    private static Paragraph TitlePara(string text)
    {
        var p = new Paragraph
        {
            Margin = new Thickness(0),
            FontSize = TitleSize,
            FontWeight = FontWeights.SemiBold,
        };

        var sp = text.LastIndexOf(' ');
        var version = sp >= 0 ? text[(sp + 1)..] : "";
        var name = sp >= 0 ? text[..sp] : text;

        if (version.Length > 0 && char.IsDigit(version[0]))
        {
            p.Inlines.Add(new Run(name + " ") { Foreground = _ink });
            p.Inlines.Add(new Run(version) { Foreground = _accent });
        }
        else
        {
            p.Inlines.Add(new Run(text) { Foreground = _ink });
        }

        return p;
    }
}
