using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Http;
using OAIRouter;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

var identify = typeof(ProxyService).GetMethod("IdentifyClient", BindingFlags.Static | BindingFlags.NonPublic)!;
(string client, string session, string source, bool trace, bool request) Identify(DefaultHttpContext ctx, string body, string mode = "auto") =>
    ((string, string, string, bool, bool))identify.Invoke(null, new object?[] { ctx, Encoding.UTF8.GetBytes(body), mode })!;

const string session = "12345678-1234-1234-1234-123456789abc";
const string requestId = "87654321-4321-4321-4321-cba987654321";
var sessionHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("conversation:" + session))).ToLowerInvariant();
var ctx = new DefaultHttpContext();
ctx.Request.Headers.UserAgent = "WorkBuddy/test";
ctx.Request.Headers["X-Conversation-ID"] = session.ToUpperInvariant();
ctx.Request.Headers["X-Conversation-Request-ID"] = requestId;
var result = Identify(ctx, "{}");
Check(result.client == "workbuddy" && result.session.Length > 0 && result.session != session && result.source == "header" && result.request, "请求头会话识别失败");
Check(result.session == sessionHash, "会话固定域哈希失败");
ctx.Request.Headers["X-Conversation-ID"] = "f4317876-20a8-41b2-9552-f24afa6eb99d";
Check(Identify(ctx, "{}").session[..20] == "03b9d32021ae5ab90ac1", "给定会话 ID 与统计摘要不匹配");
ctx.Request.Headers["X-Conversation-ID"] = session;
ctx.Request.Headers.Remove("X-Conversation-ID");
result = Identify(ctx, $"{{\"metadata\":{{\"conversationId\":\"{session}\",\"traceId\":\"{requestId}\"}}}}");
Check(result.session == sessionHash && result.source == "metadata" && result.trace, "元数据会话识别失败");
result = Identify(ctx, $"{{\"conversationRequestId\":\"{requestId}\",\"traceId\":\"{session}\"}}");
Check(result.session == "" && result.source == "none" && result.trace && result.request, "单次请求 ID 不应作为会话 ID");
ctx.Request.Headers["X-Conversation-ID"] = "not-a-guid";
Check(Identify(ctx, "{}").session == "", "无效会话 ID 不应被接受");
ctx.Request.Headers.Remove("User-Agent");
Check(Identify(ctx, "{}").client == "unknown", "无客户端标识时不应猜测 WorkBuddy");
ctx.Request.Headers["X-Conversation-ID"] = session;
Check(Identify(ctx, "{}").session == sessionHash, "客户端识别变化不应拆分会话");
Check(Identify(ctx, "{}", "deepseek").client == "deepseek-harness", "旧客户端配置未兼容");
Check(Identify(ctx, "{}", "deepseek-harness").client == "deepseek-harness", "显式客户端选择未生效");
Check(!new AppConfig().ExperimentalDiagnostics, "诊断应默认关闭");
var describe = typeof(ProxyService).GetMethod("DescribeIdentity", BindingFlags.Static | BindingFlags.NonPublic)!;
string Describe(DefaultHttpContext context, string body) => (string)describe.Invoke(null, new object?[] { context, Encoding.UTF8.GetBytes(body) })!;
ctx.Request.Headers["X-Conversation-ID"] = session;
var conflicting = $"{{\"conversationId\":\"{requestId}\",\"traceId\":\"{session}\",\"stream\":true,\"messages\":[{{\"content\":\"private-prompt-never-log\"}}]}}";
var detail = Describe(ctx, conflicting);
Check(detail.Contains("conversation_agreement=different") && detail.Contains("header_conversation=") && detail.Contains("body_conversation=") && detail.Contains("body_trace=") && detail.Contains("request_stream=true") && detail.Contains("message_count=1"), "冲突诊断应区分字段位置并提供结构信息");
Check(detail == Describe(ctx, conflicting), "同一进程内诊断标识应稳定");
var nestedOnly = Describe(new DefaultHttpContext(), $"{{\"messages\":[{{\"conversationId\":\"{session}\",\"content\":\"private-prompt-never-log\"}}]}}");
Check(!nestedOnly.Contains("conversation=") && !nestedOnly.Contains("private-prompt-never-log"), "不得扫描消息正文中的标识");
Check(!detail.Contains(session) && !detail.Contains(requestId) && !detail.Contains("private-prompt-never-log"), "诊断不得泄露标识或正文");
Check(Identify(ctx, conflicting).session == sessionHash, "冲突时不得静默改写既有会话识别策略");
Check(Describe(ctx, $"{{\"metadata\":{{\"conversationId\":\"{session}\"}}}}").Contains("conversation_agreement=same"), "相同会话来源应显示一致");

