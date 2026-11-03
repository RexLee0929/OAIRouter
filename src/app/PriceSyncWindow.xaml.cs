using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using OAIRouter.Ui;
using System.Windows.Controls;

namespace OAIRouter;

public sealed class PriceCandidate : INotifyPropertyChanged
{
    private bool _selected = true;

    public string Name { get; init; } = "";
    public string Route { get; init; } = "*";
    public string TierLabel { get; init; } = "统一";
    public string TierFrom { get; init; } = "0";
    public string TierTo { get; init; } = "";
    public double Input { get; init; }
    public double Output { get; init; }
    public double CacheRead { get; init; }
    public double CacheWrite { get; init; }

    public bool Selected { get => _selected; set { _selected = value; OnChanged(); } }

    public string Title => $"{Name}  ·  {TierLabel}";
    public string InputText => Input > 0 ? $"${Input:0.####}" : "—";
    public string OutputText => Output > 0 ? $"${Output:0.####}" : "—";
    public string CacheReadText => CacheRead > 0 ? $"${CacheRead:0.####}" : "—";
    public string CacheWriteText => CacheWrite > 0 ? $"${CacheWrite:0.####}" : "—";

    public PriceRule ToRule() => new()
    {
        Route = Route,
        Model = Name,
        TierFrom = TierFrom,
        TierTo = TierTo,
        InputPerM = Math.Round(Input, 4),
        OutputPerM = Math.Round(Output, 4),
        CacheReadPerM = Math.Round(CacheRead, 4),
        CacheWritePerM = Math.Round(CacheWrite, 4),
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public partial class PriceSyncWindow : Window
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(25) };
    private readonly AppConfig _cfg;
    private readonly ObservableCollection<PriceCandidate> _results = new();

    public List<PriceRule> Selected { get; } = new();

    public PriceSyncWindow(AppConfig cfg)
    {
        InitializeComponent();
        _cfg = cfg;

        SourceBox.ItemsSource = new[] { "已添加上游", "LiteLLM 社区价格表", "自定义 JSON 地址" };
        SourceBox.SelectedIndex = 0;

        ResultItems.ItemsSource = _results;
        Loaded += (_, _) => OnSourceChanged(null!, null!);

        ToolWindow.Attach(this);
    }

    private void OnSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        var index = SourceBox.SelectedIndex;
        var isUpstream = index == 0;

        UpstreamBox.Visibility = isUpstream ? Visibility.Visible : Visibility.Collapsed;
        AuthPanel.Visibility = Visibility.Collapsed;
        UrlBox.IsReadOnly = false;
        _results.Clear();

