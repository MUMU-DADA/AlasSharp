using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Alas.Engine.Runtime;

/// <summary>Settings for Engine sessions; no deployment, updater or upstream configuration interpreter.</summary>
public sealed class EngineSettingsWorkspace
{
    private static readonly JsonObject Definition = LoadDefinition();
    private readonly EngineProfileStore _profiles;
    private readonly string _root;
    private readonly string _settingsPath;
    private readonly string _startupPath;
    private readonly Func<bool> _demo;
    private readonly object _gate = new();

    public EngineSettingsWorkspace(string engineRoot, EngineProfileStore? profiles = null, Func<bool>? demo = null)
    {
        _root = Path.GetFullPath(engineRoot);
        _profiles = profiles ?? new EngineProfileStore(_root);
        _profiles.RejectLink(_root);
        Directory.CreateDirectory(_root);
        _settingsPath = Path.Combine(_root, "settings.json");
        _startupPath = Path.Combine(_root, "startup.json");
        _demo = demo ?? (() => Environment.GetEnvironmentVariable("DEMO") == "1");
    }

    public JsonObject Read(string language = "zh-CN")
    {
        lock (_gate)
        {
            if (Definition["translations"]?[language] is not JsonObject translations)
                throw new ArgumentException("不支持的设置语言");
            var values = ReadValues();
            string Translate(string key) => translations[key]?.GetValue<string>() ?? throw new InvalidDataException("设置翻译缺失");
            var groups = Definition["groups"]!.DeepClone().AsArray();
            foreach (var group in groups.OfType<JsonObject>())
            {
                group["label"] = Translate(group["label"]!.GetValue<string>());
                foreach (var field in group["fields"]!.AsArray().OfType<JsonObject>())
                {
                    string key = field["key"]!.GetValue<string>();
                    field["label"] = Translate(field["label"]!.GetValue<string>());
                    field["help"] = Translate(field["help"]!.GetValue<string>());
                    field["value"] = values[key]?.DeepClone();
                }
            }
            return new JsonObject { ["groups"] = groups, ["notice"] = Translate("Notice"), ["demo"] = _demo() };
        }
    }

    public JsonObject Patch(JsonObject values)
    {
        ArgumentNullException.ThrowIfNull(values);
        lock (_gate)
        {
            EnsureWritable();
            var updates = ValidateValues(values);
            if (updates.Count != 0)
            {
                using var transaction = AcquireTransaction(_settingsPath);
                var current = ReadValues();
                foreach (var (key, value) in updates) current[key] = value?.DeepClone();
                WriteDocument(_settingsPath, new JsonObject { ["contract"] = "engine-settings/1", ["values"] = current });
            }
            return new JsonObject { ["updated"] = new JsonArray(updates.Select(item => item.Key)
                .Order(StringComparer.Ordinal).Select(key => (JsonNode?)JsonValue.Create(key)).ToArray()) };
        }
    }

    /// <summary>Snapshot settings once for the next queue. Explicit environment overrides take precedence.</summary>
    public EngineRuntimeSettings ReadRuntime()
    {
        lock (_gate)
        {
            var values = ReadValues();
            string Get(string key, string environment)
            {
                string? configured = Environment.GetEnvironmentVariable(environment);
                return string.IsNullOrWhiteSpace(configured) ? values[key]!.GetValue<string>() : configured;
            }
            return new(ResolveExecutable(Get("AdbPath", "ALAS_ADB")),
                ResolveExecutable(Get("VisionRuntime", "ALAS_CV_RUNTIME")),
                Get("OcrModelDirectory", "ALAS_OCR_MODELS") is { Length: > 0 } models ? Path.GetFullPath(models, _root) : null);
        }
    }

    private string ResolveExecutable(string value) =>
        value.Contains('/') || value.Contains('\\') || Path.IsPathRooted(value) ? Path.GetFullPath(value, _root) : value;

    public JsonObject ReadStartup(string instance)
    {
        lock (_gate) return Startup(EngineProfileStore.ValidateName(instance), ReadStartupValues());
    }

    /// <summary>Stores a preference only; it never starts a task or grants device action permission.</summary>
    public JsonObject SetStartup(string instance, bool enabled)
    {
        lock (_gate)
        {
            EnsureWritable();
            string name = EngineProfileStore.ValidateName(instance);
            _profiles.Get(name);
            using var transaction = AcquireTransaction(_startupPath);
            var runs = ReadStartupValues();
            if (enabled && !runs.Contains(name, StringComparer.Ordinal)) runs.Add(name);
            if (!enabled) runs.RemoveAll(item => item == name);
            WriteDocument(_startupPath, new JsonObject { ["contract"] = "engine-startup/1",
                ["run"] = new JsonArray(runs.Select(item => (JsonNode?)JsonValue.Create(item)).ToArray()) });
            return Startup(name, runs);
        }
    }

