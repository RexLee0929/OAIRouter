using System;
using System.Drawing;
using System.Windows.Forms;

namespace OAIRouterSetup
{
    /// <summary>
    /// 自绘对话框：安装 / 卸载向导里所有「只能弹」的提示都走这里，不许用系统消息框。
    ///
    /// 规格见《安装器设计规范》§二.1：
    ///   - 无边框白底小窗（这里自绘一圈 1px <see cref="Theme.Line"/> 细边）+ 投影，不套系统边框和按钮；
    ///   - 结构：标题（10.5pt 粗体 <see cref="Theme.Ink"/>）→ 正文（9pt <see cref="Theme.Muted"/>）
    ///     → 底部右对齐按钮栏；
    ///   - 主按钮 = <see cref="Theme.Accent"/> 描边 + 白底，和向导主按钮同一套取色，换图标就跟着变；
    ///   - **不要图标**（黄三角 / 红叉是观感割裂的主要来源）；
    ///   - <see cref="Form.ShowDialog(IWin32Window)"/> 且 owner 传向导主窗体，保证居中在向导上；
    ///   - **一律不发声**：不带图标的消息框重载，不调任何系统提示音 API。
    ///
    /// 能做成向导一页的（占用、版本升降级、配置覆盖）不要走这里；
    /// 能做成页内一行的（路径非法、权限不足）也不要 —— 见 InstallForm.BuildDir 的页内警示行。
    /// </summary>
    class MsgDialog : Form
    {
        readonly float _k;
        readonly Label _body;
        readonly Label _head;

        MsgDialog(string body, string title, string ok, string cancel, string logPath = null)
        {
            using (var g = CreateGraphics()) _k = g.DpiX / 96f;
            if (_k <= 0) _k = 1f;

            int W = S(420);
            int pad = S(24);
            int contentW = W - pad * 2;

            AutoScaleMode = AutoScaleMode.None;      // 和向导一致：自己按 DPI 缩放
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = owner_center();
            BackColor = Color.White;
            ForeColor = Theme.Ink;
            Font = new Font("Microsoft YaHei UI", 9f);
            ShowInTaskbar = false;
            MinimizeBox = MaximizeBox = false;
            TopMost = false;

            // ---- 标题（没给就用程序名，和向导同一个称呼）
            _head = new Label
            {
                Text = string.IsNullOrEmpty(title) ? Program.AppName : title,
                Location = new Point(pad, pad),
                Size = new Size(contentW, S(20)),
                AutoSize = false,
                AutoEllipsis = true,
                ForeColor = Theme.Ink,
                BackColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold)
            };
            Controls.Add(_head);

            // ---- 正文：按实际高度排，不写死行高
            _body = new Label
            {
                Text = body ?? "",
                Location = new Point(pad, pad + S(20) + S(10)),
                Size = new Size(contentW, S(20)),
                AutoSize = false,
                ForeColor = Theme.Muted,
                BackColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 9f)
            };
            Controls.Add(_body);

            int bodyH = TextRenderer.MeasureText(_body.Text, _body.Font,
                new Size(contentW, 0), TextFormatFlags.WordBreak).Height;
            _body.Height = Math.Max(S(20), bodyH);

            // ---- 底部按钮栏（规范 §二.1：主按钮在左、次按钮最右 —— 与向导外壳
            //      LayoutButtons 同一套顺序。之前一版写成「主按钮最右」是失误，已改回。）
            int bh = S(28), bw = S(86), gap = S(8);
            int by = pad + S(20) + S(10) + _body.Height + S(24);

            var secondaries = new System.Collections.Generic.List<Button>();

            if (!string.IsNullOrEmpty(cancel))
            {
                var cancelBtn = MakeBtn(cancel, 0, by, bw, bh, false);
                cancelBtn.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
                secondaries.Add(cancelBtn);
            }

            if (!string.IsNullOrEmpty(logPath))
            {
                var path = logPath;
                var logBtn = MakeBtn("打开日志", 0, by, bw, bh, false);
                logBtn.Click += delegate
                {
                    // 打开日志后不关窗：用户看完回来还能点「关闭」
                    try
                    {
                        System.Diagnostics.Process.Start(
                            new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
                    }
                    catch { }
                };
                secondaries.Add(logBtn);
            }

            // 次按钮从最右往左排，主按钮排在它们左侧
            int sx = W - pad - bw;
            foreach (var b in secondaries)
            {
                b.Location = new Point(sx, by);
                Controls.Add(b);
                sx -= bw + gap;
            }

            var okBtn = MakeBtn(ok, sx, by, bw, bh, true);
            okBtn.Click += delegate { DialogResult = DialogResult.OK; Close(); };
            Controls.Add(okBtn);

            AcceptButton = okBtn;
            if (cancel != null && secondaries.Count > 0) CancelButton = secondaries[0];

            ClientSize = new Size(W, Math.Max(S(150), by + bh + pad));
        }

        FormStartPosition owner_center()
        {
            // owner 在 ShowDialog 时才进来，这里先给屏幕居中，Show() 前会被 owner 覆盖
            return FormStartPosition.CenterScreen;
        }

        Button MakeBtn(string text, int x, int y, int w, int h, bool primary)
        {
            var b = new Button
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(w, h),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = primary ? Theme.Ink : Theme.Muted,
                Font = new Font("Microsoft YaHei UI", 9f),
                UseVisualStyleBackColor = false
            };
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = primary ? Theme.Accent : Theme.Line;
            b.FlatAppearance.MouseOverBackColor = Theme.HoverBg;
            b.FlatAppearance.MouseDownBackColor = Theme.SelectBg;
            return b;
        }

        int S(int v) { return (int)Math.Round(v * _k); }

        /// <summary>无边框窗默认没有投影，浮在向导上会像贴纸；加一层。</summary>
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000;    // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Theme.Line))
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _head.Font.Dispose();
                _head.Dispose();
                _body.Font.Dispose();
                _body.Dispose();
            }
            base.Dispose(disposing);
        }

        static DialogResult Show(IWin32Window owner, MsgDialog dlg)
        {
            // 居中于 owner：没有 owner 时退回屏幕居中（比如从托盘/命令行起来的场景）
            if (owner is Form f && f.Visible) dlg.StartPosition = FormStartPosition.CenterParent;
            return dlg.ShowDialog(owner);
        }

        /// <summary>单按钮提示。owner 传向导主窗体，弹窗会居中在它上面。</summary>
        public static void Info(IWin32Window owner, string body, string title = null)
        {
            using (var d = new MsgDialog(body, title, "确定", null)) Show(owner, d);
        }

        /// <summary>双按钮确认。点主按钮返回 true。</summary>
        public static bool Ask(IWin32Window owner, string body,
                               string ok = "确定", string cancel = "取消", string title = null)
        {
            using (var d = new MsgDialog(body, title, ok, cancel)) return Show(owner, d) == DialogResult.OK;
        }

        /// <summary>
        /// 错误框：主按钮「关闭」在左，给了日志路径时最右多一个「打开日志」。
        /// 给 Program.Main 的全局兜底用 —— 出问题必须给用户一个能看的地方，
        /// 不能界面啥也没变就没了（规范 §二.1）。
        /// </summary>
        public static void Error(IWin32Window owner, string body, string logPath = null, string title = null)
        {
            using (var d = new MsgDialog(body, title ?? Program.AppName + " 遇到问题",
                                         "关闭", null, logPath)) Show(owner, d);
        }
    }
}
