using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OAIRouter;

public sealed class RouteRule : INotifyPropertyChanged
{
    private string _prefix = "";
    private string _name = "";
    private string _upstream = "";
    private string _mode = "responses";
    private bool _editing;
    private string _balanceDisplay = "—";
    private bool _balanceRefreshing;

    [JsonIgnore]
    public string BalanceDisplay { get => _balanceDisplay; set { _balanceDisplay = value; OnChanged(); } }
    [JsonIgnore]
    public bool BalanceRefreshing { get => _balanceRefreshing; set { _balanceRefreshing = value; OnChanged(); } }

    public string BalanceType { get; set; } = "NewAPI";
    public string BalanceUrl { get; set; } = "";
    public string BalanceJsonPath { get; set; } = "data.quota";
    public string BalanceUserId { get; set; } = "";
    public string ProtectedBalanceKey { get; set; } = "";
    public string ProtectedLocalKey { get; set; } = "";
    public string ProtectedUpstreamKey { get; set; } = "";

    /// <summary>路径前缀，例如 openai 对应 http://127.0.0.1:端口/openai/v1/chat/completions</summary>
    public string Prefix { get => _prefix; set { _prefix = value; OnChanged(); } }
    public string Name { get => _name; set { _name = value; OnChanged(); } }
    /// <summary>上游根地址，例如 https://api.openai.com</summary>
    public string Upstream { get => _upstream; set { _upstream = value; OnChanged(); } }
    /// <summary>responses = 翻译为 Responses API；chat = 原样透传；rewrite = 参数改写</summary>
    public string Mode { get => _mode; set { _mode = value; OnChanged(); } }

