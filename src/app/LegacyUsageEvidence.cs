using System.Globalization;
using System.IO;

namespace OAIRouter;

public static class LegacyUsageEvidence
{
    private const long MaxFileBytes = 8 * 1024 * 1024;
    private readonly record struct MatchKey(string Session, int Status, long Duration, long Input, long Output);
    private sealed record Evidence(MatchKey Key, long Second, string Trace, string Request, bool TokensUnknown, bool RequestPresent);

    public static List<UsageRecord> Enrich(IReadOnlyList<UsageRecord> records, string diagnosticFile)
    {
        var result = records.Select(r => Copy(r, null)).ToList();
        var diagnostics = new List<Evidence>();
        try
        {
            foreach (var path in new[] { diagnosticFile, diagnosticFile + ".1" })
            {
                if (!File.Exists(path)) continue;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > MaxFileBytes) return result;
                using var reader = new StreamReader(stream);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    var item = Parse(line);
                    if (item is not null) diagnostics.Add(item);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return result;
        }

        var byKey = diagnostics.Select((e, index) => (e, index)).GroupBy(x => x.e.Key)
            .ToDictionary(g => g.Key, g => g.ToList());
        var matches = new List<int>[records.Count];
        var reverseCounts = new int[diagnostics.Count];
        for (var i = 0; i < records.Count; i++)
        {
            matches[i] = new List<int>();
            var key = KeyFor(records[i]);
            if (key is null || !byKey.TryGetValue(key.Value, out var candidates)) continue;
            var second = records[i].Time.Ticks / TimeSpan.TicksPerSecond;
            foreach (var candidate in candidates)
            {
                if (second - candidate.e.Second is < 0 or > 1) continue;
                matches[i].Add(candidate.index);
                reverseCounts[candidate.index]++;
            }
        }
        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i];
            if (matches[i].Count != 1 || reverseCounts[matches[i][0]] != 1 ||
                (!string.IsNullOrWhiteSpace(r.Client) && r.Client != "workbuddy")) continue;
            result[i] = Copy(r, diagnostics[matches[i][0]]);
        }
        return result;
    }

    private static MatchKey? KeyFor(UsageRecord r)
    {
        if (r.SessionId.Length != 64 || !r.SessionId.All(Uri.IsHexDigit)) return null;
        return new MatchKey(r.SessionId[..20].ToLowerInvariant(), r.Status, r.Ms, r.Prompt, r.Completion);
    }

    private static Evidence? Parse(string line)
    {
        if (line.Length < 20 || !DateTime.TryParseExact(line[..19], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var time)) return null;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in line[19..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=');
            if (equals <= 0) continue;
            if (!fields.TryAdd(part[..equals], part[(equals + 1)..])) return null;
        }
        if (!fields.TryGetValue("id_digest", out var digest) || digest != "hmac_sha256_64_process"
            || !fields.TryGetValue("phase", out var phase) || phase != "completed"
            || !fields.TryGetValue("client", out var client) || client != "workbuddy"
            || !fields.TryGetValue("session_source", out var source) || source != "header"
            || (fields.TryGetValue("conversation_agreement", out var agreement) && agreement == "different")
            || fields.ContainsKey("body_request") || fields.ContainsKey("metadata_request")
            || !fields.TryGetValue("session_stat_sha256_20", out var session) || !Hex(session, 20)
            || !fields.TryGetValue("header_trace", out var trace) || !Hex(trace, 16)
            || !Number(fields, "status", out var status) || status > int.MaxValue
            || !Number(fields, "duration_ms", out var duration)) return null;
        var unknown = fields.TryGetValue("input_tokens", out var inputText) && inputText == "unknown"
            && fields.TryGetValue("output_tokens", out var outputText) && outputText == "unknown"
            && fields.TryGetValue("usage_present", out var present) && present == "False";
        long input = 0, output = 0;
        if (!unknown && (!Number(fields, "input_tokens", out input) || !Number(fields, "output_tokens", out output))) return null;
        var request = "";
        if (fields.TryGetValue("header_request", out var value))
        {
            if (!Hex(value, 16)) return null;
            request = value.ToLowerInvariant();
        }
        return new Evidence(new MatchKey(session.ToLowerInvariant(), (int)status, duration, input, output),
            time.Ticks / TimeSpan.TicksPerSecond, trace.ToLowerInvariant(), request, unknown,
            fields.TryGetValue("request_id_present", out var requestPresent) && requestPresent == "True");
    }

    private static bool Hex(string value, int length) => value.Length == length && value.All(Uri.IsHexDigit);
    private static bool Number(Dictionary<string, string> fields, string key, out long value)
    {
        value = 0;
        return fields.TryGetValue(key, out var text) && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0;
    }

    private static UsageRecord Copy(UsageRecord r, Evidence? evidence) => new()
    {
        Time = r.Time, StartedAt = r.StartedAt, SessionId = r.SessionId, Client = r.Client,
        TraceId = r.TraceId, DialogId = r.DialogId, DialogIdMissing = r.DialogIdMissing,
        CorrelationSessionId = r.CorrelationSessionId, ParentSessionId = r.ParentSessionId,
        RequestId = r.RequestId, RootRequestId = r.RootRequestId, CorrelationInvalid = r.CorrelationInvalid,
        LegacyDiagnostic = r.LegacyDiagnostic || evidence is not null,
        DiagnosticTraceId = evidence?.Trace ?? r.DiagnosticTraceId,
        DiagnosticDialogId = evidence?.Request ?? r.DiagnosticDialogId,
        DiagnosticTokensUnknown = evidence?.TokensUnknown ?? r.DiagnosticTokensUnknown,
        DiagnosticAssociationCandidate = evidence is null ? r.DiagnosticAssociationCandidate :
            evidence.Request.Length == 0 && evidence.RequestPresent &&
            string.IsNullOrWhiteSpace(r.TraceId) && string.IsNullOrWhiteSpace(r.DialogId) && !r.DialogIdMissing,
        Route = r.Route, Model = r.Model, Target = r.Target, Prompt = r.Prompt, Completion = r.Completion,
        CacheRead = r.CacheRead, CacheWrite = r.CacheWrite, Ms = r.Ms, Status = r.Status
    };
}
