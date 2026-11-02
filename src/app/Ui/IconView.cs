using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OAIRouter.Ui;

/// <summary>
/// XAML 里的图标位：只写键名，几何在 <see cref="Icons"/> 那一张表里。
/// <code>&lt;ui:IconView Icon="copy" IconSize="13" IconBrush="{StaticResource SubBrush}" /&gt;</code>
///
/// 为什么不用 Path：<c>Path</c> 的 <c>Stretch</c> 是按墨迹外框缩放的（缩放比 = 尺寸 ÷ 墨迹宽），
/// 全套会在 0.73~2.67 之间浮动，同一排图标粗细不齐（《图标规范》§三.3）。
/// 这里走 <see cref="Icons.Get"/>，缩放比恒为 尺寸/24。
///
/// 三个属性都是依赖属性，所以模板里的 Trigger 能直接改
/// <see cref="IconBrush"/>（例：行内删除按钮悬停变红），不用为每种状态各画一份。
/// </summary>
public sealed class IconView : ContentControl
{
    /// <summary>正文色（Theme.xaml 的 TextBrush）：不指定画刷时的默认值。</summary>
    static readonly Brush DefaultBrush = Freeze(Color.FromRgb(0x1B, 0x1B, 0x1B));

    public IconView()
    {
        IsHitTestVisible = false;          // 图标只是装饰，点击要落到父按钮上
        Rebuild();
    }

    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(nameof(Icon), typeof(string), typeof(IconView),
            new PropertyMetadata("", OnChanged));

    public static readonly DependencyProperty IconSizeProperty =
        DependencyProperty.Register(nameof(IconSize), typeof(double), typeof(IconView),
            new PropertyMetadata(16d, OnChanged));

    public static readonly DependencyProperty IconBrushProperty =
        DependencyProperty.Register(nameof(IconBrush), typeof(Brush), typeof(IconView),
            new PropertyMetadata(null, OnChanged));

    /// <summary>图标键名（icons/Unified 的文件名）。</summary>
    public string Icon
    {
        get => (string)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>画多大。只用 13 / 14 / 16 / 18 / 20 / 24 / 32 这几档（§三.4）。</summary>
    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    /// <summary>描边色。默认正文色；强调/语义色由调用方给（与文字同源，§四.1）。</summary>
    public Brush IconBrush
    {
        get => (Brush?)GetValue(IconBrushProperty) ?? DefaultBrush;
        set => SetValue(IconBrushProperty, value);
    }

    static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((IconView)d).Rebuild();

    void Rebuild()
        => Content = string.IsNullOrEmpty(Icon) ? null : Icons.Get(Icon, IconBrush, IconSize);

    static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
