using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OAIRouter;

/// <summary>
/// 24 栅格线性图标。路径数据来自 icons/Unified（《图标规范》），不依赖图片资源。
/// 表里只有几何：一条路径 = 路径数据 + 是否实心；没有颜色、没有尺寸、没有用途。
/// 用法：Icons.Get("folder", 画刷) / Icons.Get("error", Accent.ErrorBrush, 24)。
///
/// 两条纪律（《图标规范》§六.1 / §三.2）：
///   · 缩放的是 24 栅格整体（Canvas 24 + Viewbox），不是墨迹外框 —— 否则「一个加号」
///     会比「一个窗口」还大，同一排图标粗细不齐。
///   · 笔宽按渲染尺寸现算，不套素材标称的 1.4：等效物理笔宽低于 1.05px 线会塌成灰。
///
/// 程序里画图标只走这一个类：XAML 用 <c>ui:IconView</c>（见 Ui/IconView.cs），
/// 代码用 <see cref="Get"/>；不许在 XAML 里内联 Data="M…"。
/// </summary>
public static class Icons
{
    /// <summary>一条路径：D = 24 栅格里的几何，Fill = 实心块（圆点 / 开关把手 / 单选内芯）。</summary>
    public readonly record struct P(string D, bool Fill);

    /// <summary>只放程序真正用到的键；没用到的键不许留在表里当"备用"（§八.2）。</summary>
    static readonly Dictionary<string, P[]> Table = new()
    {
        ["about"] = new P[] { new("M12 3C16.9706 3 21 7.02944 21 12C21 16.9706 16.9706 21 12 21C7.02944 21 3 16.9706 3 12C3 7.02944 7.02944 3 12 3Z", false), new("M12 3v3.2M12 17.8V21M3 12h3.2M17.8 12H21", false), new("M12 11v5.5", false), new("M12 8h.01", false) },
        ["arrow-right"] = new P[] { new("M4 12h15", false), new("M13.5 6.5L20 12l-6.5 5.5", false) },
        ["check"] = new P[] { new("M4.5 12.5l5 5L19.5 7", false) },
        ["chevron-down"] = new P[] { new("M6 9.5l6 6 6-6", false) },
        ["close"] = new P[] { new("M5.5 5.5l13 13M18.5 5.5l-13 13", false) },
        ["coin"] = new P[] { new("M12 3C16.9706 3 21 7.02944 21 12C21 16.9706 16.9706 21 12 21C7.02944 21 3 16.9706 3 12C3 7.02944 7.02944 3 12 3Z", false), new("M9 7l3 4 3-4", false), new("M12 11v7", false), new("M8.8 13h6.4", false), new("M8.8 15.8h6.4", false) },
        ["copy"] = new P[] { new("M11.4 9L18.6 9C19.9255 9 21 10.0745 21 11.4L21 18.6C21 19.9255 19.9255 21 18.6 21L11.4 21C10.0745 21 9 19.9255 9 18.6L9 11.4C9 10.0745 10.0745 9 11.4 9Z", false), new("M15 9V5a1 1 0 0 0-1-1H5a1 1 0 0 0-1 1v9a1 1 0 0 0 1 1h3", false) },
        ["edit"] = new P[] { new("M4 20L4.63 16.71A1.44 1.44 0 0 1 5.92 14.28L14.58 5.62A1.44 1.44 0 0 1 16.62 5.62L18.38 7.38A1.44 1.44 0 0 1 18.38 9.42L9.72 18.08A1.44 1.44 0 0 1 7.29 19.37Z", false), new("M6.9 13.6l3.5 3.5", false) },
        ["error"] = new P[] { new("M12 3C16.9706 3 21 7.02944 21 12C21 16.9706 16.9706 21 12 21C7.02944 21 3 16.9706 3 12C3 7.02944 7.02944 3 12 3Z", false), new("M12 7.4v6", false), new("M12 17h.01", false) },
        ["folder"] = new P[] { new("M3 9.4A2.4 2.4 0 0 1 5.4 7L7.59 7A1.41 1.41 0 0 1 10 8L10 8A1.41 1.41 0 0 0 12.41 9L18.6 9A2.4 2.4 0 0 1 21 11.4L21 17.6A2.4 2.4 0 0 1 18.6 20L5.4 20A2.4 2.4 0 0 1 3 17.6Z", false) },
        ["info"] = new P[] { new("M12 3C16.9706 3 21 7.02944 21 12C21 16.9706 16.9706 21 12 21C7.02944 21 3 16.9706 3 12C3 7.02944 7.02944 3 12 3Z", false), new("M12 11.2v5.8", false), new("M12 7.8h.01", false) },
        ["list"] = new P[] { new("M8.5 6h12.5M8.5 12h12.5M8.5 18h12.5", false), new("M4 6h.01", false), new("M4 12h.01", false), new("M4 18h.01", false) },
        ["mail"] = new P[] { new("M5.4 5.5L18.6 5.5C19.9255 5.5 21 6.57452 21 7.9L21 16.1C21 17.4255 19.9255 18.5 18.6 18.5L5.4 18.5C4.07452 18.5 3 17.4255 3 16.1L3 7.9C3 6.57452 4.07452 5.5 5.4 5.5Z", false), new("M3.4 7.2l7.4 5.3a2 2 0 0 0 2.4 0l7.4-5.3", false) },
        ["play"] = new P[] { new("M8 5.92A1.92 1.92 0 0 1 9.6 5.07L18.4 10.93A1.92 1.92 0 0 1 18.4 13.07L9.6 18.93A1.92 1.92 0 0 1 8 18.08Z", false) },
        ["plus"] = new P[] { new("M12 4.5v15M4.5 12h15", false) },
        ["power"] = new P[] { new("M12 3.5v8", false), new("M7.8 6.8a7 7 0 1 0 8.4 0", false) },
        ["question"] = new P[] { new("M12 3C16.9706 3 21 7.02944 21 12C21 16.9706 16.9706 21 12 21C7.02944 21 3 16.9706 3 12C3 7.02944 7.02944 3 12 3Z", false), new("M9.6 9.6a2.5 2.5 0 1 1 3.4 2.3c-.6.3-.9.8-.9 1.5v.7", false), new("M12 16.8h.01", false) },
        ["restart"] = new P[] { new("M4 12a8 8 0 1 0 2.4-5.7", false), new("M4 4.5V10h5.5", false) },
        ["stop"] = new P[] { new("M7.92 6L16.08 6C17.1404 6 18 6.85961 18 7.92L18 16.08C18 17.1404 17.1404 18 16.08 18L7.92 18C6.85961 18 6 17.1404 6 16.08L6 7.92C6 6.85961 6.85961 6 7.92 6Z", false) },
        ["success"] = new P[] { new("M12 3C16.9706 3 21 7.02944 21 12C21 16.9706 16.9706 21 12 21C7.02944 21 3 16.9706 3 12C3 7.02944 7.02944 3 12 3Z", false), new("M8 12.2l2.9 2.9L16.2 9", false) },
        ["trash"] = new P[] { new("M3.5 6.5h17", false), new("M9 6.5V3.5h6v3", false), new("M6.15 8.65A2.16 2.16 0 0 1 8.16 6.5L15.84 6.5A2.16 2.16 0 0 1 17.85 8.65L17.15 18.85A2.16 2.16 0 0 1 14.84 21L9.16 21A2.16 2.16 0 0 1 6.85 18.85Z", false), new("M10 11v6M14 11v6", false) },
        ["warning"] = new P[] { new("M10.95 4.89A2.16 2.16 0 0 1 13.05 4.89L20.55 18.31A2.16 2.16 0 0 1 19.44 20.2L4.56 20.2A2.16 2.16 0 0 1 3.45 18.31Z", false), new("M12 9.2v5", false), new("M12 17.2h.01", false) },
        ["window"] = new P[] { new("M5.4 4.5L18.6 4.5C19.9255 4.5 21 5.57452 21 6.9L21 17.1C21 18.4255 19.9255 19.5 18.6 19.5L5.4 19.5C4.07452 19.5 3 18.4255 3 17.1L3 6.9C3 5.57452 4.07452 4.5 5.4 4.5Z", false), new("M3 9h18", false) },
    };

