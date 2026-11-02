using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace OAIRouter;

/// <summary>
/// 界面抓图工具，供 <see cref="DebugTools"/> 调用。
///
/// 两种抓法各有用途，不能互相替代：
///   Shot        —— 离屏渲染，只画 WPF 内容树，看不到系统边框与阴影
///   ShotReal    —— 读屏幕像素，能看到真实观感（边框、阴影都在）
///   ShotWindow  —— PrintWindow，含系统标题栏，但抓不到 DWM 单独合成的那层
///
/// 之前判断「窗口底部黑线」时就是因为只用离屏渲染，才漏掉了系统边框。
/// </summary>
public static class UiRender
{
    /// <summary>离屏渲染三个对话框。</summary>
    public static void Run(string dir)
    {
        Directory.CreateDirectory(dir);
        CheckIconRendering(dir);

        Shot(new AboutDialog(), Path.Combine(dir, "about.png"));
        Shot(new ImportWindow(App.Config), Path.Combine(dir, "import.png"));
        Shot(ErrorDialog.ForRender(), Path.Combine(dir, "error.png"));
    }

    /// <summary>
    /// 对当前程序图标表中的所有实际使用键逐个执行真实 WPF 离屏渲染，检查输出像素非空。
    /// 只断言 Geometry.Parse / Icons.Get 未抛异常会漏掉「成功解析但完全不显示」的路径错误。
    /// </summary>
    public static void CheckIconRendering(string? outputDir = null)
    {
        Icons.FailOnMissing = true;
        var failures = new List<string>();
        int passed = 0;
        int total = 0;

        try
        {
            Icons.Get("__icon-selftest-missing-key__", Brushes.Black, 16);
            failures.Add("未知图标键没有触发自检失败（FailOnMissing 未生效）");
        }
        catch (KeyNotFoundException)
        {
            // 预期：未知键不能由 arrow-right 兜底图掩盖。
        }

        foreach (var key in Icons.Keys)
        {
            total++;
            try
            {
                var element = Icons.Get(key, Brushes.Black, 16) as FrameworkElement;
                if (element is null)
                {
                    failures.Add($"{key}: Icons.Get 返回 null");
                    continue;
                }

                element.Measure(new Size(16, 16));
                element.Arrange(new Rect(0, 0, 16, 16));
                element.UpdateLayout();

                var bitmap = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(element);
                var pixels = new byte[64 * 64 * 4];
                bitmap.CopyPixels(pixels, 64 * 4, 0);

                int ink = 0;
                int minX = 64, minY = 64, maxX = -1, maxY = -1;
                for (int y = 0; y < 64; y++)
                {
                    for (int x = 0; x < 64; x++)
                    {
                        byte alpha = pixels[(y * 64 + x) * 4 + 3];
                        if (alpha <= 8) continue;
                        ink++;
                        minX = Math.Min(minX, x);
                        minY = Math.Min(minY, y);
                        maxX = Math.Max(maxX, x);
                        maxY = Math.Max(maxY, y);
                    }
                }

                if (ink == 0 || maxX <= minX || maxY <= minY)
                {
                    failures.Add($"{key}: 离屏渲染没有可见墨迹（pixels={ink}, bounds={minX},{minY}..{maxX},{maxY}）");
                    continue;
                }

                if (outputDir is not null)
                    Save(bitmap, Path.Combine(outputDir, $"icon-{key}.png"));
                Console.WriteLine($"icon {key}: {ink} pixels, bounds {minX},{minY}..{maxX},{maxY}");
                passed++;
            }
            catch (Exception ex)
            {
                failures.Add($"{key}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine($"图标离屏渲染 {passed}/{total}");
        foreach (var failure in failures)
            Console.Error.WriteLine("! 图标 " + failure);

        if (failures.Count > 0)
            throw new InvalidOperationException($"图标渲染自检失败 {failures.Count}/{total}: {string.Join(" | ", failures)}");

        if (total == 0)
            throw new InvalidOperationException("图标渲染自检失败：Icons 表为空");
    }

    /// <summary>
    /// 打印每个对话框按内容量出来的宽，以及「是哪一行把窗口撑到这个宽」。
    /// 这是宽度的可复现出处（规范 §七.2）：改完文案跑一次，宽度应当跟着变；
    /// 不变就说明又有人把它写死了。
    /// </summary>
    public static void ReportWidths()
    {
        (string Name, Func<Window> Make)[] dialogs =
        [
            ("关于", () => new AboutDialog()),
            ("导入", () => new ImportWindow(App.Config)),
            ("错误", () => ErrorDialog.ForRender()),
        ];

        foreach (var (name, make) in dialogs)
        {
            var window = make();
            try
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -10000;
                window.Top = -10000;
                window.ShowInTaskbar = false;
                window.Show();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                var evidence = window is Ui.DialogWindow dialog ? dialog.WidthEvidence : "";
                LogStore.Instance.Info("宽度",
                    $"{name} {Math.Round(window.ActualWidth)}×{Math.Round(window.ActualHeight)}　←　{evidence}");
            }
            finally
            {
                window.Close();
            }
        }
    }

    /// <summary>离屏渲染单个窗口：把窗口挪到屏幕外，不打扰正在用电脑的人。</summary>
    private static void Shot(Window window, string path)
    {
        try
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -10000;
            window.Top = -10000;
            window.ShowInTaskbar = false;

            window.Show();
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            var width = (int)Math.Ceiling(window.ActualWidth);
            var height = (int)Math.Ceiling(window.ActualHeight);
            if (width <= 0 || height <= 0) return;

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            Save(bitmap, path);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// 读屏幕像素抓窗口（含 DWM 边框与阴影），并输出底部逐行亮度，
    /// 用于判断「窗口边缘那条深色带」到底是阴影、边框还是内容。
    /// </summary>
    public static void ShotReal(Window window, string path)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = 200;
        window.Top = 160;
        window.Topmost = true;
        window.Show();
        window.Activate();
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Thread.Sleep(400);   // 等 DWM 完成合成

        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect)) return;

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0) return;

