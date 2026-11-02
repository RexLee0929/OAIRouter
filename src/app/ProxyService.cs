using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OAIRouter;

/// <summary>
/// 本地路由代理。按路径第一段选择上游：
///   http://127.0.0.1:端口/openai/v1/chat/completions → OpenAI 官方（走 Responses 翻译）
///   http://127.0.0.1:端口/newapi/v1/chat/completions → NewAPI 中转（原样透传）
/// </summary>
public sealed class ProxyService
{
    private WebApplication? _app;
    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(20),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public bool IsRunning => _app is not null;
    public int Port { get; private set; }
    public long TotalRequests;
    public long FailedRequests;
    public long TotalMs;

    public event Action<string>? Notify;

    public async Task<(bool ok, string message)> StartAsync(AppConfig cfg)
    {
        if (IsRunning) return (false, "服务已在运行");

        try
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls($"http://127.0.0.1:{cfg.Port}");

            var app = builder.Build();

            app.MapGet("/", () => Results.Text($"{Brand.Name} {Brand.Version} 运行中", "text/plain; charset=utf-8"));
            app.MapGet("/api/status", () => Results.Json(new
            {
                name = Brand.Name,
                version = Brand.Version,
                author = Brand.Author,
                port = cfg.Port,
                routes = cfg.Routes.Select(r => new { r.Prefix, r.Name, r.Upstream, r.Mode }),
                mappings = cfg.ModelMap.Select(m => new { m.Alias, m.Target }),
                total = TotalRequests,
                failed = FailedRequests,
                usage = BuildUsageSnapshot(cfg),
            }));

            app.Map("/{**path}", ctx => HandleAsync(ctx, cfg));

            await app.StartAsync();

            _app = app;
            Port = cfg.Port;
            LogStore.Instance.Info("服务", $"已启动，监听 http://127.0.0.1:{cfg.Port}");
            return (true, "ok");
        }
        catch (Exception ex)
        {
            var msg = ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
                ? $"端口 {cfg.Port} 已被占用，请换一个端口"
                : ex.Message;
            LogStore.Instance.Error("服务", $"启动失败：{msg}");
            Notify?.Invoke($"服务启动失败：{msg}");
            return (false, msg);
        }
    }

    public async Task StopAsync()
    {
        if (_app is null) return;
        try
        {
            await _app.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogStore.Instance.Warn("服务", $"停止时出现异常：{ex.Message}");
        }
        finally
        {
            _app = null;
            LogStore.Instance.Info("服务", "已停止");
        }
    }

    private static object BuildUsageSnapshot(AppConfig cfg)
    {
        var summary = UsageStats.Summary(cfg);
        return new
        {
            requests = summary.Requests,
            failed = summary.Failed,
            promptTokens = summary.Prompt,
            completionTokens = summary.Completion,
            averageMs = summary.Requests > 0 ? summary.TotalMs / summary.Requests : 0,
            cost = Math.Round(summary.Cost, 6),
        };
    }

    private async Task HandleAsync(HttpContext ctx, AppConfig cfg)
    {
        var path = ctx.Request.Path.Value ?? "/";
        var trimmed = path.TrimStart('/');
        var slash = trimmed.IndexOf('/');
        var prefix = slash < 0 ? trimmed : trimmed[..slash];
        var rest = slash < 0 ? "/" : trimmed[slash..];

        var route = cfg.Routes.FirstOrDefault(r =>
            !string.IsNullOrWhiteSpace(r.Prefix)
            && r.Prefix.Trim() != "*"
            && string.Equals(r.Prefix.Trim('/'), prefix, StringComparison.OrdinalIgnoreCase));

        if (route is null)
        {
            // 没有匹配到前缀时，退回到「默认路由」（前缀留空或填 *），整条路径原样转发
            route = cfg.Routes.FirstOrDefault(r =>
                string.IsNullOrWhiteSpace(r.Prefix) || r.Prefix.Trim() == "*");

            if (route is not null) rest = path;
        }

        if (route is null)
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsync(new JsonObject
            {
                ["error"] = new JsonObject
                {
                    ["message"] = $"未匹配到路由前缀「{prefix}」。可用前缀：{string.Join("、", cfg.Routes.Where(r => !string.IsNullOrWhiteSpace(r.Prefix)).Select(r => "/" + r.Prefix.Trim('/')))}；也可以添加一条前缀留空的「默认路由」兜底。",
                    ["type"] = "route_not_found",
                },
            }.ToJsonString());
            LogStore.Instance.Warn("路由", $"未匹配前缀 /{prefix}");
            return;
        }

        var isChat = rest.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
                     && HttpMethods.IsPost(ctx.Request.Method);

        if (!ctx.Request.Headers.ContainsKey("Authorization"))
            LogStore.Instance.Warn("请求", $"{route.Prefix}{rest} 未携带 Authorization 头（客户端的 API Key 可能为空）");

        var startedAt = DateTime.Now;
        // WorkBuddy 模型请求携带对话 ID。仅用作本地统计，不转发给上游。
        var conversationId = ReadConversationId(ctx);
        var sw = Stopwatch.StartNew();
        Interlocked.Increment(ref TotalRequests);

        string alias = "";
        string target = "";
        JsonObject? usage = null;
        var failed = false;
        var cancelled = false;

        try
        {
            // JSON 请求体只读一次；其他格式（例如文件上传）保持原样透传。
            var contentType = ctx.Request.ContentType ?? "";
            var isJson = isChat || contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)
                || contentType.Split(';')[0].Trim().EndsWith("+json", StringComparison.OrdinalIgnoreCase);
            var payload = isJson ? await ReadBodyAsync(ctx) : null;
            var requestModel = payload is { Length: > 0 } ? ReadModel(payload) : "";
            var mode = ResolveMode(cfg, requestModel, route);
            alias = requestModel;
            target = ResolveModel(cfg, alias);

            if (isChat && string.Equals(mode, "responses", StringComparison.OrdinalIgnoreCase))
            {
                (alias, target, usage, failed) = await TranslateChatAsync(ctx, cfg, route, payload ?? Array.Empty<byte>());
            }
            else if (isChat && string.Equals(mode, "rewrite", StringComparison.OrdinalIgnoreCase))
            {
                alias = requestModel;
                target = ResolveModel(cfg, alias);
                var rewritten = RewriteChatBody(payload ?? Array.Empty<byte>(), cfg.MinOutputTokens, cfg.MaxOutputTokens);
                rewritten = ApplyModelMapping(rewritten, target);   // 透传前必须把别名换成真实模型名
                (_, _, usage) = await PassthroughAsync(ctx, route, rest, rewritten, target);
            }
            else
            {
                alias = requestModel;
                target = ResolveModel(cfg, alias);
                var forwarded = payload is null ? null : ApplyModelMapping(payload, target);
                (_, _, usage) = await PassthroughAsync(ctx, route, rest, forwarded, target);
            }
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            cancelled = true;
            LogStore.Instance.Info("转发", $"{route.Prefix} 客户端已断开（不算上游失败）");
        }
        catch (Exception ex)
        {
            failed = true;
            LogStore.Instance.Error("转发", $"{route.Prefix} {ex.GetType().Name}: {ex.Message}");
            if (ctx.Response.HasStarted)
            {
                // 流已开始时无法改状态码；主动中止，避免客户端误以为响应完整。
                ctx.Abort();
            }
            else
            {
                ctx.Response.StatusCode = 502;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await ctx.Response.WriteAsync(new JsonObject
                {
                    ["error"] = new JsonObject { ["message"] = $"代理转发失败：{ex.Message}", ["type"] = "proxy_error" },
                }.ToJsonString());
            }
        }
        finally
        {
            // 无论成功、失败还是客户端断开，都记一条：概览与用量必须来自同一份数据，否则两边对不上
            sw.Stop();
            // 499 = 客户端主动断开，语义上不是上游故障
            var status = cancelled ? 499 : failed ? 502 : ctx.Response.StatusCode;
            Interlocked.Add(ref TotalMs, sw.ElapsedMilliseconds);
            if (status >= 400 && status != 499) Interlocked.Increment(ref FailedRequests);

            LogStore.Instance.Request(route.Prefix, alias, target, status, sw.ElapsedMilliseconds);
            // 展示名用映射名（alias），计价用真实名（target）——两列分开存，别把不同上游混在一起
            RecordUsage(route.Prefix, alias, target, usage, sw.ElapsedMilliseconds, status, conversationId, startedAt);
        }
    }

    /// <summary>Chat Completions → Responses API 翻译转发</summary>
    private async Task<(string alias, string target, JsonObject? usage, bool failed)> TranslateChatAsync(HttpContext ctx, AppConfig cfg, RouteRule route, byte[] bodyBytes)
    {
        var raw = Encoding.UTF8.GetString(bodyBytes);

        JsonObject? chat;
        try { chat = JsonNode.Parse(raw) as JsonObject; }
        catch
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsync("{\"error\":{\"message\":\"请求体不是合法 JSON\"}}");
            return ("", "", null, false);
        }
        if (chat is null) { ctx.Response.StatusCode = 400; return ("", "", null, false); }

        var alias = chat["model"]?.GetValue<string>() ?? "";
        var target = ResolveModel(cfg, alias);
        var stream = chat["stream"]?.GetValue<bool>() ?? false;
        var wantUsage = chat["stream_options"]?["include_usage"]?.GetValue<bool>() ?? false;

        JsonObject body;
        try
        {
            body = Translator.BuildResponsesRequest(chat, target, cfg.MinOutputTokens, cfg.MaxOutputTokens);
        }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = 400;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsync(new JsonObject
            {
                ["error"] = new JsonObject
                {
                    ["message"] = $"请求体无法转换成 Responses 格式：{ex.Message}",
                    ["type"] = "translate_error",
                },
            }.ToJsonString());
            LogStore.Instance.Error("翻译", $"转换失败：{ex.GetType().Name} {ex.Message}");
            return (alias, target, null, false);
        }
        var url = Combine(route.Upstream, "/v1/responses");

        var resp = await SendResponsesAsync(ctx, url, body);
        try
        {
        var status = (int)resp.StatusCode;

        // 该模型不支持 reasoning：只对本次请求去掉后重试一次
        if (status == 400 && body.ContainsKey("reasoning"))
        {
            var probe = await resp.Content.ReadAsStringAsync(ctx.RequestAborted);
            if (LooksLikeReasoningUnsupported(probe))
            {
                resp.Dispose();
                body.Remove("reasoning");
                LogStore.Instance.Info("翻译", $"{target} 不支持思考参数，本次已自动去掉后重试；建议把该模型映射的「模式」改成 chat");
                resp = await SendResponsesAsync(ctx, url, body);
                status = (int)resp.StatusCode;
            }
            else
            {
                // 不是这个原因，把已读出的错误体放回去，走原来的错误处理
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await ctx.Response.WriteAsync(probe);
                var why = UpstreamError.Explain(probe);
                LogStore.Instance.Warn("上游", $"{route.Prefix} 拒绝：{(why.Length > 0 ? why + " ｜ 原文：" : "")}{Truncate(probe, 120)}");
                resp.Dispose();
                return (alias, target, null, false);
            }
        }

        if (status != 200)
        {
            var err = await resp.Content.ReadAsStringAsync(ctx.RequestAborted);
            if (status == 524)
                LogStore.Instance.Warn("上游", $"{route.Prefix} 返回 524：上游网关等待后端超时，请联系上游服务商排查");
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsync(err);

            var explain = UpstreamError.Explain(err);
            var detail = explain.Length > 0
                ? $"{explain} ｜ 原文：{Truncate(err, 120)}"
                : Truncate(err, 160);
            LogStore.Instance.Warn("上游", $"{route.Prefix} 拒绝：{detail}");
            resp.Dispose();
            return (alias, target, null, false);
        }

        if (stream)
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream; charset=utf-8";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            var output = ctx.Response.Body;
            var translator = new SseTranslator(alias, wantUsage, async s =>
            {
                var bytes = Encoding.UTF8.GetBytes(s);
                await output.WriteAsync(bytes, ctx.RequestAborted);
                await output.FlushAsync(ctx.RequestAborted);
            });

            await using var up = await resp.Content.ReadAsStreamAsync(ctx.RequestAborted);
            using var reader = new StreamReader(up, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 8192, leaveOpen: true);
            var buffer = new char[8192];
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(), ctx.RequestAborted)) > 0)
                await translator.FeedAsync(new string(buffer, 0, read));

            await translator.EndAsync();
            if (translator.Failed)
                LogStore.Instance.Warn("上游", $"{route.Prefix} 流式响应失败或提前中断");
            resp.Dispose();
            return (alias, target, translator.Usage, translator.Failed);
        }
        else
        {
            var json = await resp.Content.ReadAsStringAsync(ctx.RequestAborted);
            JsonObject? parsed = null;
            try { parsed = JsonNode.Parse(json) as JsonObject; } catch { }
            var outJson = parsed is null
                ? json
                : Translator.ToChatCompletion(parsed, alias).ToJsonString();

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsync(outJson);
            resp.Dispose();
            return (alias, target, parsed?["usage"] as JsonObject, false);
        }
        }
        finally
        {
            resp.Dispose();
        }
    }

    /// <summary>原样透传（NewAPI 等中转）</summary>
    private async Task<(string alias, string target, JsonObject? usage)> PassthroughAsync(
        HttpContext ctx, RouteRule route, string rest, byte[]? bodyOverride = null, string? model = null)
    {
        var url = Combine(route.Upstream, rest) + ctx.Request.QueryString.Value;
        JsonObject? returnUsage = null;

        using var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), url);
        var payload = Array.Empty<byte>();

        if (bodyOverride is not null || ctx.Request.ContentLength > 0 || ctx.Request.Headers.ContainsKey("Transfer-Encoding") || HttpMethods.IsPost(ctx.Request.Method))
        {
            payload = bodyOverride ?? await ReadBodyAsync(ctx);
            if (payload.Length > 0)
            {
                req.Content = new ByteArrayContent(payload);
                var ct = ctx.Request.ContentType ?? "application/json";
                req.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(ct);
            }
        }

        // 透传模式不解析请求体，这里补一次，否则统计里没有模型名
        if (string.IsNullOrWhiteSpace(model) && payload.Length > 0)
            model = ReadModel(payload);
        CopyAuth(ctx, req);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);

        ctx.Response.StatusCode = (int)resp.StatusCode;
        foreach (var h in resp.Headers)
            if (h.Key is not ("Transfer-Encoding" or "Connection"))
                ctx.Response.Headers[h.Key] = h.Value.ToArray();
        foreach (var h in resp.Content.Headers)
            if (h.Key is not ("Transfer-Encoding" or "Content-Length"))
                ctx.Response.Headers[h.Key] = h.Value.ToArray();
        if (resp.Content.Headers.ContentType?.MediaType?.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) == true)
        {
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
        }

        await using var up = await resp.Content.ReadAsStreamAsync(ctx.RequestAborted);
        var sniffer = new UsageSniffer();
        var buffer = new byte[8192];
        int read;
        while ((read = await up.ReadAsync(buffer, ctx.RequestAborted)) > 0)
        {
            sniffer.Feed(buffer.AsSpan(0, read));
            await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, read), ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        }
        returnUsage = sniffer.ToUsage();

        if ((int)resp.StatusCode >= 400)
        {
            var bare = rest.TrimEnd('/') is "/v1" or "";
            var hint = bare
                ? "（请求落在了裸路径上：如果客户端开了「自定义协议」，它不会自动补 /chat/completions，请关掉该选项）"
                : "";
            var timeoutHint = (int)resp.StatusCode == 524 ? "（上游网关等待后端超时，请联系上游服务商排查）" : "";
            LogStore.Instance.Warn("上游", $"{route.Prefix}{rest} → {(int)resp.StatusCode}{hint}{timeoutHint}");
        }

        return (model ?? "", model ?? "", returnUsage);
    }

    private static async Task<byte[]> ReadBodyAsync(HttpContext ctx)
    {
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
        return ms.ToArray();
    }

    private static string ReadModel(byte[] payload)
    {
        try
        {
            return JsonNode.Parse(System.Text.Encoding.UTF8.GetString(payload))?["model"]?.GetValue<string>() ?? "";
        }
        catch { return ""; }
    }

    /// <summary>
    /// 透传/改写模式下把请求体里的 model 换成映射后的真实模型名。
    /// 不做这一步，上游收到的是别名，会报「No available channel for model xxx」。
    /// </summary>
    private static byte[] ApplyModelMapping(byte[] payload, string target)
    {
        if (payload.Length == 0 || string.IsNullOrWhiteSpace(target)) return payload;

        try
        {
            if (JsonNode.Parse(Encoding.UTF8.GetString(payload)) is not JsonObject body) return payload;

            var current = body["model"]?.GetValue<string>();
            if (string.Equals(current, target, StringComparison.Ordinal)) return payload;

            body["model"] = target;
            return Encoding.UTF8.GetBytes(body.ToJsonString());
        }
        catch
        {
            return payload;
        }
    }

    /// <summary>
    /// rewrite 模式：只做参数兼容改写，仍走 /v1/chat/completions。
    /// 工具调用与思考档位在 chat/completions 上互斥，这里把思考压成 none 以保住工具。
    /// </summary>
    private static byte[] RewriteChatBody(byte[] payload, int minOutputTokens, int maxOutputTokens)
    {
        try
        {
            if (JsonNode.Parse(System.Text.Encoding.UTF8.GetString(payload)) is not JsonObject body) return payload;

            var hasTools = body["tools"] is JsonArray { Count: > 0 };
            if (hasTools && body["reasoning_effort"] is not null)
                body["reasoning_effort"] = "none";

            if (body["max_tokens"] is JsonNode mt)
            {
                body["max_completion_tokens"] ??= mt.DeepClone();
                body.Remove("max_tokens");
            }
            var floor = Math.Max(1, minOutputTokens);
            if (body["max_completion_tokens"] is JsonNode mct && mct.GetValue<int?>() is { } mv)
            {
                var clamped = Math.Max(floor, mv);
                if (maxOutputTokens > 0) clamped = Math.Min(clamped, maxOutputTokens);
                if (clamped != mv) body["max_completion_tokens"] = clamped;
            }

            return System.Text.Encoding.UTF8.GetBytes(body.ToJsonString());
        }
        catch { return payload; }
    }

    /// <summary>该请求用哪种模式：映射条目里单独指定过就听映射的，否则跟随路由。</summary>
    public static string ResolveMode(AppConfig cfg, string alias, RouteRule route)
    {
        var map = cfg.ModelMap.FirstOrDefault(m =>
            string.Equals((m.Alias ?? "").Trim(), (alias ?? "").Trim(), StringComparison.Ordinal));

        return string.IsNullOrWhiteSpace(map?.Mode) ? route.Mode : map!.Mode.Trim();
    }

    private async Task<HttpResponseMessage> SendResponsesAsync(HttpContext ctx, string url, JsonObject body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        CopyAuth(ctx, req);
        return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
    }

    private static bool LooksLikeReasoningUnsupported(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.ToLowerInvariant();
        return t.Contains("reasoning")
               && (t.Contains("not supported") || t.Contains("unsupported"));
    }

    private static string ReadConversationId(HttpContext ctx)
    {
        // 禁止把每次请求都变的 request/message ID 冒充会话 ID。
        if (!ctx.Request.Headers.TryGetValue("X-Conversation-ID", out var values) || values.Count != 1)
            return "";
        var value = values[0]?.Trim() ?? "";
        return Guid.TryParseExact(value, "D", out var id) || Guid.TryParseExact(value, "N", out id)
            ? id.ToString("D") : "";
    }

    /// <summary>把 usage 记进统计。兼容 Responses（input/output）与 Chat（prompt/completion）两种形状。</summary>
    /// <param name="model">展示名：映射名（客户端填的别名），界面按它分组。</param>
    /// <param name="target">真实名：发给上游的模型名，计价用它。</param>
    private static void RecordUsage(string route, string model, string target, JsonObject? usage, long ms, int status,
                                    string conversationId, DateTime startedAt)
    {
        if (usage is null) { UsageStats.Record(route, model, target, 0, 0, 0, 0, ms, status, conversationId, startedAt); return; }

        var prompt = usage["input_tokens"]?.GetValue<long?>()
                     ?? usage["prompt_tokens"]?.GetValue<long?>() ?? 0;
        var completion = usage["output_tokens"]?.GetValue<long?>()
                         ?? usage["completion_tokens"]?.GetValue<long?>() ?? 0;

        // 缓存命中/写入：Responses 放在 input_tokens_details，Chat 放在 prompt_tokens_details，
        // 另有一些上游用 Anthropic 风格的顶层字段
        var details = usage["input_tokens_details"] as JsonObject
                      ?? usage["prompt_tokens_details"] as JsonObject;

        var cacheRead = details?["cached_tokens"]?.GetValue<long?>()
                        ?? usage["cache_read_input_tokens"]?.GetValue<long?>() ?? 0;
        var cacheWrite = details?["cache_write_tokens"]?.GetValue<long?>()
                         ?? usage["cache_creation_input_tokens"]?.GetValue<long?>() ?? 0;

        UsageStats.Record(route, model, target, prompt, completion, cacheRead, cacheWrite, ms, status, conversationId, startedAt);
    }

    private static void CopyAuth(HttpContext ctx, HttpRequestMessage req)
    {
        if (ctx.Request.Headers.TryGetValue("Authorization", out var auth))
            req.Headers.TryAddWithoutValidation("Authorization", auth.ToString());
        if (ctx.Request.Headers.TryGetValue("OpenAI-Organization", out var org))
            req.Headers.TryAddWithoutValidation("OpenAI-Organization", org.ToString());
        if (ctx.Request.Headers.TryGetValue("OpenAI-Project", out var proj))
            req.Headers.TryAddWithoutValidation("OpenAI-Project", proj.ToString());
    }

    public static string ResolveModel(AppConfig cfg, string alias)
    {
        if (string.IsNullOrWhiteSpace(alias)) return alias;
        // 上游的模型名是区分大小写的，这里不能做不敏感匹配，否则 aa 会命中 AA 的规则
        var hit = cfg.ModelMap.FirstOrDefault(m =>
            string.Equals(m.Alias?.Trim(), alias, StringComparison.Ordinal));
        return string.IsNullOrWhiteSpace(hit?.Target) ? alias : hit!.Target.Trim();
    }

    private static string Combine(string baseUrl, string rest)
    {
        var b = (baseUrl ?? "").TrimEnd('/');
        var r = rest.StartsWith('/') ? rest : "/" + rest;
        if (r.StartsWith("/v1/") && b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            return b + r[3..];
        return b + r;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    /// <summary>
    /// 透传模式下上游的响应是字节流直接转发的，这里顺手扫一眼最后出现的 usage 字段。
    /// 只做尽力而为：扫不到就记 0，不会影响转发本身。
    /// </summary>
    private sealed class UsageSniffer
    {
        private readonly System.Text.StringBuilder _tail = new();
        private JsonObject? _usage;

        public void Feed(ReadOnlySpan<byte> data)
        {
            _tail.Append(System.Text.Encoding.UTF8.GetString(data));
            if (_tail.Length > 16384) _tail.Remove(0, _tail.Length - 16384);
            if (_tail.ToString().Contains("\"usage\"", StringComparison.Ordinal)) Extract();
        }

        private void Extract()
        {
            var text = _tail.ToString();
            var idx = text.LastIndexOf("\"usage\"", StringComparison.Ordinal);
            if (idx < 0) return;

            var start = text.IndexOf('{', idx);
            if (start < 0) return;

            var depth = 0;
            for (var i = start; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}')
                {
                    depth--;
                    if (depth != 0) continue;
                    try { _usage = JsonNode.Parse(text[start..(i + 1)]) as JsonObject; } catch { }
                    return;
                }
            }
        }

        public JsonObject? ToUsage() => _usage;
    }
}
