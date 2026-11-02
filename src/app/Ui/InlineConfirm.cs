using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace OAIRouter.Ui;

/// <summary>
/// 内联确认（规范 §七 阶梯 ③）：覆盖、清空、重置这类不可逆操作**不弹窗** ——
/// 按钮就地变「确认××」（换成语义红），再点一次才执行；<see cref="Window"/> 内没动就自动还原。
///
/// §七.6 明确写着「清空日志 → 内联确认条，不弹」：为一次点击开一个模态框，
/// 既打断上下文，又要在对话框里再写一遍后果说明。
/// </summary>
public static class InlineConfirm
{
    /// <summary>确认态保持多久。太短来不及点，太长会一直红着。</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(4);

    private sealed record Armed(string NormalText, Brush Ink, DispatcherTimer Timer);

    /// <summary>按按钮记状态：同一个按钮第二次点击就是「确认」。</summary>
    private static readonly Dictionary<Button, Armed> Pending = new();

    /// <summary>
    /// 把按钮武装成确认态。
    /// 返回 true = 刚进入确认态（还没执行），返回 false = 这一下就是确认，动作已执行。
    ///
    /// <paramref name="label"/> 用于「Content 是图标 + 文字的 Grid」的菜单项 ——
    /// 直接替换 Content 会把图标和列宽一起丢掉，所以只改那个 TextBlock。
    /// </summary>
    public static bool Arm(Button button, string confirmText, Action execute, TextBlock? label = null)
    {
        if (Pending.Remove(button, out var armed))
        {
            armed.Timer.Stop();
            Write(button, label, armed.NormalText);
            Ink(label, button, armed.Ink);
            execute();
            return false;
        }

        label ??= button.Content as TextBlock;
        if (label is null && button.Content is not string) return false;   // 认不出文案就不假装确认

        var timer = new DispatcherTimer { Interval = Window };
        Pending[button] = new Armed(label?.Text ?? (string)button.Content!, label?.Foreground ?? button.Foreground, timer);

        Write(button, label, confirmText);
        Ink(label, button, Danger());

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!Pending.Remove(button, out var state)) return;
            Write(button, label, state.NormalText);
            Ink(label, button, state.Ink);
        };
        timer.Start();
        return true;
    }

    private static void Write(Button button, TextBlock? label, string text)
    {
        if (label is null) button.Content = text;
        else label.Text = text;
    }

    private static void Ink(TextBlock? label, Button button, Brush ink)
    {
        if (label is null) button.Foreground = ink;
        else label.Foreground = ink;
    }

    private static Brush Danger()
        => Application.Current?.TryFindResource("ErrorBrush") as Brush ?? Brushes.Firebrick;
}
