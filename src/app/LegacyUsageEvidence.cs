using System.Globalization;
using System.IO;

namespace OAIRouter;

public static class LegacyUsageEvidence
{
    private const long MaxFileBytes = 8 * 1024 * 1024;

    private readonly record struct MatchKey(string Session, long Second, int Status, long Duration, long Input, long Output);
    private sealed record Evidence(MatchKey Key, string Trace, string Request);

    public static List<UsageRecord> Enrich(IReadOnlyList<UsageRecord> records, string diagnosticFile)
    {
        var result = records.Select(r => Copy(r, r.Client, r.TraceId, r.DialogId, r.DialogIdMissing)).ToList();
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

        var evidenceByKey = diagnostics.GroupBy(e => e.Key).ToDictionary(g => g.Key, g => g.ToList());
        var recordKeys = records.Select(KeyFor).ToList();
        var recordCounts = recordKeys.Where(k => k.HasValue).GroupBy(k => k!.Value)
            .ToDictionary(g => g.Key, g => g.Count());
        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i];
            var key = recordKeys[i];
            if (!string.IsNullOrEmpty(r.Client) || !string.IsNullOrEmpty(r.TraceId) || !string.IsNullOrEmpty(r.DialogId)
                || r.DialogIdMissing || key is null || recordCounts[key.Value] != 1
                || !evidenceByKey.TryGetValue(key.Value, out var matches) || matches.Count != 1) continue;
            var match = matches[0];
            result[i] = Copy(r, "workbuddy", "legacy-diagnostic:" + match.Trace,
                match.Request.Length == 0 ? "" : "legacy-diagnostic:" + match.Request, match.Request.Length == 0, true);
        }
        return result;
    }

    private static MatchKey? KeyFor(UsageRecord r)
    {
        if (r.SessionId.Length != 64 || !r.SessionId.All(Uri.IsHexDigit)) return null;
        return new MatchKey(r.SessionId[..20].ToLowerInvariant(), r.Time.Ticks / TimeSpan.TicksPerSecond,
            r.Status, r.Ms, r.Prompt, r.Completion);
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
            var name = part[..equals];
            if (!fields.TryAdd(name, part[(equals + 1)..])) return null;
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
            || !Number(fields, "duration_ms", out var duration)
            || !Number(fields, "input_tokens", out var input)
            || !Number(fields, "output_tokens", out var output)) return null;
        var request = "";
        if (fields.TryGetValue("header_request", out var value))
        {
            if (!Hex(value, 16)) return null;
            request = value.ToLowerInvariant();
        }
        return new Evidence(new MatchKey(session.ToLowerInvariant(), time.Ticks / TimeSpan.TicksPerSecond,
            (int)status, duration, input, output), trace.ToLowerInvariant(), request);
    }

    private static bool Hex(string value, int length) => value.Length == length && value.All(Uri.IsHexDigit);
    private static bool Number(Dictionary<string, string> fields, string key, out long value)
    {
        value = 0;
        return fields.TryGetValue(key, out var text) && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0;
    }

    private static UsageRecord Copy(UsageRecord r, string client, string trace, string dialog, bool missing, bool? legacyDiagnostic = null) => new()
    {
        Time = r.Time, StartedAt = r.StartedAt, SessionId = r.SessionId, Client = client,
        TraceId = trace, DialogId = dialog, DialogIdMissing = missing, LegacyDiagnostic = legacyDiagnostic ?? r.LegacyDiagnostic, Route = r.Route,
        Model = r.Model, Target = r.Target, Prompt = r.Prompt, Completion = r.Completion,
        CacheRead = r.CacheRead, CacheWrite = r.CacheWrite, Ms = r.Ms, Status = r.Status
    };
}
