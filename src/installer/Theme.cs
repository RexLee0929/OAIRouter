using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace OAIRouterSetup
{
    /// <summary>
    /// 配色：唯一来源是程序图标（app.ico）。
    /// 规则见《安装器设计规范.md》§二 —— 换图标整套 UI 自动换色，代码里不写死品牌色。
    /// </summary>
    static class Theme
    {
        // ---- 中性色：与图标无关，固定
        public static readonly Color Ink = Color.FromArgb(0x1B, 0x1B, 0x1B);
        public static readonly Color Muted = Color.FromArgb(0x5C, 0x5C, 0x5C);
        public static readonly Color Dim = Color.FromArgb(0x82, 0x82, 0x82);
        public static readonly Color Line = Color.FromArgb(0xE2, 0xE2, 0xE2);
        public static readonly Color CardIdle = Color.FromArgb(0xFA, 0xFA, 0xFA);
        public static readonly Color CardBorder = Color.FromArgb(0xDE, 0xDE, 0xDE);
        /// <summary>自绘进度条的轨道底色。中性色，不跟图标派生 —— 只是个浅灰槽。</summary>
        public static readonly Color ProgressTrack = Color.FromArgb(0xEC, 0xEC, 0xEC);

        /// <summary>警示文字（降级、覆盖等需要用户注意的提示）。固定色，不参与派生。</summary>
        public static readonly Color Warn = Color.FromArgb(0x8A, 0x53, 0x00);

        /// <summary>兜底种子：只在图标取不出主色时使用（绿色，即原来那套）。</summary>
        public static readonly Color FallbackSeed = Color.FromArgb(0x1B, 0x8F, 0x72);

        // ---- 派生结果
        public static Color BandTop { get; private set; }
        public static Color BandBottom { get; private set; }
        public static Color Accent { get; private set; }
        public static Color SelectBg { get; private set; }
        public static Color HoverBg { get; private set; }

        static Theme()
        {
            // 用兜底种子跑一遍完整派生，和 Build() 里图标取色失败的路径完全一致，
            // 避免"静态初值"与"兜底路径"算出两套略有差异的绿。
            Apply(FallbackSeed);
        }

        /// <summary>从图标算一整套颜色，开机调一次。</summary>
        public static void Build(Image icon)
        {
            Color seed;
            if (!TrySeed(icon, out seed)) seed = FallbackSeed;
            Apply(seed);
        }

        /// <summary>由种子色派生五个用途色。静态初值与运行时兜底都走这里。</summary>
        static void Apply(Color seed)
        {
            double h, s;
            ToHsl(seed, out h, out s, out _);
            s = Math.Max(s, 0.35);                     // 灰调图标也要有颜色

            BandTop = FromHsl(h, s, 0.33);
            BandBottom = FromHsl(h, Math.Min(s * 1.12, 0.95), 0.15);
            Accent = FromHsl(h, Math.Min(s * 1.05, 0.95), 0.245);
            SelectBg = FromHsl(h, 0.32, 0.93);
            HoverBg = FromHsl(h, 0.28, 0.96);
        }

        // ---------------------------------------------------------- 取主色

        class Bucket
        {
            public int N;
            public double SumH, SumS;
        }

        static bool TrySeed(Image src, out Color seed)
        {
            seed = Color.Empty;
            if (src == null) return false;

            using (var bmp = new Bitmap(64, 64, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.Clear(Color.Transparent);
                    g.DrawImage(src, new Rectangle(0, 0, 64, 64));
                }

                var hist = new Dictionary<int, Bucket>();
                int valid = 0;

                for (int y = 0; y < 64; y++)
                {
                    for (int x = 0; x < 64; x++)
                    {
                        var p = bmp.GetPixel(x, y);
                        if (p.A < 200) continue;                       // 半透明毛边
                        double h, s, l;
                        ToHsl(p, out h, out s, out l);
                        if (l > 0.92 || l < 0.12) continue;            // 近白、近黑
                        if (s < 0.18) continue;                        // 灰阶

                        int key = ((int)(h * 36) << 16) | ((int)(s * 10) << 8) | (int)(l * 10);
                        Bucket b;
                        if (!hist.TryGetValue(key, out b)) hist[key] = b = new Bucket();
                        b.N++;
                        b.SumH += h;
                        b.SumS += s;
                        valid++;
                    }
                }

                if (hist.Count == 0) return false;

                int bestKey = 0, best = 0;
                foreach (var kv in hist)
                    if (kv.Value.N > best) { best = kv.Value.N; bestKey = kv.Key; }

                if (best * 1.0 / valid < 0.08) return false;           // 颜色太分散，当作没有主色

                // 相邻桶一起加权，用桶内真实 H/S 求和，不要用桶中心（会让饱和度系统性偏 ±0.05）
                int bh = bestKey >> 16, bs = (bestKey >> 8) & 0xFF;
                double wsum = 0, hsum = 0, ssum = 0;
                foreach (var kv in hist)
                {
                    int hh = kv.Key >> 16, ss = (kv.Key >> 8) & 0xFF;
                    if (Math.Abs(hh - bh) > 1 || Math.Abs(ss - bs) > 1) continue;
                    wsum += kv.Value.N;
                    hsum += kv.Value.SumH;
                    ssum += kv.Value.SumS;
                }

                seed = FromHsl(hsum / wsum, ssum / wsum, 0.5);
                return true;
            }
        }

        /// <summary>不透明区域的平均亮度，用来判断图标会不会糊在色带上。</summary>
        public static double AverageLightness(Image src)
        {
            if (src == null) return 0;

            using (var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.Clear(Color.Transparent);
                    g.DrawImage(src, new Rectangle(0, 0, 32, 32));
                }

                double sum = 0;
                int n = 0;
                for (int y = 0; y < 32; y++)
                {
                    for (int x = 0; x < 32; x++)
                    {
                        var p = bmp.GetPixel(x, y);
                        if (p.A < 200) continue;
                        sum += Luminance(p);
                        n++;
                    }
                }
                return n == 0 ? 0 : sum / n;
            }
        }

        // ---------------------------------------------------------- HSL

        /// <param name="h">0..1</param>
        public static Color FromHsl(double h, double s, double l)
        {
            h = h - Math.Floor(h);
            s = Clamp01(s);
            l = Clamp01(l);

            double c = (1 - Math.Abs(2 * l - 1)) * s;
            double x = c * (1 - Math.Abs(h * 6 % 2 - 1));
            double m = l - c / 2, r = 0, g = 0, b = 0;

            switch ((int)(h * 6) % 6)
            {
                case 0: r = c; g = x; break;
                case 1: r = x; g = c; break;
                case 2: g = c; b = x; break;
                case 3: g = x; b = c; break;
                case 4: r = x; b = c; break;
                default: r = c; b = x; break;
            }

            return Color.FromArgb(255,
                (int)Math.Round((r + m) * 255),
                (int)Math.Round((g + m) * 255),
                (int)Math.Round((b + m) * 255));
        }

        public static void ToHsl(Color c, out double h, out double s, out double l)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            l = (max + min) / 2;

            if (max == min) { h = 0; s = 0; return; }

            double d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);

            if (max == r) h = ((g - b) / d + (g < b ? 6 : 0)) / 6;
            else if (max == g) h = ((b - r) / d + 2) / 6;
            else h = ((r - g) / d + 4) / 6;
        }

        static double Clamp01(double v) { return v < 0 ? 0 : (v > 1 ? 1 : v); }

        /// <summary>相对亮度（WCAG）。</summary>
        public static double Luminance(Color c)
        {
            Func<double, double> f = v => v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            return 0.2126 * f(c.R / 255.0) + 0.7152 * f(c.G / 255.0) + 0.0722 * f(c.B / 255.0);
        }

        public static double Contrast(Color a, Color b)
        {
            double la = Luminance(a), lb = Luminance(b);
            double hi = Math.Max(la, lb), lo = Math.Min(la, lb);
            return (hi + 0.05) / (lo + 0.05);
        }
    }
}
