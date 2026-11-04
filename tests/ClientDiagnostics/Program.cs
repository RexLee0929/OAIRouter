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

var testDirectory = Directory.CreateTempSubdirectory("OAIRouter-ClientDiagnostics-").FullName;
AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", testDirectory);
Check(string.Equals(Path.GetFullPath(AppContext.BaseDirectory), Path.GetFullPath(testDirectory), StringComparison.OrdinalIgnoreCase), "无法设置独立测试目录");
typeof(App).GetProperty("IsDev")!.SetValue(null, true);
Check(App.IsDev && string.Equals(Path.GetFullPath(ConfigStore.Dir), Path.Combine(testDirectory, "dev-data"), StringComparison.OrdinalIgnoreCase), "数据必须位于独立测试目录");
Check(!File.Exists(UsageStats.FilePath) && !File.Exists(LogStore.Instance.LogFile) && !File.Exists(LogStore.Instance.DiagnosticsFile), "测试目录必须是全新的，不能读取既有数据");
Directory.CreateDirectory(ConfigStore.Dir);

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

var correlationMethod = typeof(ProxyService).GetMethod("DescribeCorrelation", BindingFlags.Static | BindingFlags.NonPublic)!;
string Correlation(DefaultHttpContext context, string? body) => (string)correlationMethod.Invoke(null, new object?[] { context, body is null ? null : Encoding.UTF8.GetBytes(body) })!;
var correlationCtx = new DefaultHttpContext();
correlationCtx.Request.Headers["X-Session-ID"] = session;
correlationCtx.Request.Headers["X-Parent-Session-ID"] = session.ToUpperInvariant();
correlationCtx.Request.Headers["X-Trace-ID"] = new Microsoft.Extensions.Primitives.StringValues(new[] { session, requestId });
correlationCtx.Request.Headers["X-Session-Secret"] = "never-log-header-name";
correlationCtx.Request.Headers.Authorization = "Bearer never-log-auth";
var correlation = Correlation(correlationCtx, $"{{\"parentSessionId\":\"{session.Replace("-", "")}\",\"sessionId\":\"{session}\",\"sessionId\":\"{requestId}\",\"traceId\":42,\"agentId\":null,\"metadata\":{{\"rootSessionId\":\"{session}\",\"taskId\":\"free-text secret key\",\"spanId\":\"abcdef0123456789\",\"requestId\":[]}},\"messages\":[{{\"sessionId\":\"{requestId}\"}}]}}");
var entries = correlation.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].Substring("correlation_v1=".Length).Split(',');
string Entry(string prefix) => entries.Single(x => x.StartsWith(prefix, StringComparison.Ordinal));
var digest = Entry("header.sessionId:").Split(':')[3];
Check(digest.Length == 24 && Entry("header.parentSessionId:").EndsWith(digest) && Entry("body.parentSessionId:").EndsWith(digest) && Entry("metadata.rootSessionId:").EndsWith(digest), "跨位置及父子字段摘要应一致");
Check(Entry("header.traceId:").Contains(":multi:2:none") && Entry("body.sessionId:").Contains(":duplicate:2:none") && Entry("body.traceId:").Contains(":number:1:none") && Entry("body.agentId:").Contains(":null:1:none") && Entry("metadata.requestId:").Contains(":array:1:none"), "多值、重复与非字符串必须标注并禁用摘要");
Check(Entry("metadata.taskId:").EndsWith(":opaque") && Entry("metadata.spanId:").EndsWith(":opaque") && correlation.Contains("correlation_digest_policy=uuid_only_opaque_skipped"), "非 UUID 值明确跳过，避免摘要凭证");
foreach (var secret in new[] { session, requestId, "free-text", "never-log-auth", "never-log-header-name", "abcdef0123456789" })
    Check(!correlation.Contains(secret, StringComparison.OrdinalIgnoreCase), "关联诊断泄露输入");
