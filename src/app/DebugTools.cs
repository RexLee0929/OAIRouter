using System.IO;
using System.Windows;

namespace OAIRouter;

/// <summary>
/// 界面调试命令。只在带特定参数启动时触发，正常运行完全不参与。
///
/// 存在的理由：窗口边框、系统阴影、自绘标题栏这类问题，代码里看不出来，
/// 只能把界面抓成像素再对比。这几个命令就是那套"眼睛"。
///
/// 用法：
///   OAIRouter.exe --test-icons              仅逐键离屏渲染图标并断言可见像素，返回测试退出码
///   OAIRouter.exe --render  &lt;目录&gt;   离屏渲染对话框并执行图标渲染断言（不含系统边框）
///   OAIRouter.exe --shotreal &lt;目录&gt;  抓导入框的屏幕像素（含边框与阴影）
///   OAIRouter.exe --widths              打印每个对话框按内容量出来的宽（宽度的可复现出处）
///   OAIRouter.exe --shotwin  &lt;目录&gt;  抓窗口的整窗图（含标题栏）
///   OAIRouter.exe --dialogs  &lt;目录&gt;  抓对话框的屏幕像素
///   OAIRouter.exe --toast    &lt;目录&gt;  抓提示条的真实落点（提示位置只能看像素）
/// </summary>
public static class DebugTools
{
    /// <summary>在单实例与主窗口启动前执行图标自检，适合本地/CI 回归。</summary>
    public static int? TryRunIconTest(string[] args)
    {
        if (!Has(args, "--test-icons")) return null;

        try
        {
            UiRender.CheckIconRendering();
            Console.WriteLine("图标自检通过：程序图标表中的键均有可见渲染像素");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("图标自检失败：" + ex);
            return 1;
        }
    }

    public static int? TryRun(string[] args)
    {
        if (Has(args, "--render"))
        {
            try
            {
                Accent.Init();
                UiRender.Run(Arg(args, "--render"));
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("图标/界面自检失败：" + ex);
                LogStore.Instance.Error("图标/界面自检", ex.Message);
                return 1;
            }
        }

        if (Has(args, "--shotreal"))
        {
            var dir = Arg(args, "--shotreal");
            UiRender.ShotReal(new ImportWindow(App.Config), Path.Combine(dir, "real-import.png"));
            return 0;
        }

        if (Has(args, "--dialogs"))
        {
            var dir = Arg(args, "--dialogs");
            UiRender.ShotReal(new ImportWindow(App.Config), Path.Combine(dir, "d-import.png"));
            UiRender.ShotReal(new AboutDialog(), Path.Combine(dir, "d-about.png"));
            return 0;
        }

        if (Has(args, "--widths"))
        {
            // 每个框的宽度是量出来的还是写死的，跑一次就知道（§七.2 的可复现出处）
            UiRender.ReportWidths();
            return 0;
        }

        if (Has(args, "--shotwin"))
        {
            var dir = Arg(args, "--shotwin");
            UiRender.ShotWindow(App.Shell!, Path.Combine(dir, "win-main.png"));
            UiRender.ShotWindow(new ImportWindow(App.Config), Path.Combine(dir, "win-import.png"));
            UiRender.ShotWindow(new PricingWindow(), Path.Combine(dir, "win-pricing.png"));
            UiRender.ShotWindow(new ModelsWindow(), Path.Combine(dir, "win-models.png"));
            UiRender.ShotWindow(new PriceSyncWindow(App.Config), Path.Combine(dir, "win-sync.png"));
            UiRender.ShotWindow(new AboutDialog(), Path.Combine(dir, "win-about.png"));
            return 0;
        }

        if (Has(args, "--toast"))
        {
            // 提示条落在哪，只能看像素
            UiRender.ShotToast(Arg(args, "--toast"));
            return 0;
        }

        return null;
    }

    private static bool Has(string[] args, string name)
        => args.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>取参数后面跟的目录，缺省为 shots，并确保目录存在。</summary>
    private static string Arg(string[] args, string name)
    {
        var dir = args
            .SkipWhile(a => !a.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Skip(1)
            .FirstOrDefault() ?? "shots";

        Directory.CreateDirectory(dir);
        return dir;
    }
}