    /// <summary>缺图时的兜底键：键名写错不至于让整行菜单没有图标（原来是 arrow）。</summary>
    const string Fallback = "arrow-right";

    /// <summary>已经报过"键不存在"的键，避免每次重建控件都刷一条日志。</summary>
    static readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    /// <summary>当前程序图标表中的全部键，供 UI 自检逐个验证实际渲染结果。</summary>
    public static IEnumerable<string> Keys => Table.Keys;

    /// <summary>调试渲染自检期间，未知键必须立即失败，不能让 arrow-right 兜底图掩盖拼写错误。</summary>
    public static bool FailOnMissing { get; set; }

    /// <summary>
    /// 笔宽 = max(1.4, 24 × 1.05 / 尺寸)：18px 及以上直接用素材标称值，
    /// 16px 及以下加粗，保证等效物理笔宽 ≥1.05px（§三.2 的公式）。
    /// </summary>
    public static double Thickness(double size) => size <= 0 ? 1.4 : Math.Max(1.4, 25.2 / size);

    /// <summary>生成一个线性图标；名字不存在时返回 null（调用方自己决定怎么兜底）。</summary>
    public static UIElement? Get(string? name, Brush stroke, double size = 16)
    {
        if (name is null) return null;
        if (!Table.TryGetValue(name, out var parts))
        {
            if (FailOnMissing)
                throw new KeyNotFoundException($"图标表里没有键：「{name}」");

            // 键名写错只会画成兜底图，画面上看不出是"写错了"——留一条日志（每个键一次）
            if (_reported.Add(name))
                LogStore.Instance.Warn("图标", $"图标表里没有「{name}」，先画成兜底图，检查是不是键名写错。");
            if (!Table.TryGetValue(Fallback, out parts)) return null;
        }

        var canvas = new Canvas { Width = 24, Height = 24 };   // 24 栅格：缩放比钉死在 size/24
        foreach (var part in parts)
        {
            var path = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(part.D),
                Stroke = part.Fill ? null : stroke,
                Fill = part.Fill ? stroke : null,
                StrokeThickness = Thickness(size),
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
            };
            canvas.Children.Add(path);
        }

        return new Viewbox
        {
            Width = size,
            Height = size,
            Child = canvas,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }
}
