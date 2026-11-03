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
    public bool LegacyDiagnostic { get; init; }
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
                              string client = "", string traceId = "", string dialogId = "", bool dialogIdMissing = false)
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
                        : $"{(first is null ? "发起时间未知" : first.Value.ToString("yyyy-MM-dd HH:mm:ss"))} · {(g.Key.Length == 64 && g.Key.All(Uri.IsHexDigit) ? g.Key[..20] : g.Key)}";
                    return BuildRow(key, g.ToList(), cfg, first?.Ticks ?? 0);
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

    public static List<SessionUsageGroup> GroupSessions(AppConfig cfg, DateTime? since)
        => GroupSessions(cfg, since, LegacyUsageEvidence.Enrich(Snapshot(), LogStore.Instance.DiagnosticsFile));

    public static List<SessionUsageGroup> GroupSessions(AppConfig cfg, DateTime? since,
        IEnumerable<UsageRecord> records, IEnumerable<UsageRecord>? associationHistory = null)
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
            .Where(i => i.Client == "workbuddy" && !string.IsNullOrWhiteSpace(i.SessionId) && !string.IsNullOrWhiteSpace(i.DialogId))
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

        var assigned = items.Select(i =>
        {
            var session = string.IsNullOrWhiteSpace(i.SessionId) ? "" : i.SessionId;
            var dialog = string.IsNullOrWhiteSpace(i.DialogId) ? "" : i.DialogId;
            var inferred = false;
            if (i.Client == "workbuddy" && i.DialogIdMissing && dialog.Length == 0 && session.Length != 0
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
            return (Record: i, Session: session, Dialog: dialog, Inferred: inferred);
        }).Where(i => since is null || i.Record.Time >= since.Value).ToList();

        static string ShortId(string id) => id.Length == 64 && id.All(Uri.IsHexDigit) ? id[..20] : id;
        static string TimeLabel(DateTime? time) => time is null ? "发起时间未知" : time.Value.ToString("yyyy-MM-dd HH:mm:ss");

        return assigned.GroupBy(i => i.Session, StringComparer.Ordinal).Select(session =>
        {
            var sessionItems = session.Select(i => i.Record).ToList();
            var first = sessionItems.Min(i => i.StartedAt);
            var title = session.Key.Length == 0 ? "未识别会话" : $"{TimeLabel(first)} · {ShortId(session.Key)}";
            var dialogs = session.GroupBy(i => i.Dialog, StringComparer.Ordinal).Select(dialog =>
            {
                var dialogItems = dialog.Select(i => i.Record).ToList();
                var dialogFirst = dialogItems.Min(i => i.StartedAt);
                var dialogTitle = dialog.Key.Length == 0 ? "未识别对话" :
                    $"对话（请求标识） · {TimeLabel(dialogFirst)} · {ShortId(dialog.Key)}";
                if (dialog.Any(i => i.Record.LegacyDiagnostic)) dialogTitle += " · 历史诊断推断";
                else if (dialog.Any(i => i.Inferred)) dialogTitle += " · 含推断关联";
                return BuildRow(dialogTitle, dialogItems, cfg, dialogFirst?.Ticks ?? 0, dialog.Key);
            }).OrderByDescending(r => r.Order).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();
            return new SessionUsageGroup
            {
                Id = session.Key,
                Total = BuildRow(title, sessionItems, cfg, first?.Ticks ?? 0, session.Key),
                Dialogs = dialogs,
            };
        }).OrderByDescending(g => g.Total.Order).ThenBy(g => g.Id, StringComparer.Ordinal).ToList();
    }

    public static string ExportCsv(AppConfig cfg, DateTime? since = null)
    {
        static string Cell(string value)
        {
            if (value.TrimStart().StartsWith('=') || value.TrimStart().StartsWith('+') ||
                value.TrimStart().StartsWith('-') || value.TrimStart().StartsWith('@')) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        var csv = new System.Text.StringBuilder("时间,发起时间,会话ID,客户端,追踪摘要,对话摘要,辅助请求候选,路由,模型,上游模型,输入Token,输出Token,缓存读取,缓存写入,耗时毫秒,状态,金额\r\n");
        foreach (var item in Snapshot().Where(i => since is null || i.Time >= since.Value).OrderByDescending(i => i.Time))
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

    private static UsageRow BuildRow(string key, List<UsageRecord> items, AppConfig cfg, long order = 0, string id = "")
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
