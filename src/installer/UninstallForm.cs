using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace OAIRouterSetup
{
    /// <summary>卸载向导：确认 → [正在运行] → 卸载中 → 完成。外观与安装向导同一套。</summary>
    public class UninstallForm : WizardShell
    {
        readonly string _dir;
        bool _busy, _removed, _failed;
        bool _keepConfig;
        string _blockError;       // 结束进程失败时的页内警示（规范 §二.1：不弹窗）
        bool _onRunningPage;      // 当前显示的是「正在运行」条件页

        CheckBox _cbKeep;
        ProgressStrip _bar;
        Label _status;

        public UninstallForm(string dir)
            : base("卸载 " + Program.AppName, 640, 400, "从计算机中移除程序")
        {
            _dir = string.IsNullOrEmpty(dir) ? Program.DefaultDir() : dir;
            // 卸载完成后删掉 %TEMP% 里的临时副本（自删由 Program 里的 cmd 收尾）
            FormClosing += delegate { if (_removed) Program.ScheduleSelfDelete(); };
        }

        protected override void BuildPages()
        {
            NextBtn.Click += delegate { OnNext(); };
            Back.Click += delegate
            {
                if (_busy || _removed) return;
                if (_onRunningPage) { ShowConfirm(); return; }   // 上一步 → 回确认页
                Close();
            };
            CancelBtn.Click += delegate { if (!_busy) Close(); };
        }

        protected override void ShowPage(int index)
        {
            if (index == 0) ShowConfirm();
        }

        void ShowConfirm()
        {
            _onRunningPage = false;
            _blockError = null;        // 回到确认页就清掉上一轮的失败警示
            BuildConfirm();

            // 确认页出现时**不**检测占用：程序开着时点开卸载向导若直接落到
            // 「正在运行」页，用户连卸载内容和「保留配置文件」选项都没看见。
            // 占用检测只放在点「卸载」那一刻 —— 见 OnNext()。
        }

        void BuildConfirm()
        {
            Body.Controls.Clear();
            TitleLabel.Text = "卸载 " + Program.AppName;

            Back.Visible = false;            // 确认页是第一页，没有「上一步」
            CancelBtn.Visible = true;
            CancelBtn.Enabled = true;
            CancelBtn.Text = "取消";
            NextBtn.Visible = true;
            NextBtn.Text = "卸载";
            NextBtn.Enabled = true;
            LayoutButtons(86);           // 从「正在运行」页回来时复位默认宽度

            bool allUsers = Program.InstalledForAllUsers() || Program.IsUnderProgramFiles(_dir);

            // 首行在 428 设计像素下会折成两行（「…（为当前用户安装）。」），
            // 固定 24px 会把第二行裁掉 —— 按实测高度定高，下面的块整体跟着下移，
            // 单行时 dy = 0，与原来的坐标一字不差。
            string line1 = "卸载程序将把 " + Program.AppName + " " + Program.Version
                           + " 从您的计算机中移除（"
                           + (allUsers ? "为所有用户安装" : "为当前用户安装") + "）。";
            int h1 = Math.Max(24, MeasureH(line1, 428));
            int dy = h1 - 24;
            L(line1, 0, 0, 428, h1);

            Strong("安装位置：", 0, 34 + dy, 428, 22);
            L(_dir, 0, 56 + dy, 428, 20);

            Strong("将一并清理：", 0, 88 + dy, 428, 22);
            L("· 程序文件与开机自启项\n"
              + "· 桌面与开始菜单快捷方式\n"
              + "· 「设置 → 应用」里的卸载登记项", 0, 110 + dy, 428, 70);

            _cbKeep = Check("保留个人数据（config.json、backups、logs）（重装后沿用）", 0, 190 + dy, 396, true);

            L("作者：" + Program.Author + "　　联系邮箱：" + Program.Email, 0, 224 + dy, 428, 20);
        }

        void OnNext()
        {
            if (_removed) { Close(); return; }
            if (_failed) { RetryUninstall(); return; }   // 主按钮位：重试（规范 §二：主按钮在左）
            if (_busy) return;

            if (_onRunningPage)
            {
                EndRunningAndUninstall();
                return;
            }

            // 点「卸载」时才检测占用（程序可能在确认页停留期间被打开）；
            // 命中就切「正在运行」页，绝不弹 MessageBox。
            if (Program.RunningInstances(_dir).Length > 0)
            {
                ShowRunning();
                return;
            }

            _keepConfig = _cbKeep != null && _cbKeep.Checked;
            StartUninstall();
        }

        /// <summary>「X 正在运行」条件页：与安装侧同款，不弹 MessageBox。</summary>
        void ShowRunning()
        {
            _onRunningPage = true;
            Body.Controls.Clear();
            TitleLabel.Text = Program.AppName + " 正在运行";
            Back.Visible = true;
            Back.Text = "上一步";
            Back.Enabled = true;
            CancelBtn.Visible = true;
            CancelBtn.Enabled = true;
            CancelBtn.Text = "取消";
            NextBtn.Visible = true;
            NextBtn.Enabled = true;
            NextBtn.Text = "结束进程并继续卸载";
            LayoutButtons(126);          // 按钮文字长，需要更宽，否则被折成两行

            Strong("需要删除的文件正被占用：", 0, 0, 428, 22);
            L(_dir, 0, 24, 428, 20);
            var exeLbl = L(Path.Combine(_dir, Program.AppExe), 0, 46, 428, 20);
            exeLbl.AutoEllipsis = true;     // 长路径单行裁切时给省略号

            Strong("请选择一种处理方式：", 0, 80, 428, 22);
            L("· 点「结束进程并继续卸载」—— 自动关闭正在运行的 " + Program.AppName
              + "（未保存的操作会丢失），随后立即继续卸载；\n"
              + "· 点「上一步」—— 回到确认页；\n"
              + "· 若结束失败，请手动关闭 " + Program.AppName
              + "，再点「结束进程并继续卸载」重试。", 0, 104, 428, 80);

            // 结束失败时留一行页内警示（规范 §二.1：能做成页内提示的就不弹窗）
            if (!string.IsNullOrEmpty(_blockError))
            {
                var warn = L(_blockError, 0, 190, 428, 40);
                warn.ForeColor = Theme.Warn;
            }
        }

        /// <summary>结束占用进程后继续卸载；仍被占用则停在这一页，让用户手动处理。</summary>
        void EndRunningAndUninstall()
        {
            NextBtn.Enabled = false;
            int n = Program.KillRunning(_dir);

            if (Program.RunningInstances(_dir).Length > 0)
            {
                NextBtn.Enabled = true;
                // 规范 §三.1-4 / §五-1b：杀不掉用**自绘对话框**兜住（不许用 MessageBox、
                // 不响提示音），停在原页不继续卸载。对话框按规范分段，页内那行用紧凑版。
                string head = "没能结束正在运行的 " + Program.AppName + "（权限不足或进程未响应）。";
                string tail = "请用任务管理器手动结束它，或重启计算机后再运行卸载程序。";
                _blockError = head + "\r\n" + tail;
                MsgDialog.Info(this, head + "\n\n" + tail, "没能结束进程");
                ShowRunning();
                return;
            }

            if (n > 0) Thread.Sleep(300);   // 等文件句柄真正释放
            _blockError = null;             // 结束成功了，别把失败警示带进卸载页
            _keepConfig = _cbKeep != null && _cbKeep.Checked;
            StartUninstall();
        }

        void StartUninstall()
        {
            _busy = true;
            Body.Controls.Clear();
            TitleLabel.Text = "正在卸载";

            Back.Visible = false;
            NextBtn.Enabled = false;
            CancelBtn.Enabled = false;

            L("正在从 " + _dir + " 移除 " + Program.AppName + "…", 0, 0, 428, 40);

            // 自绘进度条：跟随派生主色（系统 ProgressBar 是绿的，不跟主题）
            _bar = new ProgressStrip(K, 0, 60, 428, 10);
            Body.Controls.Add(_bar);

            _status = L("准备中…", 0, 78, 428, 150);

            var dir = _dir;
            var keep = _keepConfig;
            var th = new Thread(delegate()
            {
                try
                {
                    int n = Program.RemoveAll(dir, keep, delegate(int pct, string msg)
                    {
                        Ui(delegate
                        {
                            if (_bar == null) return;
                            _bar.Value = Math.Min(100, pct);
                            _status.Text = msg + "…";
                        });
                    });
                    Ui(delegate { ShowFinish(n, keep); });
                }
                catch (Exception ex)
                {
                    // 检查通过到动手删除之间用户又开了程序（RemoveAll 抛 StillRunningException，
                    // 或删文件时被占用）：切回占用页让用户再决定，不当成失败
                    // （规范 §五 / §三.1 第 7 点：执行函数只复查、不静默杀）。
                    bool running = ex is Program.StillRunningException
                        || Program.RunningInstances(dir).Length > 0;
                    if (running)
                        Ui(delegate
                        {
                            _busy = false;
                            _failed = false;
                            ShowRunning();
                        });
                    else
                        Ui(delegate { ShowFailure(ex.Message); });
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        void ShowFinish(int removed, bool keep)
        {
            // 完成页必须先核实目录（规范 §五）：逐文件 TryDelete 吞了错，
            // 目录里还有东西就不能报「已从您的计算机上移除」。
            int left = Program.RemainingFiles(_dir, keep);
            if (left > 0) { ShowResidue(left); return; }

            _busy = false;
            _removed = true;
            Body.Controls.Clear();
            TitleLabel.Text = "卸载完成";

            Back.Visible = false;
            CancelBtn.Visible = false;
            NextBtn.Text = "关闭";
            NextBtn.Enabled = true;
            LayoutButtons(86);

            // 逐行累加：没有的那一行不留空位 —— 用固定 y 排版时，缺的那行会在
            // 页面中间留个空洞，看着像漏了内容（规范 §五-3）。
            int y = 0;
            y = Line(Program.AppName + " 已从您的计算机上移除。", y, 428);
            y = Line("共删除 " + removed + " 个文件。", y, 428);

            if (keep)
            {
                // 说明、目录、清单、具体路径各占一行；两条路径用省略号截断 + 悬停看全。
                // 四个安装器这一段逐字一致。
                y += 14;
                y = Line("配置文件已保留：", y, 428);
                y = LinePath(_dir, y);
                y = Line("包含：" + Program.KeptConfigFiles(_dir), y, 428);
                y = LinePath(Program.KeptConfigPaths(_dir), y);
                y = Line("（重新安装后自动沿用，无需重新设置）", y, 428);
            }
            else
            {
                y += 14;
                y = Line("配置文件已一并删除。", y, 428);
            }

            y += 10;
            y = Line("如果以后还要用，重新运行安装包即可。", y, 428);
            Line("作者：" + Program.Author + "　　联系邮箱：" + Program.Email, y, 428);
        }

        /// <summary>
        /// 卸载执行完但目录里还有残留：不能报成功，给「重试」和明确的手动删除路径（规范 §五 3b）。
        /// </summary>
        void ShowResidue(int left)
        {
            _busy = false;
            _failed = true;          // 「重试」走 RetryUninstall
            _removed = false;        // 还有残留，别触发卸载后的清理
            Body.Controls.Clear();
            TitleLabel.Text = "卸载未完全成功";

            Back.Visible = false;
            CancelBtn.Visible = true;
            CancelBtn.Enabled = true;
            CancelBtn.Text = "关闭";
            NextBtn.Text = "重试";
            NextBtn.Enabled = true;
            LayoutButtons(86);

            L(Program.AppName + " 的程序文件已删除，但安装目录里还剩 " + left + " 个文件。\n\n"
              + "常见原因：文件被占用，或被安全软件拦下。\n\n"
              + "可以点「重试」再卸一次，或点「关闭」后手动删除：\n" + _dir, 0, 0, 428, 140);
        }

        void ShowFailure(string msg)
        {
            _busy = false;
            _failed = true;
            Body.Controls.Clear();
            TitleLabel.Text = "卸载未完全成功";

            Back.Visible = false;
            CancelBtn.Visible = true;
            CancelBtn.Enabled = true;
            CancelBtn.Text = "关闭";       // 次按钮位（最右）：退出向导，稍后手动处理
            NextBtn.Visible = true;
            NextBtn.Enabled = true;
            NextBtn.Text = "重试";         // 主按钮位（左）：再删一次（规范 §二 主按钮在左）
            LayoutButtons(86);

            L("有些文件没能删除：\n\n" + msg
              + "\n\n可以点「重试」再卸一次（比如先关掉占用文件的程序），"
              + "或点「关闭」退出后手动删除安装目录：\n" + _dir, 0, 0, 428, 140);
        }

        /// <summary>失败页的「重试」：重新走一遍卸载（占用情况重新判定，不静默杀进程）。</summary>
        void RetryUninstall()
        {
            _failed = false;
            _blockError = null;
            _keepConfig = _cbKeep != null && _cbKeep.Checked;
            // 重试也要走「先问再杀」：期间程序被打开过就切回占用页，由用户决定
            // （规范 §三.1-7：执行函数只复查、不静默杀）。
            if (Program.RunningInstances(_dir).Length > 0) { ShowRunning(); return; }
            StartUninstall();
        }

        /// <summary>
        /// 把回调切回 UI 线程。BeginInvoke 失败（句柄正在销毁的边角）时同步 Invoke 再试一次，
        /// 别让完成/失败回调静默丢掉、窗口停在「卸载中」页（规范 §五）。
        /// </summary>
        void Ui(Action a)
        {
            try { if (IsHandleCreated) BeginInvoke(a); }
            catch
            {
                try { if (IsHandleCreated && !IsDisposed) Invoke(a); } catch { }
            }
        }
    }
}
