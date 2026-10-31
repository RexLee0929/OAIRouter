using System.Collections.ObjectModel;
using System.Windows;

namespace OAIRouter;

public partial class PricingWindow : Window
{
    private readonly AppConfig _cfg = App.Config;

    public ObservableCollection<string> RouteOptions { get; } = new();
    public ObservableCollection<string> ModelOptions { get; } = new();
    public ObservableCollection<string> TimeOptions { get; } = new();

    public PricingWindow()
    {
        InitializeComponent();
        DataContext = this;

        foreach (var t in new[] { "", "00:00", "06:00", "08:00", "09:00", "12:00", "18:00", "20:00", "22:00", "23:59" })
            TimeOptions.Add(t);

        ReloadOptions();

        PriceItems.ItemsSource = _cfg.Pricing;
        foreach (var rule in _cfg.Pricing) rule.PropertyChanged += (_, _) => ConfigStore.Save(_cfg);

        Activated += (_, _) => ReloadOptions();
    }

    private void ReloadOptions()
    {
        RouteOptions.Clear();
        RouteOptions.Add("*");
        foreach (var route in _cfg.Routes)
        {
            var prefix = (route.Prefix ?? "").Trim();
            if (prefix.Length > 0 && !RouteOptions.Contains(prefix)) RouteOptions.Add(prefix);
        }

        ModelOptions.Clear();
        ModelOptions.Add("*");
        foreach (var model in _cfg.Models)
            if (!string.IsNullOrWhiteSpace(model) && !ModelOptions.Contains(model)) ModelOptions.Add(model);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowChrome.Apply(this);
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var rule = new PriceRule { Route = "*", Model = "*", TierFrom = "0" };
        rule.PropertyChanged += (_, _) => ConfigStore.Save(_cfg);
        _cfg.Pricing.Add(rule);
        ConfigStore.Save(_cfg);
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PriceRule rule })
        {
            _cfg.Pricing.Remove(rule);
            ConfigStore.Save(_cfg);
        }
    }

    private bool _asc = true;

    /// <summary>按模型名重排；用 Move 而不是重建集合，避免打断正在编辑的单元格。</summary>
    private void OnSortByName(object sender, RoutedEventArgs e)
    {
        if (_cfg.Pricing.Count < 2)
        {
            StatusText.Text = "只有一条规则，不用排";
            return;
        }

        var sorted = _cfg.Pricing.ToList();
        sorted.Sort((a, b) =>
        {
            var c = string.Compare((a.Model ?? "").Trim(), (b.Model ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
            if (c == 0) c = string.Compare((a.Route ?? "").Trim(), (b.Route ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
            if (c == 0)
            {
                var fa = long.TryParse((a.TierFrom ?? "0").Trim(), out var x) ? x : 0;
                var fb = long.TryParse((b.TierFrom ?? "0").Trim(), out var y) ? y : 0;
                c = fa.CompareTo(fb);
            }
            return _asc ? c : -c;
        });

        for (var i = 0; i < sorted.Count; i++)
        {
            var current = _cfg.Pricing.IndexOf(sorted[i]);
            if (current != i) _cfg.Pricing.Move(current, i);
        }

        ConfigStore.Save(_cfg);
        SortBtn.Content = _asc ? "名称 ↑" : "名称 ↓";
        _asc = !_asc;
        StatusText.Text = $"已按名称排序（{sorted.Count} 条）";
    }

    private void OnOpenSync(object sender, RoutedEventArgs e)
    {
        var dialog = new PriceSyncWindow(_cfg) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var added = 0;
        var updated = 0;
        foreach (var candidate in dialog.Selected)
        {
            if (Upsert(candidate)) added++;
            else updated++;
        }

        ConfigStore.Save(_cfg);
        StatusText.Text = $"已添加 {added} 条，更新 {updated} 条";
        ReloadOptions();
    }

    /// <summary>同一 (路由, 模型, 挡位) 已存在则更新价格，否则新增。</summary>
    private bool Upsert(PriceRule candidate)
    {
        var existing = _cfg.Pricing.FirstOrDefault(p =>
            string.Equals((p.Route ?? "").Trim(), candidate.Route, StringComparison.OrdinalIgnoreCase) &&
            string.Equals((p.Model ?? "").Trim(), candidate.Model, StringComparison.Ordinal) &&
            (p.TierFrom ?? "0").Trim() == (candidate.TierFrom ?? "0").Trim());

        if (existing is not null)
        {
            existing.InputPerM = candidate.InputPerM;
            existing.OutputPerM = candidate.OutputPerM;
            if (candidate.CacheReadPerM > 0) existing.CacheReadPerM = candidate.CacheReadPerM;
            if (candidate.CacheWritePerM > 0) existing.CacheWritePerM = candidate.CacheWritePerM;
            return false;
        }

        candidate.PropertyChanged += (_, _) => ConfigStore.Save(_cfg);
        _cfg.Pricing.Add(candidate);
        return true;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
