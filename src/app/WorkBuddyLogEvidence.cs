using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OAIRouter;

public static class WorkBuddyLogEvidence
{
    private const long MaxBytes = 64 * 1024 * 1024;
    private const int MaxLineChars = 128 * 1024;

    private static IEnumerable<string> CompleteLines(StreamReader reader)
    {
        var line = new StringBuilder();
        long total = 0;
        while (reader.Read() is var c && c != -1)
        {
            if (++total > MaxBytes) throw new IOException("日志读取超过限制，未加载关联证据");
            if (c == '\n')
            {
                yield return line.ToString();
                line.Clear();
            }
            else
            {
                if (line.Length >= MaxLineChars) throw new IOException("日志行过长，未加载关联证据");
                line.Append((char)c);
            }
        }
        if (line.Length != 0) throw new IOException("日志尾行不完整，未加载关联证据；请稍后刷新");
    }
    private static readonly Regex Header = new(@"Child session skipped registration:\s*id=(?<id>[^,\s]+),\s*parentSessionId=(?<parent>[^,\s]+),\s*meta=", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private sealed record Candidate(Guid Parent, Guid Root, int Depth, string Request, Guid Conversation, string Process, string Task);
    public sealed record Report(Dictionary<string, string> Sessions, int SkippedRecords);
    private sealed class Evidence
    {
        public HashSet<Candidate> Candidates { get; } = new();
        public int Complete { get; set; }
        public int Partial { get; set; }
    }

    public static string HashSession(Guid id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("conversation:" + id.ToString("D")))).ToLowerInvariant();

    public static Dictionary<string, string> Read(string path) => ReadWithReport(path).Sessions;

    public static Dictionary<string, string> Read(string path, out int skipped)
    {
        var report = ReadWithReport(path);
        skipped = report.SkippedRecords;
        return report.Sessions;
    }

    public static Report ReadWithReport(string path)
    {
        var candidates = new Dictionary<Guid, Evidence>();
        var counts = new Dictionary<Guid, int>();
        var invalid = new HashSet<Guid>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaxBytes) throw new IOException("日志超过 64 MiB，未加载关联证据");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
        foreach (var line in CompleteLines(reader))
        {
            if (!line.Contains("Child session skipped registration:", StringComparison.Ordinal)) continue;
            var idMatch = Regex.Match(line, @"Child session skipped registration:\s*id=(?<id>[^,\s]+)", RegexOptions.CultureInvariant);
            if (!idMatch.Success || !Guid.TryParse(idMatch.Groups["id"].Value, out var id) || id == Guid.Empty)
                throw new IOException("无法确定无效记录的子会话，未加载关联证据");
            counts[id] = counts.GetValueOrDefault(id) + 1;
            var match = Header.Match(line);
            if (!match.Success) { invalid.Add(id); continue; }
            var prefix = line[..match.Index];
            var processes = Regex.Matches(prefix, @"\bpid=(\d+)\b", RegexOptions.CultureInvariant);
            if (processes.Count != 1 || !long.TryParse(processes[0].Groups[1].Value, out var process) || process <= 0 ||
                !prefix.Contains("[waitFor]", StringComparison.Ordinal)) { invalid.Add(id); continue; }
            try
            {
                var json = Encoding.UTF8.GetBytes(line[(match.Index + match.Length)..]);
                var jsonReader = new Utf8JsonReader(json);
                using var doc = JsonDocument.ParseValue(ref jsonReader);
                var meta = doc.RootElement;
                if (meta.ValueKind != JsonValueKind.Object) throw new JsonException();
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in meta.EnumerateObject())
                    if (!names.Add(property.Name)) throw new JsonException();
                string Field(string name) => meta.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
                bool Id(string name, out Guid value) => Guid.TryParse(Field(name), out value) && value != Guid.Empty;
                var hasSession = meta.TryGetProperty("sessionId", out _);
                if (!Guid.TryParse(match.Groups["parent"].Value, out var parent) || parent == Guid.Empty ||
                    (hasSession && (!Id("sessionId", out var metaId) || metaId != id)) || !Id("parentSessionId", out var metaParent) || parent != metaParent ||
                    !Id("subagentRootSessionId", out var root) || !Id("codebuddy.ai/rootSessionId", out var root2) || root != root2 ||
                    !Id("parentConversationId", out var conversation) || conversation != root ||
                    string.IsNullOrWhiteSpace(Field("rootRequestId")) || string.IsNullOrWhiteSpace(Field("taskId")) ||
                    !meta.TryGetProperty("subagentDepth", out var depthValue) || depthValue.ValueKind != JsonValueKind.Number || !depthValue.TryGetInt32(out var depth) || depth < 1 ||
                    id == parent || id == root || (depth == 1 && parent != root) || (depth > 1 && parent == root))
                {
                    invalid.Add(id);
                    continue;
                }
                var candidate = new Candidate(parent, root, depth, Field("rootRequestId"), conversation, process.ToString(System.Globalization.CultureInfo.InvariantCulture), Field("taskId"));
                if (!candidates.TryGetValue(id, out var evidence)) candidates[id] = evidence = new();
                evidence.Candidates.Add(candidate);
                if (hasSession) evidence.Complete++; else evidence.Partial++;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                invalid.Add(id);
            }
        }
        foreach (var candidate in candidates.Where(p => p.Value.Candidates.Count != 1 || p.Value.Complete == 0)) invalid.Add(candidate.Key);
        var unique = candidates.Where(p => !invalid.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value.Candidates.Single());
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in unique)
        {
            var seen = new HashSet<Guid>();
            var current = entry.Key;
            var expected = entry.Value;
            var valid = true;
            while (current != expected.Root)
            {
                if (!seen.Add(current) || !unique.TryGetValue(current, out var next) ||
                    next.Root != expected.Root || next.Depth != expected.Depth || expected.Request != next.Request || next.Process != expected.Process)
                { valid = false; break; }
                current = next.Parent;
                expected = expected with { Depth = expected.Depth - 1 };
            }
            if (valid && expected.Depth == 0) result[HashSession(entry.Key)] = HashSession(entry.Value.Root);
            else invalid.Add(entry.Key);
        }
        var skipped = invalid.Sum(id => counts.GetValueOrDefault(id)) +
            candidates.Where(p => !invalid.Contains(p.Key)).Sum(p => p.Value.Partial);
        return new Report(result, skipped);
    }
}