Check(correlation == Correlation(correlationCtx, $"{{\"parentSessionId\":\"{session.Replace("-", "")}\",\"sessionId\":\"{session}\",\"sessionId\":\"{requestId}\",\"traceId\":42,\"agentId\":null,\"metadata\":{{\"rootSessionId\":\"{session}\",\"taskId\":\"free-text secret key\",\"spanId\":\"abcdef0123456789\",\"requestId\":[]}},\"messages\":[{{\"sessionId\":\"{requestId}\"}}]}}"), "进程内摘要应稳定");
Check(Correlation(new DefaultHttpContext(), "{bad").Contains("correlation_body=invalid"), "非法 JSON 应安全标记");
Check(Correlation(new DefaultHttpContext(), null).Contains("correlation_body=not_captured") && Correlation(new DefaultHttpContext(), "").Contains("correlation_body=empty"), "未采集与空正文应区别");
Check(Correlation(new DefaultHttpContext(), "[]").Contains("correlation_body=non_object"), "非对象正文应安全标记");
Check(Correlation(new DefaultHttpContext(), "{\"metadata\":{},\"metadata\":{\"sessionId\":\"" + session + "\"}}").Contains("correlation_metadata=duplicate"), "重复 metadata 不可推断");
foreach (var (json, state) in new[] { ("{}", "missing"), ("{\"metadata\":{}}", "object"), ("{\"metadata\":[]}", "array"), ("{\"metadata\":null}", "null"), ("{\"metadata\":1}", "number"), ("{\"metadata\":true}", "boolean"), ("{\"metadata\":\"x\"}", "string"), ("[1]", "not_checked") })
    Check(Correlation(new DefaultHttpContext(), json).Contains("correlation_metadata=" + state), "metadata 状态错误");
