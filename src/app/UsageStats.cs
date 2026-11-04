using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;

namespace OAIRouter;

public sealed class UsageRecord
{
    public DateTime Time { get; init; }
    public DateTime? StartedAt { get; init; }
    public string SessionId { get; init; } = "";
    public string Client { get; init; } = "";
    public string TraceId { get; init; } = "";
    public string DialogId { get; init; } = "";
    public bool DialogIdMissing { get; init; }
    public string CorrelationSessionId { get; init; } = "";
    public string ParentSessionId { get; init; } = "";
    public string RequestId { get; init; } = "";
    public string RootRequestId { get; init; } = "";
    public bool CorrelationInvalid { get; init; }
    public bool LegacyDiagnostic { get; init; }
    public string DiagnosticTraceId { get; init; } = "";
    public string DiagnosticDialogId { get; init; } = "";
    public bool DiagnosticTokensUnknown { get; init; }
    public bool DiagnosticAssociationCandidate { get; init; }
    public string Route { get; init; } = "";
    /// <summary>展示名 = 模型映射名（客户端实际填的那个别名）。分组与「按模型」列表都看它。</summary>
    public string Model { get; init; } = "";
    /// <summary>真实名 = 发给上游的模型名。价格表配的是它，计价只认它。</summary>
    public string Target { get; init; } = "";
    public long Prompt { get; init; }
    public long Completion { get; init; }
    public long CacheRead { get; init; }
    public long CacheWrite { get; init; }
    public long Ms { get; init; }
    public int Status { get; init; }
}

public sealed class UsageRow
{
    public string Id { get; init; } = "";
    public string Key { get; init; } = "";
    public string Upstream { get; init; } = "";
    public string ModelName { get; init; } = "";
    public string Client { get; init; } = "";
    public int Requests { get; init; }
    public long Prompt { get; init; }
    public long Completion { get; init; }
    public decimal Cost { get; init; }
    /// <summary>时间分组的排序键（ticks），其他分组为 0</summary>
    public long Order { get; init; }

    public string RequestsText => Requests.ToString();
    public string PromptText => Prompt.ToString("N0");
    public string CompletionText => Completion.ToString("N0");
    public string CostText
    {
        get
        {
            if (Cost > 0) return UsageStats.Money(Cost);
            // 有 token 却没算出钱，说明没匹配到价格规则，这比显示 — 更有诊断价值
            return Prompt + Completion > 0 ? "无价格" : "—";
        }
    }
}

public sealed class SessionUsageGroup
{
    public string Id { get; init; } = "";
    public UsageRow Total { get; init; } = new();
    public List<UsageRow> Dialogs { get; init; } = new();
}

public enum UsageGroup { Model, Route, Session, Time }

/// <summary>概览与用量共用的唯一数据源，避免两个视图各算一套对不上。</summary>
public readonly record struct UsageSummary(
    int Requests, int Failed, long Prompt, long Completion, long TotalMs, decimal Cost)
{
    public long AverageMs => Requests > 0 ? TotalMs / Requests : 0;
}

/// <summary>
/// 用量统计。只记数值（路由、模型、token 数、耗时），不含任何对话内容。
/// 落盘为 usage.jsonl，一行一条。
/// </summary>
public static class UsageStats
{
    private static readonly object Gate = new();
    private static readonly List<UsageRecord> Items = new();
    private const int MaxRecords = 20000;

    public static string FilePath => Path.Combine(ConfigStore.Dir, "usage.jsonl");

    /// <summary>金额自适应精度：小额也能看出小数点后的数。</summary>
    public static string Money(decimal value)
    {
        if (value <= 0) return "$0";
        if (value < 0.000001m) return "$" + value.ToString("0.00000000");
        if (value < 0.0001m) return "$" + value.ToString("0.000000");
        if (value < 0.01m) return "$" + value.ToString("0.00000");
        if (value < 1m) return "$" + value.ToString("0.0000");
        return "$" + value.ToString("0.00");
    }
    public static event Action? Changed;