            using var bitmap = new System.Drawing.Bitmap(width, height);
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            var destDc = graphics.GetHdc();
            var screenDc = GetDC(IntPtr.Zero);
            BitBlt(destDc, 0, 0, width, height, screenDc, rect.Left, rect.Top, SrcCopy);
            graphics.ReleaseHdc(destDc);
            ReleaseDC(IntPtr.Zero, screenDc);
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);

            var shadow = WindowChrome.ShadowThickness(window);
            LogStore.Instance.Info("抓屏", $"阴影 L{shadow.left} T{shadow.top} R{shadow.right} B{shadow.bottom}");

            for (var y = Math.Max(0, height - 8); y < height; y++)
            {
                var darkest = 255;
                for (var x = 0; x < width; x++)
                {
                    var pixel = bitmap.GetPixel(x, y);
                    darkest = Math.Min(darkest, (pixel.R + pixel.G + pixel.B) / 3);
                }

                LogStore.Instance.Info("抓屏", $"行 {y - height}｜最暗 {darkest}");
            }
        }
        finally
        {
            window.Topmost = false;
            if (!ReferenceEquals(window, Application.Current.MainWindow)) window.Close();
        }
    }

    /// <summary>PrintWindow 抓整窗（含系统标题栏），用于确认标题栏与按钮布局。</summary>
    public static void ShotWindow(Window window, string path)
    {
        if (!window.IsVisible)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = 60;
            window.Top = 60;
            window.Show();
        }

        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect)) return;

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0) return;

            using var bitmap = new System.Drawing.Bitmap(width, height);
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            var hdc = graphics.GetHdc();
            PrintWindow(hwnd, hdc, 2);   // 2 = PW_RENDERFULLCONTENT
            graphics.ReleaseHdc(hdc);
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        finally
        {
            // 主窗口属于应用本身，抓完不能关
            if (!ReferenceEquals(window, Application.Current.MainWindow)) window.Close();
        }
    }

    private static void Save(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>
    /// 抓一条提示条的真实落点：弹一条样例提示，等它摆好，再把主窗口所在的屏幕区域
    /// 连提示一起抓下来。提示条的位置只能看像素，代码里看不出来。
    /// </summary>
    public static void ShotToast(string dir)
    {
        Directory.CreateDirectory(dir);

        var shell = App.Shell;
        if (shell is null) return;

        shell.Dispatcher.Invoke(() => shell.ShowToast("服务已启动，监听 http://127.0.0.1:7891"));

        // 等淡入与布局（Toast 在 OnContentRendered 里才定位）
        for (var i = 0; i < 20; i++)
        {
            shell.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(60);
        }

        var hwnd = new WindowInteropHelper(shell).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect)) return;

        ShotRegion(rect.Left - 24, rect.Top - 24,
                   rect.Right - rect.Left + 48, rect.Bottom - rect.Top + 48,
                   Path.Combine(dir, "toast.png"));
    }

    private static void ShotRegion(int x, int y, int width, int height, string path)
    {
        if (width <= 0 || height <= 0) return;

        using var bitmap = new System.Drawing.Bitmap(width, height);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        var destDc = graphics.GetHdc();
        var screenDc = GetDC(IntPtr.Zero);
        BitBlt(destDc, 0, 0, width, height, screenDc, x, y, SrcCopy);
        graphics.ReleaseHdc(destDc);
        ReleaseDC(IntPtr.Zero, screenDc);
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    /* ----------------------------- Win32 ----------------------------- */

    private const int SrcCopy = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int x, int y, int w, int h,
                                      IntPtr hdcSrc, int xSrc, int ySrc, int rop);
}