var turnMethod = typeof(ProxyService).GetMethod("IdentifyWorkBuddyTurn", BindingFlags.Static | BindingFlags.NonPublic)!;
(string trace, string dialog, bool missing) Turn(DefaultHttpContext context, string body) =>
    ((string, string, bool))turnMethod.Invoke(null, new object?[] { context, Encoding.UTF8.GetBytes(body) })!;
var turnCtx = new DefaultHttpContext();
turnCtx.Request.Headers["X-Conversation-ID"] = session;
turnCtx.Request.Headers["X-Trace-ID"] = requestId;
turnCtx.Request.Headers["X-Conversation-Request-ID"] = requestId;
var turnBody = $"{{\"conversationId\":\"{session.ToUpperInvariant()}\",\"traceId\":\"{requestId.Replace("-", "").ToUpperInvariant()}\",\"conversationRequestId\":\"{requestId}\"}}";
var turn = Turn(turnCtx, turnBody);
Check(turn.trace.Length == 64 && turn.dialog.Length == 64 && !turn.missing && turn.trace != turn.dialog, "请求及追踪标识应分别安全摘要");
Check(!turn.trace.Contains(requestId) && !turn.dialog.Contains(requestId), "识别结果不能包含原始标识");
var bodyOnly = Turn(new DefaultHttpContext(), $"{{\"metadata\":{{\"conversationId\":\"{session.Replace("-", "")}\",\"traceId\":\"{requestId}\",\"conversationRequestId\":\"{requestId.ToUpperInvariant()}\"}}}}");
Check(bodyOnly == turn, "不同来源、大小写和 GUID 格式应产生相同摘要");
var traceOnly = Turn(turnCtx, $"{{\"traceId\":\"{requestId}\"}}");
Check(traceOnly.trace == turn.trace, "同一追踪标识在不同来源应保持一致");
foreach (var field in new[] { "traceId", "conversationRequestId" })
    Check(Turn(turnCtx, $"{{\"{field}\":\"{session}\"}}") == ("", "", false), "不同来源的追踪或请求冲突应无效");
Check(Turn(turnCtx, $"{{\"conversationId\":\"{requestId}\"}}") == ("", "", false), "会话冲突应无效");
foreach (var header in new[] { "X-Conversation-ID", "X-Trace-ID", "X-Conversation-Request-ID" })
{
    var multi = new DefaultHttpContext();
    multi.Request.Headers["X-Conversation-ID"] = session;
    multi.Request.Headers[header] = new Microsoft.Extensions.Primitives.StringValues(new[] { session, session });
    Check(Turn(multi, "{}") == ("", "", false), "多个标识头应无效");
}
var missingTurn = Turn(new DefaultHttpContext(), $"{{\"conversationId\":\"{session}\",\"traceId\":\"{requestId}\"}}");
Check(missingTurn.trace == turn.trace && missingTurn.dialog == "" && missingTurn.missing, "明确缺失请求标识应被标注");
var candidateCtx = new DefaultHttpContext();
candidateCtx.Request.Headers["X-Conversation-ID"] = session;
candidateCtx.Request.Headers["X-Trace-ID"] = requestId;
candidateCtx.Request.Headers["X-Conversation-Request-ID"] = "opaque-request-id";
var candidateTurn = Turn(candidateCtx, "{}");
Check(candidateTurn.trace == turn.trace && candidateTurn.dialog == "" && candidateTurn.missing, "单个非 GUID 请求头应成为辅助请求候选");
Check(!candidateTurn.trace.Contains("opaque-request-id"), "候选不得泄露原请求标识");
foreach (var body in new[] { "{\"conversationRequestId\":\"opaque-request-id\"}", $"{{\"metadata\":{{\"conversationRequestId\":\"{requestId}\"}}}}" })
    Check(Turn(candidateCtx, body) == ("", "", false), "非 GUID 请求头与正文同时存在时不能推断");
candidateCtx.Request.Headers["X-Conversation-Request-ID"] = new Microsoft.Extensions.Primitives.StringValues(new[] { "opaque-request-id", "opaque-request-id" });
Check(Turn(candidateCtx, "{}") == ("", "", false), "多个非 GUID 请求头不能成为候选");

