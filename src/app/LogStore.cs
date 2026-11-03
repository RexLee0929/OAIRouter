using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;

namespace OAIRouter;

public enum LogLevel { Info, Warn, Error }

public sealed class LogItem
{
    public string Time { get; init; } = "";
    public string Level { get; init; } = "信息";
    public string Text { get; init; } = "";
    public LogLevel Kind { get; init; }
    public string Line => $"{Time}  {Text}";
}

/// <summary>
/// 运行日志。设计约束（用户要求 + 规范 §七）：
///   · 只记事实：时间、路由、模型名、状态码、耗时、错误摘要；
///   · 绝不记录请求头、API Key、消息正文、响应正文 —— 因此这里没有"脱敏"步骤，
///     因为从源头上就不采集。万一上游错误信息里回显了密钥，落盘前再过一道正则兜底。
/// </summary>
public sealed class LogStore
{
    public static LogStore Instance { get; } = new();

    private readonly object _gate = new();
    private readonly List<LogItem> _all = new();
    private readonly Regex _secret = new(@"sk-[A-Za-z0-9_\-]{8,}", RegexOptions.Compiled);

    public ObservableCollection<LogItem> Items { get; } = new();
    public int Limit { get; set; } = 800;

    public string LogDir => Path.Combine(ConfigStore.Dir, "logs");
    public string LogFile => Path.Combine(LogDir, "app.log");
    public string DiagnosticsFile => Path.Combine(LogDir, "diagnostics.log");

    private LogStore() { }

    public void Info(string scope, string text) => Add(LogLevel.Info, scope, text);
    public void Warn(string scope, string text) => Add(LogLevel.Warn, scope, text);
    public void Error(string scope, string text) => Add(LogLevel.Error, scope, text);

    public void Request(string route, string modelFrom, string modelTo, int status, long ms)
    {
        var model = string.IsNullOrEmpty(modelTo) || modelTo == modelFrom
            ? modelFrom
            : $"{modelFrom} → {modelTo}";
        var text = $"[{status}] {route,-8} {model,-28} {ms} ms";
        Add(status >= 400 ? LogLevel.Warn : LogLevel.Info, "请求", text);
    }

    public void Add(LogLevel level, string scope, string text)
    {
        text = _secret.Replace(text ?? "", "sk-***");
        if (text.Length > 300) text = text[..300] + "…";

        var item = new LogItem
        {
            Time = DateTime.Now.ToString("HH:mm:ss"),
            Level = level switch { LogLevel.Error => "错误", LogLevel.Warn => "警告", _ => "信息" },
            Text = $"{Bullet(level)} {scope} · {text}",
            Kind = level,
        };

        lock (_gate)
        {
            _all.Add(item);
            while (_all.Count > Math.Max(50, Limit)) _all.RemoveAt(0);
        }

        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Items.Add(item);
            while (Items.Count > Math.Max(50, Limit)) Items.RemoveAt(0);
        });

        Append(item);
    }

    private static string Bullet(LogLevel level) => level switch
    {
        LogLevel.Error => "×",
        LogLevel.Warn => "!",
        _ => "·",
    };

    public void Diagnostic(string text) => Append(DiagnosticsFile, $"实验诊断 · {text}");

    private void Append(LogItem item) => Append(LogFile, item.Text);

    private void Append(string file, string text)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(LogDir);
                var info = new FileInfo(file);
                if (info.Exists && info.Length > 2 * 1024 * 1024)
                {
                    var bak = file + ".1";
                    if (File.Exists(bak)) File.Delete(bak);
                    File.Move(file, bak);
                }
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {text}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志写不进去不能影响主流程
        }
    }

    public void Clear()
    {
        lock (_gate) _all.Clear();
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Items.Clear());
    }
}
