using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OAIRouter;

/// <summary>
/// 强调色由程序图标派生（规范 §三）。代码里不出现品牌色字面值，
/// 换图标即换整套配色。
/// </summary>
public static class Accent
{
    public static Color Main { get; private set; }
    public static Color Dark { get; private set; }
    public static Color Soft { get; private set; }
    public static Color Hover { get; private set; }

    public static SolidColorBrush MainBrush { get; private set; } = Brushes.Transparent;
    public static SolidColorBrush DarkBrush { get; private set; } = Brushes.Transparent;
    public static SolidColorBrush SoftBrush { get; private set; } = Brushes.Transparent;
    public static SolidColorBrush HoverBrush { get; private set; } = Brushes.Transparent;

    // 语义色固定值，不参与派生（规范 §七）
    public static readonly Color Error = Color.FromRgb(0xC4, 0x2B, 0x1C);
    public static readonly Color Warn = Color.FromRgb(0x9D, 0x5D, 0x00);

    public static void Init()
    {
        // 淡蓝主题。§三 的常量按本主题调整过，属**有意的、已记录的偏离**，
        // 不是适配残留：
        //   Main/Dark 明度 .42/.33（规范 .245/.175）—— 规范值会派生成深藏青，与淡蓝主题冲突
        //   Soft/Hover 饱和度 .45/.40（规范 .32/.28）—— 淡底需要更明显的一点色相
        // 换图标只改种子色，这四个常量保持不变。
        var (h, s) = ReadIconSeed();
        Main = FromHsl(h, Math.Min(s * 1.05, 0.95), 0.42);
        Dark = FromHsl(h, Math.Min(s * 1.10, 0.95), 0.33);
        Soft = FromHsl(h, 0.45, 0.93);
        Hover = FromHsl(h, 0.40, 0.965);

        LogStore.Instance.Info("配色", $"由图标派生强调色 #{Main.R:X2}{Main.G:X2}{Main.B:X2}");

        MainBrush = Freeze(Main);
        DarkBrush = Freeze(Dark);
        SoftBrush = Freeze(Soft);
        HoverBrush = Freeze(Hover);
    }

    private static SolidColorBrush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>渲染图标并统计主色（H/S 分桶取众数，L 不使用）。</summary>
    private static (double h, double s) ReadIconSeed()
    {
        try
        {
            var res = System.Windows.Application.GetResourceStream(new Uri(BrandIcon.Uri));
            if (res?.Stream is null) return Fallback();

            // 取最大帧采样：小帧的抗锯齿边缘会污染色相统计
            var frameSize = IconUtil.LargestSize(res.Stream);
            res.Stream.Position = 0;
            using var ico = new System.Drawing.Icon(res.Stream, frameSize, frameSize);
            using var bmp = ico.ToBitmap();

            var buckets = new Dictionary<(int, int, int), (int count, double hx, double sx)>();
            for (int y = 0; y < bmp.Height; y++)
            {
                for (int x = 0; x < bmp.Width; x++)
                {
                    var p = bmp.GetPixel(x, y);
                    if (p.A < 200) continue;
                    var (h, s, l) = ToHsl(p.R, p.G, p.B);
                    if (l > 0.92 || l < 0.12 || s < 0.18) continue;

                    var key = ((int)(h / 10) % 36, (int)(s / 0.1), (int)(l / 0.1));
                    buckets.TryGetValue(key, out var cur);
                    buckets[key] = (cur.count + 1, cur.hx + h, cur.sx + s);
                }
            }

            if (buckets.Count == 0) return Fallback();

            var best = buckets.OrderByDescending(kv => kv.Value.count).First().Value;
            return (best.hx / best.count, best.sx / best.count);
        }
        catch
        {
            return Fallback();
        }
    }

    private static (double h, double s) Fallback()
    {
        var (h, s, _) = ToHsl(0x1B, 0x8F, 0x72);
        return (h, s);
    }

    public static (double h, double s, double l) ToHsl(byte r, byte g, byte b)
    {
        double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
        double max = Math.Max(rd, Math.Max(gd, bd)), min = Math.Min(rd, Math.Min(gd, bd));
        double l = (max + min) / 2, h = 0, s = 0;

        if (Math.Abs(max - min) > 1e-6)
        {
            double d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            if (max == rd) h = (gd - bd) / d + (gd < bd ? 6 : 0);
            else if (max == gd) h = (bd - rd) / d + 2;
            else h = (rd - gd) / d + 4;
            h *= 60;
        }
        return (h, s, l);
    }

    public static Color FromHsl(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360;
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = l - c / 2;
        double r, g, b;
        if (h < 60) (r, g, b) = (c, x, 0);
        else if (h < 120) (r, g, b) = (x, c, 0);
        else if (h < 180) (r, g, b) = (0, c, x);
        else if (h < 240) (r, g, b) = (0, x, c);
        else if (h < 300) (r, g, b) = (x, 0, c);
        else (r, g, b) = (c, 0, x);

        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}