Check(correlation.Contains("correlation_scope=headers_allowlist_body_top_metadata_only") && correlation.Contains("correlation_unknown=not_checked"), "必须说明采集范围与未知字段未检查");
Check(Correlation(new DefaultHttpContext(), new string('x', 262145)).Contains("correlation_body=too_large"), "超限正文应跳过");
foreach (var unsafeValue in new[] { "sk-abcdefghijklmnop123456", "Bearer abcdefghijklmnop", "opaque-request-id", "a1b2c3d4e5f67890", new string('a', 200), "abc\nforged=1" })
{
    var unsafeBody = System.Text.Json.JsonSerializer.Serialize(new { requestId = unsafeValue });
    var unsafeResult = Correlation(new DefaultHttpContext(), unsafeBody);
    Check(unsafeResult.Contains("body.requestId:string:1:opaque") && !unsafeResult.Contains(unsafeValue), "不透明值和潜在凭证必须跳过且不能输出");
}
var boundedCtx = new DefaultHttpContext();
foreach (var header in new[] { "X-Session-ID", "X-Conversation-ID", "X-Request-ID", "X-Conversation-Request-ID", "X-Trace-ID", "X-Span-ID", "X-Parent-ID", "X-Parent-Session-ID", "X-Parent-Conversation-ID", "X-Parent-Request-ID", "X-Parent-Trace-ID", "X-Parent-Span-ID", "X-Root-ID", "X-Root-Session-ID", "X-Root-Conversation-ID", "X-Root-Request-ID", "X-Agent-ID", "X-Parent-Agent-ID", "X-Task-ID", "X-Parent-Task-ID" }) boundedCtx.Request.Headers[header] = session;
var names = new[] { "sessionId", "conversationId", "requestId", "conversationRequestId", "traceId", "spanId", "parentId", "parentSessionId", "parentConversationId", "parentRequestId", "parentTraceId", "parentSpanId", "rootId", "rootSessionId", "rootConversationId", "rootRequestId", "agentId", "parentAgentId", "taskId", "parentTaskId" };
var allFields = string.Join(",", names.Select(n => "\"" + n + "\":\"" + session + "\""));
var bounded = Correlation(boundedCtx, "{" + allFields + ",\"metadata\":{" + allFields + "}}");
Check(bounded.Contains("correlation_truncated=False") && bounded.Split(' ')[0].Count(c => c == ',') == 59 && bounded.Length < 6000, "所有白名单字段应完整收录且输出有上界");
var aliasCtx = new DefaultHttpContext();
aliasCtx.Request.Headers["X-Root-Task-ID"] = session;
aliasCtx.Request.Headers["X-Root_Task-ID"] = session;
var aliasResult = Correlation(aliasCtx, $"{{\"root_task_id\":\"{session}\",\"parent_span_id\":\"{session}\",\"sessionId\":\"{session}\",\"session_id\":\"{session}\",\"metadata\":{{\"rootTaskId\":\"{session}\",\"root_task_id\":\"{requestId}\",\"parentSpanId\":\"{session}\"}}}}");
var aliasEntries = aliasResult.Split(' ')[0].Substring("correlation_v1=".Length).Split(',');
string AliasEntry(string prefix) => aliasEntries.Single(x => x.StartsWith(prefix, StringComparison.Ordinal));
var aliasDigest = AliasEntry("header.rootTaskId:").Split(':')[3];
Check(aliasDigest.Length == 24 && AliasEntry("body.rootTaskId:").EndsWith(aliasDigest) && AliasEntry("body.parentSpanId:").EndsWith(aliasDigest) && AliasEntry("metadata.parentSpanId:").EndsWith(aliasDigest), "蛇形别名应规范化且跨位置摘要一致");
Check(AliasEntry("body.sessionId:").Contains(":duplicate:2:none") && AliasEntry("metadata.rootTaskId:").Contains(":duplicate:2:none"), "同一对象不同别名重复不得生成摘要");
Check(!aliasResult.Contains("root_task_id") && !aliasResult.Contains("root_task") && !aliasResult.Contains("X-Root_Task") && aliasResult.Contains("correlation_digest_scope=process_cross_location"), "输出仅使用固定字段名并明确比较作用域");
var aliases = new[] { ("session_id", "sessionId"), ("conversation_id", "conversationId"), ("request_id", "requestId"), ("conversation_request_id", "conversationRequestId"), ("trace_id", "traceId"), ("span_id", "spanId"), ("parent_id", "parentId"), ("parent_session_id", "parentSessionId"), ("parent_conversation_id", "parentConversationId"), ("parent_request_id", "parentRequestId"), ("parent_trace_id", "parentTraceId"), ("parent_span_id", "parentSpanId"), ("root_id", "rootId"), ("root_session_id", "rootSessionId"), ("root_conversation_id", "rootConversationId"), ("root_request_id", "rootRequestId"), ("root_trace_id", "rootTraceId"), ("root_span_id", "rootSpanId"), ("agent_id", "agentId"), ("parent_agent_id", "parentAgentId"), ("root_agent_id", "rootAgentId"), ("task_id", "taskId"), ("parent_task_id", "parentTaskId"), ("root_task_id", "rootTaskId") };
foreach (var (snake, camel) in aliases)
{
    var input = "{\"" + snake + "\":\"" + session + "\",\"metadata\":{\"" + snake + "\":\"" + session + "\"}}";
    var output = Correlation(new DefaultHttpContext(), input);
    Check(output.Contains("body." + camel + ":string:1:") && output.Contains("metadata." + camel + ":string:1:"), "白名单蛇形别名未识别：" + camel);
}
var maxHeaders = new DefaultHttpContext();
foreach (var (snake, _) in aliases) maxHeaders.Request.Headers["X-" + snake.Replace('_', '-')] = session;
var maxFields = string.Join(",", aliases.Select(a => "\"" + a.Item1 + "\":\"" + session + "\""));
var maxOutput = Correlation(maxHeaders, "{" + maxFields + ",\"metadata\":{" + maxFields + "}}");
Check(maxOutput.Contains("correlation_truncated=False") && maxOutput.Split(' ')[0].Count(c => c == ',') == aliases.Length * 3 - 1, "所有作用域的全部白名单条目不应被截断");

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
Check(enriched[0].Client == "" && enriched[0].TraceId == "" && enriched[0].DialogId == "" &&
    enriched[0].DiagnosticTraceId == "0123456789abcdef" && enriched[0].DiagnosticDialogId == "fedcba9876543210" && enriched[0].LegacyDiagnostic,
    "双向唯一主记录应保留独立诊断证据");
