# DEVELOPMENT

面向开发者：环境、编译、结构、坑。

## 环境

- .NET 10 SDK（主程序 `net10.0-windows`，win-x64）
- 安装器是 `net48` WinForms，引用程序集由 NuGet 包 `Microsoft.NETFramework.ReferenceAssemblies` 自动提供，不需要装 .NET Framework 4.8 Developer Pack
- Python 3（只为本目录下的工具脚本）

## 编译

### 脚本

```bash
python tools/build.py                # 预演：只打印会做什么
python tools/build.py --apply        # 构建 + 同步发布区
python tools/build.py --apply --clean --no-installer   # 可选参数
```

流程三步：

1. `dotnet publish -c Release` 主程序（自包含单文件，产物在 `src/app/bin/Release/net10.0-windows/win-x64/publish/`）
2. 把产物打包成 `src/installer/payload.zip`（排除 `config.json` / `error.log` / `usage.jsonl`，开发机的数据绝不带给用户）
3. `dotnet build -c Release` 安装器，产物 `OAIRouter-<版本>-setup.exe`

所有产物统一复制到项目根 `artifacts/`（整目录重建）。

### 手工分步

```bash
dotnet publish src/app -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
# 再按 tools/build.py 里的 make_payload() 打包 payload.zip（排除 config.json 等运行时文件）
dotnet build src/installer -c Release
```

日常构建一律走 `build.py`，手工分步只在排查问题时用。

### 版本号

`src/version.props` 是唯一来源：主程序和安装器都 `<Import>` 它，安装器文件名 `OAIRouter-$(Version)-setup.exe` 自动跟版本走，程序内显示的版本从程序集 `InformationalVersion` 读。改版本只改这一处。

## 改动范围

- **加一个配置项**：`Config.cs` 加字段与默认值 → 在使用处读取 → 导入窗口的设置勾选列表与导入逻辑同步补上（共四处，漏一处导入导出就对不上）。
- **加一个菜单项**：`AppMenu.cs` 的菜单数据与图标 → `MainWindow.xaml.cs` 的 command 分发。快捷键在 `MainWindow` 的 `PreviewKeyDown`。
- **加一种转发模式**：`ProxyService.cs` 的模式分派 → `Translator.cs` 的翻译实现 → 路由行模式下拉与映射行模式下拉的数据源。
- **改配色**：语义色固定在 `Accent.cs`（错误红 / 警告橙黄），强调色由 `app.ico` 主色 HSL 分桶派生，换图标整套界面跟着换——别把具体色值写死在别处。

## 图标与配色

图标唯一来源是品牌管线（brand 仓库的 `export/OAIRouter/OAIRouter.ico`，矢量源在 `svg/OAIRouter.svg`）。改完设计：

```bash
python tools/sync_icon.py    # 把品牌目录的 ico 拷进项目
```

注意：`sync_icon.py` 目前把图标拷到**项目根**的 `app.ico`，而 csproj 引用的是 `src/app/app.ico` 与 `src/installer/app.ico`（见下面的坑）。拷完需要手动放到这两个位置，改完图标要重新构建，exe 里的资源才会更新。

## 代码结构

```
src/
  version.props     版本与作者信息（唯一来源）
  app/              主程序（WPF + Kestrel，net10.0-windows，自包含单文件）
    ProxyService.cs   核心代理：前缀路由、三种模式、SSE 流转发、请求计数
    Translator.cs     Chat Completions <-> Responses 请求/响应/SSE 翻译
    MainWindow.*      主界面（服务、统计、路由、映射、日志五块）
    AppMenu.cs        菜单数据（五菜单，图标为内联 Path）
    Config.cs         配置模型与存取（config.json、启动备份 10 份、种子数据）
    UsageStats.cs     用量记录（usage.jsonl，2 万条上限）与计价引擎
    PricingWindow.*   价格表编辑（分挡 / 时段 / 有效期）
    PriceSyncWindow.* 价格同步（new-api / LiteLLM / 自定义 JSON）
    ModelsWindow.*    模型库管理（本地增删、从用量补充、恢复默认）
    ImportWindow.*    配置导入（五类勾选）
    Accent.cs         强调色：从 app.ico 主色派生
    Brand.cs          品牌常量：名称、标语、作者、邮箱、版本
    Autostart.cs      开机自启（HKCU Run 键，"exe" --tray）
    LogStore.cs       日志（内存列表 + logs\app.log，2 MB 轮转，密钥打码）
    UpstreamError.cs  上游英文报错离线翻译成中文（15 条正则）
    TrayMenu.cs       自绘托盘右键菜单
    Toast / ConfirmDialog / AboutDialog   自绘通知 / 确认框 / 关于
    WindowChrome.cs   DWM 标题栏定制（圆角、边框与标题栏颜色）
  installer/        安装 / 卸载向导（WinForms，net48）
    WizardShell.cs    向导外壳（页面切换、按钮布局）
    InstallForm.cs    安装流程各页（模式 / 位置 / 任务 / 占用 / 完成）
    UninstallForm.cs  卸载各页（确认 / 正在运行 / 残留）
    Program.cs        payload 解包、快捷方式、注册表（卸载项固定写 64 位视图）
    Controls.cs       自绘控件
    MsgDialog.cs      自绘对话框（Info / Ask）
    Theme.cs          安装器配色
tools/
  build.py          构建入口（publish -> payload.zip -> 安装器 -> artifacts）
  sync_to_code.py   编辑区 -> 发布区单向同步（按哈希，默认预演）
  sync_icon.py      品牌图标搬运
  shot_installer.py 安装器逐页截图（不点「安装」，不写磁盘）
```