    private static JsonObject Startup(string name, List<string> runs) => new()
    {
        ["instance"] = name, ["enabled"] = runs.Contains(name, StringComparer.Ordinal),
        ["run"] = new JsonArray(runs.Select(item => (JsonNode?)JsonValue.Create(item)).ToArray()),
    };

    private void EnsureWritable()
    {
        if (_demo()) throw new EngineProfileException("FORBIDDEN", "演示模式下不能修改 Engine 设置");
    }

    private JsonObject ReadValues()
    {
        var values = new JsonObject { ["AdbPath"] = "adb", ["VisionRuntime"] = "python", ["OcrModelDirectory"] = "" };
        var stored = ReadDocument(_settingsPath, "engine-settings/1", "values");
        if (stored is null) return values;
        if (stored["values"] is not JsonObject source) throw Invalid("values 必须是对象");
        try
        {
            foreach (var (key, value) in ValidateValues(source)) values[key] = value?.DeepClone();
        }
        catch (ArgumentException error) { throw Invalid(error.Message); }
        return values;
    }

    private static JsonObject ValidateValues(JsonObject values)
    {
        var parsed = new JsonObject();
        foreach (var (key, value) in values)
        {
            if (key is not ("AdbPath" or "VisionRuntime" or "OcrModelDirectory")) throw new ArgumentException("未知 Engine 设置项: " + key);
            if (value?.GetValueKind() != JsonValueKind.String) throw new ArgumentException(key + " 必须是字符串");
            string text = value.GetValue<string>();
            if (text.Length > 32768 || text.Any(char.IsControl)) throw new ArgumentException(key + " 不能包含控制字符或过长的值");
            if (key != "OcrModelDirectory" && string.IsNullOrWhiteSpace(text)) throw new ArgumentException(key + " 不能为空");
            parsed[key] = string.IsNullOrWhiteSpace(text) ? "" : text;
        }
        return parsed;
    }

    private List<string> ReadStartupValues()
    {
        var stored = ReadDocument(_startupPath, "engine-startup/1", "run");
        if (stored is null) return [];
        if (stored["run"] is not JsonArray items) throw Invalid("run 必须是数组");
        var result = new List<string>();
        foreach (var item in items)
        {
            if (item?.GetValueKind() != JsonValueKind.String) throw Invalid("启动列表必须包含实例名");
            string name;
            try { name = EngineProfileStore.ValidateName(item.GetValue<string>()); }
            catch (ArgumentException error) { throw Invalid(error.Message); }
            if (name != item.GetValue<string>() || result.Contains(name, StringComparer.Ordinal)) throw Invalid("启动列表包含无效或重复实例名");
            result.Add(name);
        }
        return result;
    }

    private JsonObject? ReadDocument(string path, string contract, string payloadKey)
    {
        _profiles.RejectLink(path);
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 2_000_000) throw Invalid("设置文件过大");
        try
        {
            var document = JsonNode.Parse(stream) as JsonObject;
            if (document is null || document["contract"]?.GetValueKind() != JsonValueKind.String ||
                document["contract"]!.GetValue<string>() != contract || document.Count != 2 || !document.ContainsKey(payloadKey))
                throw Invalid("设置合同或字段不匹配");
            return document;
        }
        catch (JsonException) { throw Invalid("设置文件不是有效 JSON"); }
        catch (ArgumentException) { throw Invalid("设置文件包含重复或无效字段"); }
    }

    private static EngineProfileException Invalid(string message) => new("CONFIG_INVALID", message);

    private void WriteDocument(string path, JsonObject value)
    {
        string serialized = value.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (Encoding.UTF8.GetByteCount(serialized) > 2_000_000) throw new ArgumentException("Engine 设置文件过大");
        _profiles.RejectLink(path);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(Encoding.UTF8.GetBytes(serialized + "\n"));
                file.Flush(flushToDisk: true);
            }
            DateTime deadline = DateTime.UtcNow.AddSeconds(2);
            while (true)
            {
                _profiles.RejectLink(path);
                try { File.Move(temporary, path, true); break; }
                catch (Exception error) when (OperatingSystem.IsWindows() &&
                    error is IOException or UnauthorizedAccessException &&
                    (error.HResult & 0xffff) is 5 or 32 or 33 && DateTime.UtcNow < deadline)
                { Thread.Sleep(25); }
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private FileStream AcquireTransaction(string path)
    {
        string lockPath = path + ".lock";
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            _profiles.RejectLink(lockPath);
            try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(20); }
        }
    }

    private static JsonObject LoadDefinition()
    {
        using var stream = typeof(EngineSettingsWorkspace).Assembly.GetManifestResourceStream("Alas.Engine.Runtime.Resources.engine-settings.json")
            ?? throw new InvalidOperationException("Engine 设置资源缺失");
        return JsonNode.Parse(stream)!.AsObject();
    }
}

public sealed record EngineRuntimeSettings(string Adb, string VisionRuntime, string? ModelDirectory);
