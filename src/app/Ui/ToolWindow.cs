using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace OAIRouter.Ui;

/// <summary>
/// 工具窗口（非模态）：模型库 / 价格设置 / 从上游同步价格这类「主窗口旁边开的工作区」。
///
/// 它们不是对话框，规范 §七 那一套（NoResize、Esc=次按钮、Owner 居中）不适用；
/// 这里单列一档，只统一三件事：
///   · 原生标题栏 + 同一套 DWM 配色（与主窗口一致，别再出现第二种标题）；
///   · 可拉伸（工作区要能放大看表格），进任务栏、可最小化；
///   · Esc 关闭；但焦点在输入框里时不抢 Esc，先让输入框自己退出。
/// </summary>
public static class ToolWindow
{
    public static void Attach(Window window)
    {
        WindowChrome.Apply(window);

        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            if (Keyboard.FocusedElement is TextBox) return;   // 正在输入，不抢

            e.Handled = true;
            window.Close();
        };
    }
}
