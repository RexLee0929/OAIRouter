using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OAIRouter;

/// <summary>
/// 把上游返回的英文错误翻译成中文说明（离线规则匹配，不联网、不调用任何模型）。
/// 目标是「大致看懂」，不是精确翻译：匹配不到就返回空，由调用方只记原文。
/// </summary>
public static class UpstreamError
{
    private sealed record Rule(Regex Pattern, string Explain);

    private static readonly Rule[] Rules =
    {
        new(new Regex(@"Invalid '(\w+)'[^.]*Expected a value >= (\d+), but got (\d+)", RegexOptions.IgnoreCase),
            "参数 $1 低于下限：要求 ≥ $2，实际是 $3（客户端探测请求常见）"),

        new(new Regex(@"Unsupported parameter: '(\w+)'[^.]*moved to 'input'", RegexOptions.IgnoreCase),
            "这个地址是 Responses 端点，收到的是 Chat 格式：把该路由模式改成 responses"),

        new(new Regex(@"Function tools with reasoning_effort are not supported", RegexOptions.IgnoreCase),
            "该模型在 chat/completions 上不允许「工具调用 + 思考」共存：改 responses 模式，或把思考强度设为 none"),

        new(new Regex(@"Unsupported parameter: '(\w+)'", RegexOptions.IgnoreCase),
            "不支持参数 $1：在路由模式里改用 rewrite 让程序自动改名/去掉"),

        new(new Regex(@"Invalid '(\w+)'[:\s]+(.+)", RegexOptions.IgnoreCase),
            "参数 $1 取值不合法：$2"),

        new(new Regex(@"invalid[_ ]?(api[ _]?key|token)|Incorrect API key|Invalid token", RegexOptions.IgnoreCase),
            "密钥无效或已过期：检查客户端该模型条目里填的 API Key 是否与同一路由下其他模型一致"),

        new(new Regex(@"no available channel|model[_ ]not[_ ]found|does not exist|unknown model", RegexOptions.IgnoreCase),
            "上游找不到该模型：模型名写错，或当前渠道/账号没有这个模型的权限"),

        new(new Regex(@"does not support image|not support.{0,10}image|image inputs", RegexOptions.IgnoreCase),
            "该模型不支持图片输入：这条消息里带了图片，换一个多模态模型，或去掉图片再试"),

        new(new Regex(@"model.{0,20}not supported|not supported.{0,20}model", RegexOptions.IgnoreCase),
            "该模型不支持这个端点：换一种路由模式（responses / chat / rewrite）再试"),

        new(new Regex(@"rate limit|too many requests|429", RegexOptions.IgnoreCase),
            "请求过于频繁，被上游限流：降低并发或稍后重试"),

        new(new Regex(@"context[_ ]length|maximum context|too many tokens", RegexOptions.IgnoreCase),
            "上下文超出模型上限：减少历史消息，或调低上下文窗口"),

        new(new Regex(@"insufficient[_ ]quota|exceeded your current quota|billing|余额|quota", RegexOptions.IgnoreCase),
            "账户额度不足：去上游控制台充值或检查套餐"),

        new(new Regex(@"timeout|timed out|deadline exceeded", RegexOptions.IgnoreCase),
            "上游超时：网络不通或服务端过慢"),

        new(new Regex(@"content[_ ]filter|safety|blocked", RegexOptions.IgnoreCase),
            "内容被上游安全策略拦截"),

        new(new Regex(@"unauthorized|forbidden|401|403", RegexOptions.IgnoreCase),
            "未授权：密钥无效，或该账号无权访问此资源"),
    };

    /// <summary>从上游响应体里取出可读说明；取不到或没匹配上返回空串。</summary>
    public static string Explain(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var message = ExtractMessage(raw);
        if (string.IsNullOrWhiteSpace(message)) return "";

        foreach (var rule in Rules)
        {
            var m = rule.Pattern.Match(message);
            if (!m.Success) continue;

            var text = rule.Explain;
            for (var i = 1; i < m.Groups.Count; i++)
                text = text.Replace($"${i}", m.Groups[i].Value);
            return text;
        }

        return "";
    }

    private static string ExtractMessage(string raw)
    {
        try
        {
            if (JsonNode.Parse(raw) is JsonObject obj)
            {
                var msg = obj["error"]?["message"]?.GetValue<string>()
                          ?? obj["message"]?.GetValue<string>()
                          ?? obj["error"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(msg)) return msg;
            }
        }
        catch
        {
            // 不是 JSON 就直接拿原文
        }
        return raw.Length > 400 ? raw[..400] : raw;
    }
}
