using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace OAIRouter;

public sealed record TrayEntry(string? Text, string? Icon, Action? Action)
{
    public static TrayEntry Sep() => new(null, null, null);
    public static TrayEntry Of(string text, string icon, Action action) => new(text, icon, action);
}

/// <summary>
/// 托盘右键菜单。不用 WinForms 的 ContextMenuStrip（外观是系统灰底，和主体两张皮），
/// 也不直接用 WPF ContextMenu（主窗口隐藏时定位与关闭都不可靠）。
/// 这里用一个轻量无边框窗口承载，外观与菜单栏弹层完全一致，失焦即关。
/// </summary>
public sealed class TrayMenu : Window
{
    private readonly IReadOnlyList<TrayEntry> _entries;
    private bool _closing;

    private TrayMenu(IReadOnlyList<TrayEntry> entries)
    {
        _entries = entries;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = new FontFamily("Microsoft YaHei UI");

        Content = BuildCard();
        Deactivated += (_, _) => SafeClose();
    }

    private UIElement BuildCard()
    {
        var panel = new StackPanel();

        foreach (var entry in _entries)
        {
            if (entry.Text is null)
            {
                panel.Children.Add(new Rectangle
                {
                    Height = 1,
                    Fill = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF1)),
                    Margin = new Thickness(10, 5, 10, 5),
                });
                continue;
            }

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var icon = new Path
            {
                Data = Geometry.Parse(Icons.Paths.TryGetValue(entry.Icon ?? "", out var d) ? d : Icons.Paths["arrow"]),
                Stroke = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1B)),
                StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Width = 16,
                Height = 16,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(icon, 0);
            grid.Children.Add(icon);

            var text = new TextBlock
            {
                Text = entry.Text,
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(9, 0, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1B)),
            };
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            var button = new Button
            {
                Content = grid,
                Style = (Style)Application.Current.FindResource("MenuItemButton"),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            var action = entry.Action;
            button.Click += (_, _) =>
            {
                SafeClose();
                // 等这一轮关闭流程走完再执行动作，避免在窗口关闭期间去 Show 主窗口
                if (action is not null) Dispatcher.BeginInvoke(action, DispatcherPriority.Background);
            };
            panel.Children.Add(button);
        }

        return new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF1)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(5),
            Margin = new Thickness(10),
            MinWidth = 190,
            Child = panel,
            Effect = new DropShadowEffect
            {
                BlurRadius = 14,
                ShadowDepth = 2,
                Direction = 270,
                Opacity = 0.14,
                Color = Colors.Black,
            },
        };
    }

    /// <summary>Deactivated 与点击可能同时触发，重复 Close 会抛「窗口正在关闭」。</summary>
    private void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        try { Close(); } catch { }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        base.OnClosing(e);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var dpi = VisualTreeHelper.GetDpi(this);
        var cursor = System.Windows.Forms.Cursor.Position;
        var x = cursor.X / dpi.DpiScaleX;
        var y = cursor.Y / dpi.DpiScaleY;

        var area = SystemParameters.WorkArea;
        var w = ActualWidth > 0 ? ActualWidth : 210;
        var h = ActualHeight > 0 ? ActualHeight : 220;

        // 靠近屏幕边缘时朝内侧弹出
        if (x + w > area.Right) x = area.Right - w;
        if (y + h > area.Bottom) y = y - h;
        if (x < area.Left) x = area.Left;
        if (y < area.Top) y = area.Top;

        Left = x;
        Top = y;

        Activate();
        Focus();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) SafeClose();
        base.OnPreviewKeyDown(e);
    }

    public static void Show(IReadOnlyList<TrayEntry> entries)
    {
        var menu = new TrayMenu(entries);
        menu.Show();
    }
}
