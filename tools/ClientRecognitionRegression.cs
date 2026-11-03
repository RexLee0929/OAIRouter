using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Http;
using OAIRouter;

var identify = typeof(ProxyService).GetMethod("IdentifyClient", BindingFlags.NonPublic | BindingFlags.Static)!;
var id = Guid.NewGuid().ToString("D");
(string client, string session, string source, bool trace, bool request) Run(string json, string agent = "", string configured = "auto", string header = "")
{
    var ctx = new DefaultHttpContext();
    ctx.Request.Headers.UserAgent = agent;
    if (header.Length > 0) ctx.Request.Headers["X-Conversation-ID"] = header;
    return ((string, string, string, bool, bool))identify.Invoke(null, new object?[] { ctx, Encoding.UTF8.GetBytes(json), configured })!;
}
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    Console.WriteLine("PASS " + name);
}
var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("conversation:" + id))).ToLowerInvariant();
var a = Run($$"""{"conversationId":"{{id}}","traceId":"secret","conversationRequestId":"secret"}""", "WorkBuddy/1");
Check(a.client == "workbuddy" && a.session == expected && a.session != id && a.source == "body" && a.trace && a.request, "顶层标识散列与 WorkBuddy 识别");
var b = Run("{\"metadata\":{\"conversationId\":\"" + id + "\"}}", configured: "harness");
Check(b.client == "deepseek-harness" && b.session == expected && b.session == a.session && b.source == "metadata", "旧客户端配置与 metadata");
var c = Run($$"""{"conversationRequestId":"{{id}}","traceId":"{{id}}"}""");
Check(c.session == "" && c.trace && c.request, "请求 ID 不作为会话 ID");
var d = Run($$"""{"messages":[{"conversationId":"{{id}}"}]}""", header: id);
Check(d.session == expected && d.source == "header", "请求头优先");
var e = Run($$"""{"messages":[{"conversationId":"{{id}}"}]}""");
Check(e.session == "", "不遍历正文");
var f = Run("{bad", configured: "deepseek");
Check(f.client == "deepseek-harness" && f.session == "", "旧 DeepSeek 配置与无效 JSON");
Check(Run("{}", configured: "deepseek-harness").client == "deepseek-harness", "显式 DeepSeek Harness");
Check(Run("{}", agent: "DeepSeek-Harness/1").client == "deepseek-harness", "完整客户端标识");
Check(Run("{}", agent: "DeepSeek/1").client == "unknown", "仅模型名称不应识别为客户端");
Check(Run("{}", agent: "Other-Harness/1").client == "unknown", "通用 Harness 不应识别为 DeepSeek Harness");
Check(Run("{\"conversationId\":\"not-a-uuid\"}").session == "", "无效 UUID 不作为会话");
Check(Run("{}", header: id.Replace("-", "").ToUpperInvariant()).session == expected, "UUID 规范化后稳定关联");
Check(!new AppConfig().ExperimentalDiagnostics, "诊断默认关闭");