## 界面怎么验证

主窗口没有自动化截图工具，改动界面后手动跑一遍核心链路：启动服务 → 客户端发一条流式请求 → 看概览与用量 → 停止服务。

安装器界面用离屏截图确认：

```bash
python tools/shot_installer.py artifacts/OAIRouter-<版本>-setup.exe <输出目录> <标签>
```

逐页点「下一步」抓 6 页截图，**不点「安装」、不写磁盘**，可以放心对未发布的包跑。

## 坑

- **安装器是 32 位进程，写注册表必须显式指定 64 位视图**：AnyCPU + Prefer32Bit 下 `RegistryView.Default` 会落到 `WOW6432Node`，64 位「设置 → 应用」读不到，卸载项等于没写。写入统一用 64 位视图；读取时 64 / 32 位两个视图都看（`Program.OpenUninstallKey`）。
- **WinForms 的 TextBox 只认 CRLF**：内嵌的 LICENSE 若是 LF 行尾，许可页会整篇挤成一段。展示前统一 `ToCrlf()`（`Program.cs`）。
- **配置与数据全在 exe 所在目录**：`config.json`、`usage.jsonl`、`logs`、`backups` 都跟着程序走，绿色好备份，但「为所有用户」装进 Program Files 后普通权限写不进去。打包时 `build.py` 按文件名黑名单排除这三类运行时文件，别把开发机数据带进 payload。
- **模型名匹配区分大小写，路由前缀不区分**：映射表 `Alias` 精确比对，路由前缀忽略大小写。两处口径不一致是刻意的（前缀是地址的一部分），写文档和排查问题时别混。
- **流式转发不能设整体超时**：SSE 可能持续几分钟，`HttpClient.Timeout` 必须无限，只靠 20 秒连接超时 + 5 分钟连接池兜底。改动 `ProxyService` 时别顺手加回超时。
- **单实例 Mutex 名按安装目录散列**：`OAIRouter.<目录MD5前8位>.mutex`，免安装版与安装版互不冲突，可以同时各跑一个。再次启动通过 `EventWaitHandle` 唤醒已有实例，不是开新窗口。
- **`sync_icon.py` 拷贝目标与 csproj 引用不一致**：脚本把图标拷到项目根，csproj 引用的是 `src/app/app.ico` 与 `src/installer/app.ico`，拷完要手动挪。

## 同步到发布区

编辑区改代码与文档，发布区是另一个目录下的 git 仓库，两侧靠脚本单向同步：

```bash
python tools/sync_to_code.py            # 预演：只打印计划
python tools/sync_to_code.py --apply    # 真正写入
python tools/sync_to_code.py --apply --prune   # 源里删了的文件，发布区也删
```

- 白名单硬编码在脚本顶部（`SOURCE_FILES` / `SOURCE_DIRS`）：新增根目录 md 或新目录**必须加进列表**，否则永远同步不过去且不报错。加完先跑一次预演确认出现在计划里。
- 发布区文件在两次同步之间被外部改过（哈希对不上）→ 报冲突并跳过，绝不静默覆盖；确认要覆盖加 `--force`。发布区是 git 仓库，回滚靠 `git checkout`，脚本不留备份目录。
- `--with-build` 可把编译产物（主程序 exe、runtimes、安装包）平铺同步到发布区根，日常发布建议产物只走 GitHub Releases、不进仓库。
