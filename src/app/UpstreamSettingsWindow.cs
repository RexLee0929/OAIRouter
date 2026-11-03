using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using OAIRouter.Ui;

namespace OAIRouter;

public sealed class UpstreamSettingsWindow : DialogWindow
{
    private readonly RouteRule _route;
    private readonly Func<string, bool> _prefixInUse;
    private bool _decryptFailed;
    private readonly CheckBox _replaceUnreadable = new() { Content = "确认替换无法解密的密钥（留空则清除）", Margin = new Thickness(0, 6, 0, 0) };
    private readonly ComboBox _type = new() { ItemsSource = new[] { "NewAPI", "OpenAI", "自定义" } };
    private readonly ComboBox _mode = new() { ItemsSource = new[] { "responses", "chat", "rewrite" } };
    private readonly TextBox _name = new(), _upstreamUrl = new(), _prefix = new(), _url = new(), _path = new(), _user = new();
    private readonly PasswordBox _balance = new(), _local = new(), _upstream = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 0, 0, 0), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };

    public UpstreamSettingsWindow(RouteRule route, Func<string, bool> prefixInUse, bool isNew = false)
    {
        _route = route; _prefixInUse = prefixInUse;
        Title = "上游配置"; Width = 700; Height = 400; SizeToContent = SizeToContent.Height;
        var panel = new StackPanel();
        var fields = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
        panel.Children.Add(fields);
        var shell = new DialogShell { Style = (Style)FindResource("DialogShell"), Kind = DialogKind.Info, Headline = isNew ? "上游配置 · 添加" : "上游配置 · 编辑", PrimaryText = "保存", SecondaryText = "取消" };
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Content = shell;
        shell.Content = panel;
        void Add(string label, Control control)
        {
            var field = new StackPanel { Margin = new Thickness(0, 0, 8, 3) };
            field.Children.Add(new TextBlock { Text = label, FontSize = 11, Margin = new Thickness(0, 0, 0, 2) });
            if (control is TextBox) { control.Style = (Style)FindResource("Input"); control.Padding = new Thickness(7, 2, 7, 2); }
            if (control is PasswordBox) { control.Style = (Style)FindResource("PasswordInput"); control.Padding = new Thickness(7, 2, 7, 2); }
            if (control is ComboBox) control.Style = (Style)FindResource("CellCombo");
            control.Height = 26;
            control.MinHeight = 0;
            field.Children.Add(control);
            fields.Children.Add(field);
        }
        Add("上游类型", _type);
        Add("显示名称", _name);
        Add("上游地址", _upstreamUrl);
        Add("前缀", _prefix);
        Add("模式（rewrite 为旧配置兼容模式）", _mode);
        Add("Access Token（余额查询）", _balance);
        Add("API Key（实际上游密钥，留空透传）", _upstream);
        Add("自定义 API Key（客户端本地密钥，可留空）", _local);
        Add("NewAPI 用户 ID（可选）", _user);
        Add("余额查询地址（NewAPI 留空使用上游地址）", _url);
        Add("自定义余额 JSON 路径", _path);
        var actions = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var test = new Button { Content = "查询余额", Style = (Style)FindResource("GhostButton") };
        actions.Children.Add(test);
        Grid.SetColumn(_status, 1); actions.Children.Add(_status); panel.Children.Add(actions);
        _type.SelectedItem = route.BalanceType; if (_type.SelectedIndex < 0) _type.SelectedIndex = 0;
        _mode.SelectedItem = route.Mode; if (_mode.SelectedIndex < 0) { _mode.ItemsSource = new[] { "responses", "chat", "rewrite", route.Mode }; _mode.SelectedItem = route.Mode; }
        _name.Text = route.Name; _upstreamUrl.Text = route.Upstream; _prefix.Text = route.Prefix;
        _url.Text = route.BalanceUrl; _path.Text = route.BalanceJsonPath; _user.Text = route.BalanceUserId;
        void LoadSecret(PasswordBox box, string value)
        {
            try { box.Password = SecretProtection.Unprotect(value); }
            catch { _decryptFailed = true; }
        }
        LoadSecret(_balance, route.ProtectedBalanceKey);
        LoadSecret(_local, route.ProtectedLocalKey);
        LoadSecret(_upstream, route.ProtectedUpstreamKey);
        if (_decryptFailed)
        {
            _status.Text = "部分密钥无法解密，请重新输入；留空将清除。";
            panel.Children.Add(_replaceUnreadable);
        }
        test.Click += async (_, _) => { test.IsEnabled = false; try { _status.Text = await QueryBalanceAsync((string)_type.SelectedItem, _upstreamUrl.Text, _url.Text, _path.Text, _user.Text, _balance.Password); } finally { test.IsEnabled = true; } };
        shell.PrimaryInvoked += (_, _) => Save(); shell.SecondaryInvoked += (_, _) => DialogResult = false;
        Loaded += (_, _) => { shell.ApplyTemplate(); DialogHost.Setup(this, shell); };
    }

    // 表单按自然高度显示，避免继承的高度拟合逻辑在压缩布局中留下空白。
    protected override void FitToContent() { }

    private static bool ValidUrl(string text, out Uri? uri) => Uri.TryCreate(text, UriKind.Absolute, out uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;

    private void Save()
    {
        if (_decryptFailed && _replaceUnreadable.IsChecked != true) { _status.Text = "请确认替换无法解密的密钥，或取消并使用原 Windows 账户打开。"; return; }
        var prefix = _prefix.Text.Trim().Trim('/');
        if (_name.Text.Trim().Length == 0 || prefix.Length == 0 || prefix.Contains('/') || prefix.Contains('?') || prefix.Contains('#') || _prefixInUse(prefix)) { _status.Text = "请填写名称和唯一的单段前缀。"; return; }
        if (!ValidUrl(_upstreamUrl.Text.Trim(), out _) || (_url.Text.Trim().Length > 0 && !ValidUrl(_url.Text.Trim(), out _))) { _status.Text = "地址须为不含用户信息、查询参数或片段的 HTTP(S) 地址。"; return; }
        try
        {
            var balance = SecretProtection.Protect(_balance.Password); var local = SecretProtection.Protect(_local.Password); var upstream = SecretProtection.Protect(_upstream.Password);
            _route.BalanceType = (string)_type.SelectedItem; _route.BalanceUrl = _url.Text.Trim(); _route.BalanceJsonPath = _path.Text.Trim(); _route.BalanceUserId = _user.Text.Trim();
            _route.ProtectedBalanceKey = balance; _route.ProtectedLocalKey = local; _route.ProtectedUpstreamKey = upstream;
            _route.Name = _name.Text.Trim(); _route.Upstream = _upstreamUrl.Text.Trim(); _route.Prefix = prefix; _route.Mode = (string)_mode.SelectedItem;
            DialogResult = true;
        }
        catch { _status.Text = "密钥加密失败，未保存。"; }
    }

    public static async Task<string> QueryBalanceAsync(RouteRule route, HttpClient? client = null)
    {
        try { return await QueryBalanceAsync(route.BalanceType, route.Upstream, route.BalanceUrl, route.BalanceJsonPath, route.BalanceUserId, SecretProtection.Unprotect(route.ProtectedBalanceKey), client); }
        catch { return "密钥无法解密"; }
    }

    private static async Task<string> QueryBalanceAsync(string type, string upstream, string url, string path, string user, string token, HttpClient? client = null)
    {
        url = url.Trim();
        if (url.Length == 0 && type == "NewAPI")
        {
            var baseUrl = upstream.Trim().TrimEnd('/');
            if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) baseUrl = baseUrl[..^3];
            url = baseUrl + "/api/user/self";
        }
        if (!ValidUrl(url, out var uri)) return type == "OpenAI" && url.Length == 0 ? "请配置余额地址" : "余额地址无效";
        if (uri!.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback && (token.Length > 0 || (type == "NewAPI" && !string.IsNullOrWhiteSpace(user)))) return "非本机凭据必须使用 HTTPS";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (token.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (type == "NewAPI" && user.Trim().Length > 0) request.Headers.TryAddWithoutValidation("New-Api-User", user.Trim());
            using var response = await (client ?? Http).SendAsync(request);
            if (!response.IsSuccessStatusCode) return $"HTTP {(int)response.StatusCode}";
            var node = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            foreach (var segment in (type == "NewAPI" ? "data.quota" : path.Trim()).Split('.', StringSplitOptions.RemoveEmptyEntries)) node = node?[segment];
            if (node is not JsonValue || !decimal.TryParse(node.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var amount)) return "余额路径无数值";
            return type == "NewAPI" ? $"${amount / 500000m:0.####}" : amount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch { return "查询失败"; }
    }
}
