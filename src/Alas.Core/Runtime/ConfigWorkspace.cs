using System.Security.Cryptography;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace Alas.Runtime;

/// <summary>
/// Read/write access to the upstream instance configuration contract.
/// The shape is intentionally data driven by <c>args.json</c>; this class does
/// not maintain a second task or field table. Runtime campaign execution still
/// reads the upstream configuration through the Python host.
/// </summary>
public sealed class ConfigWorkspace
{
    private static readonly Regex NamePattern = new(
        @"^[\p{L}\p{N}][\p{L}\p{N}_. \-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex DateTimePattern = new(
        @"\A\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "template", "deploy", "backup", "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };
    private readonly string _root;
    private readonly string _config;
    private readonly string _argument;
    private readonly string _i18n;
    private readonly object _gate = new();

    public ConfigWorkspace(string root)
    {
        _root = Path.GetFullPath(root);
        _config = Path.Combine(_root, "config");
        _argument = Path.Combine(_root, "module", "config", "argument");
        _i18n = Path.Combine(_root, "module", "config", "i18n");
        EnsureDirectory(_config);
        RejectLink(_root);
    }

    public IReadOnlyList<ConfigInstance> List()
    {
        lock (_gate)
        {
            var result = new List<ConfigInstance>();
            foreach (string file in Directory.EnumerateFiles(_config, "*.json", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (name.Equals("template", StringComparison.OrdinalIgnoreCase) || !TryValidateName(name))
                    continue;
                try
                {
                    var values = ReadMerged(name, out string revision);
                    result.Add(new ConfigInstance(name, revision,
                        ReadString(values, "Alas", "Emulator", "Serial"),
                        ReadString(values, "Alas", "Emulator", "ServerName")));
                }
                catch (IOException) { /* A concurrently removed invalid file is not an instance. */ }
                catch (JsonException) { /* Keep the list usable; the bad file is reported by get. */ }
                catch (ConfigWorkspaceException) { /* Invalid JSON objects are not runnable instances. */ }
            }
            return result;
        }
    }

    public ConfigSnapshot Get(string instance)
    {
        lock (_gate)
        {
            string name = ValidateName(instance);
            var values = ReadMerged(name, out string revision);
            return new ConfigSnapshot(name, revision, values);
        }
    }

    public ConfigSchema Schema(string language = "zh-CN")
    {
        if (language is not ("zh-CN" or "zh-MIAO" or "en-US" or "ja-JP" or "zh-TW"))
            throw new ArgumentException("不支持的配置语言");
        return new ConfigSchema(
            ReadObject(Path.Combine(_argument, "menu.json")),
            ReadObject(Path.Combine(_argument, "args.json")),
            ReadObject(Path.Combine(_i18n, language + ".json")));
    }

    public ConfigSnapshot Patch(string instance, string? revision, IReadOnlyList<ConfigChange> changes)
    {
        if (changes.Count is < 1 or > 200) throw new ArgumentException("一次最多保存 200 个配置字段");
        lock (_gate)
        {
            string name = ValidateName(instance);
            using var transaction = AcquireTransaction(name);
            var raw = ReadRaw(name, out _);
            var schema = ReadObject(Path.Combine(_argument, "args.json"));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (ConfigChange change in changes)
            {
                string path = ValidatePath(change.Path);
                if (!seen.Add(path)) throw new ArgumentException("同一次保存不能重复修改同一个参数");
                JsonObject descriptor = FindDescriptor(schema, path);
                ValidateValue(descriptor, change.Value, path);
                SetPath(raw, path, change.Value?.DeepClone());
                SyncRecordTime(raw, path);
            }
            WriteRaw(name, raw);
            // The revision argument is intentionally compatibility-only, matching
            // ConfigService.patch: merge against the newest locked snapshot.
            _ = revision;
            string updatedRevision = ReadRaw(name, out string computedRevision) is not null
                ? computedRevision : throw new InvalidOperationException("配置保存后无法读取");
            return new ConfigSnapshot(name, updatedRevision, MergeTemplate(raw));
        }
    }

    public ConfigSnapshot Create(string instance, string? source = null, string? importFile = null)
    {
        lock (_gate)
        {
            string name = ValidateName(instance);
            string target = ConfigPath(name);
            RejectLink(target);
            if (File.Exists(target)) throw new ConfigWorkspaceException("ALREADY_EXISTS", "同名实例已存在");
            if (source is not null && importFile is not null)
                throw new ArgumentException("source 与 import_file 不能同时提供");
            JsonObject data = importFile is not null
                ? ReadImport(importFile)
                : source is null
                    ? ReadObject(Path.Combine(_config, "template.json"))
                    : ReadRaw(ValidateName(source), out _);
            WriteNew(name, data);
            return Get(name);
        }
    }

    private JsonObject ReadImport(string importFile)
    {
        string name = ValidateName(importFile);
        string path = Path.Combine(_config, "import", name + ".json");
        RejectLink(path);
        if (!File.Exists(path)) throw new ConfigWorkspaceException("NOT_FOUND", "导入配置不存在");
        var data = ReadObject(path);
        if (data["Alas"] is not JsonObject)
            throw new ConfigWorkspaceException("INVALID_CONFIG", "导入配置缺少 Alas 段");
        return data;
    }

    public void Delete(string instance, string revision)
    {
        lock (_gate)
        {
            string name = ValidateName(instance);
            using var transaction = AcquireTransaction(name);
            ReadRaw(name, out string currentRevision);
            if (currentRevision != revision)
                throw new ConfigWorkspaceException("CONFLICT", "配置已变化，请重新加载后删除");
            string backup = Path.Combine(_config, "backup");
            EnsureDirectory(backup);
            string target = Path.Combine(backup, name + "-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + ".json");
            File.Move(ConfigPath(name), target);
        }
    }

    /// <summary>Stage an uploaded instance configuration; never accept a server path.</summary>
    public ConfigImport Import(string instance, string content)
    {
        string name = ValidateName(instance);
        if (string.IsNullOrEmpty(content) || content.Length > 2_000_000)
            throw new ArgumentException("导入配置必须是长度不超过 2000000 的 JSON 文本");
        var data = JsonNode.Parse(content) as JsonObject;
        if (data?["Alas"] is not JsonObject)
            throw new ConfigWorkspaceException("INVALID_CONFIG", "导入配置缺少 Alas 段");
        lock (_gate)
        {
            string directory = Path.Combine(_config, "import");
            EnsureDirectory(directory);
            string path = Path.Combine(directory, name + ".json");
            RejectLink(path);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return new ConfigImport(name, File.GetLastWriteTimeUtc(path));
        }
    }

    public IReadOnlyList<ConfigImport> ListImports()
    {
        lock (_gate)
        {
            string directory = Path.Combine(_config, "import");
            RejectLink(directory);
            if (!Directory.Exists(directory)) return [];
            var result = new List<ConfigImport>();
            foreach (string path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                if (!TryValidateName(name) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                try
                {
                    if (ReadObject(path)["Alas"] is JsonObject)
                        result.Add(new ConfigImport(name, File.GetLastWriteTimeUtc(path)));
                }
                catch (Exception error) when (error is IOException or JsonException or ConfigWorkspaceException) { }
            }
            return result;
        }
    }

    private JsonObject ReadMerged(string name, out string revision)
    {
        using var transaction = AcquireTransaction(name);
        var raw = ReadRaw(name, out revision);
        return MergeTemplate(raw);
    }

    private JsonObject ReadRaw(string name, out string revision)
    {
        string path = ConfigPath(name);
        RejectLink(path);
        if (!File.Exists(path)) throw new ConfigWorkspaceException("NOT_FOUND", "找不到配置实例");
        byte[] bytes = File.ReadAllBytes(path);
        revision = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (JsonNode.Parse(bytes) is not JsonObject data || data["Alas"] is not JsonObject)
            throw new ConfigWorkspaceException("INVALID_CONFIG", "配置文件必须包含 Alas 对象");
        return data;
    }

    private JsonObject MergeTemplate(JsonObject raw)
    {
        var template = ReadObject(Path.Combine(_config, "template.json"));
        return MergeObjects(template, raw);
    }

    private JsonObject MergeObjects(JsonObject defaults, JsonObject overrides)
    {
        var result = (JsonObject)defaults.DeepClone();
        foreach (var pair in overrides)
        {
            if (pair.Value is JsonObject child && result[pair.Key] is JsonObject defaultChild)
                result[pair.Key] = MergeObjects(defaultChild, child);
            else
                result[pair.Key] = pair.Value?.DeepClone();
        }
        return result;
    }

    private void WriteNew(string name, JsonObject data)
    {
        string target = ConfigPath(name);
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            File.Move(temporary, target);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void WriteRaw(string name, JsonObject data)
    {
        string target = ConfigPath(name);
        RejectLink(target);
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private FileStream AcquireTransaction(string name)
    {
        string path = ConfigPath(name) + ".lock";
        RejectLink(path);
        Directory.CreateDirectory(_config);
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(20); }
        }
    }

    private string ConfigPath(string name) => Path.Combine(_config, ValidateName(name) + ".json");

    internal static string ValidateName(string value)
        => TryValidateName(value) ? value.Trim().TrimEnd(' ', '.') : throw new ArgumentException("实例名无效");

    private static bool TryValidateName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        string name = value.Trim().TrimEnd(' ', '.');
        return name.Length > 0 && NamePattern.IsMatch(name) &&
               !ReservedNames.Contains(name.Split('.')[0]);
    }

    private static string ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 180 || path.Split('.').Length != 3 ||
            path.Split('.').Any(part => part.Length == 0 || part.Any(c => !char.IsLetterOrDigit(c) && c != '_')))
            throw new ArgumentException("配置路径必须为 Task.Group.Field");
        return path;
    }

