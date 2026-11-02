using System.Windows;
using OAIRouter.Ui;
using System.Windows.Input;

namespace OAIRouter;

public partial class ModelsWindow : Window
{
    private readonly AppConfig _cfg = App.Config;

    public ModelsWindow()
    {
        InitializeComponent();
        ModelList.ItemsSource = _cfg.Models;
        ToolWindow.Attach(this);
    }

    private void OnNewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnAdd(sender, e);
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var name = (NewBox.Text ?? "").Trim();
        if (name.Length == 0) return;

        if (_cfg.Models.Any(m => string.Equals(m, name, StringComparison.Ordinal)))
        {
            StatusText.Text = $"「{name}」已经在列表里了（区分大小写）";
            return;
        }

        _cfg.Models.Add(name);
        NewBox.Text = "";
        ConfigStore.Save(_cfg);
        StatusText.Text = $"已添加 {name}";
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string name })
        {
            _cfg.Models.Remove(name);
            ConfigStore.Save(_cfg);
            StatusText.Text = $"已移除 {name}";
        }
    }

    private bool _asc = true;

    private void OnSort(object sender, RoutedEventArgs e)
    {
        var ordered = _cfg.Models.ToList();
        ordered.Sort((a, b) =>
        {
            var c = string.Compare(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);
            return _asc ? c : -c;
        });

        for (var i = 0; i < ordered.Count; i++)
        {
            var current = _cfg.Models.IndexOf(ordered[i]);
            if (current != i) _cfg.Models.Move(current, i);
        }

        ConfigStore.Save(_cfg);
        SortBtn.Content = _asc ? "名称 ↑" : "名称 ↓";
        _asc = !_asc;
        StatusText.Text = "已按名称排序";
    }

    private void OnImportUsage(object sender, RoutedEventArgs e)
    {
        var known = UsageStats.KnownModels();
        var added = 0;

        foreach (var name in known)
        {
            if (_cfg.Models.Any(m => string.Equals(m, name, StringComparison.Ordinal))) continue;
            _cfg.Models.Add(name);
            added++;
        }

        ConfigStore.Save(_cfg);
        StatusText.Text = known.Count == 0
            ? "还没有用量记录"
            : $"从用量补充了 {added} 个（共 {known.Count} 个）";
    }

    private void OnRestoreDefault(object sender, RoutedEventArgs e)
    {
        foreach (var name in new[]
        {
            "gpt-6-luna", "gpt-6-astra", "gpt-6.1-sol", "gpt-6.1-terra", "gpt-5.6-sol",
            "deepseek-v4-pro", "deepseek-v4-flash", "deepseek-v4.1-flash",
            "glm-5.3", "glm-5.3-flash", "kimi-k3", "kimi-k2.6", "minimax-m3", "hy4-preview",
        })
        {
            if (_cfg.Models.Any(m => string.Equals(m, name, StringComparison.Ordinal))) continue;
            _cfg.Models.Add(name);
        }

        ConfigStore.Save(_cfg);
        StatusText.Text = "已补回默认模型";
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
