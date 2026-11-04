using OAIRouter;

var dir = Path.Combine(Path.GetTempPath(), "OAIRouter-pricing-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
ConfigStore.Dir = dir;
try
{
    var cfg = new AppConfig();
    cfg.Pricing.Add(new PriceRule { Route = "route", Model = "target", InputPerM = 10, OutputPerM = 20, CacheReadPerM = 2, CacheWritePerM = 5 });
    var at = new DateTime(2025, 1, 1);
    void Check(string name, decimal expected, decimal actual)
    {
        if (Math.Abs(expected - actual) > 0.0000000001m)
            throw new Exception($"{name}: expected {expected}, actual {actual}");
        Console.WriteLine($"PASS {name}");
    }
    decimal Cost(long p, long c, long r, long w) => Pricing.Cost(cfg, "route", "target", p, c, r, w, at);
    Check("无缓存", 12m, Cost(1_000_000, 100_000, 0, 0));
    Check("仅读缓存", 10.4m, Cost(1_000_000, 100_000, 200_000, 0));
    Check("仅写缓存", 9.5m, Cost(1_000_000, 0, 0, 100_000));
    Check("读写缓存", 8.9m, Cost(1_000_000, 100_000, 200_000, 300_000));
    Check("缓存总和超过输入", 6.5m, Cost(500_000, 100_000, 500_000, 700_000));

    cfg.Pricing[0].TierTo = "1000000";
    cfg.Pricing.Add(new PriceRule { Route = "route", Model = "target", TierFrom = "1000000", InputPerM = 30, OutputPerM = 40, CacheReadPerM = 3, CacheWritePerM = 6 });
    Check("分档边界以下", 7.29999m, Cost(999_999, 0, 200_000, 220_000));
    Check("分档边界按完整输入选择", 19.32m, Cost(1_000_000, 0, 200_000, 220_000));

    File.WriteAllText(UsageStats.FilePath,
        "{\"t\":\"2025-01-01 00:00:00\",\"r\":\"route\",\"m\":\"alias\",\"mt\":\"target\",\"p\":1000000,\"c\":100000,\"cr\":200000,\"cw\":300000}\n" +
        "{\"t\":\"2025-01-01 00:00:00\",\"r\":\"route\",\"m\":\"target\",\"p\":1000000,\"c\":0,\"cr\":0,\"cw\":0}\n");
    UsageStats.Load();
    Check("历史记录加载并按当前价格重算", 51.4m, UsageStats.Summary(cfg).Cost);
    Check("历史记录按展示名分组并按真实名计价", 21.4m, UsageStats.Group(UsageGroup.Model, cfg).Single(x => x.Key == "alias").Cost);
    cfg.Pricing[1].InputPerM = 40;
    Check("调整价格后重新计算历史", 66.4m, UsageStats.Summary(cfg).Cost);

    var sessionA = "c744d464-70de-404e-8f4b-a6216b7ee8c4";
    var sessionB = "af19f79f-103b-4fb9-82c2-a2292af47811";
    var first = new DateTime(2026, 11, 2, 16, 0, 0, DateTimeKind.Local);
    UsageStats.Record("route", "alias", "target", 10, 1, 0, 0, 100, 200, sessionA, first.AddMinutes(8));
    UsageStats.Record("route", "alias", "target", 10, 1, 0, 0, 100, 200, sessionB, first.AddMinutes(1));
    UsageStats.Record("route", "alias", "target", 10, 1, 0, 0, 100, 200, sessionA, first);
    var groups = UsageStats.Group(UsageGroup.Session, cfg);
    if (groups.Count != 3 || groups.Single(x => x.Key.Contains(sessionA)).Requests != 2 ||
        groups.Single(x => x.Key.Contains(sessionB)).Requests != 1 ||
        groups.Single(x => x.Key == "未识别会话").Requests != 2 ||
        !groups.Single(x => x.Key.Contains(sessionA)).Key.StartsWith("2026-11-02 16:00:00"))
        throw new Exception("按会话 ID、起始时间或历史未识别分组失败");
    UsageStats.Load();
    groups = UsageStats.Group(UsageGroup.Session, cfg);
    if (groups.Count != 3 || groups.Single(x => x.Key.Contains(sessionA)).Requests != 2 ||
        !groups.Single(x => x.Key.Contains(sessionA)).Key.StartsWith("2026-11-02 16:00:00") ||
        groups.Single(x => x.Key == "未识别会话").Requests != 2)
        throw new Exception("重启后会话分组与历史数据不一致");
    Console.WriteLine("PASS 按真实会话 ID、首次请求时间分组和重启加载");

    var hashA = new string('a', 64);
    var hashB = new string('a', 20) + new string('b', 44);
    UsageStats.Record("route", "alias", "target", 0, 0, 0, 0, 1, 200, hashA, first);
    UsageStats.Record("route", "alias", "target", 0, 0, 0, 0, 1, 200, hashB, first);
    var shortened = UsageStats.Group(UsageGroup.Session, cfg).Where(x => x.Key.EndsWith(new string('a', 20))).ToList();
    if (shortened.Count != 2 || shortened.Any(x => x.Requests != 1 || x.Key.Contains(hashA) || x.Key.Contains(hashB)))
        throw new Exception("会话显示缩短或完整哈希分组失败");
    if (!UsageStats.ExportCsv(cfg).Contains(hashA) || !UsageStats.ExportCsv(cfg).Contains(hashB))
        throw new Exception("导出未保留完整会话哈希");
    Console.WriteLine("PASS 会话哈希完整分组与缩短显示");

    var since = DateTime.Now.AddHours(-1);
    foreach (var group in new[] { UsageGroup.Model, UsageGroup.Route, UsageGroup.Session })
        if (UsageStats.Group(group, cfg, since: since).Sum(x => x.Requests) != 5)
            throw new Exception($"{group} 时间筛选失败");
    if (UsageStats.Group(UsageGroup.Time, cfg).Sum(x => x.Requests) != 7)
        throw new Exception("时间分组应保留全部记录");
    UsageStats.Record("=危险,\"路由", "模型", "target", 1, 2, 0, 0, 1, 200);
    var csv = UsageStats.ExportCsv(cfg, since);
    if (!csv.Contains("\"'=危险,\"\"路由\"") || csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length != 7)
        throw new Exception("CSV 筛选或转义失败");
    Console.WriteLine("PASS 统计时间范围及 CSV 导出");
}
finally
{
    Directory.Delete(dir, true);
}

namespace OAIRouter
{
    public sealed class AppConfig
    {
        public List<PriceRule> Pricing { get; } = new();
        public List<RouteConfig> Routes { get; } = new();
    }
    public sealed class RouteConfig
    {
        public string Prefix { get; set; } = "";
        public string Name { get; set; } = "";
        public string BalanceType { get; set; } = "";
    }
    public sealed class PriceRule
    {
        public string Route { get; set; } = "*";
        public string Model { get; set; } = "*";
        public string TierFrom { get; set; } = "0";
        public string TierTo { get; set; } = "";
        public string TimeFrom { get; set; } = "";
        public string TimeTo { get; set; } = "";
        public string ValidTo { get; set; } = "";
        public double InputPerM { get; set; }
        public double OutputPerM { get; set; }
        public double CacheReadPerM { get; set; }
        public double CacheWritePerM { get; set; }
    }
    public static class ConfigStore { public static string Dir { get; set; } = ""; }
    public sealed class LogStore
    {
        public static LogStore Instance { get; } = new();
        public string DiagnosticsFile => Path.Combine(ConfigStore.Dir, "diagnostics.log");
        public void Warn(string category, string message) => throw new Exception($"{category}: {message}");
    }
}
