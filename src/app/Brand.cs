using System.Reflection;

namespace OAIRouter;

/// <summary>品牌信息的唯一来源。界面任何位置都不许写字面量。</summary>
public static class Brand
{
    public const string Name = "OAIRouter";
    public const string Tagline = "模型路由与协议转换";
    public const string Author = "Rex Lee";
    public const string Email = "rexlee@rexleepro.com";

    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var cut = info.IndexOf('+');
            return cut > 0 ? info[..cut] : info;
        }
        return asm.GetName().Version?.ToString(3) ?? "1.0.0";
    }
}
