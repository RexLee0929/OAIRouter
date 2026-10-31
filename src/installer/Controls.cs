using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace OAIRouterSetup
{
    /// <summary>
    /// 自绘进度条。系统 ProgressBar 的颜色来自系统主题（绿），不跟派生色走，
    /// 换非绿色图标时会出现「彩色带子 + 绿进度条」。这里用 Theme.Accent 自己画，见规范 §四。
    /// </summary>
    class ProgressStrip : Panel
    {
        readonly float _k;
        int _value;

        public ProgressStrip(float k, int x, int y, int w, int h)
        {
            _k = k < 0.8f ? 1f : k;
            Location = new Point((int)Math.Round(x * _k), (int)Math.Round(y * _k));
            Size = new Size((int)Math.Round(w * _k), (int)Math.Round(h * _k));
            BackColor = Color.White;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        /// <summary>0..100，越界自动夹紧。</summary>
        public int Value
        {
            get { return _value; }
            set
            {
                int v = value < 0 ? 0 : (value > 100 ? 100 : value);
                if (v == _value) return;
                _value = v;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            int radius = Height / 2;
            var track = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));

            using (var path = Round(track, radius))
            using (var br = new SolidBrush(Theme.ProgressTrack))
                g.FillPath(br, path);

            int fillW = (int)Math.Round((Width - 1) * _value / 100.0);
            if (fillW <= 0) return;

            // 至少留一个圆头，否则进度很小时看着像个方块
            var fill = new Rectangle(0, 0, Math.Max(fillW, Height), Height - 1);
            using (var path = Round(fill, radius))
            using (var br = new SolidBrush(Theme.Accent))
                g.FillPath(br, path);
        }

        static GraphicsPath Round(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    /// <summary>
    /// 经典「浏览文件夹」对话框（与 txtFormat 的 Inno 安装器同一套外观）：
    /// 只有盘符与真实文件夹，不列「图库 / iCloud 云盘」这类虚拟项。
    /// </summary>
    static class FolderPicker
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct BROWSEINFO
        {
            public IntPtr hwndOwner;
            public IntPtr pidlRoot;
            public IntPtr pszDisplayName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszTitle;
            public uint ulFlags;
            public IntPtr lpfn;
            public IntPtr lParam;
            public int iImage;
        }

        const uint BIF_RETURNONLYFSDIRS = 0x0001;
        const uint BIF_EDITBOX = 0x0010;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SHBrowseForFolder(ref BROWSEINFO lpbi);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern bool SHGetPathFromIDList(IntPtr pidl, StringBuilder pszPath);

        [DllImport("shell32.dll")]
        static extern int SHGetSpecialFolderLocation(IntPtr hwnd, int csidl, out IntPtr pidl);

        [DllImport("ole32.dll")]
        static extern void CoTaskMemFree(IntPtr p);

        const int CSIDL_DRIVES = 0x0011;   // 「此电脑」，让树从盘符开始

        /// <summary>返回选中的目录；取消返回 null。</summary>
        public static string Pick(IWin32Window owner, string title)
        {
            var display = Marshal.AllocHGlobal(260 * sizeof(char));
            IntPtr root = IntPtr.Zero;
            try
            {
                SHGetSpecialFolderLocation(IntPtr.Zero, CSIDL_DRIVES, out root);

                var bi = new BROWSEINFO
                {
                    hwndOwner = owner == null ? IntPtr.Zero : owner.Handle,
                    pidlRoot = root,
                    pszDisplayName = display,
                    lpszTitle = title,
                    ulFlags = BIF_RETURNONLYFSDIRS | BIF_EDITBOX
                };

                var pidl = SHBrowseForFolder(ref bi);
                if (pidl == IntPtr.Zero) return null;

                try
                {
                    var sb = new StringBuilder(260);
                    return SHGetPathFromIDList(pidl, sb) ? sb.ToString() : null;
                }
                finally { CoTaskMemFree(pidl); }
            }
            catch { return null; }
            finally
            {
                if (root != IntPtr.Zero) CoTaskMemFree(root);
                Marshal.FreeHGlobal(display);
            }
        }
    }

    /// <summary>
    /// 安装模式选项卡片：整行可点，选中后描绿边 + 浅绿底。
    /// 对应 txtFormat 里那个「仅为我安装 / 为所有用户安装」对话框，但直接放进向导页。
    /// </summary>
    class ModeCard : Panel
    {
        public bool Selected;
        public event EventHandler Picked;

        readonly string _title, _sub;
        readonly bool _shield;
        readonly float _k;

        public ModeCard(string title, string sub, bool shield, float k, int x, int y, int w, int h)
        {
            _title = title;
            _sub = sub;
            _shield = shield;
            _k = k < 0.8f ? 1f : k;

            Location = new Point((int)Math.Round(x * _k), (int)Math.Round(y * _k));
            Size = new Size((int)Math.Round(w * _k), (int)Math.Round(h * _k));
            BackColor = Color.White;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        int S(int v) { return (int)Math.Round(v * _k); }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (Picked != null) Picked(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent == null ? Color.White : Parent.BackColor);

            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Round(rect, S(6)))
            {
                using (var br = new SolidBrush(Selected ? Theme.SelectBg : Theme.CardIdle))
                    g.FillPath(br, path);

                using (var pen = new Pen(Selected ? Theme.Accent : Theme.CardBorder, Selected ? S(2) : S(1)))
                    g.DrawPath(pen, path);
            }

            int iconSize = S(26);
            int iconX = S(14);
            int iconY = (Height - iconSize) / 2;

            if (_shield) DrawShield(g, new Rectangle(iconX, iconY, iconSize, iconSize));
            else DrawPerson(g, new Rectangle(iconX, iconY, iconSize, iconSize));

            int textX = S(52);
            using (var f = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold))
            using (var br = new SolidBrush(Selected ? Theme.Accent : Theme.Ink))
                g.DrawString(_title, f, br, textX, S(9));

            using (var f = new Font("Microsoft YaHei UI", 8.5f))
            using (var br = new SolidBrush(Theme.Dim))   // 中性色只认调色板：#828282，不是随手一个 #6E6E6E
                g.DrawString(_sub, f, br, textX, S(32));
        }

        static GraphicsPath Round(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>绿色方块 + 白色人形：仅当前用户。</summary>
        void DrawPerson(Graphics g, Rectangle r)
        {
            using (var path = Round(r, S(6)))
            using (var br = new SolidBrush(Theme.Accent))
                g.FillPath(br, path);

            using (var white = new SolidBrush(Color.White))
            {
                float cx = r.X + r.Width / 2f;
                float head = r.Height * 0.26f;
                g.FillEllipse(white, cx - head / 2, r.Y + r.Height * 0.20f, head, head);

                var body = new RectangleF(cx - r.Width * 0.26f, r.Y + r.Height * 0.54f,
                                          r.Width * 0.52f, r.Height * 0.46f);
                g.FillPie(white, body.X, body.Y, body.Width, body.Height * 2, 180, 180);
            }
        }

        /// <summary>黄蓝盾牌：所有用户（UAC 语义）。</summary>
        void DrawShield(Graphics g, Rectangle r)
        {
            float w = r.Width, h = r.Height;
            float x = r.X, y = r.Y;
            var shield = new GraphicsPath();
            shield.AddLine(x + 0.10f * w, y + 0.14f * h, x + 0.50f * w, y + 0.04f * h);
            shield.AddLine(x + 0.50f * w, y + 0.04f * h, x + 0.90f * w, y + 0.14f * h);
            shield.AddLine(x + 0.90f * w, y + 0.14f * h, x + 0.90f * w, y + 0.52f * h);
            shield.AddLine(x + 0.90f * w, y + 0.52f * h, x + 0.50f * w, y + 0.96f * h);
            shield.AddLine(x + 0.50f * w, y + 0.96f * h, x + 0.10f * w, y + 0.52f * h);
            shield.CloseFigure();

            var left = new RectangleF(x, y, w * 0.52f, h);
            var right = new RectangleF(x + w * 0.48f, y, w * 0.52f, h);

            var old = g.Clip;
            g.SetClip(shield, CombineMode.Intersect);
            using (var b1 = new SolidBrush(Color.FromArgb(0xE8, 0xB3, 0x1F)))
                g.FillRectangle(b1, left);
            using (var b2 = new SolidBrush(Color.FromArgb(0x2D, 0x7D, 0xD2)))
                g.FillRectangle(b2, right);
            g.Clip = old;

            using (var pen = new Pen(Theme.Dim, Math.Max(1f, S(1))))    // 中性色表里的 Dim #828282，不私调灰
                g.DrawPath(pen, shield);
            shield.Dispose();
        }
    }
}