Check(enriched[1].Client == "" && enriched[1].DiagnosticTraceId == enriched[0].DiagnosticTraceId &&
    enriched[1].DiagnosticDialogId == "" && !enriched[1].DialogIdMissing && enriched[1].LegacyDiagnostic,
    "诊断未记录请求标识不能证明原请求缺失");
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
Check(legacyGroups.Count == 2 && legacyGroups.Single(g => g.Id == syntheticMainSession).Dialogs.Single().Id == "" &&
    legacyGroups.Single(g => g.Id == syntheticMainSession).Dialogs.Single().Key.Contains("未确认对话") &&
    legacyGroups.Single(g => g.Id == syntheticAuxSession).Dialogs.Single().Id == "",
    "无进程边界时历史诊断标识不能拆分或归并对话");
Conserves(legacyGroups, 2);
var repeatedDiagnostic = Groups(enriched[0], enriched[0]);
Check(repeatedDiagnostic.Single().Dialogs.Count == 1 && repeatedDiagnostic.Single().Dialogs.Single().Id == "" &&
    repeatedDiagnostic.Single().Dialogs.Single().Requests == 2,
    "相同诊断摘要不能人为拆出多个已确认对话");
Conserves(repeatedDiagnostic, 2);
Check(File.ReadAllBytes(syntheticPath).SequenceEqual(originalEvidenceBytes), "历史关联不得改写合成证据文件");

UsageRecord EvidenceRecord(string id, int second, long prompt = 0, long completion = 0, string client = "workbuddy", string trace = "", string dialog = "", bool missing = false) => new()
{
    Time = stamp.AddSeconds(second), SessionId = id, Client = client, TraceId = trace, DialogId = dialog,
    DialogIdMissing = missing, Status = 500, Ms = 42, Prompt = prompt, Completion = completion
};
string ZeroLine(string id, int second, string tokens) =>
    $"{stamp.AddSeconds(second):yyyy-MM-dd HH:mm:ss} id_digest=hmac_sha256_64_process phase=completed client=workbuddy session_source=header session_stat_sha256_20={id[..20]} header_trace=0123456789abcdef status=500 duration_ms=42 {tokens}";
List<UsageRecord> Match(UsageRecord[] records, params string[] lines)
{
    File.WriteAllLines(syntheticPath, lines);
    return LegacyUsageEvidence.Enrich(records, syntheticPath);
}
var z = new string('f', 64);
var zero = EvidenceRecord(z, 1, trace: "original-trace", missing: true);
var unknownLine = ZeroLine(z, 0, "usage_present=False input_tokens=unknown output_tokens=unknown");
var zeroLine = ZeroLine(z, 0, "usage_present=True input_tokens=0 output_tokens=0");
var matchUnknown = Match(new[] { zero }, unknownLine).Single();
Check(matchUnknown.LegacyDiagnostic && matchUnknown.DiagnosticTokensUnknown && matchUnknown.TraceId == "original-trace" && matchUnknown.DialogIdMissing && matchUnknown.Client == "workbuddy", "跨秒 unknown 应保留原识别信息");
var matchZero = Match(new[] { zero }, zeroLine).Single();
Check(matchZero.LegacyDiagnostic && !matchZero.DiagnosticTokensUnknown, "精确零应区别于 unknown");
Check(!Match(new[] { zero }, unknownLine.Replace("usage_present=False", "usage_present=True")).Single().LegacyDiagnostic, "unknown 必须明确无 usage");
Check(!Match(new[] { EvidenceRecord(z, 1, prompt: 1) }, unknownLine).Single().LegacyDiagnostic, "unknown 不能匹配非零统计");
Check(!Match(new[] { zero }, unknownLine, zeroLine).Single().LegacyDiagnostic, "真实零和 unknown 都是候选时不能消歧");
Check(!Match(new[] { zero, EvidenceRecord(z, 0) }, unknownLine).Any(r => r.LegacyDiagnostic), "跨秒统计多候选不能匹配");
Check(!Match(new[] { EvidenceRecord(z, 2) }, unknownLine).Single().LegacyDiagnostic &&
    !Match(new[] { EvidenceRecord(z, -1) }, unknownLine).Single().LegacyDiagnostic, "时间窗口必须单向且最多跨一秒");