var stamp = new DateTime(2025, 1, 2, 12, 0, 0);
UsageRecord Record(string s, string t, string d = "", bool missing = false, int minute = 0, bool legacy = false) => new()
{
    Time = stamp.AddMinutes(minute), StartedAt = stamp, Client = "workbuddy", SessionId = s,
    TraceId = t, DialogId = d, DialogIdMissing = missing, LegacyDiagnostic = legacy,
    Prompt = 10, Completion = 3
};
var statsConfig = new AppConfig();
List<SessionUsageGroup> Groups(params UsageRecord[] records) => UsageStats.GroupSessions(statsConfig, null, records);
void Conserves(List<SessionUsageGroup> groups, int count)
{
    Check(groups.Sum(g => g.Total.Requests) == count && groups.Sum(g => g.Total.Prompt) == count * 10 &&
        groups.Sum(g => g.Total.Completion) == count * 3, "会话总数应守恒");
    foreach (var group in groups)
        Check(group.Dialogs.Sum(d => d.Requests) == group.Total.Requests &&
            group.Dialogs.Sum(d => d.Prompt) == group.Total.Prompt &&
            group.Dialogs.Sum(d => d.Completion) == group.Total.Completion &&
            group.Dialogs.Sum(d => d.Cost) == group.Total.Cost, "父子请求、token 和金额应守恒");
}
var sharedTrace = Groups(Record("main", "t1", "d1"), Record("main", "t1", "d2"));
Check(sharedTrace.Count == 1 && sharedTrace[0].Dialogs.Count == 2, "同会话同 trace 的不同对话不能合并");
Conserves(sharedTrace, 2);
var separateTrace = Groups(Record("main", "t1", "d1"), Record("main", "t2", "d2"));
Check(separateTrace.Count == 1 && separateTrace[0].Dialogs.Count == 2, "同会话不同 trace 的对话应分组");
var associated = Groups(Record("main", "t1", "d1"), Record("aux", "t1", missing: true));
Check(associated.Count == 1 && associated[0].Id == "main" && associated[0].Dialogs.Single().Requests == 2 &&
    associated[0].Dialogs.Single().Key.Contains("推断"), "唯一同 trace 辅助请求应归并并标记推断");
Conserves(associated, 2);
foreach (var ambiguous in new[]
{
    Groups(Record("main", "t1", "d1"), Record("aux", "t1")),
    Groups(Record("main", "t1", "d1"), Record("", "t1", missing: true)),
    Groups(Record("main", "t1", "d1"), Record("other", "t1", "d2"), Record("aux", "t1", missing: true)),
    Groups(Record("main", "t1", "d1"), Record("aux", "t2", "own"), Record("aux", "t1", missing: true)),
    Groups(Record("main", "t1", "d1"), Record("other", "t2", "d2"), Record("aux", "t1", missing: true), Record("aux", "t2", missing: true))
})
{
    var unrecognized = ambiguous.Single(g => g.Id == "aux" || g.Id == "");
    Check(unrecognized.Dialogs.Any(d => d.Id == "" && d.Key.Contains("未识别")), "有歧义的辅助请求不能归并");
    Conserves(ambiguous, ambiguous.Sum(g => g.Total.Requests));
}
var oldMain = Record("main", "t1", "d1", minute: -1);
var recentAux = Record("aux", "t1", missing: true);
var filtered = UsageStats.GroupSessions(statsConfig, stamp, new[] { oldMain, recentAux });
Check(filtered.Count == 1 && filtered[0].Id == "main" && filtered[0].Total.Requests == 1 && filtered[0].Dialogs.Single().Key.Contains("推断"), "必须先利用历史关联再筛选时间");
var externalHistory = UsageStats.GroupSessions(statsConfig, stamp, new[] { recentAux }, new[] { oldMain });
Check(externalHistory.Count == 1 && externalHistory[0].Id == "main" && externalHistory[0].Total.Requests == 1, "额外历史只能提供关联证据，不计入总数");
var oldUnknown = Groups(Record("main", "t1", "d1"), Record("aux", "t1", legacy: true));
Check(oldUnknown.Count == 2 && oldUnknown.Single(g => g.Id == "aux").Dialogs.Single().Key.Contains("未识别"), "旧历史缺失对话不能被误推断");

var syntheticMainSession = new string('a', 64);
var syntheticAuxSession = new string('b', 64);
var syntheticDuplicateLogSession = new string('c', 64);
var syntheticDuplicateUsageSession = new string('d', 64);
UsageRecord LegacyRecord(string id, int seconds, bool marked = false) => new()
{
    Time = stamp.AddSeconds(seconds), StartedAt = stamp, SessionId = id,
    Route = "synthetic-route", Model = "synthetic-model", Target = "synthetic-target",
    Prompt = 10, Completion = 3, CacheRead = 2, CacheWrite = 1, Ms = 42, Status = 200,
    LegacyDiagnostic = marked
};
string EvidenceLine(string id, int seconds, bool main) =>
    $"{stamp.AddSeconds(seconds):yyyy-MM-dd HH:mm:ss} id_digest=hmac_sha256_64_process phase=completed client=workbuddy session_source=header session_stat_sha256_20={id[..20]} header_trace=0123456789abcdef status=200 duration_ms=42 input_tokens=10 output_tokens=3" +
    (main ? " header_request=fedcba9876543210" : "");
