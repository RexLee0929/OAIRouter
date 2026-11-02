using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace OAIRouter.Ui;

/// <summary>
/// 对话框的键盘与焦点规则，一处定义、所有对话框共用：
/// Esc 走次按钮、Enter 走主按钮、危险操作默认焦点落在次按钮且不响应 Enter。
/// </summary>
public static class DialogHost
{
    /// <summary>
    /// 给对话框装上统一行为。在构造函数里 InitializeComponent 之后调用一次。
    /// </summary>
    /// <param name="window">承载对话框的窗口</param>
    /// <param name="shell">对话框外壳，用于取主/次按钮</param>
    /// <param name="danger">危险操作：焦点给次按钮，Enter 不触发主按钮</param>
    public static void Setup(Window window, DialogShell shell, bool danger = false)
    {
        // 标题栏与边框由 DialogWindow 统一处理，这里只管键盘与焦点
        var primary = shell.PrimaryButton;
        var secondary = shell.SecondaryButton;

        if (primary is not null && !danger)
        {
            // 非危险：Enter 即确认
            primary.IsDefault = true;
        }

        if (secondary is not null)
        {
            // Esc 一律走「取消/关闭」，与 Windows 通用约定一致
            secondary.IsCancel = true;
        }

        window.PreviewKeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape:
                    if (secondary is not null)
                    {
                        secondary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    else
                    {
                        window.Close();
                    }
                    e.Handled = true;
                    break;

                case Key.Enter when primary is not null && !danger:
                    primary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    e.Handled = true;
                    break;
            }
        };

        window.Loaded += (_, _) =>
        {
            // 危险操作把焦点放在「取消」上，避免顺手一个回车就删干净
            var target = danger ? secondary : primary;
            target?.Focus();
        };
    }
}