        if (isUpstream)
        {
            var items = _cfg.Routes.Where(r => !string.IsNullOrWhiteSpace(r.Upstream)).ToList();
            UpstreamBox.ItemsSource = items;
            if (items.Count > 0)
            {
                UpstreamBox.SelectedIndex = 0;
                UpdateUpstreamSource(items[0]);
            }
            else
            {
                UrlBox.Text = "";
                HintText.Text = "请先添加上游";
            }
        }
        else if (index == 1)
        {
            UrlBox.Text = "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json";
            HintText.Text = "社区维护，单位美元/token，自动换算";
        }
        else
        {
            UrlBox.Text = "";
            HintText.Text = "填写完整 JSON 地址；支持 NewAPI、LiteLLM 或 models 数组格式";
        }
    }

    private void OnUpstreamChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceBox.SelectedIndex != 0) return;
        if (UpstreamBox.SelectedItem is not RouteRule route) return;
        _results.Clear();
        UpdateUpstreamSource(route);
    }

    private void UpdateUpstreamSource(RouteRule route)
    {
        var isNewApi = IsNewApi(route);
        UrlBox.Text = isNewApi ? NormalizeNewApiUrl(route.Upstream) : "";
        var isCustom = string.Equals(route.BalanceType, "自定义", StringComparison.OrdinalIgnoreCase) && !IsOfficialOpenAi(route);
        AuthPanel.Visibility = isNewApi || isCustom ? Visibility.Visible : Visibility.Collapsed;
        UseUpstreamCredential.IsChecked = false;
        UseUpstreamCredential.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
        AuthHint.Text = isCustom ? "仅同源 HTTPS（本机可 HTTP）；GET JSON" : "凭据与用户 ID 在上游配置中设置。";
        HintText.Text = isNewApi
            ? "NewAPI：使用该上游余额令牌和用户 ID"
            : string.Equals(route.BalanceType, "OpenAI", StringComparison.OrdinalIgnoreCase) || IsOfficialOpenAi(route)
                ? "OpenAI 无通用价格 API；请填写公开 JSON 地址"
                : "自定义上游：GET JSON，可选复用上游余额令牌";
    }

    private static bool IsOfficialOpenAi(RouteRule route) =>
        Uri.TryCreate(route.Upstream, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Host, "api.openai.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsNewApi(RouteRule route) =>
        !IsOfficialOpenAi(route) && string.Equals(route.BalanceType, "NewAPI", StringComparison.OrdinalIgnoreCase);

    /// <summary>用主机名把地址匹配回已配置的路由前缀。</summary>
    private string MatchRoutePrefix(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target)) return "*";

        foreach (var route in _cfg.Routes)
        {
            if (!Uri.TryCreate((route.Upstream ?? "").Trim(), UriKind.Absolute, out var upstream)) continue;
            if (string.Equals(upstream.Host, target.Host, StringComparison.OrdinalIgnoreCase)
                && upstream.Port == target.Port)
            {
                return route.Prefix;
            }
        }

        return "*";
    }

    /// <summary>把用户填的站点地址补成 …/api/pricing。</summary>
    private static string NormalizeNewApiUrl(string input)
    {
        var url = (input ?? "").Trim().TrimEnd('/');
        if (url.Length == 0) return "";
        if (url.EndsWith("/api/pricing", StringComparison.OrdinalIgnoreCase)) return url;
        if (url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) url = url[..^3];
        return url + "/api/pricing";
    }

    private async void OnFetch(object sender, RoutedEventArgs e)
    {
        var url = (UrlBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            StatusText.Text = "请先填写地址";
            return;
        }

        var selected = SourceBox.SelectedIndex == 0 ? UpstreamBox.SelectedItem as RouteRule : null;
        if (SourceBox.SelectedIndex == 0 && selected is null)
        {
            StatusText.Text = "请先选择已添加上游";
            return;
        }
        var routePrefix = selected?.Prefix ?? MatchRoutePrefix(url);

        try
        {
            StatusText.Text = "正在获取 …";
            _results.Clear();

            var isNewApi = selected is not null && IsNewApi(selected);
            if (isNewApi)
            {
                url = NormalizeNewApiUrl(url);
                UrlBox.Text = url;
            }
            if (!Uri.TryCreate(url, UriKind.Absolute, out var actual) ||
                (actual.Scheme != Uri.UriSchemeHttp && actual.Scheme != Uri.UriSchemeHttps) ||
                !string.IsNullOrEmpty(actual.UserInfo))
                throw new InvalidOperationException("请输入完整的 HTTP(S) JSON 地址，且不能包含用户信息。");

            using var request = new HttpRequestMessage(HttpMethod.Get, actual);

            var useCustomCredential = selected is not null &&
                string.Equals(selected.BalanceType, "自定义", StringComparison.OrdinalIgnoreCase) &&
                !IsOfficialOpenAi(selected) && UseUpstreamCredential.IsChecked == true;
            if ((isNewApi || useCustomCredential) && selected is not null)
            {
                if (!Uri.TryCreate(selected.Upstream, UriKind.Absolute, out var expected) ||
                    actual.Scheme != expected.Scheme || actual.Host != expected.Host || actual.Port != expected.Port)
                    throw new InvalidOperationException("地址与所选上游不一致，未发送凭据。");
                var token = SecretProtection.Unprotect(selected.ProtectedBalanceKey);
                if ((token.Length > 0 || (isNewApi && !string.IsNullOrWhiteSpace(selected.BalanceUserId))) &&
                    actual.Scheme != Uri.UriSchemeHttps && !actual.IsLoopback)
                    throw new InvalidOperationException("非本机地址必须使用 HTTPS 才能发送凭据。");
                if (token.Length > 0)
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (isNewApi && !string.IsNullOrWhiteSpace(selected.BalanceUserId))
                    request.Headers.TryAddWithoutValidation("New-Api-User", selected.BalanceUserId);
            }

            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                StatusText.Text = response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? "站点需要登录：请检查该上游的余额查询令牌和用户 ID"
                    : $"获取失败：HTTP {(int)response.StatusCode}";
                return;
            }

            var json = await response.Content.ReadAsStringAsync();
            var parsed = Parse(json, routePrefix);

            // 按模型名排序，同名再按挡位起点升序，避免列表看着乱
            foreach (var item in parsed
                         .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(i => long.TryParse(i.TierFrom, out var from) ? from : 0))
            {
                _results.Add(item);
            }

            StatusText.Text = parsed.Count == 0
                ? "没有解析出可用价格，检查地址或格式"
                : $"解析出 {parsed.Count} 条（含分档），勾选后添加";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"获取失败：{ex.Message}";
        }
    }

    /* ------------------------------ 解析 ------------------------------ */

    private static List<PriceCandidate> Parse(string json, string route)
    {
        if (JsonNode.Parse(json) is not JsonObject root) return new List<PriceCandidate>();

        if (root["data"] is JsonArray data && data.Count > 0 && data[0]?["model_name"] is not null)
            return ParseNewApi(root, data, route);

        var litellm = ParseLiteLlm(root, route);
        if (litellm.Count > 0) return litellm;

        return ParseGeneric(root, route);
    }

    /// <summary>
    /// new-api：非分档模型用 ratio×2（ratio=1 对应 $2/1M）；
    /// 带 billing_expr 的分档模型，表达式里的系数本身就是美元/百万 token。
    /// </summary>
    private static List<PriceCandidate> ParseNewApi(JsonObject root, JsonArray data, string route)
    {
        var list = new List<PriceCandidate>();

        var groupRatio = 1.0;
        if (root["group_ratio"] is JsonObject gr
            && gr["default"]?.GetValue<double?>() is { } g && g > 0)
        {
            groupRatio = g;
        }

        foreach (var node in data)
        {
            if (node is not JsonObject model) continue;
            var name = model["model_name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)) continue;

            var expr = model["billing_expr"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(expr) && expr.Contains("tier("))
            {
                list.AddRange(ParseTiers(name, expr, route, groupRatio));
                continue;
            }

            var ratio = model["model_ratio"]?.GetValue<double?>() ?? 0;
            if (ratio <= 0) continue;

            var completion = model["completion_ratio"]?.GetValue<double?>() ?? 1;
            var cache = model["cache_ratio"]?.GetValue<double?>() ?? 0;
            var cacheCreate = model["create_cache_ratio"]?.GetValue<double?>() ?? 0;

            list.Add(new PriceCandidate
            {
                Name = name,
                Route = route,
                TierLabel = "统一",
                Input = Math.Round(ratio * 2 * groupRatio, 4),
                Output = Math.Round(ratio * Math.Max(1, completion) * 2 * groupRatio, 4),
                CacheRead = Math.Round(ratio * cache * 2 * groupRatio, 4),
                CacheWrite = Math.Round(ratio * cacheCreate * 2 * groupRatio, 4),
            });
        }

        return list;
    }

    /// <summary>
    /// 形如：len &lt;= 272000 ? tier("0_272k", p*2 + c*12 + cr*0.2 + cc*2.5)
    ///                      : tier("272k_plus", p*4 + c*18 + cr*0.4 + cc*5)
    /// 系数即美元/百万 token，不需要再乘 2。
    /// </summary>
    private static List<PriceCandidate> ParseTiers(string name, string expr, string route, double groupRatio)
    {
        var list = new List<PriceCandidate>();

        var tiers = Regex.Matches(expr, "tier\\(\"([^\"]+)\"\\s*,\\s*([^)]*)\\)");
        var bounds = Regex.Matches(expr, @"len\s*<=\s*(\d+)");

        for (var i = 0; i < tiers.Count; i++)
        {
            var label = tiers[i].Groups[1].Value;
            var coefs = ParseCoefficients(tiers[i].Groups[2].Value);

            var from = i == 0 ? 0L : long.TryParse(bounds[i - 1].Groups[1].Value, out var b) ? b : 0;
            var to = i < bounds.Count && long.TryParse(bounds[i].Groups[1].Value, out var t) ? t : 0;

            double Get(string key) => coefs.TryGetValue(key, out var v) ? v * groupRatio : 0;

            list.Add(new PriceCandidate
            {
                Name = name,
                Route = route,
                TierLabel = to > 0 ? $"{from:N0}–{to:N0}" : $"{from:N0}+",
                TierFrom = from.ToString(),
                TierTo = to > 0 ? to.ToString() : "",
                Input = Math.Round(Get("p"), 4),
                Output = Math.Round(Get("c"), 4),
                CacheRead = Math.Round(Get("cr"), 4),
                CacheWrite = Math.Round(Get("cc") is var cc && cc > 0 ? cc : Get("cc1h"), 4),
            });
        }

        return list;
    }

    private static Dictionary<string, double> ParseCoefficients(string expression)
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(expression, @"([A-Za-z_][A-Za-z0-9_]*)\s*\*\s*([0-9.]+)"))
        {
            if (double.TryParse(m.Groups[2].Value, out var value)) map[m.Groups[1].Value] = value;
        }
        return map;
    }

    private static List<PriceCandidate> ParseLiteLlm(JsonObject root, string route)
    {
        var list = new List<PriceCandidate>();

        foreach (var (key, value) in root)
        {
            if (value is not JsonObject info) continue;
            if (info["input_cost_per_token"] is null && info["output_cost_per_token"] is null) continue;

            double PerMillion(string field)
                => (info[field]?.GetValue<double?>() ?? 0) * 1_000_000;

            var input = PerMillion("input_cost_per_token");
            var output = PerMillion("output_cost_per_token");
            if (input <= 0 && output <= 0) continue;

            list.Add(new PriceCandidate
            {
                Name = key,
                Route = route,
                TierLabel = "统一",
                Input = Math.Round(input, 4),
                Output = Math.Round(output, 4),
                CacheRead = Math.Round(PerMillion("cache_read_input_token_cost"), 4),
                CacheWrite = Math.Round(PerMillion("cache_creation_input_token_cost"), 4),
            });
        }

        return list;
    }

    private static List<PriceCandidate> ParseGeneric(JsonObject root, string route)
    {
        var list = new List<PriceCandidate>();
        if (root["models"] is not JsonArray models) return list;

        foreach (var node in models)
        {
            var name = node?["model"]?.GetValue<string>() ?? node?["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)) continue;

            list.Add(new PriceCandidate
            {
                Name = name,
                Route = route,
                TierLabel = "统一",
                Input = node?["input"]?.GetValue<double?>() ?? node?["input_per_m"]?.GetValue<double?>() ?? 0,
                Output = node?["output"]?.GetValue<double?>() ?? node?["output_per_m"]?.GetValue<double?>() ?? 0,
            });
        }

        return list;
    }

    /* ------------------------------ 操作 ------------------------------ */

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        var want = SelectAll.IsChecked == true;
        foreach (var item in _results) item.Selected = want;
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        Selected.Clear();
        foreach (var item in _results.Where(r => r.Selected))
            Selected.Add(item.ToRule());

        if (Selected.Count == 0)
        {
            StatusText.Text = "还没有勾选任何条目";
            return;
        }

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
