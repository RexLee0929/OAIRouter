using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OAIRouter.Ui;

/// <summary>对话框的语气。决定图标形状与配色，不决定布局。</summary>
public enum DialogKind
{
    Info,
    Question,
    Warning,
    Danger,
    Success,
}

/// <summary>
/// 对话框外壳：图标 + 标题 + 正文 + 按钮栏。
/// 外观在 Theme.xaml 的 DialogShell 模板里，这里只管状态与事件。
/// 用 ContentControl 而不是 UserControl，内容才会留在调用方的 namescope 里，
/// 调用方才能用 x:Name 直接引用自己的控件。
/// </summary>
public class DialogShell : ContentControl
{
    /// <summary>
    /// 标题行字号：与 Theme.xaml 里 H1 的 FontSize 一致。两个值必须同源 ——
    /// 改一个忘了另一个，按内容量宽就会差一截（量窄了标题折行、量宽了空一片）。
    /// </summary>
    public const double HeadlineSize = 14;

    /// <summary>标题行左侧的图标列：26px 圆形 + 10px 间距（Theme.xaml 的 DialogShell 模板）。</summary>
    public const double IconColumn = 36;

    /// <summary>
    /// 卡片自身的左右占位：外边距 18×2 + 内边距 16×2 + 边框 1×2（同上，Theme.xaml 模板）。
    /// 按内容量宽时加在「最长一行」两侧，就是这一屏该有的宽。
    /// </summary>
    public const double HorizontalInset = 70;

    public DialogShell()
    {
        ApplyKind(DialogKind.Info);
    }

    public event RoutedEventHandler? PrimaryInvoked;
    public event RoutedEventHandler? SecondaryInvoked;

    /// <summary>主按钮（右侧、蓝底）。模板套用后可用。</summary>
    public Button? PrimaryButton { get; private set; }

    /// <summary>次按钮（左侧、白底）。模板套用后可用。</summary>
    public Button? SecondaryButton { get; private set; }

    /// <summary>
    /// 按钮栏的自然宽（含按钮之间 8px 的间距）。文案很短时窗口宽由它兜底 ——
    /// 只量文字会把按钮挤出卡片，横着就没有「按内容定宽」这回事了。
    /// </summary>
    public double ButtonRowWidth
    {
        get
        {
            var width = 0d;
            if (SecondaryButton is not null) width += SecondaryButton.Width;
            if (PrimaryButton is not null) width += PrimaryButton.Width + (SecondaryButton is null ? 0 : 8);
            return width;
        }
    }

    /* ----------------------------- 依赖属性 ----------------------------- */

    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(nameof(Kind), typeof(DialogKind), typeof(DialogShell),
            new PropertyMetadata(DialogKind.Info, (d, e) => ((DialogShell)d).ApplyKind((DialogKind)e.NewValue)));

    public static readonly DependencyProperty HeadlineProperty =
        DependencyProperty.Register(nameof(Headline), typeof(string), typeof(DialogShell),
            new PropertyMetadata(""));

    public static readonly DependencyProperty MessageProperty =
        DependencyProperty.Register(nameof(Message), typeof(string), typeof(DialogShell),
            new PropertyMetadata(""));

    public static readonly DependencyProperty PrimaryTextProperty =
        DependencyProperty.Register(nameof(PrimaryText), typeof(string), typeof(DialogShell),
            new PropertyMetadata("确定", OnButtonTextChanged));

    public static readonly DependencyProperty SecondaryTextProperty =
        DependencyProperty.Register(nameof(SecondaryText), typeof(string), typeof(DialogShell),
            new PropertyMetadata("取消", OnButtonTextChanged));

    /// <summary>
    /// 主按钮是不是「危险动作」。与 <see cref="Kind"/> 分开：错误框的图标是红的，
    /// 但「打开日志」不危险，不该染红、也不该把 Enter 挡掉（§七.3）。
    /// </summary>
    public static readonly DependencyProperty DangerActionProperty =
        DependencyProperty.Register(nameof(DangerAction), typeof(bool), typeof(DialogShell),
            new PropertyMetadata(false, OnButtonTextChanged));

