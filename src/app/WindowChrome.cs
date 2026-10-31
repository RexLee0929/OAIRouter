using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace OAIRouter;

/// <summary>
/// 标题栏定制（规范 §四）。主窗口与每个对话框都走同一个入口，
/// 避免"一大一小两种标题"。老系统不支持就静默退回默认。
/// </summary>
public static class WindowChrome
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private static int Rgb(int r, int g, int b) => (b << 16) | (g << 8) | r;

    public static void Apply(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            var round = 2;
            var border = Rgb(0xE2, 0xE8, 0xF1);
            var caption = Rgb(0xF2, 0xF6, 0xFB);
            var text = Rgb(0x1B, 0x1B, 0x1B);

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