    /// <summary>仅界面状态：该行是否处于编辑模式，不写入配置文件</summary>
    [JsonIgnore]
    public bool IsEditing { get => _editing; set { _editing = value; OnChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>
/// 计价规则。单价单位「元 / 百万 token」。
/// 支持：按上游区分、按模型区分、按输入长度分挡、按每天时段生效、按日期过期。
/// </summary>
public sealed class PriceRule : INotifyPropertyChanged
{
    private string _route = "*";
    private string _model = "*";
    private string _tierFrom = "0";
    private string _tierTo = "";
    private double _inputPerM;
    private double _outputPerM;
    private double _cacheReadPerM;
    private double _cacheWritePerM;
    private string _timeFrom = "";
    private string _timeTo = "";
    private string _validTo = "";

    /// <summary>上游前缀，* 表示所有上游</summary>
    public string Route { get => _route; set { _route = value; OnChanged(); } }
    /// <summary>模型名，* 表示所有模型</summary>
    public string Model { get => _model; set { _model = value; OnChanged(); } }
    /// <summary>挡位起点（输入 token 数，含）</summary>
    public string TierFrom { get => _tierFrom; set { _tierFrom = value; OnChanged(); } }
    /// <summary>挡位终点（输入 token 数，不含）；留空表示无上限</summary>
    public string TierTo { get => _tierTo; set { _tierTo = value; OnChanged(); } }

    public double InputPerM { get => _inputPerM; set { _inputPerM = value; OnChanged(); } }
    public double OutputPerM { get => _outputPerM; set { _outputPerM = value; OnChanged(); } }
    public double CacheReadPerM { get => _cacheReadPerM; set { _cacheReadPerM = value; OnChanged(); } }
    public double CacheWritePerM { get => _cacheWritePerM; set { _cacheWritePerM = value; OnChanged(); } }

    /// <summary>每天生效起点 HH:mm，留空表示全天</summary>
    public string TimeFrom { get => _timeFrom; set { _timeFrom = value; OnChanged(); } }
    /// <summary>每天生效终点 HH:mm，可跨零点（如 22:00–06:00）</summary>
    public string TimeTo { get => _timeTo; set { _timeTo = value; OnChanged(); } }

    /// <summary>有效期截止 yyyy-MM-dd，留空表示长期</summary>
    public string ValidTo { get => _validTo; set { _validTo = value; OnChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed class ModelMapping : INotifyPropertyChanged
{
    private string _alias = "";
    private string _target = "";
    private string _mode = "";
    private bool _editing;

    /// <summary>WorkBuddy 里填的模型名（别名）</summary>
    public string Alias { get => _alias; set { _alias = value; OnChanged(); } }
    /// <summary>真正发给上游的模型名</summary>
    public string Target { get => _target; set { _target = value; OnChanged(); } }


    /// <summary>该模型单独指定的模式；留空表示跟随路由（responses / chat / rewrite）</summary>
    public string Mode { get => _mode; set { _mode = value; OnChanged(); OnChanged(nameof(ModeDisplay)); } }

    /// <summary>下拉显示用：空值显示为「跟随路由」</summary>
    [JsonIgnore]
    public string ModeDisplay
    {
        get => string.IsNullOrWhiteSpace(_mode) ? "跟随路由" : _mode;
        set
        {
            var v = string.Equals(value, "跟随路由", StringComparison.Ordinal) ? "" : (value ?? "").Trim();
            if (v == _mode) return;
            _mode = v;
            OnChanged();
            OnChanged(nameof(Mode));
        }
    }

    /// <summary>仅界面状态：该行是否处于编辑模式，不写入配置文件</summary>
    [JsonIgnore]
    public bool IsEditing { get => _editing; set { _editing = value; OnChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed class AppConfig
{
    public int Port { get; set; } = 8787;
    public bool AutoStart { get; set; }
    public int LogLimit { get; set; } = 800;
    /// <summary>输出 token 下限。客户端做连通性探测时常发 1，低于此值会被抬到该值</summary>
    public int MinOutputTokens { get; set; } = 16;

    /// <summary>输出 token 上限。0 表示不限制；设了之后无论客户端要多少都会被压到该值</summary>
    public int MaxOutputTokens { get; set; }
    public bool AutoScrollLog { get; set; } = true;
    public string ClientType { get; set; } = "auto";
    public bool ExperimentalDiagnostics { get; set; }
    public double[] RouteColumnWidths { get; set; } = { 88, 104, 180, 110, 82 };
    public ObservableCollection<RouteRule> Routes { get; set; } = new();
    public ObservableCollection<ModelMapping> ModelMap { get; set; } = new();
    public ObservableCollection<PriceRule> Pricing { get; set; } = new();

    /// <summary>模型库：价格表与映射表的模型名都从这里选，可增删</summary>
    public ObservableCollection<string> Models { get; set; } = new();

    [JsonIgnore]
    public string ListenPrefix => $"http://127.0.0.1:{Port}";
}

public static class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Dir => App.IsDev ? System.IO.Path.Combine(AppContext.BaseDirectory, "dev-data") : AppContext.BaseDirectory;
    public static string Path => System.IO.Path.Combine(Dir, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var json = File.ReadAllText(Path);
                var cfg = Deserialize(json);
                if (cfg is not null)
                {
                    if (cfg.Routes.Count == 0) SeedRoutes(cfg);
                    if (cfg.ModelMap.Count == 0) SeedMappings(cfg);
                    if (cfg.Models.Count == 0) SeedModels(cfg);
                    Snapshot();          // 每次启动留一份快照，防止误操作后无从恢复
                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            LogStore.Instance.Error("配置", $"读取 config.json 失败，已使用默认配置：{ex.Message}");
        }

        var fresh = new AppConfig();
        SeedRoutes(fresh);
        SeedMappings(fresh);
        SeedModels(fresh);
        Save(fresh);   // 首次运行把默认配置落盘，方便用户直接编辑
        return fresh;
    }

    public static string ToJson(AppConfig cfg) => JsonSerializer.Serialize(cfg, Options);

    public static AppConfig? FromJson(string json)
    {
        try { return Deserialize(json); }
        catch { return null; }
    }

    private static AppConfig? Deserialize(string json)
    {
        var cfg = JsonSerializer.Deserialize<AppConfig>(json, Options);
        if (cfg is null) return null;
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty(nameof(AppConfig.Routes), out var routes) || routes.ValueKind != JsonValueKind.Array)
            return cfg;
        var index = 0;
        foreach (var item in routes.EnumerateArray())
        {
            if (index >= cfg.Routes.Count) break;
            var route = cfg.Routes[index++];
            if (item.ValueKind == JsonValueKind.Object && !item.TryGetProperty(nameof(RouteRule.BalanceType), out _) &&
                Uri.TryCreate(route.Upstream, UriKind.Absolute, out var uri) &&
                string.Equals(uri.Host, "api.openai.com", StringComparison.OrdinalIgnoreCase))
                route.BalanceType = "OpenAI";
        }
        return cfg;
    }

    /// <summary>全新的默认配置（含种子数据）。</summary>
    public static AppConfig CreateDefault()
    {
        var cfg = new AppConfig();
        SeedRoutes(cfg);
        SeedMappings(cfg);
        SeedModels(cfg);
        return cfg;
    }

    public static string BackupDir => System.IO.Path.Combine(Dir, "backups");

    /// <summary>把当前 config.json 复制到 backups/ 下，最多保留 10 份。</summary>
    private static void Snapshot()
    {
        try
        {
            if (!File.Exists(Path)) return;

            Directory.CreateDirectory(BackupDir);
            var target = System.IO.Path.Combine(BackupDir, $"config-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Copy(Path, target, overwrite: true);

            var files = new DirectoryInfo(BackupDir)
                .GetFiles("config-*.json")
                .OrderByDescending(f => f.Name)
                .Skip(10)
                .ToList();

            foreach (var old in files) old.Delete();
        }
        catch
        {
            // 备份失败不影响启动
        }
    }

    public static bool Save(AppConfig cfg)
    {
        try
        {
            var json = JsonSerializer.Serialize(cfg, Options);
            File.WriteAllText(Path, json);
            return true;
        }
        catch (Exception ex)
        {
            LogStore.Instance.Error("配置", $"保存 config.json 失败：{ex.Message}");
            return false;
        }
    }

    private static void SeedRoutes(AppConfig cfg)
    {
        cfg.Routes.Add(new RouteRule { Prefix = "openai", Name = "OpenAI 官方", Upstream = "https://api.openai.com", Mode = "responses", BalanceType = "OpenAI" });
    }

    private static void SeedModels(AppConfig cfg)
    {
        foreach (var name in new[]
        {
            "gpt-6-luna", "gpt-6-astra", "gpt-6.1-sol", "gpt-6.1-terra", "gpt-5.6-sol",
            "deepseek-v4-pro", "deepseek-v4-flash", "deepseek-v4.1-flash",
            "glm-5.3", "glm-5.3-flash", "kimi-k3", "kimi-k2.6", "minimax-m3", "hy4-preview",
        })
        {
            if (!cfg.Models.Contains(name)) cfg.Models.Add(name);
        }
    }

    private static void SeedMappings(AppConfig cfg)
    {
        // 默认不带任何模型映射：用户加了别名才有
    }
}
