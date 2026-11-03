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
        AllowAutoRedirect = false,
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
            LogStore.Instance.Error("服务", "启动失败");
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
        catch (Exception)
        {
            LogStore.Instance.Warn("服务", "停止时出现异常");
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
            LogStore.Instance.Warn("路由", "未匹配路由");
            return;
        }

        var isChat = rest.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
                     && HttpMethods.IsPost(ctx.Request.Method);

        if (!ctx.Request.Headers.ContainsKey("Authorization"))
            LogStore.Instance.Warn("请求", "未携带 Authorization 头");

        var startedAt = DateTime.Now;
        var conversationId = "";
        var client = "unknown";
        var sessionSource = "none";
        var hasTraceId = false;
        var hasRequestId = false;
        var traceId = "";
        var dialogId = "";
        var dialogIdMissing = false;
        var diagnosticDetails = "";
        var diagnosticMode = "unknown";
        var diagnosticBodyBytes = -1;
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
            (client, conversationId, sessionSource, hasTraceId, hasRequestId) = IdentifyClient(ctx, payload, cfg.ClientType);
            if (client == "workbuddy")
                (traceId, dialogId, dialogIdMissing) = IdentifyWorkBuddyTurn(ctx, payload);
            if (cfg.ExperimentalDiagnostics) diagnosticDetails = DescribeIdentity(ctx, payload);
            var requestModel = payload is { Length: > 0 } ? ReadModel(payload) : "";
            var mode = ResolveMode(cfg, requestModel, route);
            if (cfg.ExperimentalDiagnostics)
            {
                diagnosticBodyBytes = payload?.Length ?? -1;
                diagnosticMode = mode.ToLowerInvariant() switch { "responses" => "responses", "rewrite" => "rewrite", "chat" => "chat", _ => "other" };
            }
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
            LogStore.Instance.Info("转发", "客户端已断开（不算上游失败）");
        }
        catch (Exception ex)
        {
            failed = true;
            LogStore.Instance.Error("转发", "请求转发失败");
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

            if (status >= 400)
                LogStore.Instance.Warn("请求", $"失败（HTTP {status}）");
            else
                LogStore.Instance.Request(route.Prefix, alias, target, status, sw.ElapsedMilliseconds);
            if (cfg.ExperimentalDiagnostics)
            {
                static string Count(JsonObject? obj, string first, string second)
                {
                    var value = obj?[first] ?? obj?[second];
                    return value is JsonValue number && number.TryGetValue<long>(out var count) && count >= 0 ? count.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown";
                }
                var selected = conversationId.Length >= 20 ? conversationId[..20] : "missing";
                var error = cancelled ? "cancelled" : failed ? "proxy_or_stream" : status >= 400 ? "http_error" : "none";
                LogStore.Instance.Diagnostic($"client={client} session_source={sessionSource} session_stat_sha256_20={selected} trace_present={hasTraceId} request_id_present={hasRequestId} mode={diagnosticMode} body_bytes={diagnosticBodyBytes} status={status} cancelled={cancelled} error_category={error} duration_ms={sw.ElapsedMilliseconds} response_stream={ctx.Response.ContentType?.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase) == true} usage_present={usage is not null} input_tokens={Count(usage, "input_tokens", "prompt_tokens")} output_tokens={Count(usage, "output_tokens", "completion_tokens")} {diagnosticDetails}");
            }
            // 展示名用映射名（alias），计价用真实名（target）——两列分开存，别把不同上游混在一起
            RecordUsage(route.Prefix, alias, target, usage, sw.ElapsedMilliseconds, status, conversationId, startedAt,
                        client, traceId, dialogId, dialogIdMissing);
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
            LogStore.Instance.Error("翻译", "请求转换失败");
            return (alias, target, null, false);
        }
        var url = Combine(route.Upstream, "/v1/responses");

        var resp = await SendResponsesAsync(ctx, route, url, body);
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
                LogStore.Instance.Info("翻译", "上游不支持思考参数，本次已去掉后重试");
                resp = await SendResponsesAsync(ctx, route, url, body);
                status = (int)resp.StatusCode;
            }
            else
            {
                // 不是这个原因，把已读出的错误体放回去，走原来的错误处理
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await ctx.Response.WriteAsync(probe);
                LogStore.Instance.Warn("上游", $"拒绝请求（HTTP {status}）");
                resp.Dispose();
                return (alias, target, null, false);
            }
        }

        if (status != 200)
        {
            var err = await resp.Content.ReadAsStringAsync(ctx.RequestAborted);
            if (status == 524)
                LogStore.Instance.Warn("上游", "HTTP 524：上游网关等待后端超时");
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsync(err);

            LogStore.Instance.Warn("上游", $"拒绝请求（HTTP {status}）");
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
                LogStore.Instance.Warn("上游", "流式响应失败或提前中断");
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
        CopyAuth(ctx, req, route);

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
            LogStore.Instance.Warn("上游", $"请求失败（HTTP {(int)resp.StatusCode}）");
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

    private async Task<HttpResponseMessage> SendResponsesAsync(HttpContext ctx, RouteRule route, string url, JsonObject body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        CopyAuth(ctx, req, route);
        return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
    }

    private static bool LooksLikeReasoningUnsupported(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.ToLowerInvariant();
        return t.Contains("reasoning")
               && (t.Contains("not supported") || t.Contains("unsupported"));
    }

    private static (string client, string session, string source, bool trace, bool request) IdentifyClient(
        HttpContext ctx, byte[]? payload, string? configuredClient)
    {
        var client = (configuredClient ?? "auto").Trim().ToLowerInvariant() switch
        {
            "workbuddy" => "workbuddy", "deepseek" or "harness" or "deepseek-harness" => "deepseek-harness", _ => "auto"
        };
        if (client == "auto")
        {
            var agent = ctx.Request.Headers.UserAgent.ToString();
            client = agent.Contains("workbuddy", StringComparison.OrdinalIgnoreCase) ? "workbuddy"
                : agent.Contains("deepseek", StringComparison.OrdinalIgnoreCase) && agent.Contains("harness", StringComparison.OrdinalIgnoreCase) ? "deepseek-harness" : "unknown";
        }

        static string Normalize(string? value)
        {
            value = value?.Trim();
            return Guid.TryParseExact(value, "D", out var id) || Guid.TryParseExact(value, "N", out id)
                ? id.ToString("D") : "";
        }

        var session = "";
        var source = "none";
        if (ctx.Request.Headers.TryGetValue("X-Conversation-ID", out var values) && values.Count == 1)
        {
            session = Normalize(values[0]);
            if (session.Length > 0) source = "header";
        }
        var trace = ctx.Request.Headers.ContainsKey("X-Trace-ID");
        var request = ctx.Request.Headers.ContainsKey("X-Conversation-Request-ID");
        try
        {
            if (payload is { Length: > 0 } && JsonNode.Parse(payload) is JsonObject body)
            {
                // 只检查明确的元数据位置，不遍历消息内容。
                foreach (var metadata in new[] { body, body["metadata"] as JsonObject })
                {
                    if (metadata is null) continue;
                    trace |= metadata.ContainsKey("traceId");
                    request |= metadata.ContainsKey("conversationRequestId");
                    if (session.Length == 0 && metadata["conversationId"] is JsonValue value
                        && value.TryGetValue<string>(out var text))
                    {
                        session = Normalize(text);
                        if (session.Length > 0) source = ReferenceEquals(metadata, body) ? "body" : "metadata";
                    }
                }
            }
        }
        catch (System.Text.Json.JsonException) { }
        if (session.Length > 0)
            session = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("conversation:" + session))).ToLowerInvariant();
        return (client, session, source, trace, request);
    }

    // 会话识别只检查协议元数据。请求/trace 摘要使用本进程随机密钥，避免保存原始标识，
    // 也避免不同进程的历史 trace 被意外关联；它们是关联线索，不是已证明的用户轮次。
    private static readonly byte[] UsageIdentityKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

    private static (string traceId, string dialogId, bool dialogIdMissing) IdentifyWorkBuddyTurn(HttpContext ctx, byte[]? payload)
    {
        JsonObject body;
        try
        {
            if (payload is not { Length: > 0 } || JsonNode.Parse(payload) is not JsonObject parsed)
                return ("", "", false);
            body = parsed;
        }
        catch (System.Text.Json.JsonException) { return ("", "", false); }

        // 任一位置的字段无效、重复或不一致时，禁止据此推断。
        (Guid? id, bool missing, bool valid) ReadIdentity(string header, string field)
        {
            var found = false;
            var valid = true;
            Guid? first = null;
            void Add(string? value)
            {
                found = true;
                if (!Guid.TryParseExact(value?.Trim(), "D", out var id) && !Guid.TryParseExact(value?.Trim(), "N", out id))
                {
                    valid = false;
                    return;
                }
                if (first is not null && first != id) valid = false;
                first ??= id;
            }
            if (ctx.Request.Headers.TryGetValue(header, out var headers))
            {
                if (headers.Count != 1) { found = true; valid = false; }
                else Add(headers[0]);
            }
            foreach (var obj in new[] { body, body["metadata"] as JsonObject })
            {
                if (obj is null || !obj.ContainsKey(field)) continue;
                if (obj[field] is JsonValue value && value.TryGetValue<string>(out var text)) Add(text);
                else { found = true; valid = false; }
            }
            return (first, !found, valid);
        }

        var conversation = ReadIdentity("X-Conversation-ID", "conversationId");
        var trace = ReadIdentity("X-Trace-ID", "traceId");
        var request = ReadIdentity("X-Conversation-Request-ID", "conversationRequestId");
        if (!conversation.valid || conversation.missing || !trace.valid) return ("", "", false);
        // WorkBuddy 的辅助请求可能携带非 UUID 请求头；它不是“缺失”，只能作为同 trace 的
        // 唯一主请求的候选关联。多值、正文同时提供、空值等情况保持未知，不能据此合并。
        var unrecognizedRequest = !request.valid &&
            ctx.Request.Headers.TryGetValue("X-Conversation-Request-ID", out var requestHeaders) &&
            requestHeaders.Count == 1 && !string.IsNullOrWhiteSpace(requestHeaders[0]) &&
            !body.ContainsKey("conversationRequestId") &&
            !(body["metadata"] is JsonObject metadata && metadata.ContainsKey("conversationRequestId"));
        if (!request.valid && !unrecognizedRequest) return ("", "", false);
        static string Digest(string domain, Guid? id)
        {
            if (id is null) return "";
            var bytes = Encoding.UTF8.GetBytes(domain + ":" + id.Value.ToString("D"));
            return Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(UsageIdentityKey, bytes)).ToLowerInvariant();
        }
        return (Digest("trace", trace.id), Digest("dialog", request.id), request.missing || unrecognizedRequest);
    }

    // 仅在本进程生命周期内关联诊断标识，避免无密钥哈希被离线枚举。
    private static readonly byte[] DiagnosticKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

    private static string DescribeIdentity(HttpContext ctx, byte[]? payload)
    {
        var fields = new List<string>();
        var sessions = new List<Guid>();
        void AddId(string location, string? text, bool conversation)
        {
            if (!Guid.TryParseExact(text?.Trim(), "D", out var id) && !Guid.TryParseExact(text?.Trim(), "N", out id)) return;
            if (conversation) sessions.Add(id);
            var hash = System.Security.Cryptography.HMACSHA256.HashData(DiagnosticKey, Encoding.UTF8.GetBytes(id.ToString("D")));
            fields.Add($"{location}={Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant()}");
        }
        void Header(string name, string location, bool conversation)
        {
            if (ctx.Request.Headers.TryGetValue(name, out var values) && values.Count == 1)
                AddId(location, values[0], conversation);
        }
        Header("X-Conversation-ID", "header_conversation", true);
        Header("X-Trace-ID", "header_trace", false);
        Header("X-Conversation-Request-ID", "header_request", false);
        var requestStream = "unknown";
        var messageCount = -1;
        var toolCount = -1;
        try
        {
            if (payload is { Length: > 0 } && JsonNode.Parse(payload) is JsonObject body)
            {
                void ObjectFields(JsonObject? obj, string location)
                {
                    if (obj is null) return;
                    foreach (var (name, suffix, conversation) in new[] { ("conversationId", "conversation", true), ("traceId", "trace", false), ("conversationRequestId", "request", false) })
                        if (obj[name] is JsonValue value && value.TryGetValue<string>(out var text)) AddId(location + "_" + suffix, text, conversation);
                }
                ObjectFields(body, "body");
                ObjectFields(body["metadata"] as JsonObject, "metadata");
                if (body["stream"] is JsonValue stream && stream.TryGetValue<bool>(out var enabled)) requestStream = enabled ? "true" : "false";
                if (body["messages"] is JsonArray messages) messageCount = messages.Count;
                if (body["tools"] is JsonArray tools) toolCount = tools.Count;
            }
        }
        catch (System.Text.Json.JsonException) { }
        var agreement = sessions.Count < 2 ? "unknown" : sessions.Distinct().Count() == 1 ? "same" : "different";
        return $"id_digest=hmac_sha256_64_process phase=completed is_chat={HttpMethods.IsPost(ctx.Request.Method) && (ctx.Request.Path.Value?.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) == true)} request_stream={requestStream} message_count={messageCount} tool_count={toolCount} conversation_agreement={agreement} {string.Join(" ", fields)}";
    }

    /// <summary>把 usage 记进统计。兼容 Responses（input/output）与 Chat（prompt/completion）两种形状。</summary>
    /// <param name="model">展示名：映射名（客户端填的别名），界面按它分组。</param>
    /// <param name="target">真实名：发给上游的模型名，计价用它。</param>
    private static void RecordUsage(string route, string model, string target, JsonObject? usage, long ms, int status,
                                    string conversationId, DateTime startedAt, string client, string traceId,
                                    string dialogId, bool dialogIdMissing)
    {
        if (usage is null) { UsageStats.Record(route, model, target, 0, 0, 0, 0, ms, status, conversationId, startedAt, client, traceId, dialogId, dialogIdMissing); return; }

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

        UsageStats.Record(route, model, target, prompt, completion, cacheRead, cacheWrite, ms, status, conversationId, startedAt, client, traceId, dialogId, dialogIdMissing);
    }

    private static void CopyAuth(HttpContext ctx, HttpRequestMessage req, RouteRule route)
    {
        var authorization = ctx.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(route.ProtectedUpstreamKey))
        {
            var local = SecretProtection.Unprotect(route.ProtectedLocalKey);
            var upstream = SecretProtection.Unprotect(route.ProtectedUpstreamKey);
            if (!string.IsNullOrEmpty(local) && !string.Equals(authorization, "Bearer " + local, StringComparison.Ordinal))
                throw new InvalidOperationException("客户端 API Key 与此上游配置不匹配，已拒绝转发。");
            if (req.RequestUri is not { } destination || (destination.Scheme != Uri.UriSchemeHttps && !(destination.Scheme == Uri.UriSchemeHttp && destination.IsLoopback)))
                throw new InvalidOperationException("已配置上游密钥时只能使用 HTTPS 或本机 HTTP 地址。");
            authorization = "Bearer " + upstream;
        }
        if ((!string.IsNullOrEmpty(authorization) || ctx.Request.Headers.ContainsKey("OpenAI-Organization") || ctx.Request.Headers.ContainsKey("OpenAI-Project"))
            && (req.RequestUri is not { } uri || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))))
            throw new InvalidOperationException("包含认证信息的请求只能发送至 HTTPS 或本机 HTTP 地址。");
        if (!string.IsNullOrEmpty(authorization))
            req.Headers.TryAddWithoutValidation("Authorization", authorization);
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
