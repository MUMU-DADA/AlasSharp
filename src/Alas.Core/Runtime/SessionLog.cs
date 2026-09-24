using System.Text.Json;
using System.Text.Json.Nodes;

namespace Alas.Runtime;

/// <summary>一条结构化日志。字段是结构化的，不是拼进字符串里 —— 前端/诊断要能直接消费。</summary>
public sealed record SessionLogEntry(
    string Time, string Level, string Scope, string Message,
    IReadOnlyDictionary<string, object?> Fields)
{
    public JsonObject ToJson()
    {
        var fields = new JsonObject();
        foreach (var (key, value) in Fields)
            fields[key] = value is null ? null : JsonValue.Create(value);
        return new JsonObject
        {
            ["time"] = Time,
            ["level"] = Level,
            ["scope"] = Scope,
            ["message"] = Message,
            ["fields"] = fields,
        };
    }
}

/// <summary>
/// 会话级结构化日志（R1 交付物之一）。同时保留内存条目与 JSONL 落盘：
/// 内存条目给同一进程里的判定用（例如"宿主只初始化一次"的可核对记录），
/// 落盘给事后排查用 —— 两者内容一致，不存在"只打了控制台就查不到"的情况。
/// </summary>
public sealed class SessionLog
{
    private readonly List<SessionLogEntry> _entries = new();
    private readonly object _gate = new();
    private readonly bool _echo;

    public SessionLog(bool echo = true) => _echo = echo;

    public IReadOnlyList<SessionLogEntry> Entries
    {
        get { lock (_gate) return _entries.ToArray(); }
    }

    /// <summary>只复制显示所需的日志尾部；不改变完整日志及最终落盘。</summary>
    public IReadOnlyList<SessionLogEntry> Recent(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        lock (_gate) return _entries.TakeLast(limit).ToArray();
    }

    public SessionLogEntry Add(string level, string scope, string message,
                               IReadOnlyDictionary<string, object?>? fields = null)
    {
        var entry = new SessionLogEntry(
            DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz"),
            level, scope, message, fields ?? new Dictionary<string, object?>());
        lock (_gate) _entries.Add(entry);
        if (_echo)
        {
            string suffix = entry.Fields.Count == 0
                ? ""
                : " " + string.Join(" ", entry.Fields.Select(kv => $"{kv.Key}={kv.Value}"));
            Console.WriteLine($"[{level,-7}] {scope}: {message}{suffix}");
        }
        return entry;
    }

    public SessionLogEntry Info(string scope, string message,
                                IReadOnlyDictionary<string, object?>? fields = null)
        => Add("INFO", scope, message, fields);

    public SessionLogEntry Warn(string scope, string message,
                                IReadOnlyDictionary<string, object?>? fields = null)
        => Add("WARN", scope, message, fields);

    public SessionLogEntry Error(string scope, string message,
                                 IReadOnlyDictionary<string, object?>? fields = null)
        => Add("ERROR", scope, message, fields);

    public int Count(string level) => Entries.Count(e => e.Level == level);

    /// <summary>写 JSONL（每行一条），返回写出的行数。</summary>
    public int WriteJsonLines(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var lines = Entries.Select(e => e.ToJson().ToJsonString()).ToList();
        File.WriteAllLines(path, lines);
        return lines.Count;
    }
}
