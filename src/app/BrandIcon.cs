using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OAIRouter;

/// <summary>
/// 程序图标的唯一取用入口（规范 §二「图标取帧」/ §十三.8）。
///
/// app.ico 里有 16/24/32/48/64/128/256 七帧，但 WPF 的 ICO 解码器会重排帧序，
/// 首帧实测是 48×48 —— <c>Icon="app.ico"</c>、<c>BitmapFrame.Create(ico)</c> 拿到的都是它，
/// 放到 150% DPI 的标题栏、任务栏位置上会被放大发虚。所以这里是「谁要多大就取多大的原始帧」：
/// 窗口图标取最大帧，托盘取外壳当前真正要的那一档。
/// </summary>
public static class BrandIcon
{
    /// <summary>
    /// 显式写程序集名：相对的 <c>pack://application:,,,/app.ico</c> 按**入口程序集**解析，
    /// 在 ui_render 这类外壳工具里会抛「找不到资源 app.ico」（规范 §二）。
    /// </summary>
    public const string Uri = "pack://application:,,,/OAIRouter;component/app.ico";

    private static ImageSource? _source;

    /// <summary>托盘图标的底流。Icon(Stream) 要求流在图标存活期内有效，这里由静态字段一直持有。</summary>
    private static MemoryStream? _bytes;

    /// <summary>窗口 / 任务栏 / 启动画面用的图标：ico 里面积最大的那一帧（本程序是 256）。</summary>
    public static ImageSource Source => _source ??= LoadLargestFrame();

    /// <summary>
    /// 托盘用的 GDI 图标：按 <paramref name="size"/> 取原始帧，不交给系统缩放。
    /// 尺寸由调用方问外壳要（<see cref="System.Windows.Forms.SystemInformation.SmallIconSize"/>），
    /// 96 DPI 是 16、150% 是 24 —— ico 里都有对应帧，取到的就是原图。
    /// </summary>
    public static System.Drawing.Icon DrawingIcon(int size)
    {
        var stream = Bytes();
        if (stream is null) return (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();

        stream.Position = 0;
        using var icon = new System.Drawing.Icon(stream, size, size);
        return (System.Drawing.Icon)icon.Clone();
    }

    private static Stream? OpenResource()
    {
        try
        {
            return Application.GetResourceStream(new Uri(Uri))?.Stream;
        }
        catch
        {
            return null;   // 设计器预览里 Application.Current 为空，不该让整窗崩掉
        }
    }

    private static MemoryStream? Bytes()
    {
        if (_bytes is not null) return _bytes;

        var stream = OpenResource();
        if (stream is null) return null;

        using (stream)
        {
            var ms = new MemoryStream();
            stream.CopyTo(ms);
            ms.Position = 0;
            _bytes = ms;
        }

        return _bytes;
    }

    private static ImageSource LoadLargestFrame()
    {
        var stream = OpenResource();
        if (stream is null) return Blank();

        using (stream)
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var best = decoder.Frames
                .OrderByDescending(f => (long)f.PixelWidth * f.PixelHeight)
                .FirstOrDefault();

            if (best is not null)
            {
                if (best.CanFreeze && !best.IsFrozen) best.Freeze();
                return best;
            }
        }

        return Blank();
    }

    /// <summary>取不到资源时的兜底：1×1 透明。图标取不到是小事，别把启动搞崩。</summary>
    private static ImageSource Blank()
    {
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
        bitmap.Freeze();
        return bitmap;
    }
}
