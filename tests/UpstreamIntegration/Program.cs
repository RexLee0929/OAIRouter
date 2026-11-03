using System.Text.Json;
using System.Net;
using System.Net.Http;
using System.Reflection;
using OAIRouter;

var failures = new List<string>();

void Run(string name, Action test)
{
    try { test(); Console.WriteLine($"通过：{name}"); }
    catch (Exception ex) { failures.Add($"{name}：{ex.Message}"); }
}

void Check(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

Run("测试凭据加密往返和损坏密文拒绝", () =>
{
    const string secret = "仅供回归测试的凭据-123";
    var protectedValue = SecretProtection.Protect(secret);
    Check(protectedValue != secret && !protectedValue.Contains(secret, StringComparison.Ordinal), "密文包含明文");
    Check(SecretProtection.Unprotect(protectedValue) == secret, "解密结果与原文不一致");
    Check(SecretProtection.Protect("") == "" && SecretProtection.Unprotect("") == "", "空凭据未保持为空");
    try { SecretProtection.Unprotect("invalid!"); }
    catch (FormatException) { return; }
    throw new InvalidOperationException("损坏的密文未被拒绝");
});

Run("余额刷新状态不进入持久化配置", () =>
{
    var route = new RouteRule { Name = "测试", Prefix = "test", BalanceDisplay = "$12", BalanceRefreshing = true };
    var json = JsonSerializer.Serialize(route);
    Check(!json.Contains(nameof(RouteRule.BalanceDisplay), StringComparison.Ordinal), "余额显示被序列化");
    Check(!json.Contains(nameof(RouteRule.BalanceRefreshing), StringComparison.Ordinal), "刷新状态被序列化");
    var restored = JsonSerializer.Deserialize<RouteRule>(json)!;
    Check(restored.BalanceDisplay == "—" && !restored.BalanceRefreshing, "恢复了临时刷新状态");
});

Run("价格来源类型分派与官方上游保护", () =>
{
    var method = typeof(PriceSyncWindow).GetMethod("IsNewApi", BindingFlags.Static | BindingFlags.NonPublic)!;
    bool IsNewApi(RouteRule route) => (bool)method.Invoke(null, new object[] { route })!;
    Check(IsNewApi(new RouteRule { BalanceType = "NewAPI", Upstream = "https://example.test" }), "NewAPI 未被识别");
    Check(!IsNewApi(new RouteRule { BalanceType = "OpenAI", Upstream = "https://example.test" }), "OpenAI 被误识别为 NewAPI");
    Check(!IsNewApi(new RouteRule { BalanceType = "自定义", Upstream = "https://example.test" }), "自定义来源被误识别为 NewAPI");
    Check(!IsNewApi(new RouteRule { BalanceType = "NewAPI", Upstream = "https://api.openai.com" }), "官方地址被误识别为 NewAPI");
});

Run("价格解析保留指定路由前缀", () =>
{
    var method = typeof(PriceSyncWindow).GetMethod("Parse", BindingFlags.Static | BindingFlags.NonPublic)!;
    var parsed = (List<PriceCandidate>)method.Invoke(null, new object[] { "{\"models\":[{\"model\":\"test-model\",\"input\":1,\"output\":2}]}", "route-b" })!;
    Check(parsed.Count == 1 && parsed[0].ToRule().Route == "route-b", "解析后路由前缀丢失");
});

Run("余额刷新使用 NewAPI 凭据及配额换算", () =>
{
    var calls = 0;
    using var client = new HttpClient(new FakeHandler(request =>
    {
        calls++;
        Check(request.RequestUri!.AbsoluteUri == "https://example.test/api/user/self", "余额地址错误");
        Check(request.Headers.Authorization?.ToString() == "Bearer test-token", "令牌缺失");
        Check(request.Headers.GetValues("New-Api-User").Single() == "42", "用户 ID 缺失");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":{\"quota\":1000000}}") };
    }));
    var route = new RouteRule { Upstream = "https://example.test", BalanceUserId = "42", ProtectedBalanceKey = SecretProtection.Protect("test-token") };
    Check(UpstreamSettingsWindow.QueryBalanceAsync(route, client).GetAwaiter().GetResult() == "$2", "配额换算错误");
    Check(calls == 1, "请求次数错误");
});

Run("NewAPI 默认余额地址处理尾部 v1 并保留自定义地址", () =>
{
    string? requested = null;
    using var client = new HttpClient(new FakeHandler(request =>
    {
        requested = request.RequestUri!.AbsoluteUri;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":{\"quota\":500000}}") };
    }));
    foreach (var upstream in new[] { "https://example.test", "https://example.test/", "https://example.test/v1", "https://example.test/v1/" })
    {
        var route = new RouteRule { Upstream = upstream };
        Check(UpstreamSettingsWindow.QueryBalanceAsync(route, client).GetAwaiter().GetResult() == "$1", "余额查询失败");
        Check(requested == "https://example.test/api/user/self", $"默认地址错误：{upstream} → {requested}");
        Check(route.BalanceUrl == "", "默认地址不应写入配置");
    }
    var legacy = new RouteRule { Upstream = "https://example.test/v1", BalanceUrl = "https://example.test/v1/api/user/self" };
    UpstreamSettingsWindow.QueryBalanceAsync(legacy, client).GetAwaiter().GetResult();
    Check(requested == legacy.BalanceUrl, "旧显式余额地址被覆盖");
    legacy.BalanceUrl = "https://example.test/custom/balance";
    UpstreamSettingsWindow.QueryBalanceAsync(legacy, client).GetAwaiter().GetResult();
    Check(requested == legacy.BalanceUrl, "自定义余额地址被覆盖");
});

Run("余额查询拒绝非本机明文凭据", () =>
{
    var calls = 0;
    using var client = new HttpClient(new FakeHandler(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.OK); }));
    var route = new RouteRule { Upstream = "http://example.test", ProtectedBalanceKey = SecretProtection.Protect("test-token") };
    Check(UpstreamSettingsWindow.QueryBalanceAsync(route, client).GetAwaiter().GetResult() == "非本机凭据必须使用 HTTPS", "未拒绝明文凭据");
    Check(calls == 0, "拒绝后仍发送请求");
});

Run("余额查询拒绝非本机明文用户 ID", () =>
{
    var calls = 0;
    using var client = new HttpClient(new FakeHandler(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.OK); }));
    var route = new RouteRule { Upstream = "http://example.test", BalanceUserId = "42" };
    Check(UpstreamSettingsWindow.QueryBalanceAsync(route, client).GetAwaiter().GetResult() == "非本机凭据必须使用 HTTPS", "未拒绝明文用户 ID");
    Check(calls == 0, "拒绝后仍发送请求");
});

Run("余额查询支持自定义 JSON 路径及错误状态", () =>
{
    using var client = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"wallet\":{\"amount\":12.5}}") }));
    var route = new RouteRule { BalanceType = "自定义", BalanceUrl = "https://example.test/balance", BalanceJsonPath = "wallet.amount" };
    Check(UpstreamSettingsWindow.QueryBalanceAsync(route, client).GetAwaiter().GetResult() == "12.5", "自定义路径解析错误");
    route.BalanceJsonPath = "wallet.missing";
    Check(UpstreamSettingsWindow.QueryBalanceAsync(route, client).GetAwaiter().GetResult() == "余额路径无数值", "缺失路径未报错");
});

if (failures.Count > 0)
{
    foreach (var failure in failures) Console.Error.WriteLine($"失败：{failure}");
    return 1;
}
Console.WriteLine("上游集成回归测试通过");
return 0;

sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(handler(request));
}