Check(!Match(new[] { EvidenceRecord(z, 1, client: "deepseek") }, unknownLine).Single().LegacyDiagnostic, "客户端冲突不能应用诊断");
var existing = EvidenceRecord(z, 1, trace: "native-trace", dialog: "native-dialog");
var existingMatched = Match(new[] { existing }, zeroLine).Single();
Check(existingMatched.TraceId == existing.TraceId && existingMatched.DialogId == existing.DialogId &&
    Groups(existingMatched).Single().Dialogs.Single().Id == "native-dialog" &&
    !Groups(existingMatched).Single().Dialogs.Single().Key.Contains("推断"), "已有对话与追踪不得改变或误标推断");
var blockedSource = Groups(Record("main", "t1", "d1"), Record("aux", "t1", missing: true), Record("aux", "", ""));
Check(blockedSource.Count == 2 && blockedSource.Single(g => g.Id == "aux").Total.Requests == 2, "来源会话含非候选记录不能跨会话归并");
var main103 = EvidenceRecord(syntheticMainSession, 0, client: "workbuddy", trace: "native-trace", dialog: "native-dialog");
var aux103 = EvidenceRecord(syntheticAuxSession, 1, client: "workbuddy");
var main103Line = ZeroLine(syntheticMainSession, 0, "usage_present=True input_tokens=0 output_tokens=0 request_id_present=True header_request=fedcba9876543210");
var aux103Line = ZeroLine(syntheticAuxSession, 1, "usage_present=True input_tokens=0 output_tokens=0 request_id_present=True");
var enriched103 = Match(new[] { main103, aux103 }, main103Line, aux103Line);
var groups103 = Groups(enriched103.ToArray());
Check(enriched103[1].DiagnosticAssociationCandidate && !enriched103[1].DialogIdMissing && enriched103[1].TraceId == "" &&
    groups103.Count == 2 && groups103.Single(g => g.Id == syntheticAuxSession).Dialogs.Single().Key.Contains("诊断关联候选") &&
    groups103.Single(g => g.Id == syntheticMainSession).Dialogs.Single().Id == "native-dialog" &&
    !groups103.Single(g => g.Id == syntheticMainSession).Dialogs.Single().Key.Contains("推断"),
    "1.0.3 候选应标记但不能在无进程边界时归并");
Check(groups103.Sum(g => g.Total.Requests) == 2 && groups103.Sum(g => g.Total.Cost) == 0, "候选请求与费用应守恒");
Check(!Match(new[] { aux103 }, aux103Line.Replace("request_id_present=True", "request_id_present=False")).Single().DiagnosticAssociationCandidate,
    "没有明确 request_id_present 不能标为候选");
var six = Enumerable.Range(0, 6).Select(i => EvidenceRecord(new string((char)('a' + i), 64), 10 + i, trace: "trace" + i, dialog: "dialog" + i)).ToArray();
var sixLines = six.Select((r, i) => ZeroLine(r.SessionId, 10 + i, "usage_present=True input_tokens=0 output_tokens=0 request_id_present=True header_request=fedcba9876543210")).ToArray();
var sixGroups = Groups(Match(six, sixLines).ToArray());
Check(sixGroups.Count == 6 && sixGroups.All(g => g.Dialogs.Single().Id.StartsWith("dialog") && !g.Dialogs.Single().Key.Contains("推断")),
    "六个已有有效对话不得按诊断摘要或时间合并");
