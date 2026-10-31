using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace OAIRouterSetup
{
    /// <summary>
    /// 安装向导：欢迎 → 安装模式 → 许可协议 → 安装位置 → 附加任务 → 准备 → 安装 → 完成。
    /// 顺序与 txtFormat 的 Inno 向导一致，「选择安装模式」从独立对话框挪进了向导本身。
    /// </summary>
    public class InstallForm : WizardShell
    {
        const int PageWelcome = 0, PageMode = 1, PageLicense = 2,
                  PageDir = 3, PageTasks = 4, PageReady = 5;

        int _page;
        bool _busy, _done, _failed, _blocked;
        string _blockedPath;      // 被占用的那个文件，用于在页面上指名道姓
        string _blockError;       // 结束进程失败时的页内警示（规范 §二.1：不弹窗）

        /// <summary>安装目录从哪来：默认值 / 已装版本 / 用户手改。
        /// 只有「用户手改过」才不许切模式时自动重算目录 —— 提权后选了「为所有用户安装」
        /// 却还装着旧的当前用户目录，就是这么来的（见 PickMode）。</summary>
        enum DirSource { Default, Installed, User }

        DirSource _dirSource = DirSource.Default;

        string _dir;
        InstallMode _mode = Program.Mode;
        bool _desktop = true, _start = true, _run = true, _autostart;
        bool _overwriteConfig;    // 勾了「覆盖现有配置」才为 true；默认不动本地配置

        ModeCard _cardUser, _cardAll;
        Label _modeHint;
        TextBox _dirBox;
        CheckBox _cbDesktop, _cbStart, _cbRun, _cbAutostart;
        ProgressStrip _bar;
        Label _status;
        CheckBox _cbRunNow;
        Label _localExe, _localCfg;   // 「选择安装位置」页的本地状态行，没有就整块不出现
        Label _localCfgPath;          // 上面那份配置的完整地址，单独一行（过长省略号）
        Label _otherInstall;          // 本机装在**别处**的那一份（换了安装模式时出现）
        Label _otherHint;             // 上面那行怎么处理的建议
        Label _dirError;              // 路径非法时的页内警示（规范 §二.1：不弹窗）
        CheckBox _cbOverwrite;

        /// <summary>提权重启后从 --resume 指定的页接着走，只跳一次。</summary>
        bool _resumed;

        public InstallForm()
            : base(Program.AppName + " 安装", 640, 400, Program.Tagline)
        {
            _mode = Program.Mode;

            // 已装过就默认装在原位置（覆盖升级 / 修复），没装过就按模式给默认目录。
            // 模式页随时显示「将安装到：<这个值>」——它就是要被安装的真实路径，不能替换成一句
            // 「已在后面单独设置」：那样用户在点下一步之前根本不知道会装到哪（规范 §三）。
            //
            // 但「已装的是当前用户 → 这次提权改成所有用户」不能沿用：那会把机器级安装
            // 塞进 %LOCALAPPDATA%，跟用户刚选的模式自相矛盾。三套安装器用同一套判定，
            // 位置不一致时由位置页的「本机已安装过」提示把旧的那份说出来（见 _otherInstall）。
            var installed = Program.InstalledDir();
            bool installedIsMachine = !string.IsNullOrEmpty(installed)
                                      && Program.IsUnderProgramFiles(installed);
            bool wantMachine = Program.Mode == InstallMode.AllUsers;

            if (!string.IsNullOrEmpty(installed) && installedIsMachine == wantMachine)
            {
                _dir = installed;
                _dirSource = DirSource.Installed;
            }
            else
            {
                _dir = Program.DefaultDir(_mode);
                _dirSource = DirSource.Default;
            }
        }

        /// <summary>按钮在外壳构建完成后才存在，事件必须绑在这里而不是构造函数。</summary>
        protected override void BuildPages()
        {
            NextBtn.Click += delegate { OnNext(); };
            Back.Click += delegate { OnBack(); };
            CancelBtn.Click += delegate
            {
                if (_failed) { Close(); return; }          // 失败页最右那格是「关闭」
                if (!_busy) Close();
            };
        }

        // ------------------------------------------------------------ 页签
        protected override void ShowPage(int index)
        {
            // 提权重启后的实例：模式页那一步已经选过了，直接回到「许可协议」，
            // 不用从欢迎页重新点一遍（Program.ResumePage 由 --resume=N 传入）
            if (!_resumed && index == 0 && Program.ResumePage > 0)
            {
                _resumed = true;
                int target = Program.ResumePage;
                Program.ResumePage = 0;   // 只跳一次，之后「上一步」还能正常回到欢迎页
                ShowPage(target);
                return;
            }

            _page = index;
            _blocked = false;
            Body.Controls.Clear();
            Back.Visible = true;
            Back.Text = "上一步";
            CancelBtn.Visible = true;
            CancelBtn.Text = "取消";
            Back.Enabled = index > PageWelcome;
            NextBtn.Enabled = true;
            NextBtn.Text = index == PageReady ? "安装" : "下一步";
            LayoutButtons(86);          // 复位默认宽度，长文字页会自己再调宽

            switch (index)
            {
                case PageWelcome: BuildWelcome(); break;
                case PageMode: BuildMode(); break;
                case PageLicense: BuildLicense(); break;
                case PageDir: BuildDir(); break;
                case PageTasks: BuildTasks(); break;
                default: BuildReady(); break;
            }
        }

        void BuildWelcome()
        {
            TitleLabel.Text = "欢迎使用 " + Program.AppName + " 安装向导";

            L("安装程序将把 " + Program.AppName + " " + Program.Version
              + " 安装到您的计算机。\n\n"
              + "作者：" + Program.Author + "　　联系邮箱：" + Program.Email + "\n\n"
              + "继续之前，建议关闭其它正在运行的程序。", 0, 0, 428, 110);

            Strong("这个程序做什么", 0, 120, 428, 22);
            L("在本地起一个 OpenAI 兼容的路由代理：按路径前缀把请求分发到官方 / 中转上游，"
              + "Chat Completions 与 Responses 双向协议翻译，常驻系统托盘，"
              + "附带模型价格同步与用量统计。", 0, 144, 428, 70);
        }

        void BuildMode()
        {
            TitleLabel.Text = "选择安装模式";

            L(Program.AppName + " 可以仅为您自己安装，也可以为这台计算机上的所有用户安装。",
              0, 0, 428, 44);

            _cardUser = new ModeCard("仅为我安装（推荐）",
                "只有当前用户能使用；不需要管理员权限", false, K, 0, 50, 428, 62);
            _cardUser.Picked += delegate { PickMode(InstallMode.PerUser); };
            Body.Controls.Add(_cardUser);

            _cardAll = new ModeCard("为所有用户安装",
                "本机所有用户都能使用；需要管理员权限", true, K, 0, 122, 428, 62);
            _cardAll.Picked += delegate { PickMode(InstallMode.AllUsers); };
            Body.Controls.Add(_cardAll);

            _modeHint = L("", 0, 196, 428, 40);      // 两行余量：长路径会折行，不截断
            PickMode(_mode);
        }

        void PickMode(InstallMode m)
        {
            // 只有「真的换了模式」才重算路径：页面初始化时也会调到这里，
            // 若无条件重算，覆盖升级场景下已装目录会被立刻换成默认目录。
            bool switched = m != _mode;
            _mode = m;

            if (_cardUser != null) { _cardUser.Selected = m == InstallMode.PerUser; _cardUser.Invalidate(); }
            if (_cardAll != null) { _cardAll.Selected = m == InstallMode.AllUsers; _cardAll.Invalidate(); }

            if (switched && _dirSource != DirSource.User)
            {
                _dir = Program.DefaultDir(m);
                _dirSource = DirSource.Default;
            }

            // 卡片下方永远是「将安装到：<当前解析出来的真实路径>」，切卡片实时变化（规范 §三）
            if (_modeHint != null) _modeHint.Text = "将安装到：" + _dir;
        }

        void BuildLicense()
        {
            TitleLabel.Text = "许可协议";

            // 不做「必须同意」的勾选（规范 §三 只要求一行提示 + 只读文本框），
            // 所以措辞要对得上交互：继续就是同意，别写「必须接受」却没有任何接受动作。
            L("请阅读以下许可协议。点「下一步」继续安装，即表示你同意这些条款。", 0, 0, 428, 20);

            var tb = new TextBox
            {
                Text = Program.License,
                Location = P(0, 26),
                Size = Z(428, 210),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Theme.CardIdle,
                Font = new Font("Consolas", 8.5f),
                WordWrap = true
            };
            // 焦点落进只读文本框时 WinForms 会把全文选中 —— 进页面第一眼就是一大块蓝底，
            // 像是协议被高亮了。拿到焦点就把选区收回开头，用户自己拖选不受影响。
            tb.GotFocus += delegate { tb.Select(0, 0); };
            Body.Controls.Add(tb);
        }

        void BuildDir()
        {
            TitleLabel.Text = "选择安装位置";

            L("安装程序将把 " + Program.AppName + " 安装到下面的文件夹。\n"
              + "若要装到其它文件夹，点「浏览」选择目标文件夹。", 0, 0, 428, 40);

            _dirBox = Box(_dir, 0, 48, 328, 24);

            var browse = SmallButton("浏览…", 338, 46, 90, 27);
            browse.Click += delegate
            {
                var picked = FolderPicker.Pick(this, "选择 " + Program.AppName + " 的安装位置");
                if (string.IsNullOrEmpty(picked)) return;

                // 选完自动补一层程序名，和 Inno 装出来的效果一致
                var trimmed = picked.TrimEnd('\\');
                var name = Path.GetFileName(trimmed);
                var full = string.Equals(name, Program.AppId, StringComparison.OrdinalIgnoreCase)
                    ? trimmed
                    : Path.Combine(trimmed, Program.AppId);

                _dirSource = DirSource.User;   // 用户自己挑的位置，之后切模式不再自动改
                _dirBox.Text = full;
                ValidateDir(false);       // 换了目录：路径合法性与本地状态都要重新判定
            };

            L(SizeText(), 0, 84, 428, 20);

            _dirError = L("", 0, 104, 428, 20);
            _dirError.ForeColor = Theme.Warn;      // 警示色：和降级那行同色
            _dirError.Visible = false;

            // ---- 本地状态：没有就整块不出现（规范 §三.2）
            _localExe = L("", 0, 128, 428, 20);
            _localCfg = L("", 0, 152, 428, 20);
            // 配置文件的具体地址单独一行：只说「有配置」用户不知道动的是哪一份，
            // 长路径用省略号，别让它把右边的复选框挤下去。
            _localCfgPath = L("", 0, 174, 428, 18);
            _localCfgPath.ForeColor = Theme.Dim;
            _localCfgPath.AutoEllipsis = true;
            // 「本机装在别处」两行：目标目录没有程序、但注册表里记着另一份时出现
            _otherInstall = L("", 0, 196, 428, 20);
            _otherInstall.ForeColor = Theme.Dim;
            _otherInstall.AutoEllipsis = true;
            _otherHint = L("", 0, 218, 428, 20);
            _otherHint.ForeColor = Theme.Dim;
            _cbOverwrite = Check("覆盖现有配置（恢复为默认设置）", 0, 242, 380, false);
            _cbOverwrite.CheckedChanged += delegate { _overwriteConfig = _cbOverwrite.Checked; };

            _dirBox.TextChanged += delegate
            {
                _dirSource = DirSource.User;   // 手输 / 粘贴 / 浏览都算「用户指定过」
                ValidateDir(false);
            };
            ValidateDir(false);
        }

        /// <summary>
        /// 路径可用性 = 页内一行警示 + 主按钮置灰，不弹系统消息框（规范 §二.1）。
        /// <paramref name="probe"/> 为真才建目录试探可写性 —— 边打字边建会在用户取消时
        /// 留下一串空目录，所以实时校验只做格式检查与空间检查（QuickCheckDir / CheckSpace）。
        /// </summary>
        /// <returns>通过返回 true。</returns>
        bool ValidateDir(bool probe)
        {
            var dir = _dirBox == null ? _dir : _dirBox.Text.Trim();
            string err = probe ? CheckDir(dir) : QuickCheckDir(dir);
            // 空间不够也走页内提示（规范 §二.1 列举的三类：路径非法、权限不足、空间不够）。
            // CheckSpace 只读 DriveInfo、不建目录，实时校验也安全。
            if (err == null && dir.Length > 0) err = CheckSpace(dir);
            ShowDirError(err);
            return err == null;
        }

        /// <summary>页内警示行：显示 err 并把主按钮置灰；传 null 清除。本地状态区跟着让位。</summary>
        void ShowDirError(string err)
        {
            if (_dirError == null) return;

            _dirError.Visible = err != null;
            _dirError.Text = err ?? "";
            if (err != null)
            {
                // 警示文案可能折到两三行 —— 标签按真实需要长高，下面那块本地状态跟着挪，
                // 不能让 20px 的固定行高把第二行裁掉，也不能压住「检测到本地已有配置」。
                int need = TextRenderer.MeasureText(err, _dirError.Font,
                    new Size(_dirError.Width, 0), TextFormatFlags.WordBreak).Height;
                _dirError.Height = Math.Max(20, need);
            }

            if (NextBtn != null) NextBtn.Enabled = err == null;
            RefreshLocalState();       // 警示行出现/消失时，本地状态区重新排位
        }

        /// <summary>便宜检查：边打字边跑也毫无副作用（不建目录、不写文件）。</summary>
        static string QuickCheckDir(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return "请填写安装位置。";
            if (!Path.IsPathRooted(dir))
                return "安装位置要写完整路径，例如：D:\\Apps\\OAIRouter";

            try
            {
                var full = Path.GetFullPath(dir);
                if (dir.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                    return "路径里有不能用的字符，请换一个位置。";
                if (!Path.IsPathRooted(full)) return "安装位置要写完整路径。";
            }
            catch
            {
                return "这个路径写不成文件系统的路径，请检查一下。";
            }
            return null;
        }

        /// <summary>已装版本 → 一行结论文案。降级必须说清楚，别让用户默默退回旧版。</summary>
        static string UpgradeText(Program.UpgradeKind kind, string installed)
        {
            var have = string.IsNullOrEmpty(installed) ? "未知版本" : "v" + installed;
            var now = "v" + Program.Version;

            switch (kind)
            {
                case Program.UpgradeKind.Update:
                    return "该位置已安装 " + have + "，将更新到 " + now + "。";
                case Program.UpgradeKind.Same:
                    return "该位置已安装 " + have + "（与安装包相同），将重新安装覆盖。";
                case Program.UpgradeKind.Downgrade:
                    return "该位置已安装 " + have + "，比本安装包 " + now + " 新。继续将降级到 " + now + "。";
                case Program.UpgradeKind.Unknown:
                    return "该位置已安装 " + Program.AppName + "（" + have + "），将覆盖程序文件。";
                default:
                    return "";
            }
        }


        /// <summary>
        /// 按当前路径刷新「本地状态」区：本目录已装 / 本目录有配置 / 别处装过 才显示，
        /// 都没有就整块隐藏，不留空行（规范 §三.2）。
        /// 切到没有配置的目录时，复选框要连勾选状态一起复位，不能把上一个目录的选择带过去。
        /// </summary>
        void RefreshLocalState()
        {
            if (_localExe == null || _localCfg == null || _cbOverwrite == null) return;

            var dir = _dirBox == null ? _dir : _dirBox.Text.Trim();
            bool hasExe = false, hasCfg = false;
            Program.UpgradeKind kind = Program.UpgradeKind.Fresh;
            string installed = null;
            try
            {
                if (!string.IsNullOrEmpty(dir) && Path.IsPathRooted(dir))
                {
                    hasExe = File.Exists(Program.ExePathIn(dir));
                    hasCfg = Program.HasConfig(dir);
                    if (hasExe) kind = Program.DetectUpgrade(dir, out installed);
                }
            }
            catch { }

            _localExe.Visible = hasExe;
            _localExe.Text = hasExe ? UpgradeText(kind, installed) : "";
            // 降级用警示色，其它用普通灰
            _localExe.ForeColor = kind == Program.UpgradeKind.Downgrade ? Theme.Warn : Theme.Muted;

            _localCfg.Visible = hasCfg;
            _localCfg.Text = hasCfg ? "检测到本地已有配置，将保留你现有的设置。" : "";

            _localCfgPath.Visible = hasCfg;
            _localCfgPath.Text = hasCfg ? Program.SettingsPathIn(dir) : "";

            _cbOverwrite.Visible = hasCfg;
            _cbOverwrite.Enabled = hasCfg;
            if (!hasCfg) _cbOverwrite.Checked = false;   // 没配置就不可能覆盖
            _overwriteConfig = _cbOverwrite.Checked;

            // 目标目录本身没有程序、但本机别处装着一份（多半是这次换了安装模式）：
            // 必须说出来，否则页面看着跟首次安装一模一样（用户实测踩过）。
            string other = hasExe ? null : Program.OtherInstalledDir(dir);
            _otherInstall.Visible = other != null;
            _otherHint.Visible = other != null;
            if (other != null)
            {
                string otherVer;
                Program.DetectUpgrade(other, out otherVer);
                _otherInstall.Text = "本机已安装过"
                    + (string.IsNullOrEmpty(otherVer) ? "" : "（v" + otherVer + "）") + "：" + other;
                _otherInstall.AutoEllipsis = true;
                _otherHint.Text = "本次会装到当前位置，两处同时存在；要升级那一份，请改回原来的安装模式。";
            }

            // 依次紧凑排布，没有的那块不占位。
            // 起点跟着警示行走：警示行折成两行时往下让位，不折行就停在固定位置。
            int y = _dirError != null && _dirError.Visible ? _dirError.Bottom + 8 : 128;
            if (hasExe) { _localExe.Location = P(0, y); y += 24; }
            if (other != null)
            {
                _otherInstall.Location = P(0, y);
                _otherHint.Location = P(0, y + 22);
                y += 44;
            }
            if (hasCfg)
            {
                _localCfg.Location = P(0, y);
                _localCfgPath.Location = P(0, y + 22);
                _cbOverwrite.Location = P(0, y + 46);
                // 复选框提到最前：它是可操作项，绝不能被上面的文字标签盖住
                _cbOverwrite.BringToFront();
            }
        }

        static string SizeText()
        {
            try
            {
                using (var s = typeof(Program).Assembly.GetManifestResourceStream("payload.zip"))
                {
                    if (s != null)
                    {
                        // 按解压后的体积算，压缩包大小会明显偏小
                        using (var zip = new System.IO.Compression.ZipArchive(
                                   s, System.IO.Compression.ZipArchiveMode.Read))
                        {
                            long total = 0;
                            foreach (var e in zip.Entries) total += e.Length;
                            var mb = total / 1048576.0;
                            return "所需磁盘空间：约 " + mb.ToString("0.0") + " MB";
                        }
                    }
                }
            }
            catch { }
            return "所需磁盘空间：约 1.5 MB";
        }

        /// <summary>安装包解压后的总字节数（用来跟目标盘剩余空间比）。</summary>
        static long PayloadBytes()
        {
            try
            {
                using (var s = typeof(Program).Assembly.GetManifestResourceStream("payload.zip"))
                {
                    if (s == null) return 0;
                    using (var zip = new System.IO.Compression.ZipArchive(
                               s, System.IO.Compression.ZipArchiveMode.Read))
                    {
                        long total = 0;
                        foreach (var e in zip.Entries) total += e.Length;
                        return total;
                    }
                }
            }
            catch { return 0; }
        }

        /// <summary>
        /// 目标盘剩余空间够不够（规范 §二.1 明确要求「空间不够」做页内提示）。
        /// 返回 null 表示够用或测不出来（测不出来不该拦住用户）。
        /// </summary>
        static string CheckSpace(string dir)
        {
            long need = PayloadBytes();
            if (need <= 0) return null;
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(dir));
                if (string.IsNullOrEmpty(root)) return null;
                var drive = new DriveInfo(root);
                if (!drive.IsReady) return null;
                // 留 50MB 余量，别把盘占得一丝不剩
                if (drive.AvailableFreeSpace >= need + 50L * 1048576L) return null;
                double needMb = need / 1048576.0;
                double freeMb = drive.AvailableFreeSpace / 1048576.0;
                return string.Format("目标盘剩余空间不足（需要约 {0:0} MB，仅剩 {1:0} MB）。请换一个磁盘或先清理空间。",
                                     needMb, freeMb);
            }
            catch { return null; }
        }

        void BuildTasks()
        {
            TitleLabel.Text = "选择附加任务";

            L("选择安装程序要执行的附加任务，然后点「下一步」。", 0, 0, 428, 20);

            Strong("附加图标：", 0, 34, 428, 22);
            _cbDesktop = Check("创建桌面快捷方式", 16, 60, 320, _desktop);
            _cbStart = Check("创建开始菜单快捷方式", 16, 86, 320, _start);

            Strong("启动选项：", 0, 130, 428, 22);
            _cbAutostart = Check("开机自启（登录后启动到系统托盘）", 16, 156, 320, _autostart);

            Strong("完成后：", 0, 194, 428, 22);
            _cbRun = Check("立即运行 " + Program.AppName, 16, 220, 320, _run);
        }

        void BuildReady()
        {
            TitleLabel.Text = "准备安装";

            L("点「安装」开始安装，或点「上一步」复查或更改设置。", 0, 0, 428, 20);

            // 左标签右值两列：标签右对齐成一列（88px），值从 x=96 起自动折行。
            // 比原来「标签一行、值一行」省掉近一半竖向空间，六行也放得下。
            int y = 30;
            y += Row("安装模式", _mode == InstallMode.AllUsers
                ? "为所有用户安装（写入 Program Files，需要管理员权限）"
                : "仅为我安装（写入当前用户目录）", y) + 8;

            y += Row("安装位置", _dir, y) + 8;

            // 已装过就说明这次是更新 / 重装 / 降级（规范 §三.3）
            string installed;
            var kind = Program.DetectUpgrade(_dir, out installed);
            if (kind != Program.UpgradeKind.Fresh)
            {
                y += Row("操作类型", ActionText(kind, installed), y,
                         kind == Program.UpgradeKind.Downgrade ? Theme.Warn : Theme.Ink) + 8;
            }

            // 只在目标目录确实有配置时才出现（规范 §三.2：没有就不占位）
            if (Program.HasConfig(_dir))
            {
                y += Row("本地配置",
                         _overwriteConfig ? "覆盖（恢复为默认设置）" : "保留现有配置", y) + 8;
            }

            Row("附加任务", TaskSummary(), y);
        }

        /// <summary>「准备安装」页的操作类型文案，比页面提示更短。</summary>
        static string ActionText(Program.UpgradeKind kind, string installed)
        {
            var have = string.IsNullOrEmpty(installed) ? "未知版本" : "v" + installed;
            switch (kind)
            {
                case Program.UpgradeKind.Update:
                    return "更新 " + have + " → v" + Program.Version;
                case Program.UpgradeKind.Same:
                    return "重新安装（已装 " + have + "，版本相同）";
                case Program.UpgradeKind.Downgrade:
                    return "降级 " + have + " → v" + Program.Version + "（已装版本更新）";
                case Program.UpgradeKind.Unknown:
                    return "覆盖安装（已装版本无法识别）";
                default:
                    return "全新安装";
            }
        }

        string TaskSummary()
        {
            var s = "无";
            if (_desktop && _start) s = "桌面快捷方式、开始菜单快捷方式";
            else if (_desktop) s = "桌面快捷方式";
            else if (_start) s = "开始菜单快捷方式";
            if (_autostart) s += "；开机自启";
            if (_run) s += "；安装完成后运行程序";
            return s;
        }

        // ------------------------------------------------------------ 交互
        void OnBack()
        {
            if (_busy || _done || _failed) return;

            // 从「文件被占用」页返回，退回上一页（准备安装）让用户改设置
            if (_blocked)
            {
                _blocked = false;
                _blockedPath = null;
                ShowPage(PageReady);
                return;
            }
            if (_page > PageWelcome) ShowPage(_page - 1);
        }

        void OnNext()
        {
            if (_done)
            {
                if (_cbRunNow != null && _cbRunNow.Checked) Program.Launch(_dir);
                Close();
                return;
            }

            if (_blocked) { EndRunningAndRetry(); return; }
            if (_failed) { RetryInstall(); return; }       // 主按钮位：重试
            if (_busy) return;

            switch (_page)
            {
                case PageMode:
                    {
                        if (_mode == InstallMode.AllUsers && !Program.IsElevated)
                        {
                            // 自绘对话框，不用系统消息框：字体/按钮/边框全是系统主题，
                            // 弹出来和自绘向导两套观感，带图标的重载还会响系统提示音（规范 §二.1）
                            bool go = MsgDialog.Ask(this,
                                "「为所有用户安装」需要管理员权限。\n\n"
                                + "接下来会请求提升权限并重新启动安装程序，你的选择会被保留。",
                                "继续", "取消");
                            if (!go) return;

                            // 权限提升后重启，并告诉新实例跳到「许可协议」页（--resume=2），
                            // 别让用户从欢迎页重新点一遍（安装范围与目录选择都还在）
                            if (Program.RelaunchAsAdmin("--all-users --resume=" + PageLicense)) { Close(); return; }

                            MsgDialog.Info(this,
                                "没有获得管理员权限，已取消。\n可以改选「仅为我安装」，不需要提权。");
                            return;
                        }
                        ShowPage(PageLicense);
                        return;
                    }
                case PageDir:
                    {
                        // 不合规就停在页内：警示行 + 「下一步」置灰，不弹系统消息框（规范 §二.1）
                        if (!ValidateDir(true)) return;

                        _dir = _dirBox.Text.Trim();
                        _dirSource = DirSource.User;

                        // 不再弹「已存在，是否覆盖」确认框：本地状态已在上面的页面里显示，
                        // 是否覆盖配置由复选框表达（默认保留）。见规范 §三.2
                        _overwriteConfig = _cbOverwrite != null && _cbOverwrite.Visible
                                           && _cbOverwrite.Checked;
                        ShowPage(PageTasks);
                        return;
                    }
                case PageTasks:
                    {
                        if (_cbDesktop != null) _desktop = _cbDesktop.Checked;
                        if (_cbStart != null) _start = _cbStart.Checked;
                        if (_cbAutostart != null) _autostart = _cbAutostart.Checked;
                        if (_cbRun != null) _run = _cbRun.Checked;
                        ShowPage(PageReady);
                        return;
                    }
                case PageReady:
                    {
                        // 点「安装」前先探一次：目标程序正开着就别等写文件时才炸
                        if (File.Exists(Program.ExePathIn(_dir))
                            && Program.RunningInstances(_dir).Length > 0)
                        {
                            NextBtn.Text = "结束进程并继续";
                            ShowBlocked(Program.ExePathIn(_dir));
                            return;
                        }
                        StartInstall();
                        return;
                    }
                default:
                    ShowPage(_page + 1);
                    return;
            }
        }

        static string CheckDir(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return "请填写安装位置。";
            if (!Path.IsPathRooted(dir))
                return "安装位置要写完整路径，例如：\nD:\\Apps\\OAIRouter";

            bool created = !Directory.Exists(dir);
            try
            {
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, ".cc-write-test");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
            }
            catch (Exception ex)
            {
                return "无法写入该目录：\n" + ex.Message
                     + "\n\nProgram Files 这类系统目录需要管理员权限，建议改选「为所有用户安装」或装到用户目录。";
            }
            finally
            {
                // 只是试探可写性，别在用户取消时留下一个空目录
                try
                {
                    if (created && Directory.Exists(dir)
                        && Directory.GetFileSystemEntries(dir).Length == 0)
                        Directory.Delete(dir);
                }
                catch { }
            }
            return null;
        }

        // ------------------------------------------------------------ 安装
        void StartInstall()
        {
            _busy = true;
            _blocked = false;
            _blockedPath = null;
            _blockError = null;             // 新一轮安装不带上一轮的旧警示
            Body.Controls.Clear();
            TitleLabel.Text = "正在安装";
            Back.Visible = false;
            NextBtn.Text = "安装";
            NextBtn.Enabled = false;
            CancelBtn.Text = "取消";
            CancelBtn.Enabled = false;

            L(Program.AppName + " 正在安装到：" + _dir, 0, 0, 428, 40);

            // 自绘进度条：跟随派生主色（系统 ProgressBar 是绿的，不跟主题）
            _bar = new ProgressStrip(K, 0, 60, 428, 10);
            Body.Controls.Add(_bar);

            _status = L("准备中…", 0, 78, 428, 160);

            var dir = _dir;
            var mode = _mode;
            var th = new Thread(delegate()
            {
                try
                {
                    Program.DoInstall(dir, mode, _desktop, _start, _autostart, _overwriteConfig, delegate(int pct, string name)
                    {
                        Ui(delegate
                        {
                            if (_bar == null) return;
                            _bar.Value = Math.Min(100, pct);
                            _status.Text = pct + "%　" + name;
                        });
                    });
                    Ui(ShowFinish);
                }
                catch (Exception ex)
                {
                    // 文件被占用（多半是目标程序正开着）→ 转成可操作的「正在运行」页，
                    // 而不是一句「修正后重新运行安装程序」把用户晾在那。
                    // DoInstall 开头的复查抛 StillRunningException（规范 §三.1-7）也算这一类：
                    // 必须按异常类型认，不能只靠 RunningInstances 二次探测 —— 进程可能恰好
                    // 在抛异常与 catch 之间退出，那样会拿着一句空 message 走失败页。
                    var stillRunning = ex is Program.StillRunningException;
                    var msg = ex.Message;
                    var path = Program.QuotedPath(msg);
                    Ui(delegate
                    {
                        if (stillRunning
                            || Program.LooksLikeFileInUse(msg)
                            || (File.Exists(Program.ExePathIn(_dir))
                                && Program.RunningInstances(_dir).Length > 0))
                        {
                            NextBtn.Text = "结束进程并继续";
                            ShowBlocked(path ?? Program.ExePathIn(_dir));
                        }
                        else
                        {
                            ShowFailure(msg);
                        }
                    });
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        void ShowFailure(string msg)
        {
            _busy = false;
            _failed = true;
            Body.Controls.Clear();
            TitleLabel.Text = "安装未完成";
            Back.Visible = false;

            // 主按钮位（左）放「重试」，最右是「关闭」——规范 §二：主按钮在左、取消最右。
            // 这里的主操作是重试，不是关掉重来，别把两者位置弄反。
            NextBtn.Text = "重试";
            NextBtn.Enabled = true;

            L("安装过程中出错：\n\n" + msg + "\n\n"
              + "可以点「重试」再装一次（比如先关掉占用文件的程序），"
              + "或点「关闭」退出后重新运行安装程序。",
              0, 0, 428, 140);

            CancelBtn.Visible = true;
            CancelBtn.Enabled = true;
            CancelBtn.Text = "关闭";
        }

        /// <summary>失败页的「重试」：重新走一遍安装（占用情况重新判定，不静默杀进程）。</summary>
        void RetryInstall()
        {
            _failed = false;
            _blockError = null;            // 上一轮的旧警示不能带进新一轮
            CancelBtn.Text = "取消";
            // 期间程序被打开过就切回占用页，由用户决定要不要结束（规范 §三.1-7：
            // 执行函数只复查、不静默杀进程 —— 与 txtFormat / ClipSync 同款）。
            if (Program.RunningInstances(_dir).Length > 0)
            {
                ShowBlocked(Program.ExePathIn(_dir));
                return;
            }
            StartInstall();
        }

        void ShowFinish()
        {
            _busy = false;
            _done = true;
            Body.Controls.Clear();

            TitleLabel.Text = "安装完成";
            Back.Visible = false;
            CancelBtn.Visible = false;
            NextBtn.Text = "完成";
            NextBtn.Enabled = true;
            LayoutButtons(86);

            // 完成页版式（规范 §三 完成页）：结论行加粗 → 位置行单行截断 → 说明段 →
            // 「立即运行」一个复选项 → 收尾。四个安装器逐字一致，只有「专属说明」那行各自不同。
            int y = Head(Program.AppName + " 已安装在您的计算机上。", 0, 428);
            y = LinePath("安装位置：" + _dir, y);
            y += 10;

            y = Line("作者：" + Program.Author + "　　联系邮箱：" + Program.Email, y, 428);
            y = Line("桌面与开始菜单都有快捷方式，卸载可从「设置 → 应用」里操作。", y, 428);
            y += 12;
            _cbRunNow = Check("立即运行 " + Program.AppName, 0, y, 380, _run);
        }

        /// <summary>
        /// 把回调切回 UI 线程。BeginInvoke 失败（句柄正在销毁的边角）时同步 Invoke 再试一次，
        /// 别让完成/失败回调静默丢掉；两次都失败说明窗口真的没了，只能放弃。
        /// </summary>
        void Ui(Action a)
        {
            try { if (IsHandleCreated) BeginInvoke(a); }
            catch
            {
                try { if (IsHandleCreated && !IsDisposed) Invoke(a); } catch { }
            }
        }

        void ShowBlocked(string path)
        {
            _busy = false;
            _failed = false;
            _blocked = true;
            _blockedPath = path;
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
            NextBtn.Text = "结束进程并继续";
            LayoutButtons(126);          // 这个按钮文字长，需要更宽，否则被折成两行

            Strong("需要写入的文件正被占用：", 0, 0, 428, 22);
            L(_dir, 0, 24, 428, 20);
            if (!string.IsNullOrEmpty(path))
            {
                var p = L(path, 0, 46, 428, 20);
                p.AutoEllipsis = true;      // 长路径单行裁切时给省略号，别硬截成半截
            }

            Strong("请选择一种处理方式：", 0, 80, 428, 22);
            L("· 点「结束进程并继续」—— 自动关闭正在运行的 " + Program.AppName
              + "（未保存的操作会丢失），随后立即继续安装；\n"
              + "· 点「上一步」—— 回到上一页更改设置；\n"
              + "· 若上一步无效，请手动关闭 " + Program.AppName
              + "，再点「结束进程并继续」重试。", 0, 104, 428, 80);

            // 结束失败时留一行页内警示（规范 §二.1：能做成页内提示的就不弹窗）。
            // 与 txtFormat / ClipSync 同款 —— 对话框会被关掉，这行会一直留在页上。
            if (!string.IsNullOrEmpty(_blockError))
            {
                var warn = L(_blockError, 0, 190, 428, 40);
                warn.ForeColor = Theme.Warn;
            }
        }

        /// <summary>结束占用进程后重试安装；仍被占用则停在这一页，让用户手动处理。</summary>
        void EndRunningAndRetry()
        {
            NextBtn.Enabled = false;
            int n = Program.KillRunning(_dir);

            if (Program.RunningInstances(_dir).Length > 0)
            {
                NextBtn.Enabled = true;
                // 规范 §三.1-4：杀不掉要用**自绘对话框**兜住（不许用 MessageBox、
                // 不响提示音），停在原页不继续安装，避免二次失败。
                // 对话框按规范分段（空一行），页内那行行高有限，用不空行的紧凑版。
                string head = "没能结束正在运行的 " + Program.AppName + "（权限不足或进程未响应）。";
                string tail = "请用任务管理器手动结束它，或重启计算机后再运行安装程序。";
                _blockError = head + "\r\n" + tail;
                MsgDialog.Info(this, head + "\n\n" + tail, "没能结束进程");
                ShowBlocked(_blockedPath);
                return;
            }

            if (n > 0) Thread.Sleep(300);   // 等文件句柄真正释放
            _blockError = null;             // 结束成功了，别把失败警示带进安装页
            StartInstall();
        }
    }
}