    public static void Load()
    {
        try
        {
            lock (Gate)
            {
                if (!System.IO.File.Exists(FilePath)) return;
                var lines = System.IO.File.ReadAllLines(FilePath);
                var start = Math.Max(0, lines.Length - MaxRecords);
                Items.Clear();
                for (var i = start; i < lines.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(lines[i])) continue;
                    try
                    {
                        if (JsonNode.Parse(lines[i]) is not JsonObject o) continue;
                        var display = o["m"]?.GetValue<string>() ?? "";
                        // 老记录只有 m（当时存的就是真实名），没有 mt —— 回退成 m，
                        // 既保证计价仍能命中，也保证历史数据不会因为这次改动而变「无价格」。
                        var real = o["mt"]?.GetValue<string>() is { Length: > 0 } mt ? mt : display;
                        Items.Add(new UsageRecord
                        {
                            Time = DateTime.TryParse(o["t"]?.GetValue<string>(), out var t) ? t : DateTime.Now,
                            StartedAt = DateTime.TryParse(o["startedAt"]?.GetValue<string>(), null, DateTimeStyles.RoundtripKind, out var startedAt) ? startedAt : null,
                            SessionId = o["sessionId"]?.GetValue<string>() ?? "",
                            Client = o["client"]?.GetValue<string>() ?? "",
                            TraceId = o["traceId"]?.GetValue<string>() ?? "",
                            DialogId = o["dialogId"]?.GetValue<string>() ?? "",
                            DialogIdMissing = o["dialogIdMissing"]?.GetValue<bool>() ?? false,
                            CorrelationSessionId = o["correlationSessionId"]?.GetValue<string>() ?? "",
                            ParentSessionId = o["parentSessionId"]?.GetValue<string>() ?? "",
                            RequestId = o["requestId"]?.GetValue<string>() ?? "",
                            RootRequestId = o["rootRequestId"]?.GetValue<string>() ?? "",
                            CorrelationInvalid = o["correlationInvalid"]?.GetValue<bool>() ?? false,
                            Route = o["r"]?.GetValue<string>() ?? "",
                            Model = display,
                            Target = real,
                            Prompt = o["p"]?.GetValue<long>() ?? 0,
                            Completion = o["c"]?.GetValue<long>() ?? 0,
                            CacheRead = o["cr"]?.GetValue<long>() ?? 0,
                            CacheWrite = o["cw"]?.GetValue<long>() ?? 0,
                            Ms = o["ms"]?.GetValue<long>() ?? 0,
                            Status = o["s"]?.GetValue<int>() ?? 200,
                        });
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            LogStore.Instance.Warn("统计", $"读取 usage.jsonl 失败：{ex.Message}");
        }
    }

    /// <param name="model">展示名：模型映射名（客户端填的别名）。</param>
    /// <param name="target">真实名：发给上游的模型名，计价用它。为空时退化成展示名。</param>
    public static void Record(string route, string model, string target, long prompt, long completion,
                              long cacheRead, long cacheWrite, long ms, int status,
                              string sessionId = "", DateTime? startedAt = null,
                              string client = "", string traceId = "", string dialogId = "", bool dialogIdMissing = false,
                              string correlationSessionId = "", string parentSessionId = "", string requestId = "", string rootRequestId = "", bool correlationInvalid = false)
    {
        var display = string.IsNullOrWhiteSpace(model) ? (target ?? "") : model;
        var real = string.IsNullOrWhiteSpace(target) ? display : target;

        var record = new UsageRecord
        {
            Time = DateTime.Now,
            StartedAt = startedAt,
            SessionId = string.IsNullOrWhiteSpace(sessionId) ? "" : sessionId,
            Client = string.IsNullOrWhiteSpace(client) ? "" : client,
            TraceId = string.IsNullOrWhiteSpace(traceId) ? "" : traceId,
            DialogId = string.IsNullOrWhiteSpace(dialogId) ? "" : dialogId,
            DialogIdMissing = dialogIdMissing,
            CorrelationSessionId = correlationSessionId,
            ParentSessionId = parentSessionId,
            RequestId = requestId,
            RootRequestId = rootRequestId,
            CorrelationInvalid = correlationInvalid,
            Route = route ?? "",
            Model = display,
            Target = real,
            Prompt = Math.Max(0, prompt),
            Completion = Math.Max(0, completion),
            CacheRead = Math.Max(0, cacheRead),
            CacheWrite = Math.Max(0, cacheWrite),
            Ms = Math.Max(0, ms),
            Status = status,
        };

        lock (Gate)
        {
            Items.Add(record);
            if (Items.Count > MaxRecords) Items.RemoveRange(0, Items.Count - MaxRecords);
            try
            {
                var line = new JsonObject
                {
                    ["t"] = record.Time.ToString("yyyy-MM-dd HH:mm:ss"),
                    ["startedAt"] = record.StartedAt?.ToString("O"),
                    ["sessionId"] = record.SessionId,
                    ["client"] = record.Client,
                    ["traceId"] = record.TraceId,
                    ["dialogId"] = record.DialogId,
                    ["dialogIdMissing"] = record.DialogIdMissing,
                    ["correlationSessionId"] = record.CorrelationSessionId,
                    ["parentSessionId"] = record.ParentSessionId,
                    ["requestId"] = record.RequestId,
                    ["rootRequestId"] = record.RootRequestId,
                    ["correlationInvalid"] = record.CorrelationInvalid,
                    ["r"] = record.Route,
                    ["m"] = record.Model,
                    // 真实名单独一列：界面按映射名分组，计价仍按真实名匹配价格表，两者互不影响
                    ["mt"] = record.Target,
                    ["p"] = record.Prompt,
                    ["c"] = record.Completion,
                    ["cr"] = record.CacheRead,
                    ["cw"] = record.CacheWrite,
                    ["ms"] = record.Ms,
                    ["s"] = record.Status,
                }.ToJsonString();
                System.IO.File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch { }
        }

        Changed?.Invoke();
    }

    private static List<UsageRecord> Snapshot()
    {
        lock (Gate) return Items.ToList();
    }

    public static List<UsageRow> Group(UsageGroup by, AppConfig cfg, TimeSpan? bucketSize = null, DateTime? since = null)
    {
        var items = Snapshot().Where(i => since is null || i.Time >= since.Value).ToList();

        if (by == UsageGroup.Time)
        {
            var span = bucketSize is { TotalMinutes: > 0 } size ? size : TimeSpan.FromDays(1);
            var rows = items
                .GroupBy(i => BucketStart(i.Time, span))
                .Select(g => BuildRow(BucketLabel(g.Key, span), g.ToList(), cfg, g.Key.Ticks))
                .OrderByDescending(r => r.Order)
                .ToList();
            return rows;
        }

        if (by == UsageGroup.Session)
        {
            return items
                .GroupBy(i => string.IsNullOrWhiteSpace(i.SessionId) ? "" : i.SessionId, StringComparer.Ordinal)
                .Select(g =>
                {
                    var first = g.Min(i => i.StartedAt);
                    var key = g.Key.Length == 0
                        ? "未识别会话"
                        : $"{(first is null ? "发起时间未知" : first.Value.ToString("yyyy-MM-dd HH:mm"))} · {(g.Key.Length == 64 && g.Key.All(Uri.IsHexDigit) ? g.Key[..20] : g.Key)}";
                    return BuildRow(key, g.ToList(), cfg, first?.Ticks ?? 0, modelName: "");
                })
                .OrderByDescending(r => r.Order)
                .ToList();
        }

        var groups = items.GroupBy(i => by == UsageGroup.Model
            ? (string.IsNullOrWhiteSpace(i.Model) ? "(未标注)" : i.Model)
            : (string.IsNullOrWhiteSpace(i.Route) ? "(未知)" : i.Route));

        return groups
            .Select(g => BuildRow(g.Key, g.ToList(), cfg))
            .OrderByDescending(r => r.Cost)
            .ThenByDescending(r => r.Prompt + r.Completion)
            .ToList();
    }

    public static List<SessionUsageGroup> GroupSessions(AppConfig cfg, DateTime? since, string? model = null, bool realName = false)
    {
        var records = LegacyUsageEvidence.Enrich(Snapshot(), LogStore.Instance.DiagnosticsFile).ToList();
        return GroupSessions(cfg, since, records.Where(i => MatchesModel(i, model, realName)), records, realName);
    }

    public static List<SessionUsageGroup> GroupSessions(AppConfig cfg, DateTime? since,
        IEnumerable<UsageRecord> records, IEnumerable<UsageRecord>? associationHistory = null, bool realName = false,
        IReadOnlyDictionary<string, string>? workBuddySessions = null)
    {
        var items = records.ToList();
        var history = associationHistory is null ? items : items.Concat(associationHistory).ToList();
        var associations = history
            .Where(i => i.Client == "workbuddy" && !string.IsNullOrWhiteSpace(i.TraceId) && !string.IsNullOrWhiteSpace(i.DialogId))
            .GroupBy(i => i.TraceId, StringComparer.Ordinal)
            .Select(g => new { Trace = g.Key, Records = g.ToList() })
            .Where(g => g.Records.All(i => !string.IsNullOrWhiteSpace(i.SessionId)))
            .Select(g => new { g.Trace, Pairs = g.Records.Select(i => (i.SessionId, i.DialogId)).Distinct().ToList() })
            .Where(g => g.Pairs.Count == 1)
            .ToDictionary(g => g.Trace, g => g.Pairs[0], StringComparer.Ordinal);

        var mainSessions = history
            .Where(i => !string.IsNullOrWhiteSpace(i.SessionId) &&
                (i.Client != "workbuddy" || !i.DialogIdMissing || !string.IsNullOrWhiteSpace(i.DialogId)))
            .Select(i => i.SessionId).ToHashSet(StringComparer.Ordinal);
        var evidenceByTrace = history
            .Where(i => i.Client == "workbuddy" && !string.IsNullOrWhiteSpace(i.TraceId) && !string.IsNullOrWhiteSpace(i.DialogId))
            .GroupBy(i => i.TraceId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(i => i.SessionId).Distinct(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        var crossTargets = history
            .Where(i => i.Client == "workbuddy" && i.DialogIdMissing && string.IsNullOrWhiteSpace(i.DialogId)
                && !string.IsNullOrWhiteSpace(i.SessionId) && !string.IsNullOrWhiteSpace(i.TraceId)
                && evidenceByTrace.ContainsKey(i.TraceId))
            .SelectMany(i => evidenceByTrace[i.TraceId].Select(target => (Source: i.SessionId, Target: target)))
            .GroupBy(i => i.Source, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(i => i.Target).Distinct(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);

        // 只使用入站证据；按子会话和根请求逐条分配，不传播会话级映射。
        var correlated = history.Where(i => i.Client == "workbuddy" && i.CorrelationSessionId.Length > 0).ToList();
        var parentCandidates = correlated.Where(i => !i.CorrelationInvalid && i.ParentSessionId.Length == 0 && (i.RootRequestId.Length == 0 || i.RootRequestId == i.RequestId)
                && i.RequestId.Length > 0 && i.SessionId.Length > 0 && (i.DialogId.Length == 0 || i.DialogId == i.RequestId))
            .GroupBy(i => (i.CorrelationSessionId, i.RequestId))
            .ToDictionary(g => g.Key, g => g.Select(i => (i.SessionId, DialogId: i.RequestId)).Distinct().ToList());
        var ambiguousRequests = parentCandidates.Keys.GroupBy(k => k.RequestId, StringComparer.Ordinal)
            .Where(g => g.Select(k => k.CorrelationSessionId).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var edges = correlated.Where(i => !i.CorrelationInvalid && i.ParentSessionId.Length > 0 && i.RootRequestId.Length > 0 && i.RequestId.Length > 0)
            .GroupBy(i => (i.CorrelationSessionId, i.RootRequestId))
            .ToDictionary(g => g.Key, g => g.Select(i => i.ParentSessionId).Distinct(StringComparer.Ordinal).ToList());
        var conflictingRequests = correlated.Where(i => !i.CorrelationInvalid && i.ParentSessionId.Length > 0 && i.RootRequestId.Length > 0 && i.RequestId.Length > 0)
            .GroupBy(i => (i.CorrelationSessionId, i.RequestId))
            .Where(g => g.Select(i => (i.ParentSessionId, i.RootRequestId)).Distinct().Count() > 1)
            .Select(g => g.Key).ToHashSet();
        var graph = edges.SelectMany(e => e.Value.Select(parent => (Child: e.Key.CorrelationSessionId, Parent: parent)))
            .GroupBy(e => e.Child).ToDictionary(g => g.Key, g => g.Select(e => e.Parent).Distinct().ToList());
        bool Cyclic(string start)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>();
            pending.Push(start);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (!seen.Add(current)) continue;
                if (!graph.TryGetValue(current, out var parents)) continue;
                foreach (var parent in parents)
                {
                    if (parent == start) return true;
                    pending.Push(parent);
                }
            }
            return false;
        }
        var direct = new Dictionary<(string, string), (string SessionId, string DialogId)>();
        foreach (var edge in edges)
        {
            if (edge.Value.Count != 1 || ambiguousRequests.Contains(edge.Key.RootRequestId) || Cyclic(edge.Key.CorrelationSessionId)) continue;
            var parent = edge.Value[0];
            if (parent == edge.Key.CorrelationSessionId || Cyclic(parent)) continue;
            if (!parentCandidates.TryGetValue((parent, edge.Key.RootRequestId), out var candidates) || candidates.Count != 1) continue;
            direct[edge.Key] = candidates[0];
        }

        var assigned = items.Select(i =>
        {
            var session = string.IsNullOrWhiteSpace(i.SessionId) ? "" : i.SessionId;
            var dialog = string.IsNullOrWhiteSpace(i.DialogId) ? "" : i.DialogId;
            var inferred = false;
            if (i.Client == "workbuddy" && !i.CorrelationInvalid && i.ParentSessionId.Length == 0 &&
                (i.RootRequestId.Length == 0 || i.RootRequestId == i.RequestId) && i.RequestId.Length > 0 && dialog.Length == 0 &&
                parentCandidates.TryGetValue((i.CorrelationSessionId, i.RequestId), out var own) && own.Count == 1)
                dialog = i.RequestId;
            var child = i.Client == "workbuddy" && !i.CorrelationInvalid && i.ParentSessionId.Length > 0 && i.RootRequestId.Length > 0
                && !conflictingRequests.Contains((i.CorrelationSessionId, i.RequestId))
                && direct.ContainsKey((i.CorrelationSessionId, i.RootRequestId))
                && edges[(i.CorrelationSessionId, i.RootRequestId)][0] == i.ParentSessionId;
            if (child)
            {
                var target = direct[(i.CorrelationSessionId, i.RootRequestId)];
                session = target.SessionId;
                dialog = target.DialogId;
            }
            else if (i.ParentSessionId.Length == 0 && i.RootRequestId.Length == 0 && !i.CorrelationInvalid && i.Client == "workbuddy" && i.DialogIdMissing && dialog.Length == 0 && session.Length != 0
                && !string.IsNullOrWhiteSpace(i.TraceId)
                && associations.TryGetValue(i.TraceId, out var pair)
                && (session == pair.SessionId || (!mainSessions.Contains(session)
                    && crossTargets.TryGetValue(session, out var targets)
                    && targets.Count == 1 && targets[0] == pair.SessionId)))
            {
                session = pair.SessionId;
                dialog = pair.DialogId;
                inferred = true;
            }
            return (Record: i, Session: session, Dialog: dialog, Inferred: inferred, Child: child);
        }).Where(i => since is null || i.Record.Time >= since.Value).ToList();

        static string ShortId(string id) => id.Length == 64 && id.All(Uri.IsHexDigit) ? id[..20] : id;
        static string TimeLabel(DateTime? time) => time is null ? "发起时间未知" : time.Value.ToString("yyyy-MM-dd HH:mm");

        return assigned.GroupBy(i => i.Session, StringComparer.Ordinal).Select(session =>
        {
            var sessionItems = session.Select(i => i.Record).ToList();
            var first = sessionItems.Min(i => i.StartedAt);
            var clients = string.Join(", ", sessionItems.Select(i => i.Client switch { "workbuddy" => "WorkBuddy", "deepseek" => "DeepSeek Harness", "deepseek-harness" => "DeepSeek Harness", "harness" => "DeepSeek Harness", "unknown" => "未知客户端", _ => i.Client }).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.OrdinalIgnoreCase));
            var title = $"{(first is null ? "发起时间未知" : first.Value.ToString("yyyy-MM-dd HH:mm"))} · {(clients.Length == 0 ? "未知客户端" : clients)} · {(session.Key.Length == 0 ? "未识别会话" : ShortId(session.Key))}";
            var dialogs = session.GroupBy(i => i.Dialog, StringComparer.Ordinal).Select(dialog =>
            {
                var dialogItems = dialog.Select(i => i.Record).ToList();
                var dialogFirst = dialogItems.Min(i => i.StartedAt);
                var childDialog = dialog.Key.StartsWith("\0child:", StringComparison.Ordinal);
                var childParts = childDialog ? dialog.Key[7..].Split(':', 2) : Array.Empty<string>();
                var dialogTitle = childDialog
                    ? $"未识别对话（子任务） · {TimeLabel(dialogFirst)} · 子任务（已关联） · {ShortId(childParts[0])}" + (childParts.Length > 1 && childParts[1].Length > 0 ? $" · 子对话 {ShortId(childParts[1])}" : "")
                    : dialog.Key.Length == 0 ? $"未识别对话 · {TimeLabel(dialogFirst)}" :
                    $"对话（请求标识） · {TimeLabel(dialogFirst)} · {ShortId(dialog.Key)}";
                if (dialog.Any(i => i.Child)) dialogTitle += " · 含子任务";
                if (dialog.Any(i => i.Inferred)) dialogTitle += " · 含推断关联";
                if (dialog.Key.Length == 0 && dialog.Any(i => i.Record.DiagnosticAssociationCandidate)) dialogTitle += " · 诊断关联候选";
                else if (dialog.Key.Length == 0 && dialog.Any(i => !string.IsNullOrEmpty(i.Record.DiagnosticDialogId))) dialogTitle += " · 含诊断记录（未确认对话）";
                return BuildRow(dialogTitle, dialogItems, cfg, dialogFirst?.Ticks ?? 0, dialog.Key, modelName: SessionModels(dialogItems, realName));
            }).OrderByDescending(r => r.Order).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();
            return new SessionUsageGroup
            {
                Id = session.Key,
                Total = BuildRow(title, sessionItems, cfg, first?.Ticks ?? 0, session.Key, modelName: ""),
                Dialogs = dialogs,
            };
        }).OrderByDescending(g => g.Total.Order).ThenBy(g => g.Id, StringComparer.Ordinal).ToList();
    }

    private static string SessionModels(IEnumerable<UsageRecord> items, bool realName)
        => string.Join(", ", items.Select(i => realName ? i.Target : i.Model).Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.Ordinal).OrderBy(m => m, StringComparer.OrdinalIgnoreCase));

    private static bool MatchesModel(UsageRecord item, string? model, bool realName)
        => string.IsNullOrEmpty(model) || string.Equals(realName ? item.Target : item.Model, model, StringComparison.Ordinal);

    public static List<string> FilterModels(DateTime? since, bool realName)
        => Snapshot().Where(i => since is null || i.Time >= since.Value)
            .Select(i => realName ? i.Target : i.Model).Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.Ordinal).OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ThenBy(m => m, StringComparer.Ordinal).ToList();

    public static List<SessionUsageGroup> GroupBreakdown(UsageGroup by, AppConfig cfg, TimeSpan bucket, DateTime? since, string? model, bool realName)
    {
        var items = Snapshot().Where(i => (since is null || i.Time >= since.Value) && MatchesModel(i, model, realName)).ToList();
        string Model(UsageRecord i) => realName ? i.Target : i.Model;
        string RouteName(string route)
        {
            var matches = cfg.Routes.Where(r => string.Equals(r.Prefix, route, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1) return string.IsNullOrWhiteSpace(route) ? "未知上游" : $"未知上游（前缀: {route}）";
            var r = matches[0];
            return string.IsNullOrWhiteSpace(r.Name) ? r.Prefix : r.Name;
        }
        if (by == UsageGroup.Model)
            return items.GroupBy(i => (i.Model, i.Target)).Select(g => new SessionUsageGroup
            {
                Id = g.Key.Model + "\0" + g.Key.Target,
                Total = BuildRow(g.Key.Model, g.ToList(), cfg, modelName: g.Key.Target)
            }).OrderBy(g => g.Total.Key, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Total.ModelName, StringComparer.OrdinalIgnoreCase).ToList();
        return items.GroupBy(i => by == UsageGroup.Time ? BucketStart(i.Time, bucket).Ticks.ToString(CultureInfo.InvariantCulture) : i.Route)
            .Select(g =>
            {
                var data = g.ToList();
                var tick = by == UsageGroup.Time ? long.Parse(g.Key, CultureInfo.InvariantCulture) : 0;
                var title = by == UsageGroup.Time ? BucketLabel(new DateTime(tick), bucket) : RouteName(g.Key);
                var routeMatches = cfg.Routes.Where(r => string.Equals(r.Prefix, g.Key, StringComparison.OrdinalIgnoreCase)).ToList();
                var routeType = by == UsageGroup.Route && routeMatches.Count == 1 ? routeMatches[0].BalanceType : "";
                if (routeType.Length > 0) title = string.IsNullOrWhiteSpace(routeMatches[0].Name) ? routeMatches[0].Prefix : routeMatches[0].Name;
                var children = data.GroupBy(i => (i.Route, Model: Model(i)))
                    .Select(c => BuildRow("", c.ToList(), cfg, upstream: by == UsageGroup.Time ? RouteName(c.Key.Route) : "", modelName: c.Key.Model))
                    .OrderBy(r => r.Upstream, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.ModelName, StringComparer.OrdinalIgnoreCase).ToList();
                return new SessionUsageGroup { Id = g.Key, Total = BuildRow(title, data, cfg, tick, g.Key, upstream: routeType, modelName: ""), Dialogs = children };
            }).OrderBy(g => by == UsageGroup.Time ? g.Total.Order : 0).ThenBy(g => g.Total.Key, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string ExportCsv(AppConfig cfg, DateTime? since = null, string? model = null, bool realName = false)
    {
        static string Cell(string value)
        {
            if (value.TrimStart().StartsWith('=') || value.TrimStart().StartsWith('+') ||
                value.TrimStart().StartsWith('-') || value.TrimStart().StartsWith('@')) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        var csv = new System.Text.StringBuilder("时间,发起时间,会话ID,客户端,追踪摘要,对话摘要,辅助请求候选,路由,模型,上游模型,输入Token,输出Token,缓存读取,缓存写入,耗时毫秒,状态,金额\r\n");
        foreach (var item in Snapshot().Where(i => (since is null || i.Time >= since.Value) && MatchesModel(i, model, realName)).OrderByDescending(i => i.Time))
        {
            var cost = Pricing.Cost(cfg, item.Route, item.Target, item.Prompt, item.Completion, item.CacheRead, item.CacheWrite, item.Time);
            csv.AppendJoin(',', Cell(item.Time.ToString("yyyy-MM-dd HH:mm:ss")), Cell(item.StartedAt?.ToString("O") ?? ""),
                Cell(item.SessionId), Cell(item.Client), Cell(item.TraceId), Cell(item.DialogId),
                item.DialogIdMissing ? "true" : "false", Cell(item.Route), Cell(item.Model), Cell(item.Target),
                item.Prompt.ToString(CultureInfo.InvariantCulture), item.Completion.ToString(CultureInfo.InvariantCulture),
                item.CacheRead.ToString(CultureInfo.InvariantCulture), item.CacheWrite.ToString(CultureInfo.InvariantCulture),
                item.Ms.ToString(CultureInfo.InvariantCulture), item.Status.ToString(CultureInfo.InvariantCulture),
                cost.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        }
        return csv.ToString();
    }

    private static DateTime BucketStart(DateTime time, TimeSpan span)
    {
        var minutes = Math.Max(1, span.TotalMinutes);
        var index = (long)((time - time.Date).TotalMinutes / minutes);
        return time.Date.AddMinutes(index * minutes);
    }

    private static string BucketLabel(DateTime start, TimeSpan span)
        => span.TotalHours >= 24 ? start.ToString("yyyy-MM-dd") : start.ToString("MM-dd HH:mm");

    private static UsageRow BuildRow(string key, List<UsageRecord> items, AppConfig cfg, long order = 0, string id = "", string upstream = "", string? modelName = null)
    {
        decimal cost = 0;
        // 计价只认真实名（价格表配的是上游真实模型名）；分组键另有其人
        foreach (var item in items)
            cost += Pricing.Cost(cfg, item.Route, item.Target, item.Prompt, item.Completion,
                                 item.CacheRead, item.CacheWrite, item.Time);

        return new UsageRow
        {
            Id = id,
            Key = key,
            Upstream = upstream,
            ModelName = modelName ?? string.Join(", ", items.Select(i => i.Model).Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.Ordinal).OrderBy(m => m, StringComparer.OrdinalIgnoreCase)),
            Requests = items.Count,
            Prompt = items.Sum(i => i.Prompt),
            Completion = items.Sum(i => i.Completion),
            Cost = cost,
            Order = order,
        };
    }

    public static UsageSummary Summary(AppConfig cfg)
    {
        var items = Snapshot();
        decimal cost = 0;
        foreach (var item in items)
            cost += Pricing.Cost(cfg, item.Route, item.Target, item.Prompt, item.Completion,
                                 item.CacheRead, item.CacheWrite, item.Time);

        return new UsageSummary(
            items.Count,
            items.Count(i => i.Status >= 400 && i.Status != 499),
            items.Sum(i => i.Prompt),
            items.Sum(i => i.Completion),
            items.Sum(i => i.Ms),
            cost);
    }

    /// <summary>
    /// 用量里出现过的模型名，供价格导入与「从用量补充模型库」用 —— 给的是**真实名**，
    /// 因为价格表与模型库配的都是发给上游的真实模型名，不是映射别名。
    /// </summary>
    public static List<string> KnownModels()
        => Snapshot().Select(i => string.IsNullOrWhiteSpace(i.Target) ? i.Model : i.Target)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    public static void Clear()
    {
        lock (Gate)
        {
            Items.Clear();
            try { if (System.IO.File.Exists(FilePath)) System.IO.File.Delete(FilePath); } catch { }
        }
        Changed?.Invoke();
    }
}

/// <summary>
/// 计价。单价单位「元 / 百万 token」。
/// 匹配优先级：路由精确 > 路由通配，模型精确 > 模型通配，限定时段 > 不限时段，挡位窄 > 挡位宽。
/// </summary>
public static class Pricing
{
    public static PriceRule? Resolve(AppConfig cfg, string route, string model, long promptTokens, DateTime at)
    {
        return cfg.Pricing
            .Where(p => Matches(p, route, model, promptTokens, at))
            .OrderByDescending(Specificity)
            .ThenByDescending(p => ParseInt(p.TierFrom))
            .FirstOrDefault();
    }

    private static int Specificity(PriceRule p)
    {
        var score = 0;
        if (!IsWildcard(p.Route)) score += 8;
        if (!IsWildcard(p.Model)) score += 4;
        if (!string.IsNullOrWhiteSpace(p.TimeFrom) || !string.IsNullOrWhiteSpace(p.TimeTo)) score += 2;
        if (ParseInt(p.TierFrom) > 0) score += 1;
        return score;
    }

    private static bool IsWildcard(string? s)
        => string.IsNullOrWhiteSpace(s) || s.Trim() == "*";

    private static bool Matches(PriceRule p, string route, string model, long promptTokens, DateTime at)
    {
        if (!IsWildcard(p.Route) && !string.Equals(p.Route.Trim(), route, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!IsWildcard(p.Model) && !string.Equals(p.Model.Trim(), model, StringComparison.Ordinal))
            return false;

        if (!InDateRange(p.ValidTo, at)) return false;
        if (!InTimeWindow(p.TimeFrom, p.TimeTo, at)) return false;

        var from = ParseInt(p.TierFrom);
        var to = ParseInt(p.TierTo);
        if (promptTokens < from) return false;
        if (to > 0 && promptTokens >= to) return false;

        return true;
    }

    private static bool InDateRange(string? validTo, DateTime at)
    {
        if (string.IsNullOrWhiteSpace(validTo)) return true;
        return DateTime.TryParseExact(validTo.Trim(), "yyyy-MM-dd",
                   CultureInfo.InvariantCulture, DateTimeStyles.None, out var until)
               && at.Date <= until.Date;
    }

    private static bool InTimeWindow(string? from, string? to, DateTime at)
    {
        var hasFrom = TryParseTime(from, out var f);
        var hasTo = TryParseTime(to, out var t);
        if (!hasFrom && !hasTo) return true;
        if (hasFrom && !hasTo) return at.TimeOfDay >= f;
        if (!hasFrom && hasTo) return at.TimeOfDay < t;
        return f <= t
            ? at.TimeOfDay >= f && at.TimeOfDay < t
            : at.TimeOfDay >= f || at.TimeOfDay < t;   // 跨零点
    }

    private static bool TryParseTime(string? s, out TimeSpan value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        return TimeSpan.TryParseExact(s.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out value)
               || TimeSpan.TryParseExact(s.Trim(), @"h\:mm", CultureInfo.InvariantCulture, out value);
    }

    private static long ParseInt(string? s)
        => long.TryParse((s ?? "").Replace(",", "").Trim(), out var v) ? Math.Max(0, v) : 0;

    public static decimal Cost(AppConfig cfg, string route, string model,
                               long prompt, long completion, long cacheRead, long cacheWrite, DateTime at)
    {
        var rule = Resolve(cfg, route, model, prompt, at);
        if (rule is null) return 0;

        var uncachedInput = Math.Max(0, prompt - cacheRead - cacheWrite);
        var cost = uncachedInput / 1_000_000.0 * rule.InputPerM
                   + cacheRead / 1_000_000.0 * rule.CacheReadPerM
                   + cacheWrite / 1_000_000.0 * rule.CacheWritePerM
                   + completion / 1_000_000.0 * rule.OutputPerM;

        return (decimal)cost;
    }
}