    private static JsonObject FindDescriptor(JsonObject schema, string path)
    {
        JsonNode? node = schema;
        foreach (string part in path.Split('.')) node = node?[part];
        return node as JsonObject ?? throw new ArgumentException("参数不存在或不允许修改：" + path);
    }

    private static void ValidateValue(JsonObject descriptor, JsonNode? value, string path)
    {
        string? display = descriptor["display"]?.GetValue<string>();
        string? kind = descriptor["type"]?.GetValue<string>();
        if (kind == "storage" && display != "hide" && value is JsonObject { Count: 0 }) return;
        if (display is "hide" or "disabled" or "readonly" || kind is "storage" or "stored" or "state" or "lock")
            throw new ConfigWorkspaceException("READ_ONLY", "参数不允许修改：" + path);
        JsonArray? options = descriptor["option"] as JsonArray;
        if (kind == "multiselect")
        {
            if (value is not JsonArray selected || options is null || selected.Count > options.Count ||
                selected.Any(item => !options.Any(option => SameOption(option, item))) ||
                selected.DistinctBy(OptionText).Count() != selected.Count)
                throw new ArgumentException("多选参数包含无效或重复选项：" + path);
            return;
        }
        if (options is { Count: > 0 } && !options.Any(item => SameOption(item, value)))
            throw new ArgumentException("请选择有效选项：" + path);
        JsonNode? defaultValue = descriptor["value"];
        bool valid = kind == "checkbox" || defaultValue?.GetValueKind() is JsonValueKind.True or JsonValueKind.False
            ? value?.GetValueKind() is JsonValueKind.True or JsonValueKind.False
            : defaultValue?.GetValueKind() == JsonValueKind.Number
                ? value?.GetValueKind() == JsonValueKind.Number &&
                  (IsFloating(defaultValue) || options is { Count: > 0 } ? IsFiniteNumber(value) : IsInteger(value))
                : value?.GetValueKind() == JsonValueKind.String || defaultValue is null && value is null;
        if (!valid || value?.GetValueKind() == JsonValueKind.String && value.GetValue<string>().Length > 20000)
            throw new ArgumentException("参数类型或长度不正确：" + path);
        JsonNode? rule = descriptor["validate"];
        if (rule?.GetValueKind() == JsonValueKind.String && rule.GetValue<string>() == "datetime" || kind == "datetime")
        {
            if (value is null || value.GetValueKind() != JsonValueKind.String ||
                !DateTimePattern.IsMatch(value.GetValue<string>()) ||
                !DateTime.TryParseExact(value.GetValue<string>(), "yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                throw new ArgumentException("日期格式应为 YYYY-MM-DD HH:mm:ss：" + path);
        }
        else if (rule is JsonArray range && range.Count == 2)
        {
            if (!TryFiniteDouble(value, out double number) ||
                !TryFiniteDouble(range[0], out double minimum) ||
                !TryFiniteDouble(range[1], out double maximum) || number < minimum || number > maximum)
                throw new ArgumentException("参数超出允许范围：" + path);
        }
        else if (rule?.GetValueKind() == JsonValueKind.String && value?.GetValueKind() is JsonValueKind.String or JsonValueKind.Number)
        {
            string pattern = rule.GetValue<string>();
            Regex validator;
            try
            {
                validator = new Regex($"\\A(?:{pattern})\\z", RegexOptions.CultureInvariant,
                    TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException error)
            {
                throw new ArgumentException("参数校验规则无效：" + path, error);
            }
            try
            {
                string candidate = value is JsonValue jsonValue && jsonValue.GetValueKind() == JsonValueKind.String
                    ? jsonValue.GetValue<string>()
                    : value!.ToJsonString();
                if (!validator.IsMatch(candidate)) throw new ArgumentException("参数格式不正确：" + path);
            }
            catch (RegexMatchTimeoutException error)
            {
                throw new ArgumentException("参数校验超时：" + path, error);
            }
        }
        if (descriptor["mode"]?.GetValueKind() == JsonValueKind.String && descriptor["mode"]!.GetValue<string>() == "yaml")
            ValidateYaml(value, path);
    }

    private static bool SameOption(JsonNode? option, JsonNode? value)
        => option?.GetValueKind() == value?.GetValueKind() &&
           (option?.GetValueKind() != JsonValueKind.Number || IsInteger(option) == IsInteger(value)) &&
           JsonNode.DeepEquals(option, value);

    private static string OptionText(JsonNode? value) => value?.GetValueKind() switch
    {
        null or JsonValueKind.Null => "None",
        JsonValueKind.String => value.GetValue<string>(),
        JsonValueKind.True => "True",
        JsonValueKind.False => "False",
        _ => value.ToJsonString(),
    };

    private static bool IsInteger(JsonNode? value)
    {
        if (value?.GetValueKind() != JsonValueKind.Number) return false;
        using var document = JsonDocument.Parse(value.ToJsonString());
        return document.RootElement.GetRawText().IndexOfAny(['.', 'e', 'E']) < 0;
    }

    private static bool IsFloating(JsonNode value)
        => value.GetValueKind() == JsonValueKind.Number && !IsInteger(value);

    private static bool IsFiniteNumber(JsonNode? value)
    {
        if (value?.GetValueKind() != JsonValueKind.Number) return false;
        using var document = JsonDocument.Parse(value.ToJsonString());
        return document.RootElement.TryGetDouble(out double parsed) && double.IsFinite(parsed);
    }

    private static bool TryFiniteDouble(JsonNode? value, out double result)
    {
        result = 0;
        if (value?.GetValueKind() != JsonValueKind.Number) return false;
        using var document = JsonDocument.Parse(value.ToJsonString());
        return document.RootElement.TryGetDouble(out result) && double.IsFinite(result);
    }

    private static void ValidateYaml(JsonNode? value, string path)
    {
        if (value?.GetValueKind() != JsonValueKind.String)
            throw new ArgumentException("YAML 必须是文本：" + path);
        try
        {
            using var reader = new StringReader(value.GetValue<string>());
            object? parsed = new DeserializerBuilder().Build().Deserialize<object>(reader);
            if (parsed is not null && parsed is not IDictionary<object, object>)
                throw new ArgumentException("YAML 顶层必须是键值映射：" + path);
        }
        catch (YamlException error)
        {
            throw new ArgumentException("YAML 格式不正确：" + path, error);
        }
    }

    private static void SetPath(JsonObject root, string path, JsonNode? value)
    {
        string[] parts = path.Split('.');
        JsonObject current = root;
        for (int i = 0; i < parts.Length - 1; i++)
            current = current[parts[i]] as JsonObject ?? (current[parts[i]] = new JsonObject()).AsObject();
        current[parts[^1]] = value;
    }

    private static void SyncRecordTime(JsonObject root, string path)
    {
        string[] parts = path.Split('.');
        if (!parts[^1].EndsWith("Value", StringComparison.Ordinal)) return;
        string record = parts[^1][..^5] + "Record";
        JsonNode? group = root[parts[0]]?[parts[1]];
        if (group is JsonObject fields && fields.ContainsKey(record))
            fields[record] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    }

    private static string? ReadString(JsonObject root, params string[] path)
    {
        JsonNode? current = root;
        foreach (string part in path) current = current?[part];
        return current?.GetValueKind() == JsonValueKind.String ? current.GetValue<string>() : null;
    }

    private JsonObject ReadObject(string path)
    {
        RejectLink(path);
        if (!File.Exists(path)) throw new ConfigWorkspaceException("SCHEMA_UNAVAILABLE", "配置 schema 不可用");
        return JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new ConfigWorkspaceException("SCHEMA_INVALID", "配置 schema 不是 JSON 对象");
    }

    private void EnsureDirectory(string path)
    {
        RejectLink(path);
        Directory.CreateDirectory(path);
    }

    internal void RejectLink(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                // The bundled runtime uses one project-local config junction.
                // Permit precisely that directory link after checking its target;
                // never permit a linked instance file or an external directory.
                if (string.Equals(current, _config, StringComparison.OrdinalIgnoreCase) &&
                    Directory.Exists(current) &&
                    new DirectoryInfo(current).ResolveLinkTarget(false) is { FullName: var target } &&
                    IsWithin(Path.GetFullPath(target), Directory.GetParent(_root)?.FullName ?? _root))
                    continue;
                throw new ConfigWorkspaceException("PATH_LINK", "配置路径不能使用链接");
            }
        }
    }

    private static bool IsWithin(string path, string root)
        => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
           path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                           StringComparison.OrdinalIgnoreCase);
}

public sealed record ConfigInstance(string Instance, string Revision, string? Serial, string? Server)
{
    public string Status { get; init; } = "stopped";
    public string? CurrentTask { get; init; }
}
public sealed record ConfigImport(string Name, DateTimeOffset ModifiedAt);
public sealed record ConfigSnapshot(string Instance, string Revision, JsonObject Values);
public sealed record ConfigSchema(JsonObject Menu, JsonObject Args, JsonObject Translations);
public sealed class ConfigChange(string path, JsonNode? value)
{
    public string Path { get; } = path;
    public JsonNode? Value { get; } = value;
}
public sealed class ConfigWorkspaceException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
