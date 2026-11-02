using System.Windows.Input;

namespace OAIRouter;

/// <summary>
/// 窗口级快捷键（规范 §五.2：菜单里的 Gesture 只负责显示，**注册另做**）。
///
/// 用 RoutedUICommand 而不是在 OnPreviewKeyDown 里读键：菜单上写的「Ctrl+L」和真正生效的那条
/// 绑定必须是同一份声明。分开写就会退回「显示好看的装饰」—— 上一轮审核在仓库里搜
/// KeyBinding / InputBindings 零命中，就是这么来的。
/// </summary>
public static class ShellCommands
{
    public static readonly RoutedUICommand ClearLog =
        new("清空日志", nameof(ClearLog), typeof(ShellCommands));

    public static readonly RoutedUICommand Quit =
        new("退出", nameof(Quit), typeof(ShellCommands));
}
