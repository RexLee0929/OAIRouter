using System.IO;
using System.Reflection;
using OAIRouter;

internal static class Program
{
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }

    static int RunWorkBuddyEvidence()
    {
        var root = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var child = Guid.Parse("22222222-2222-4222-8222-222222222222");
        var grandchild = Guid.Parse("33333333-3333-4333-8333-333333333333");
        var other = Guid.Parse("44444444-4444-4444-8444-444444444444");
        var directory = Path.Combine(Path.GetTempPath(), "oairouter-evidence-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "synthetic.log");
        int failed = 0, passed = 0;
        string Event(Guid id, Guid parent, Guid rootId, int depth, string request = "synthetic-request") =>
            $"2026-11-04 12:00:00 INFO pid=34564 [waitFor] Child session skipped registration: id={id:D}, parentSessionId={parent:D}, meta=" +
            System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> {
                ["sessionId"] = id.ToString("D"), ["parentSessionId"] = parent.ToString("D"),
                ["subagentRootSessionId"] = rootId.ToString("D"), ["codebuddy.ai/rootSessionId"] = rootId.ToString("D"),
                ["parentConversationId"] = rootId.ToString("D"), ["rootRequestId"] = request, ["subagentDepth"] = depth,
                ["agentId"] = "synthetic-agent", ["taskId"] = "synthetic-task" }) + "\n";
        var first = Event(child, root, root, 1);
        var second = Event(grandchild, child, root, 2);
        Dictionary<string, string> Parse(string content) { File.WriteAllText(path, content); return WorkBuddyLogEvidence.Read(path); }
        void Test(string name, Action action)
        {
            try { action(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}"); }
        }
        void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        void Reject(string content)
        {
            try { Require(Parse(content).Count == 0, "无效证据不得建立关联"); }
            catch (IOException) { }
        }
        try
        {
            var hRoot = WorkBuddyLogEvidence.HashSession(root);
            var hChild = WorkBuddyLogEvidence.HashSession(child);
            var hGrandchild = WorkBuddyLogEvidence.HashSession(grandchild);
            Test("实际日志格式及重复相同证据", () => {
                var map = Parse(first + first);
                Require(map.Count == 1 && map[hChild] == hRoot, "子会话未映射到根或重复记录产生额外结果");
            });
            Test("嵌套链正序与乱序", () => {
                foreach (var text in new[] { first + second, second + first }) {
                    var map = Parse(text);
                    Require(map.Count == 2 && map[hChild] == hRoot && map[hGrandchild] == hRoot, "多层关联失败");
                }
            });
            Test("缺 sessionId 的兼容记录和跳过数量", () => {
                var partial = first.Replace("\"sessionId\":\"" + child + "\",", "");
                File.WriteAllText(path, first + partial);
                var report = WorkBuddyLogEvidence.ReadWithReport(path);
                Require(report.Sessions.Count == 1 && report.Sessions[hChild] == hRoot && report.SkippedRecords == 1, "兼容记录或计数错误");
                Reject(partial);
                Reject(first + partial.Replace("\"taskId\":\"synthetic-task\"", "\"taskId\":\"other-task\""));
                Reject(first + partial.Replace("pid=34564", "pid=34565"));
                Reject(first + partial.Replace("\"subagentDepth\":1", "\"subagentDepth\":\"1\""));
                Require(Parse(first + partial.Replace("pid=34564", "pid=34565") + second).Count == 0, "父会话冲突须排除子会话");
            });
            Test("重复 JSON 字段", () => Reject(first.Replace("\"subagentDepth\":1", "\"subagentDepth\":1,\"subagentDepth\":1")));
            Test("字段类型异常", () => {
                Reject(first.Replace("\"subagentDepth\":1", "\"subagentDepth\":\"1\""));
                Reject(first.Replace("\"rootRequestId\":\"synthetic-request\"", "\"rootRequestId\":42"));
            });
            Test("冲突根字段", () => Reject(first.Replace("\"codebuddy.ai/rootSessionId\":\"" + root + "\"", "\"codebuddy.ai/rootSessionId\":\"" + other + "\"")));
            Test("双向父及子标识校验", () => {
                Reject(first.Replace("\"parentSessionId\":\"" + root + "\"", "\"parentSessionId\":\"" + other + "\""));
                Reject(first.Replace("\"sessionId\":\"" + child + "\"", "\"sessionId\":\"" + other + "\""));
                Reject(first.Replace("parentSessionId=" + root, "parentSessionId=" + other));
            });
            Test("同一子会话多个父", () => Reject(first + Event(child, other, other, 1)));
            Test("嵌套链根和请求冲突", () => {
                foreach (var nested in new[] { Event(grandchild, child, other, 2), Event(grandchild, child, root, 2, "other-request") })
                {
                    var map = Parse(first + nested);
                    Require(map.Count == 1 && map[hChild] == hRoot && !map.ContainsKey(hGrandchild), "无效子链不应影响有效父会话");
                }
            });
            Test("深度不连续和自指", () => {
                var map = Parse(first + Event(grandchild, child, root, 3));
                Require(map.Count == 1 && map[hChild] == hRoot && !map.ContainsKey(hGrandchild), "深度不连续不应影响有效父会话");
                Reject(Event(child, child, root, 2));
            });
            Test("循环链拒绝或不产生关联", () => {
                try { var map = Parse(Event(child, grandchild, root, 2) + Event(grandchild, child, root, 3)); Require(map.Count == 0, "循环链被错误关联"); }
                catch (IOException) { }
            });
            Test("缺失中间父链不能关联", () => Require(Parse(second).Count == 0, "不完整父链不应建立关联"));
            Test("截断 JSON 与不完整末行", () => { Reject(first[..^3] + "\n"); Reject(first[..^1]); });
            Test("空日志与普通伪文本", () => {
                Require(Parse("").Count == 0, "空日志应无关联");
                Require(Parse("synthetic text parentSessionId=" + root + " sessionId=" + child + "\n").Count == 0, "普通文本不应建立关联");
            });
            Test("超长行限制", () => Reject(new string('x', 128 * 1024 + 1) + "\n"));
            Test("文件大小限制", () => {
                using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)) file.SetLength(64L * 1024 * 1024 + 1);
                try { WorkBuddyLogEvidence.Read(path); throw new Exception("超限文件未拒绝"); } catch (IOException) { }
            });
            var map = Parse(first + second);
            var cfg = new AppConfig();
            cfg.Pricing.Add(new PriceRule { Route = "*", Model = "real-one", InputPerM = 1, OutputPerM = 2 });
            var time = new DateTime(2026, 11, 4, 12, 0, 0);
            var records = new[] {
                new UsageRecord { Time = time.AddMinutes(-1), SessionId = hRoot, Client = "workbuddy", DialogId = "main-dialog", Model = "alias-one", Target = "real-one", Prompt = 1000000, Completion = 1000000 },
                new UsageRecord { Time = time, SessionId = hChild, Client = "workbuddy", DialogId = "child-dialog", Model = "alias-one", Target = "real-one", Prompt = 2000000, Completion = 1000000 },
                new UsageRecord { Time = time.AddMinutes(1), SessionId = hGrandchild, Client = "workbuddy", Model = "alias-two", Target = "real-one", Prompt = 3000000, Completion = 2000000 },
                new UsageRecord { Time = time, SessionId = hChild, Client = "other-client", DialogId = "other-dialog", Model = "alias-one", Target = "real-one", Prompt = 4000000, Completion = 1000000 },
                new UsageRecord { Time = time, SessionId = "", Client = "workbuddy", Model = "alias-one", Target = "real-one", Prompt = 5000000, Completion = 1000000 }
            };
            var before = System.Text.Json.JsonSerializer.Serialize(records);
            void Conservation(List<SessionUsageGroup> groups, UsageRecord[] included) {
                Require(groups.Sum(g => g.Total.Requests) == included.Length, "请求数不守恒");
                Require(groups.Sum(g => g.Total.Prompt) == included.Sum(r => r.Prompt), "输入 token 不守恒");
                Require(groups.Sum(g => g.Total.Completion) == included.Sum(r => r.Completion), "输出 token 不守恒");
                Require(groups.Sum(g => g.Total.Cost) == included.Sum(r => Pricing.Cost(cfg, r.Route, r.Target, r.Prompt, r.Completion, r.CacheRead, r.CacheWrite, r.Time)), "金额不守恒");
                foreach (var group in groups) Require(group.Dialogs.Sum(d => d.Requests) == group.Total.Requests && group.Dialogs.Sum(d => d.Prompt) == group.Total.Prompt && group.Dialogs.Sum(d => d.Completion) == group.Total.Completion && group.Dialogs.Sum(d => d.Cost) == group.Total.Cost, "会话与对话汇总不守恒");
            }
            Test("关联父会话显示子任务且原始数据不变", () => {
                var groups = UsageStats.GroupSessions(cfg, null, records, records, false, map);
                var parent = groups.Single(g => g.Id == hRoot);
                Require(parent.Total.Requests == 1, "旧会话级映射不应合并子任务");
                Require(groups.Single(g => g.Id == hChild).Total.Requests == 2, "无请求证据的子会话应保持独立");
                Require(groups.Single(g => g.Id == "").Total.Requests == 1, "未知会话应保留");
                Conservation(groups, records);
                Require(System.Text.Json.JsonSerializer.Serialize(records) == before, "原始记录被修改");
            });
            Test("时间和模型筛选及统计守恒", () => {
                var filtered = records.Where(r => r.Model == "alias-one" && r.Time >= time).ToArray();
                var modelFiltered = records.Where(r => r.Model == "alias-one").ToArray();
                var groups = UsageStats.GroupSessions(cfg, time, modelFiltered, records, false, map);
                Conservation(groups, filtered);
                Require(groups.Single(g => g.Id == hChild).Total.Requests == 2, "无请求证据的子会话应保持独立");
            });
            Test("缺主会话仍保留子任务计费", () => {
                var onlyChild = new[] { records[1] };
                var groups = UsageStats.GroupSessions(cfg, null, onlyChild, onlyChild, false, map);
                Conservation(groups, onlyChild);
                Require(groups.Single().Id == hChild, "缺主会话时不得关联子任务");
            });
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine($"合成回归: {passed} 通过, {failed} 失败");
        return failed == 0 ? 0 : 1;
    }

    static void DiagnoseEvidenceFields(string path)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        void Count(string reason) => counts[reason] = counts.GetValueOrDefault(reason) + 1;
        var header = new System.Text.RegularExpressions.Regex(@"Child session skipped registration:\s*id=(?<id>[^,\s]+),\s*parentSessionId=(?<parent>[^,\s]+),\s*meta=");
        foreach (var line in File.ReadLines(path, new System.Text.UTF8Encoding(false, true)))
        {
            if (!line.Contains("Child session skipped registration:", StringComparison.Ordinal)) continue;
            var match = header.Match(line);
            if (!match.Success || !Guid.TryParse(match.Groups["id"].Value, out var id) || id == Guid.Empty) { Count("头部格式或 id 无效"); continue; }
            try
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(line[(match.Index + match.Length)..]);
                var reader = new System.Text.Json.Utf8JsonReader(bytes);
                using var doc = System.Text.Json.JsonDocument.ParseValue(ref reader);
                var meta = doc.RootElement;
                if (meta.ValueKind != System.Text.Json.JsonValueKind.Object) { Count("元数据非对象"); continue; }
                var names = new HashSet<string>(StringComparer.Ordinal);
                if (meta.EnumerateObject().Any(p => !names.Add(p.Name))) { Count("重复字段"); continue; }
                string Field(string name) => meta.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() ?? "" : "";
                bool Id(string name, out Guid value) => Guid.TryParse(Field(name), out value) && value != Guid.Empty;
                bool parentOk = Guid.TryParse(match.Groups["parent"].Value, out var parent) && parent != Guid.Empty;
                bool idOk = Id("sessionId", out var metaId), metaParentOk = Id("parentSessionId", out var metaParent);
                bool rootOk = Id("subagentRootSessionId", out var root), root2Ok = Id("codebuddy.ai/rootSessionId", out var root2);
                bool conversationOk = Id("parentConversationId", out var conversation);
                if (!parentOk) Count("头部 parent 无效");
                if (!idOk) Count(meta.TryGetProperty("sessionId", out _) ? "sessionId 无效" : "sessionId 缺失");
                else if (metaId != id) Count("sessionId 与头部 id 不匹配");
                if (!metaParentOk || !parentOk || parent != metaParent) Count("parentSessionId 缺失、无效或不匹配");
                if (!rootOk || !root2Ok || root != root2) Count("根字段缺失、无效或不匹配");
                if (!conversationOk || !rootOk || conversation != root) Count("parentConversationId 缺失、无效或不等于根");
                if (string.IsNullOrWhiteSpace(Field("rootRequestId"))) Count("rootRequestId 缺失或无效");
                int depth = 0;
                bool depthOk = meta.TryGetProperty("subagentDepth", out var depthValue) && depthValue.ValueKind == System.Text.Json.JsonValueKind.Number && depthValue.TryGetInt32(out depth) && depth >= 1;
                if (!depthOk) Count("subagentDepth 缺失或无效");
                if (parentOk && rootOk && depthOk && (id == parent || id == root || (depth == 1 && parent != root) || (depth > 1 && parent == root))) Count("id、parent、root 或深度关系不一致");
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException) { Count("JSON 格式无效"); }
        }
        foreach (var item in counts) Console.WriteLine($"字段诊断: {item.Key}: {item.Value}");
    }

    static int RunWorkBuddyFileDiagnostic(string path)
    {
        try
        {
            long chars = 0, lines = 0, maxLine = 0, line = 0;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, new System.Text.UTF8Encoding(false, true), true))
            {
                Console.WriteLine($"字节数: {stream.Length}");
                while (reader.Read() is var c && c != -1)
                {
                    chars++;
                    if (c == '\n') { lines++; maxLine = Math.Max(maxLine, line); line = 0; }
                    else line++;
                }
                maxLine = Math.Max(maxLine, line);
            }
            Console.WriteLine($"字符数: {chars}; 完整行数: {lines}; 最长行字符数: {maxLine}; 尾行完整: {line == 0}");
            Dictionary<string, string> map;
            try { map = WorkBuddyLogEvidence.Read(path); }
            catch (IOException ex)
            {
                var reason = ex.Message switch {
                    "日志超过 64 MiB，未加载关联证据" => "MaxBytes（文件大小）",
                    "日志读取超过限制，未加载关联证据" => "MaxBytes（字符数）",
                    "日志行过长，未加载关联证据" => "MaxLineChars",
                    "日志尾行不完整，未加载关联证据；请稍后刷新" => "尾行不完整",
                    "子会话日志字段不一致，未加载关联证据" => "子会话字段不一致",
                    "子会话日志格式无效，未加载关联证据" => "子会话格式无效",
                    "子会话日志解析失败，未加载关联证据" => "子会话 JSON 解析失败",
                    "子会话日志存在冲突，未加载关联证据" => "子会话证据冲突",
                    "子会话日志存在循环，未加载关联证据" => "父链循环",
                    "子会话日志父链不一致，未加载关联证据" => "父链不一致",
                    _ => "其他读取错误"
                };
                Console.Error.WriteLine("Read 失败: " + reason);
                if (reason == "子会话字段不一致") DiagnoseEvidenceFields(path);
                return 1;
            }
            Console.WriteLine($"关系数: {map.Count}");
            foreach (var entry in map.OrderBy(e => e.Key, StringComparer.Ordinal))
                Console.WriteLine($"{entry.Key[..20]} -> {entry.Value[..20]}");
            var prefixes = Environment.GetEnvironmentVariable("OAIR_EVIDENCE_PREFIXES");
            var root = Environment.GetEnvironmentVariable("OAIR_EVIDENCE_ROOT_PREFIX");
            if (prefixes == null && root == null) return 0;
            var expected = prefixes?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
            if (expected.Length != 6 || expected.Distinct(StringComparer.Ordinal).Count() != 6 || string.IsNullOrWhiteSpace(root) ||
                expected.Any(p => p.Length < 8 || p.Length > 64 || !p.All(Uri.IsHexDigit)) ||
                root.Length < 8 || root.Length > 64 || !root.All(Uri.IsHexDigit))
                throw new ArgumentException("需提供六个不同的十六进制摘要前缀和一个根摘要前缀（每项 8–64 位）");
            foreach (var prefix in expected)
            {
                var matches = map.Where(e => e.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1 || !matches[0].Value.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("摘要前缀未唯一匹配或根不一致");
            }
            Console.WriteLine("PASS 六个摘要前缀均唯一指向指定根");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("诊断失败: " + ex.GetType().Name);
            return 1;
        }
    }

    static void CheckHeaderCorrelation()
    {
        var identify = typeof(ProxyService).GetMethod("IdentifyWorkBuddyCorrelation", BindingFlags.NonPublic | BindingFlags.Static)!;
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        var headerSession = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        context.Request.Headers["X-Conversation-ID"] = headerSession.ToString("D");
        context.Request.Headers["X-Conversation-Request-ID"] = requestId.ToString("N");
        var sessionHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("conversation:" + headerSession.ToString("D")))).ToLowerInvariant();
        (string, string, string, string, bool) Identify(byte[]? body = null) => ((string, string, string, string, bool))identify.Invoke(null, new object?[] { context, body, sessionHash })!;
        var identity = Identify();
        context.Request.Headers["X-Root-Request-ID"] = requestId.ToString("D");
        Check(!Identify().Item5, "主请求根等于自身请求有效");
        context.Request.Headers["X-Root-Request-ID"] = Guid.NewGuid().ToString("D");
        Check(Identify().Item5, "主请求根不等于自身请求无效");
        context.Request.Headers["X-Root-Request-ID"] = requestId.ToString("D");
        foreach (var body in new[] {
            $"{{\"conversationId\":\"{headerSession}\",\"conversation_id\":\"{headerSession}\"}}",
            "{\"metadata\":{},\"metadata\":{}}",
            $"{{\"root_request_id\":\"{Guid.NewGuid()}\"}}",
            "{\"parent_conversation_id\":[]}",
            $"{{\"metadata\":{{\"conversation_request_id\":\"{Guid.NewGuid()}\"}}}}"
        }) Check(Identify(System.Text.Encoding.UTF8.GetBytes(body)).Item5, "重复、类型或正文冲突拒绝");
        Check(!Identify(System.Text.Encoding.UTF8.GetBytes($"{{\"unknown\":{{\"conversationId\":\"secret\"}},\"metadata\":{{\"root_request_id\":\"{requestId}\"}}}}")).Item5, "未知字段不影响关联");
        var parentId = Guid.NewGuid();
        context.Request.Headers["X-Parent-Conversation-ID"] = parentId.ToString("D");
        Check(!Identify(System.Text.Encoding.UTF8.GetBytes($"{{\"parent_conversation_id\":\"{parentId}\",\"rootRequestId\":\"{requestId}\"}}")).Item5, "子请求父和根有效");
        foreach (var (camel, snake, value) in new[] { ("conversationId", "conversation_id", headerSession), ("parentConversationId", "parent_conversation_id", parentId), ("conversationRequestId", "conversation_request_id", requestId), ("rootRequestId", "root_request_id", requestId) })
        {
            Check(Identify(System.Text.Encoding.UTF8.GetBytes($"{{\"metadata\":{{\"{camel}\":\"{value}\",\"{snake}\":\"{value}\"}}}}")).Item5, "元数据别名重复拒绝 " + camel);
            Check(Identify(System.Text.Encoding.UTF8.GetBytes($"{{\"{snake}\":\"{Guid.NewGuid()}\"}}")).Item5, "正文冲突拒绝 " + camel);
        }
        context.Request.Headers.Remove("X-Root-Request-ID");
        Check(Identify().Item5, "子请求缺根无效");
        context.Request.Headers.Remove("X-Parent-Conversation-ID");
        Check(!identity.Item5 && identity.Item1.Length == 64 && identity.Item3.Length == 64 && !identity.ToString().Contains(headerSession.ToString("D")), "入口摘要不暴露原始标识");
        context.Request.Headers["X-Root-Request-ID"] = new Microsoft.Extensions.Primitives.StringValues(new[] { requestId.ToString("D"), requestId.ToString("D") });
        Check(Identify().Item5, "重复头拒绝");
        context.Request.Headers["X-Root-Request-ID"] = "";
        Check(Identify().Item5, "空头拒绝");
        context.Request.Headers.Remove("X-Root-Request-ID");
        Check(Identify(System.Text.Encoding.UTF8.GetBytes("{\"conversationId\":\"" + Guid.NewGuid().ToString("D") + "\"}")).Item5, "正文与头冲突拒绝");
        var cfg = new AppConfig();
        cfg.Pricing.Add(new PriceRule { Route = "*", Model = "real", InputPerM = 1, OutputPerM = 2 });
        var now = new DateTime(2026, 1, 1);
        UsageRecord Record(string session, string sid, string request, string parent = "", string root = "", string model = "a", bool invalid = false, string client = "workbuddy") => new()
        {
            Time = now, SessionId = session, CorrelationSessionId = sid, RequestId = request, ParentSessionId = parent,
            RootRequestId = root, DialogId = request, Client = client, Model = model, Target = "real", Prompt = 1000000,
            Completion = 1000000, CorrelationInvalid = invalid
        };
        var main1 = Record("主", "p", "r1", root: "r1");
        var main2 = Record("主", "p", "r2");
        var child1 = Record("子", "c", "c1", "p", "r1", "b");
        var child2 = Record("子", "c", "c2", "p", "r2", "b");
        var child3 = Record("子2", "c3", "c3r", "p", "r1", "b");
        var history = new[] { child1, child2, child3, main2, main1 };
        var groups = UsageStats.GroupSessions(cfg, null, history, history);
        var group = groups.Single();
        Check(group.Id == "主" && group.Total.Requests == 5 && group.Dialogs.Count == 2, "乱序及多子任务按根请求归属");
        Check(group.Dialogs.Single(d => d.Id == "r1").Requests == 3 && group.Dialogs.Single(d => d.Id == "r1").ModelName == "a, b", "对话显示实际多模型");
        Check(group.Dialogs.Sum(d => d.Prompt) == group.Total.Prompt && group.Dialogs.Sum(d => d.Completion) == group.Total.Completion && group.Dialogs.Sum(d => d.Cost) == group.Total.Cost, "关联数值及金额守恒");
        var filtered = UsageStats.GroupSessions(cfg, null, new[] { child1 }, history);
        Check(filtered.Single().Id == "主" && filtered.Single().Total.Requests == 1, "过滤前历史提供父证据");
        void Separate(string label, params UsageRecord[] records) => Check(UsageStats.GroupSessions(cfg, null, records, records).Count > 1, label);
        Separate("缺父不关联", child1, main2);
        Separate("重启密钥不关联", child1, Record("主", "另一进程", "r1"));
        Separate("非 WorkBuddy 不关联", child1, Record("主", "p", "r1", client: "other"));
        Separate("无效证据不关联", main1, Record("子", "c", "c1", "p", "r1", invalid: true));
        Separate("同根多个父拒绝", main1, child1, Record("子", "c", "c2", "q", "r1"));
        var withOpaque = UsageStats.GroupSessions(cfg, null, new[] { main1, child1, Record("主", "p", "", invalid: true) });
        Check(withOpaque.Single().Total.Requests == 3 && withOpaque.Single().Dialogs.Single(d => d.Id == "r1").Requests == 2, "无关无效请求不污染正常关联");
        Separate("同请求多个根拒绝", main1, child1, Record("子", "c", "c1", "p", "r2"));
        Separate("多个主拒绝", main1, child1, Record("另一主", "q", "r1"));
        Separate("环拒绝", main1, child1, Record("主", "p", "x", "c", "c1"));
        var legacy = new UsageRecord { SessionId = "旧", Client = "workbuddy", DialogId = "d" };
        Check(UsageStats.GroupSessions(cfg, null, new[] { legacy }).Single().Id == "旧", "旧数据兼容");
        var copy = LegacyUsageEvidence.Enrich(new[] { child1 }, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))).Single();
        Check(copy.CorrelationSessionId == "c" && copy.ParentSessionId == "p" && copy.RequestId == "c1" && copy.RootRequestId == "r1", "诊断复制保留关联字段");
    }

    [STAThread] static int Main(string[] args)
    {
        if (args.Contains("--workbuddy-evidence-file"))
        {
            var path = Environment.GetEnvironmentVariable("OAIR_EVIDENCE_PATH");
            if (string.IsNullOrWhiteSpace(path)) { Console.Error.WriteLine("请设置 OAIR_EVIDENCE_PATH"); return 1; }
            return RunWorkBuddyFileDiagnostic(path);
        }
        if (args.Contains("--workbuddy-evidence-only")) return RunWorkBuddyEvidence();
        try
        {
            CheckHeaderCorrelation();
            typeof(App).GetProperty("IsDev")!.SetValue(null, true);
            var items = (List<UsageRecord>)typeof(UsageStats).GetField("Items", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            var cfg = new AppConfig();
            cfg.Routes.Add(new RouteRule { Prefix = "alpha", Name = "测试上游 A", Mode = "chat", BalanceType = "NewAPI" });
            cfg.Routes.Add(new RouteRule { Prefix = "beta", Name = "测试上游 B", Mode = "responses", BalanceType = "自定义" });
            cfg.Pricing.Add(new PriceRule { Route = "*", Model = "real-one", InputPerM = 1 });
            var cutoff = new DateTime(2026, 10, 28, 12, 0, 0);
            items.AddRange(new[] {
                new UsageRecord { Time = cutoff.AddSeconds(-1), StartedAt = cutoff.AddDays(-1), Route = "alpha", Model = "alias-one", Target = "real-one", SessionId = "old", Client = "client-old", Prompt = 100 },
                new UsageRecord { Time = cutoff, StartedAt = cutoff, Route = "alpha", Model = "alias-one", Target = "real-one", SessionId = "s1", Client = "client-a", DialogId = "d1", Prompt = 1_000_000 },
                new UsageRecord { Time = cutoff.AddMinutes(1), StartedAt = cutoff, Route = "beta", Model = "alias-two", Target = "real-one", SessionId = "s1", Client = "client-b", DialogId = "d2", Prompt = 10 },
                new UsageRecord { Time = cutoff.AddDays(1), StartedAt = cutoff.AddDays(1), Route = "alpha", Model = "alias-one", Target = "real-two", SessionId = "s2", Client = "client-a", DialogId = "d3", Prompt = 20 }
            });
            var rows = UsageStats.Group(UsageGroup.Model, cfg, since: cutoff);
            Check(rows.Sum(r => r.Requests) == 3 && rows.Single(r => r.Key == "alias-one").Requests == 2, "时间起点包含边界且排除更早记录");
            Check(rows.Single(r => r.Key == "alias-one").Cost == 1m, "按真实模型计价而非映射名");
            var groups = UsageStats.GroupSessions(cfg, cutoff, items);
            Check(groups.Sum(g => g.Total.Requests) == 3 && groups.All(g => g.Id != "old"), "会话统计应用同一时间过滤");
            var group = groups.Single(g => g.Id == "s1");
            Check(group.Total.Requests == 2 && group.Dialogs.Sum(r => r.Requests) == 2, "会话父子请求计数一致");
            Check(group.Total.ModelName == "" && group.Dialogs.Select(d => d.ModelName).OrderBy(x => x).SequenceEqual(new[] { "alias-one", "alias-two" }), "会话汇总模型留空，对话保留各自模型");
            var realGroup = UsageStats.GroupSessions(cfg, cutoff, items, realName: true).Single(g => g.Id == "s1");
            Check(realGroup.Total.ModelName == "" && realGroup.Dialogs.All(d => d.ModelName == "real-one"), "切换真实模型不影响会话空列和对话模型");
            Check(group.Total.Key.Contains("client-a") && group.Total.Key.Contains("client-b"), "会话汇总保留多个客户端");
            var aliases = UsageStats.FilterModels(cutoff, false);
            var targets = UsageStats.FilterModels(cutoff, true);
            Check(aliases.SequenceEqual(new[] { "alias-one", "alias-two" }) && targets.SequenceEqual(new[] { "real-one", "real-two" }), "筛选列表分别列出映射名和真实名");
            var aliasGroups = UsageStats.GroupBreakdown(UsageGroup.Route, cfg, TimeSpan.FromDays(1), cutoff, "alias-one", false);
            var targetGroups = UsageStats.GroupBreakdown(UsageGroup.Route, cfg, TimeSpan.FromDays(1), cutoff, "real-one", true);
            Check(aliasGroups.Sum(g => g.Total.Requests) == 2 && targetGroups.Sum(g => g.Total.Requests) == 2, "按映射名和真实名分别筛选");
            Check(targetGroups.Single(g => g.Id == "alpha").Total.Key == "测试上游 A" && targetGroups.Single(g => g.Id == "alpha").Total.Upstream == "NewAPI", "上游名称和配置类型分别显示");
            Check(targetGroups.Single(g => g.Id == "beta").Total.Key == "测试上游 B" && targetGroups.Single(g => g.Id == "beta").Total.Upstream == "自定义", "不同上游名称和类型正确");
            var timeGroups = UsageStats.GroupBreakdown(UsageGroup.Time, cfg, TimeSpan.FromDays(1), cutoff, null, false);
            Check(timeGroups.Count == 2 && timeGroups.Sum(g => g.Total.Requests) == 3 && timeGroups.All(g => g.Dialogs.Sum(d => d.Requests) == g.Total.Requests), "时间分组明细不丢失或重复请求");
            Check(timeGroups.SelectMany(g => g.Dialogs).Any(d => d.Upstream.Contains("测试上游 B") && d.ModelName == "alias-two"), "时间明细显示上游及映射名");
            Check(targetGroups.All(g => g.Dialogs.Sum(d => d.Requests) == g.Total.Requests) && targetGroups.SelectMany(g => g.Dialogs).All(d => d.ModelName == "real-one"), "上游明细按真实模型拆分");
            var filteredSessions = UsageStats.GroupSessions(cfg, cutoff, "real-one", true);
            Check(filteredSessions.Sum(g => g.Total.Requests) == 2 && filteredSessions.All(g => g.Id != "s2"), "会话按真实模型名筛选");
            var filteredCsv = UsageStats.ExportCsv(cfg, cutoff, "alias-two", false);
            Check(filteredCsv.Contains("alias-two") && !filteredCsv.Contains("alias-one"), "CSV 跟随映射名筛选");
            var realCsv = UsageStats.ExportCsv(cfg, cutoff, "real-two", true);
            Check(realCsv.Contains("real-two") && !realCsv.Contains("real-one"), "CSV 跟随真实名筛选");
            var modelRows = UsageStats.GroupBreakdown(UsageGroup.Model, cfg, TimeSpan.FromDays(1), cutoff, null, false);
            Check(modelRows.Count == 3 && modelRows.Count(g => g.Total.Key == "alias-one") == 2 && modelRows.Sum(g => g.Total.Requests) == 3, "相同别名对应不同真实名分别展示");
            var csv = UsageStats.ExportCsv(cfg, cutoff);
            Check(!csv.Contains("client-old") && csv.Contains("2026-10-28 12:01:00"), "CSV 过滤旧记录并保留请求精确秒数");
            Check(csv.Contains("alias-one") && csv.Contains("real-one"), "CSV 包含映射名和真实模型名");

            var precise = new DateTime(2026, 10, 28, 12, 34, 56, 789, DateTimeKind.Utc);
            var displayRecords = new[] {
                new UsageRecord { Time = precise, StartedAt = precise, SessionId = "display", DialogId = "known", Model = "alias", Target = "real" },
                new UsageRecord { Time = precise, StartedAt = precise, SessionId = "display", Model = "alias", Target = "real" },
                new UsageRecord { Time = precise, SessionId = "missing-time", DialogId = "known" },
                new UsageRecord { Time = precise, SessionId = "missing-time" },
                new UsageRecord { Time = precise, StartedAt = precise, SessionId = "child", Client = "workbuddy" }
            };
            var displayGroups = UsageStats.GroupSessions(cfg, null, displayRecords, workBuddySessions: new Dictionary<string, string> { ["child"] = "display" });
            var display = displayGroups.Single(g => g.Id == "display");
            Check(display.Total.ModelName == "" && display.Dialogs.Single(d => d.Id == "known").Key == "对话（请求标识） · 2026-10-28 12:34 · known", "已识别对话时间精确到分钟");
            Check(display.Dialogs.Single(d => d.Id == "").Key == "未识别对话 · 2026-10-28 12:34", "未识别对话时间精确到分钟");
            Check(displayGroups.Single(g => g.Id == "child").Total.Requests == 1, "旧映射参数不能触发关联");
            Check(displayGroups.Single(g => g.Id == "missing-time").Dialogs.All(d => d.Key.Contains("发起时间未知")), "缺失发起时间不使用请求完成时间替代");
            items.Add(displayRecords[0]);
            var preciseCsv = UsageStats.ExportCsv(cfg, cutoff);
            Check(preciseCsv.Contains("2026-10-28 12:34:56") && preciseCsv.Contains(precise.ToString("O")), "CSV 保留请求秒数和发起时间完整精度");
            items.Remove(displayRecords[0]);

            var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary { Source = new Uri("/OAIRouter;component/Theme.xaml", UriKind.Relative) });
            app.Resources["BoolToVis"] = new BoolToVis(); app.Resources["InverseBoolToVis"] = new InverseBoolToVis(); app.Resources["LevelToBrush"] = new LevelToBrush();
            Accent.Init(); app.Resources["AccentBrush"] = Accent.MainBrush; app.Resources["AccentDarkBrush"] = Accent.DarkBrush;
            app.Resources["AccentSoftBrush"] = Accent.SoftBrush; app.Resources["AccentHoverBrush"] = Accent.HoverBrush;
            var window = new MainWindow();
            var range = (System.Windows.Controls.ComboBox)window.FindName("UsageRangeBox");
            var since = (DateTime?)typeof(MainWindow).GetMethod("UsageSince", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
            Check(range.SelectedIndex == 5 && since.HasValue && Math.Abs((DateTime.Now.AddDays(-7) - since.Value).TotalSeconds) < 5, "界面默认过去七天");
            typeof(MainWindow).GetMethod("OnGroupByTime", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, new object[] { window, new System.Windows.RoutedEventArgs() });
            since = (DateTime?)typeof(MainWindow).GetMethod("UsageSince", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
            var bucket = (System.Windows.Controls.ComboBox)window.FindName("BucketBox");
            Check(range.Visibility == System.Windows.Visibility.Collapsed && bucket.Visibility == System.Windows.Visibility.Visible && since.HasValue && Math.Abs((DateTime.Now.AddDays(-7) - since.Value).TotalSeconds) < 5, "时间分组显示粒度并保留七天过滤");
            window.AllowClose(); window.Close(); app.Shutdown();
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
