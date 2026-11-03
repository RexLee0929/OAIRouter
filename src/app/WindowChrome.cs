using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace OAIRouter;

/// <summary>
/// 标题栏定制（规范 §四）。主窗口与每个对话框都走同一个入口，
/// 避免"一大一小两种标题"。老系统不支持就静默退回默认。
///
/// 做三件 DWM 允许的事：圆角、标题栏配色、强制浅色边框。
/// 最后一条是给「系统深色模式下小窗口底部那条深色边」用的：那条边是系统画的框，
/// 把窗口的深色模式关掉（DWMWA_USE_IMMERSIVE_DARK_MODE = 0）它才会跟着变浅 ——
/// 不必像以前那样把原生标题栏拆掉自绘。
/// </summary>
public static class WindowChrome
{
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int Size; public RECT Monitor, Work; public int Flags; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    public static void CenterOnOwner(Window window)
    {
        if (window.WindowStartupLocation != WindowStartupLocation.CenterOwner ||
            window.Owner is not { IsVisible: true, WindowState: not WindowState.Minimized } owner) return;

        var hwnd = new WindowInteropHelper(window).Handle;
        var ownerHwnd = new WindowInteropHelper(owner).Handle;
        if (hwnd == IntPtr.Zero || ownerHwnd == IntPtr.Zero ||
            !GetWindowRect(hwnd, out var bounds) || !GetWindowRect(ownerHwnd, out var parent)) return;

        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(ownerHwnd, 2), ref info)) return;
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        var x = parent.Left + (parent.Right - parent.Left - width) / 2;
        var y = parent.Top + (parent.Bottom - parent.Top - height) / 2;
        x = Math.Max(info.Work.Left, Math.Min(x, info.Work.Right - width));
        y = Math.Max(info.Work.Top, Math.Min(y, info.Work.Bottom - height));
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010);
    }

    private static int Rgb(int r, int g, int b) => (b << 16) | (g << 8) | r;

    public static void Apply(Window window, bool hideBorder = false)
    {
        ApplyCore(window, hideBorder);

        window.SourceInitialized += (_, _) => ApplyCore(window, hideBorder);
        window.Loaded += (_, _) => ApplyCore(window, hideBorder);
    }

    /// <summary>
    /// 窗口矩形与「真实可见边界」的差值，即系统阴影厚度（底部通常 9px）。
    /// 用于诊断「窗口边缘那条深色带」到底来自阴影还是内容。
    /// </summary>
    public static (int left, int top, int right, int bottom) ShadowThickness(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return (0, 0, 0, 0);
            if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out var frame, Marshal.SizeOf<RECT>()) != 0)
                return (0, 0, 0, 0);

            GetWindowRect(hwnd, out var full);
            return (frame.Left - full.Left, frame.Top - full.Top,
                    full.Right - frame.Right, full.Bottom - frame.Bottom);
        }
        catch
        {
            return (0, 0, 0, 0);
        }
    }

    private static void ApplyCore(Window window, bool hideBorder)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            var round = 2;
            var lightFrame = 0;              // 0 = 浅色边框：程序是浅色界面，别让系统深色模式把框线染黑
            // 系统的「无边框」在部分版本/缩放下仍会留下深色边；与其他程序一致显式着浅色，
            // 对上游表单使用窗口背景色，使原生细边与背景融合，同时保留系统标题栏和圆角。
            var border = hideBorder ? Rgb(0xF2, 0xF6, 0xFB) : Rgb(0xE2, 0xE8, 0xF1);
            var caption = Rgb(0xF2, 0xF6, 0xFB);
            var text = Rgb(0x1B, 0x1B, 0x1B);

            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref lightFrame, sizeof(int));
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));
            DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref border, sizeof(int));
            DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(int));
            DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref text, sizeof(int));
        }
        catch
        {
            // 老系统不支持，保持系统默认外观
        }
    }
}
