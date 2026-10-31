using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace OAIRouterSetup
{
    /// <summary>
    /// 向导外壳：左侧色带 + 右侧白底内容区 + 底部按钮栏。
    /// 布局沿用 Inno Setup 的 modern 样式（与 txtFormat 的安装器一致），
    /// 坐标按 96dpi 书写，运行时按 DeviceDpi 等比放大。
    /// </summary>
    public class WizardShell : Form
    {
        // 中性色固定；品牌色（色带两端、强调色）全部由程序图标派生，见 Theme.cs / 安装器设计规范 §二
        public static Color Ink { get { return Theme.Ink; } }
        public static Color Muted { get { return Theme.Muted; } }
        public static Color Dim { get { return Theme.Dim; } }
        public static Color Accent { get { return Theme.Accent; } }
        public static Color LineGray { get { return Theme.Line; } }
        public static Color BannerTop { get { return Theme.BandTop; } }
        public static Color BannerBottom { get { return Theme.BandBottom; } }

        protected float K = 1f;
        protected Panel Body;
        protected Label TitleLabel;
        protected Button Back, NextBtn, CancelBtn;

        readonly Font _uiFont = new Font("Microsoft YaHei UI", 9f);
        readonly int _w, _h;
        readonly string _subtitle;
        Image _appIcon;
        bool _plate;
        bool _built;

        protected WizardShell(string windowTitle, int width, int height, string subtitle)
        {
            _w = width;
            _h = height;
            _subtitle = subtitle;
            Text = windowTitle;
            AutoScaleMode = AutoScaleMode.None;      // 自己按 DPI 缩放，交给 WinForms 会算错
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowIcon = true;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;
            Font = _uiFont;
            ClientSize = new Size(width, height);

            // 图标是整套配色的唯一种子：先取它，再算 Theme，再画界面。
            // ExtractAssociatedIcon 固定只返回 32×32（ico 里有多少帧都一样），
            // 之前再 new Icon(ico, 256, 256) 等于 32→256→64 两次重采样，
            // 侧栏 logo 被拉糊了。PrivateExtractIcons 会按请求尺寸挑帧，
            // 要 256 就拿真 256 帧。取不到再退回老 API 兜底。
            try
            {
                var big = FrameIcon(256);
                if (big != null)
                {
                    _appIcon = big.ToBitmap();
                    big.Dispose();
                    Icon = FrameIcon(32) ?? Icon.ExtractAssociatedIcon(Application.ExecutablePath);

                    Theme.Build(_appIcon);

                    // 图标和色带同色系会糊在一起，这时垫一块底板（规范 §二.3）
                    _plate = Math.Abs(Theme.AverageLightness(_appIcon) - 0.33) < 0.12;
                }
            }
            catch { }
        }

        // ------------------------------------------------------------------
        // 品牌图标：PrivateExtractIconsW 按尺寸取 exe 内嵌资源里的帧。
        // ExtractAssociatedIcon 永远只给 32×32，多帧 ico 也一样。
        // ------------------------------------------------------------------

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern uint PrivateExtractIconsW(string file, int index, int cx, int cy,
            IntPtr[] icons, int[] ids, int count, int flags);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr handle);

        /// <summary>按尺寸从自己的 exe 里取一帧图标（自有句柄，用完可 Dispose）；失败返回 null。</summary>
        static Icon FrameIcon(int size)
        {
            try
            {
                var h = new IntPtr[1];
                if (PrivateExtractIconsW(Application.ExecutablePath, 0, size, size, h, null, 1, 0) > 0
                    && h[0] != IntPtr.Zero)
                {
                    var owned = (Icon)Icon.FromHandle(h[0]).Clone();   // 克隆成自有句柄，原句柄销毁
                    DestroyIcon(h[0]);
                    return owned;
                }
            }
            catch { }
            return null;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            if (_built) return;
            _built = true;

            // Form.DeviceDpi 在系统 DPI 感知进程里可能仍报 96，这里直接问绘图上下文
            try
            {
                using (var g = CreateGraphics()) K = g.DpiX / 96f;
            }
            catch { K = 1f; }
            if (K < 0.8f || K > 4f) K = 1f;

            ClientSize = Z(_w, _h);
            BuildShell(_w, _h, _subtitle);
            BuildPages();
            ShowPage(0);
        }

        /// <summary>子类在这里注册页面。</summary>
        protected virtual void BuildPages() { }

        /// <summary>子类在这里渲染指定页面。</summary>
        protected virtual void ShowPage(int index) { }

        // ------------------------------------------------------------ 缩放辅助
        protected int S(int v) { return (int)Math.Round(v * K); }
        protected Point P(int x, int y) { return new Point(S(x), S(y)); }
        protected Size Z(int w, int h) { return new Size(S(w), S(h)); }

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

        void BuildShell(int w, int h, string subtitle)
        {
            // ---- 左侧色带
            var banner = new Panel { Location = P(0, 0), Size = Z(164, h), BackColor = BannerTop };
            banner.Paint += delegate(object s, PaintEventArgs e)
            {
                var r = banner.ClientRectangle;
                if (r.Width <= 1 || r.Height <= 1) return;
                using (var br = new LinearGradientBrush(r, BannerTop, BannerBottom, 90f))
                    e.Graphics.FillRectangle(br, r);

                // 图标与色带同色系时垫一块底板，否则图标边界会消失在色带里
                if (!_plate) return;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                var box = new Rectangle(S(46), S(30), S(72), S(72));
                using (var path = Round(box, S(14)))
                {
                    using (var fill = new SolidBrush(Color.FromArgb(36, 255, 255, 255)))
                        e.Graphics.FillPath(fill, path);
                    using (var pen = new Pen(Color.FromArgb(56, 255, 255, 255), Math.Max(1f, S(1))))
                        e.Graphics.DrawPath(pen, path);
                }
            };
            Controls.Add(banner);

            var logo = new PictureBox
            {
                Location = P(50, 34),
                Size = Z(64, 64),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Transparent,
                Image = _appIcon
            };
            banner.Controls.Add(logo);

            banner.Controls.Add(new Label
            {
                Text = Program.AppName,
                Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Location = P(10, 108),
                Size = Z(144, 24),
                TextAlign = ContentAlignment.MiddleCenter
            });

            banner.Controls.Add(new Label
            {
                Text = "v" + Program.Version,
                Font = new Font("Microsoft YaHei UI", 8.5f),
                ForeColor = Color.FromArgb(200, 255, 255, 255),
                BackColor = Color.Transparent,
                Location = P(10, 132),
                Size = Z(144, 18),
                TextAlign = ContentAlignment.MiddleCenter
            });

            banner.Controls.Add(new Panel
            {
                Location = P(32, 160),
                Size = Z(100, 1),
                BackColor = Color.FromArgb(70, 255, 255, 255)
            });

            banner.Controls.Add(new Label
            {
                Text = subtitle,
                Font = new Font("Microsoft YaHei UI", 8.5f),
                ForeColor = Color.FromArgb(215, 255, 255, 255),
                BackColor = Color.Transparent,
                Location = P(10, 172),
                Size = Z(144, 40),
                TextAlign = ContentAlignment.TopCenter
            });

            banner.Controls.Add(new Label
            {
                Text = Program.Author + "\n" + Program.Email,
                Font = new Font("Microsoft YaHei UI", 7.5f),
                ForeColor = Color.FromArgb(170, 255, 255, 255),
                BackColor = Color.Transparent,
                Location = P(10, h - 56),
                Size = Z(144, 40),
                TextAlign = ContentAlignment.BottomCenter
            });

            // ---- 右侧内容区
            var content = new Panel
            {
                Location = P(164, 0),
                Size = Z(w - 164, h),
                BackColor = Color.White
            };
            Controls.Add(content);

            TitleLabel = new Label
            {
                Location = P(24, 20),
                Size = Z(w - 164 - 48 - 44, 26),
                Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold),
                ForeColor = Ink,
                BackColor = Color.White,
                AutoEllipsis = true
            };
            content.Controls.Add(TitleLabel);

            var corner = new PictureBox
            {
                Location = P(w - 164 - 32 - 20, 18),
                Size = Z(32, 32),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.White,
                Image = _appIcon
            };
            content.Controls.Add(corner);

            Body = new Panel
            {
                Location = P(24, 58),
                Size = Z(w - 164 - 48, h - 58 - 78),
                BackColor = Color.White
            };
            content.Controls.Add(Body);

            content.Controls.Add(new Panel
            {
                Location = P(24, h - 74),
                Size = Z(w - 164 - 48, 1),
                BackColor = LineGray
            });

            // ---- 底部按钮（先建，再统一交给 LayoutButtons 定位）
            CancelBtn = MakeButton("取消", 0, 0, 86, 28, false);
            NextBtn = MakeButton("下一步", 0, 0, 86, 28, true);
            Back = MakeButton("上一步", 0, 0, 86, 28, false);
            content.Controls.Add(Back);
            content.Controls.Add(NextBtn);
            content.Controls.Add(CancelBtn);

            LayoutButtons(86);

            AcceptButton = NextBtn;
            CancelButton = CancelBtn;
        }

        /// <summary>
        /// 重排底部三键。默认 86px；某些页（如「正在运行」）按钮文字更长，需要更宽的按钮，
        /// 这里统一从右边缘往左排，避免文字被折成两行。
        /// </summary>
        protected void LayoutButtons(int nextWidth)
        {
            const int bw = 86, bh = 28, gap = 8;
            int by = _h - 56;
            int rightEdge = _w - 164 - 24;

            CancelBtn.Size = Z(bw, bh);
            CancelBtn.Location = P(rightEdge - bw, by);

            NextBtn.Size = Z(nextWidth, bh);
            NextBtn.Location = P(rightEdge - bw - gap - nextWidth, by);

            Back.Size = Z(bw, bh);
            Back.Location = P(rightEdge - bw - gap - nextWidth - gap - bw, by);
        }

        /// <summary>
        /// 自绘 flat 按钮：白底 + 1px 描边 + hover/pressed 自控，和 MsgDialog 的按钮同一套
        /// （主按钮 Accent 描边，次按钮 Line 描边）。之前用 FlatStyle.System，
        /// 系统主题渲染的白底直角和弹窗按钮不是一种质感。
        /// </summary>
        Button MakeButton(string text, int x, int y, int w, int h, bool primary)
        {
            var b = new Button
            {
                Text = text,
                Location = P(x, y),
                Size = Z(w, h),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = primary ? Theme.Ink : Theme.Muted,
                Font = _uiFont,
                UseVisualStyleBackColor = false
            };
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.MouseOverBackColor = Theme.HoverBg;
            b.FlatAppearance.MouseDownBackColor = Theme.SelectBg;
            // 自绘按钮不会自动画禁用态：置灰逻辑自己接住，否则「下一步」被禁用时
            // 还是绿描边黑字，看着照样能点（系统样式原本会自动变灰）。
            b.EnabledChanged += delegate { ApplyButtonState(b, primary); };
            ApplyButtonState(b, primary);
            return b;
        }

        static void ApplyButtonState(Button b, bool primary)
        {
            bool on = b.Enabled;
            b.ForeColor = on ? (primary ? Theme.Ink : Theme.Muted) : Theme.Dim;
            b.FlatAppearance.BorderColor = on && primary ? Theme.Accent : Theme.Line;
        }

        // ------------------------------------------------------------ 页面构件
        protected Label L(string text, int x, int y, int w, int h)
        {
            var lbl = new Label
            {
                Text = text,
                Location = P(x, y),
                Size = Z(w, h),
                ForeColor = Muted,
                BackColor = Color.White
            };
            Body.Controls.Add(lbl);
            return lbl;
        }

        protected Label Strong(string text, int x, int y, int w, int h)
        {
            var lbl = new Label
            {
                Text = text,
                Location = P(x, y),
                Size = Z(w, h),
                ForeColor = Ink,
                BackColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold)
            };
            Body.Controls.Add(lbl);
            return lbl;
        }

        protected CheckBox Check(string text, int x, int y, int w, bool on)
        {
            var c = new CheckBox
            {
                Text = text,
                Checked = on,
                Location = P(x, y),
                Size = Z(w, 24),
                ForeColor = Ink,
                BackColor = Color.White
            };
            Body.Controls.Add(c);
            return c;
        }

        // ------------------------------------------------------------ 两列与逐行累加
        /// <summary>
        /// 「标签 + 值」两列一行：标签右对齐成一列，值左对齐并自动折行。
        /// 返回这一行实际占用的高度（设计像素），调用方累加 y 即可。
        /// 标签用中性灰（#5C5C5C）而不是浅灰 —— 一整屏浅灰标签基本读不出来。
        /// </summary>
        protected int Row(string label, string value, int y)
        {
            return Row(label, value, y, Theme.Ink);
        }

        protected int Row(string label, string value, int y, Color valueColor)
        {
            const int LabelW = 88, Gap = 8;
            const int ValueW = 428 - LabelW - Gap;

            var lb = new Label
            {
                Text = label,
                Location = P(0, y),
                Size = Z(LabelW, 20),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Theme.Muted,
                BackColor = Color.White
            };
            Body.Controls.Add(lb);

            int h = MeasureH(value, ValueW);
            var vb = new Label
            {
                Text = value,
                Location = P(LabelW + Gap, y),
                Size = Z(ValueW, h),
                ForeColor = valueColor,
                BackColor = Color.White
            };
            Body.Controls.Add(vb);
            return h;
        }

        /// <summary>
        /// 按设计宽度实测折行后的高度（设计像素）。Label 的 AutoSize 默认 false，
        /// 高度算不准就会把长路径的第二行裁掉。
        /// </summary>
        protected int MeasureH(string text, int designWidth)
        {
            return MeasureH(text, designWidth, Body.Font);
        }

        protected int MeasureH(string text, int designWidth, Font f)
        {
            if (string.IsNullOrEmpty(text)) return 20;
            int devH = TextRenderer.MeasureText(text, f,
                new Size(S(designWidth), 0), TextFormatFlags.WordBreak).Height;
            int h = (int)Math.Ceiling(devH / K);
            return h < 20 ? 20 : h + 2;      // +2：中文字体实测会吃掉半行行距
        }

        /// <summary>
        /// 完成页/说明区逐行累加：返回下一行的 y。**空文本不占位** —— 用固定 y 排版时，
        /// 没有的那一行会留下一个空洞，看着像漏了内容（规范 §五-3）。
        /// </summary>
        protected int Line(string text, int y, int w)
        {
            if (string.IsNullOrEmpty(text)) return y;
            int h = MeasureH(text, w);
            L(text, 0, y, w, h);
            return y + h + 4;
        }

        /// <summary>同上，但按路径处理：固定单行 + 省略号截断 + 悬停看全（规范 §五-3）。</summary>
        protected int LinePath(string text, int y)
        {
            if (string.IsNullOrEmpty(text)) return y;
            var lbl = L(text, 0, y, 428, 20);
            lbl.AutoEllipsis = true;
            var tip = new ToolTip();
            tip.SetToolTip(lbl, text);
            return y + 24;
        }

        /// <summary>
        /// 完成页的结论行：9.5pt 加粗，让「装好了」一眼看到（规范 §三 完成页）。
        /// 返回下一行的 y。
        /// </summary>
        protected int Head(string text, int y, int w)
        {
            if (string.IsNullOrEmpty(text)) return y;
            var f = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            int h = MeasureH(text, w, f);
            var lbl = new Label
            {
                Text = text,
                Location = P(0, y),
                Size = Z(w, h),
                ForeColor = Ink,
                BackColor = Color.White,
                Font = f,
                AutoSize = false
            };
            Body.Controls.Add(lbl);
            return y + h + 6;
        }


        protected Button SmallButton(string text, int x, int y, int w, int h)
        {
            var b = MakeButton(text, x, y, w, h, false);   // 页内小按钮（浏览…）都是次按钮样式
            Body.Controls.Add(b);
            return b;
        }

        protected TextBox Box(string text, int x, int y, int w, int h)
        {
            var t = new TextBox
            {
                Text = text,
                Location = P(x, y),
                Size = Z(w, h),
                BorderStyle = BorderStyle.FixedSingle
            };
            Body.Controls.Add(t);
            return t;
        }
    }
}