    private static readonly DependencyPropertyKey IconPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(Icon), typeof(UIElement), typeof(DialogShell),
            new PropertyMetadata(null));

    private static readonly DependencyPropertyKey IconBackdropPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(IconBackdrop), typeof(Brush), typeof(DialogShell),
            new PropertyMetadata(null));

    public static readonly DependencyProperty IconProperty = IconPropertyKey.DependencyProperty;
    public static readonly DependencyProperty IconBackdropProperty = IconBackdropPropertyKey.DependencyProperty;

    public DialogKind Kind
    {
        get => (DialogKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public string Headline
    {
        get => (string)GetValue(HeadlineProperty);
        set => SetValue(HeadlineProperty, value);
    }

    /// <summary>纯文本正文。设了它就不用 Content。</summary>
    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public string PrimaryText
    {
        get => (string)GetValue(PrimaryTextProperty);
        set => SetValue(PrimaryTextProperty, value);
    }

    public string SecondaryText
    {
        get => (string)GetValue(SecondaryTextProperty);
        set => SetValue(SecondaryTextProperty, value);
    }

    public bool DangerAction
    {
        get => (bool)GetValue(DangerActionProperty);
        set => SetValue(DangerActionProperty, value);
    }

    /// <summary>标题行左侧的图标（14px，画在 26px 圆底里；键名见 <see cref="IconKeyFor"/>）。</summary>
    public UIElement? Icon => (UIElement?)GetValue(IconProperty);

    public Brush? IconBackdrop => (Brush?)GetValue(IconBackdropProperty);

    /* ----------------------------- 模板 ----------------------------- */

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        RebuildButtons();
    }

    private static void OnButtonTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((DialogShell)d).RebuildButtons();

    private void ApplyKind(DialogKind kind)
    {
        // 图标与标题同色同源：形状取表里的键，颜色取同一个语义色（§四.1）
        SetValue(IconPropertyKey, Icons.Get(IconKeyFor(kind), BrushFor(kind, soft: false), 14));
        SetValue(IconBackdropPropertyKey, BrushFor(kind, soft: true));
    }

    private void RebuildButtons()
    {
        if (GetTemplateChild("PART_Buttons") is not StackPanel bar) return;

        bar.Children.Clear();
        PrimaryButton = null;
        SecondaryButton = null;

        if (!string.IsNullOrWhiteSpace(SecondaryText))
        {
            SecondaryButton = new Button
            {
                Content = SecondaryText,
                Style = (Style)FindResource("GhostButton"),
                Width = 84,
            };
            SecondaryButton.Click += (_, e) => SecondaryInvoked?.Invoke(this, e);
            bar.Children.Add(SecondaryButton);
        }

        if (!string.IsNullOrWhiteSpace(PrimaryText))
        {
            PrimaryButton = new Button
            {
                Content = PrimaryText,
                Style = (Style)FindResource("PrimaryButton"),
                Width = 96,
                Margin = SecondaryButton is null ? new Thickness(0) : new Thickness(8, 0, 0, 0),
            };
            PrimaryButton.Click += (_, e) => PrimaryInvoked?.Invoke(this, e);

            // 危险动作的主按钮必须是语义红，而不是强调色
            if (DangerAction)
            {
                PrimaryButton.Background = new SolidColorBrush(Accent.Error);
                PrimaryButton.Foreground = Brushes.White;
            }

            bar.Children.Add(PrimaryButton);
        }
    }

    /// <summary>四种语气各自对应的图标键（《图标规范》§七「对话框」那一行）。</summary>
    private static string IconKeyFor(DialogKind kind) => kind switch
    {
        DialogKind.Question => "question",
        DialogKind.Warning => "warning",
        DialogKind.Danger => "error",
        DialogKind.Success => "success",
        _ => "info",
    };

    /// <summary>语义色不参与强调色派生，与 Accent.Error / Accent.Warn 同源。</summary>
    private static Brush BrushFor(DialogKind kind, bool soft)
    {
        return kind switch
        {
            DialogKind.Danger => Freeze(Accent.Error, soft ? 0.10 : 1.0),
            DialogKind.Warning => Freeze(Accent.Warn, soft ? 0.12 : 1.0),
            _ => soft ? Accent.SoftBrush : Accent.MainBrush,
        };
    }

    private static SolidColorBrush Freeze(Color color, double alpha)
    {
        var brush = new SolidColorBrush(color) { Opacity = alpha };
        brush.Freeze();
        return brush;
    }
}