var legacyMain = LegacyRecord(syntheticMainSession, 0);
var legacyAux = LegacyRecord(syntheticAuxSession, 1);
var duplicateLogRecord = LegacyRecord(syntheticDuplicateLogSession, 2);
var duplicateUsageRecord = LegacyRecord(syntheticDuplicateUsageSession, 3);
var alreadyMarked = LegacyRecord(new string('e', 64), 4, marked: true);
var duplicateLine = EvidenceLine(syntheticDuplicateLogSession, 2, true);
var syntheticEvidence = string.Join(Environment.NewLine, new[]
{
    EvidenceLine(syntheticMainSession, 0, true), EvidenceLine(syntheticAuxSession, 1, false),
    duplicateLine, duplicateLine, EvidenceLine(syntheticDuplicateUsageSession, 3, true)
}) + Environment.NewLine;
var syntheticPath = Path.Combine(AppContext.BaseDirectory, $"synthetic-legacy-evidence-{Guid.NewGuid():N}.log");
using (var syntheticFile = new FileStream(syntheticPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
using (var writer = new StreamWriter(syntheticFile, new UTF8Encoding(false)))
    writer.Write(syntheticEvidence);
var originalEvidenceBytes = File.ReadAllBytes(syntheticPath);
var originalRecords = new[] { legacyMain, legacyAux, duplicateLogRecord, duplicateUsageRecord, LegacyRecord(syntheticDuplicateUsageSession, 3), alreadyMarked };
var enriched = LegacyUsageEvidence.Enrich(originalRecords, syntheticPath);
Check(enriched.Count == originalRecords.Length && enriched.Zip(originalRecords).All(p => !ReferenceEquals(p.First, p.Second)), "历史关联必须返回独立副本");
Check(enriched[0].Client == "workbuddy" && enriched[0].TraceId == "legacy-diagnostic:0123456789abcdef" &&
    enriched[0].DialogId == "legacy-diagnostic:fedcba9876543210" && !enriched[0].DialogIdMissing && enriched[0].LegacyDiagnostic,
    "双向唯一主记录应匹配并标记历史诊断");
Check(enriched[1].Client == "workbuddy" && enriched[1].TraceId == enriched[0].TraceId &&
    enriched[1].DialogId == "" && enriched[1].DialogIdMissing && enriched[1].LegacyDiagnostic,
    "双向唯一辅助记录应成为历史候选");
Check(enriched.Skip(2).Take(3).All(r => r.Client == "" && r.TraceId == "" && r.DialogId == "" && !r.DialogIdMissing && !r.LegacyDiagnostic),
    "重复日志或重复用量均不能匹配");
Check(enriched[5].LegacyDiagnostic && enriched[5].Client == "", "未匹配的副本应保留历史诊断标记");
Check(enriched[0].Time == legacyMain.Time && enriched[0].StartedAt == legacyMain.StartedAt &&
    enriched[0].SessionId == legacyMain.SessionId && enriched[0].Route == legacyMain.Route &&
    enriched[0].Model == legacyMain.Model && enriched[0].Target == legacyMain.Target &&
    enriched[0].Prompt == legacyMain.Prompt && enriched[0].Completion == legacyMain.Completion &&
    enriched[0].CacheRead == legacyMain.CacheRead && enriched[0].CacheWrite == legacyMain.CacheWrite &&
    enriched[0].Ms == legacyMain.Ms && enriched[0].Status == legacyMain.Status &&
    legacyMain.Client == "" && legacyMain.TraceId == "" && !legacyMain.LegacyDiagnostic,
    "关联应保留原始用量字段且不修改输入");
var legacyGroups = UsageStats.GroupSessions(statsConfig, null, enriched.Take(2));
Check(legacyGroups.Count == 1 && legacyGroups[0].Id == syntheticMainSession &&
    legacyGroups[0].Total.Requests == 2 && legacyGroups[0].Dialogs.Single().Requests == 2 &&
    legacyGroups[0].Dialogs.Single().Key.Contains("历史诊断推断"), "历史主辅助记录应重建分组并显示历史诊断推断");
Conserves(legacyGroups, 2);
Check(File.ReadAllBytes(syntheticPath).SequenceEqual(originalEvidenceBytes), "历史关联不得改写合成证据文件");

var handle = typeof(ProxyService).GetMethod("HandleAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
var service = new ProxyService();
var cfg = new AppConfig();
cfg.Routes.Add(new RouteRule { Prefix = "test", Upstream = "http://127.0.0.1:1", Mode = "chat" });
var log = LogStore.Instance.LogFile;
var diagnosticLog = LogStore.Instance.DiagnosticsFile;
Check(Path.GetFullPath(log).StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase), "测试日志必须位于测试目录");
Check(log != diagnosticLog, "诊断日志必须与转发日志分离");
var beforeDiagnostics = File.Exists(diagnosticLog) ? File.ReadAllText(diagnosticLog) : "";
async Task Request(bool enabled)
{
    cfg.ExperimentalDiagnostics = enabled;
    var request = new DefaultHttpContext();
    request.Request.Method = "POST";
    request.Request.Path = "/test/v1/chat/completions";
    request.Request.ContentType = "application/json";
    request.Request.Headers.Authorization = "Bearer secret-key-never-log";
    request.Request.Headers.UserAgent = "WorkBuddy/test";
    request.Request.Headers["X-Conversation-Request-ID"] = requestId;
    request.Request.Headers["X-Conversation-ID"] = session;
    request.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"model\":\"safe-model\",\"messages\":[{\"content\":\"private-prompt-never-log\"}]}"));
    request.Response.Body = new MemoryStream();
    await (Task)handle.Invoke(service, new object[] { request, cfg })!;
}
await Request(false);
var offText = File.ReadAllText(log);
Check((File.Exists(diagnosticLog) ? File.ReadAllText(diagnosticLog) : "") == beforeDiagnostics, "关闭诊断时不能写入诊断文件");
await Request(true);
var text = File.ReadAllText(log);
var added = text[offText.Length..];
var diagnosticAdded = File.ReadAllText(diagnosticLog)[beforeDiagnostics.Length..];
Check(!added.Contains("实验诊断") && !added.Contains("session_source="), "转发日志不能混入诊断信息");
Check(diagnosticAdded.Contains("实验诊断") && diagnosticAdded.Contains("session_source=") && diagnosticAdded.Contains("session_stat_sha256_20=" + sessionHash[..20]) && diagnosticAdded.Contains("id_digest=hmac_sha256_64_process"), "开启诊断时应写入统计同款摘要和关联方式");
Check(!diagnosticAdded.Contains("请求 ·") && !diagnosticAdded.Contains("转发 ·"), "诊断文件不能混入转发日志");
Check(!diagnosticAdded.Contains("secret-key-never-log") && !diagnosticAdded.Contains("private-prompt-never-log") && !diagnosticAdded.Contains(requestId), "诊断不应记录密钥、正文或单次请求 ID");
await Request(false);
Check(File.ReadAllText(diagnosticLog) == beforeDiagnostics + diagnosticAdded, "关闭诊断后不能继续写入诊断文件");
var upstream = new ProxyService();
typeof(ProxyService).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(upstream, new HttpClient(new ErrorHandler()));
var errorCtx = new DefaultHttpContext();
errorCtx.Request.Method = "POST";
errorCtx.Request.Path = "/test/secret-path-never-log";
errorCtx.Request.QueryString = new QueryString("?key=secret-query-never-log");
errorCtx.Request.ContentType = "application/json";
errorCtx.Request.Headers.Authorization = "Bearer secret-key-never-log";
errorCtx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"model\":\"safe-model\"}"));
errorCtx.Response.Body = new MemoryStream();
cfg.ExperimentalDiagnostics = true;
var beforeError = File.ReadAllText(log).Length;
var beforeErrorDiagnostics = File.ReadAllText(diagnosticLog).Length;
await (Task)handle.Invoke(upstream, new object[] { errorCtx, cfg })!;
errorCtx.Response.Body.Position = 0;
var returned = await new StreamReader(errorCtx.Response.Body).ReadToEndAsync();
Check(returned == ErrorHandler.Body, "上游错误响应应原样返回");
var errorLog = File.ReadAllText(log)[beforeError..] + File.ReadAllText(diagnosticLog)[beforeErrorDiagnostics..];
foreach (var secret in new[] { ErrorHandler.Body, "secret-path-never-log", "secret-query-never-log", "secret-key-never-log" })
    Check(!errorLog.Contains(secret), "上游错误日志泄露敏感信息");
Console.WriteLine("客户端识别和诊断测试通过");

sealed class ErrorHandler : HttpMessageHandler
{
    public const string Body = "private-upstream-error-never-log";
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(Body) });
}