var collision = Groups(Record("main", "t1", "d1"), Record("other", "t1", "d1"));
Check(collision.Count == 2, "已有对话的不同会话不得按 trace 合并");

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
var syntheticService = new ProxyService();
typeof(ProxyService).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(syntheticService, new HttpClient(new FlipHandler(() => { })));
var syntheticItems = (List<UsageRecord>)typeof(UsageStats).GetField("Items", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
var startCount = syntheticItems.Count;
var parentId = Guid.NewGuid().ToString("D");
var childId = Guid.NewGuid().ToString("D");
var rootId = Guid.NewGuid().ToString("D");
async Task SyntheticRequest(string conversation, string requestIdValue, string? parent, string? root, string body, string agent)
{
    var request = new DefaultHttpContext();
    request.Request.Method = "POST";
    request.Request.Path = "/test/v1/chat/completions";
    request.Request.ContentType = "application/json";
    request.Request.Headers.UserAgent = agent;
    request.Request.Headers["X-Conversation-ID"] = conversation;
    request.Request.Headers["X-Conversation-Request-ID"] = requestIdValue;
    if (parent != null) request.Request.Headers["X-Parent-Conversation-ID"] = parent;
    if (root != null) request.Request.Headers["X-Root-Request-ID"] = root;
    request.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
    request.Response.Body = new MemoryStream();
    await (Task)handle.Invoke(syntheticService, new object[] { request, cfg })!;
    Check(request.Response.StatusCode == 200, "采集异常或无效证据不能导致 502");
}
cfg.ExperimentalDiagnostics = false;
await SyntheticRequest(parentId, "opaque", null, null, "{}", "WorkBuddy/test");
await SyntheticRequest(parentId, rootId, null, rootId, "{}", "different-agent");
await SyntheticRequest(childId, Guid.NewGuid().ToString("D"), parentId, rootId, "{}", "WorkBuddy/test");
await SyntheticRequest(parentId, Guid.NewGuid().ToString("D"), null, null, "{\"metadata\":{},\"metadata\":{}}", "WorkBuddy/test");
var captured = syntheticItems.Skip(startCount).ToArray();
Check(captured.Length == 4 && captured[0].CorrelationInvalid && captured[3].CorrelationInvalid, "实际路径应记录无效证据");
var syntheticGroups = UsageStats.GroupSessions(cfg, null, captured.Skip(2).Take(1), captured);
Check(syntheticGroups.Single().Id == captured[1].SessionId && syntheticGroups.Single().Dialogs.Single().Id == captured[1].RequestId, "诊断关闭、不同 UA 及筛选后仍按根关联");
var integrationService = new ProxyService();
typeof(ProxyService).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(integrationService, new HttpClient(new UsageHandler()));
var integrationSession = Guid.NewGuid().ToString("D");
var integrationChildren = new[] { Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D") };
var integrationRoot = Guid.NewGuid().ToString("D");
var integrationRequests = new[] { integrationRoot, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D") };
var conflictingRoot = Guid.NewGuid().ToString("D");
const string privateBody = "synthetic-private-body-never-persist";
var integrationStart = syntheticItems.Count;
var logStart = File.Exists(log) ? File.ReadAllText(log).Length : 0;
var diagBeforeIntegration = File.Exists(diagnosticLog) ? File.ReadAllText(diagnosticLog) : "";
async Task IntegrationRequest(string conversation, string requestIdValue, bool child, bool conflict = false)
{
    var request = new DefaultHttpContext();
    request.Request.Method = "POST";
    request.Request.Path = "/test/v1/chat/completions";
    request.Request.ContentType = "application/json";
    request.Request.Headers.UserAgent = "WorkBuddy/test";
    request.Request.Headers["X-Conversation-ID"] = conversation;
    request.Request.Headers["X-Conversation-Request-ID"] = requestIdValue;
    request.Request.Headers["X-Root-Request-ID"] = integrationRoot;
    if (child) request.Request.Headers["X-Parent-Conversation-ID"] = integrationSession;
    var body = System.Text.Json.JsonSerializer.Serialize(new { model = "synthetic-model", messages = new[] { new { role = "user", content = privateBody } }, rootRequestId = conflict ? conflictingRoot : integrationRoot });
    request.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
    request.Response.Body = new MemoryStream();
    await (Task)handle.Invoke(integrationService, new object[] { request, cfg })!;
    Check(request.Response.StatusCode == 200, "合成代理请求应成功");
}
await IntegrationRequest(integrationSession, integrationRequests[0], false);
await IntegrationRequest(integrationChildren[0], integrationRequests[1], true);
await IntegrationRequest(integrationChildren[1], integrationRequests[2], true);
await IntegrationRequest(integrationChildren[2], integrationRequests[3], true, true);
var integrationRecords = syntheticItems.Skip(integrationStart).ToArray();
Check(integrationRecords.Length == 4 && integrationRecords.Take(3).All(r => !r.CorrelationInvalid) && integrationRecords[3].CorrelationInvalid, "冲突请求必须标记无效");
Check(integrationRecords[0].RequestId.Length == 64 && integrationRecords[0].RootRequestId == integrationRecords[0].RequestId, "主请求 root 必须等于自身 request 摘要");
void CheckIntegrationGroups(UsageRecord[] records)
{
    var groups = UsageStats.GroupSessions(cfg, null, records, records);
    var mainGroup = groups.Single(g => g.Id == records[0].SessionId);
    Check(groups.Count == 2 && mainGroup.Total.Requests == 3 && mainGroup.Dialogs.Single().Id == records[0].RequestId && mainGroup.Dialogs.Single().Requests == 3, "两个子请求应归并至主请求对话，冲突请求保持独立");
    Check(groups.Single(g => g.Id == records[3].SessionId).Total.Requests == 1, "冲突请求不能归并");
    Check(groups.Sum(g => g.Total.Requests) == 4 && groups.Sum(g => g.Total.Prompt) == 44 && groups.Sum(g => g.Total.Completion) == 28 && groups.All(g => g.Dialogs.Sum(d => d.Requests) == g.Total.Requests && g.Dialogs.Sum(d => d.Prompt) == g.Total.Prompt && g.Dialogs.Sum(d => d.Completion) == g.Total.Completion), "请求和 token 总数必须守恒");
}
CheckIntegrationGroups(integrationRecords);
var usageText = File.ReadAllText(UsageStats.FilePath);
var integrationLines = usageText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).TakeLast(4).ToArray();
Check(integrationLines.Length == 4, "四条实际请求必须持久化");
var persisted = string.Join("\n", integrationLines) + File.ReadAllText(log)[logStart..];
foreach (var secret in integrationChildren.Concat(integrationRequests).Concat(new[] { integrationSession, conflictingRoot, privateBody }))
    Check(!persisted.Contains(secret, StringComparison.OrdinalIgnoreCase), "持久化数据和日志不得泄露原始标识或正文");
Check((File.Exists(diagnosticLog) ? File.ReadAllText(diagnosticLog) : "") == diagBeforeIntegration, "诊断关闭时实际请求不得写诊断日志");
UsageStats.Load();
var restored = syntheticItems.TakeLast(4).ToArray();
Check(restored.Length == 4 && restored.Zip(integrationRecords).All(p => p.First.SessionId == p.Second.SessionId && p.First.CorrelationSessionId == p.Second.CorrelationSessionId && p.First.ParentSessionId == p.Second.ParentSessionId && p.First.RequestId == p.Second.RequestId && p.First.RootRequestId == p.Second.RootRequestId && p.First.CorrelationInvalid == p.Second.CorrelationInvalid && p.First.Prompt == p.Second.Prompt && p.First.Completion == p.Second.Completion), "Load 必须还原关联字段和 token");
CheckIntegrationGroups(restored);
await Request(false);
var offText = File.ReadAllText(log);
Check((File.Exists(diagnosticLog) ? File.ReadAllText(diagnosticLog) : "") == beforeDiagnostics, "关闭诊断时不能写入诊断文件");
await Request(true);
var text = File.ReadAllText(log);
var added = text[offText.Length..];
var diagnosticAdded = File.ReadAllText(diagnosticLog)[beforeDiagnostics.Length..];
Check(!added.Contains("实验诊断") && !added.Contains("session_source="), "转发日志不能混入诊断信息");
Check(diagnosticAdded.Contains("实验诊断") && diagnosticAdded.Contains("session_source=") && diagnosticAdded.Contains("session_stat_sha256_20=" + sessionHash[..20]) && diagnosticAdded.Contains("id_digest=hmac_sha256_64_process") && diagnosticAdded.Contains("correlation_v1=") && diagnosticAdded.Contains("header.conversationId:string:1:"), "开启诊断时应写入旧字段及关联信息");
Check(!diagnosticAdded.Contains("请求 ·") && !diagnosticAdded.Contains("转发 ·"), "诊断文件不能混入转发日志");
Check(!diagnosticAdded.Contains("secret-key-never-log") && !diagnosticAdded.Contains("private-prompt-never-log") && !diagnosticAdded.Contains(requestId), "诊断不应记录密钥、正文或单次请求 ID");
await Request(false);
Check(File.ReadAllText(diagnosticLog) == beforeDiagnostics + diagnosticAdded, "关闭诊断后不能继续写入诊断文件");
async Task SnapshotRequest(bool initiallyEnabled)
{
    cfg.ExperimentalDiagnostics = initiallyEnabled;
    var snapshotService = new ProxyService();
    typeof(ProxyService).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(snapshotService, new HttpClient(new FlipHandler(() => cfg.ExperimentalDiagnostics = !initiallyEnabled)));
    var request = new DefaultHttpContext();
    request.Request.Method = "POST";
    request.Request.Path = "/test/v1/chat/completions";
    request.Request.ContentType = "application/json";
    request.Request.Headers["X-Session-ID"] = session;
    request.Request.Body = new FlipStream(Encoding.UTF8.GetBytes("{}"), () => cfg.ExperimentalDiagnostics = !initiallyEnabled);
    request.Response.Body = new MemoryStream();
    var before = File.ReadAllText(diagnosticLog);
    await (Task)handle.Invoke(snapshotService, new object[] { request, cfg })!;
    var after = File.ReadAllText(diagnosticLog);
    Check(request.Response.StatusCode == 200, "开关变化不能影响转发");
    if (initiallyEnabled)
        Check(after.Length > before.Length && after[before.Length..].Contains("header.sessionId:string:1:") && after[before.Length..].Contains("body_bytes=2"), "入站开启时即使中途关闭仍应完整采集并写入");
    else Check(after == before, "入站关闭时中途开启不能采集或写入");
}
await SnapshotRequest(true);
await SnapshotRequest(false);
cfg.ExperimentalDiagnostics = true;
var writeFailureService = new ProxyService();
typeof(ProxyService).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(writeFailureService, new HttpClient(new FlipHandler(() => { })));
var writeFailureRequest = new DefaultHttpContext();
writeFailureRequest.Request.Method = "POST";
writeFailureRequest.Request.Path = "/test/v1/chat/completions";
writeFailureRequest.Request.ContentType = "application/json";
writeFailureRequest.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
writeFailureRequest.Response.Body = new MemoryStream();
var requestsBeforeFailure = writeFailureService.TotalRequests;
using (var lockedDiagnostic = new FileStream(diagnosticLog, FileMode.Open, FileAccess.Read, FileShare.None))
    await (Task)handle.Invoke(writeFailureService, new object[] { writeFailureRequest, cfg })!;
Check(writeFailureRequest.Response.StatusCode == 200 && writeFailureService.TotalRequests == requestsBeforeFailure + 1, "诊断写入失败不能影响转发和统计");
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

sealed class FlipStream(byte[] bytes, Action flip) : MemoryStream(bytes)
{
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        flip();
        return await base.ReadAsync(buffer, cancellationToken);
    }
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        flip();
        return await base.ReadAsync(buffer, offset, count, cancellationToken);
    }
    public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
    {
        flip();
        return base.CopyToAsync(destination, bufferSize, cancellationToken);
    }
}

sealed class FlipHandler(Action flip) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        flip();
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
    }
}

sealed class UsageHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"usage\":{\"prompt_tokens\":11,\"completion_tokens\":7}}", Encoding.UTF8, "application/json") });
}

sealed class ErrorHandler : HttpMessageHandler
{
    public const string Body = "private-upstream-error-never-log";
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(Body) });
}
