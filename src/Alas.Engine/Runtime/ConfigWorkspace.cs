using System.Security.Cryptography;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Alas.Engine.Runtime;

/// <summary>
/// Read/write access to an Engine instance profile.
/// Runtime task behavior is owned by typed Engine runners; this store only
/// persists instance identity and user supplied profile values.
/// </summary>
public sealed partial class ConfigWorkspace
{
    private static readonly Regex NamePattern = new(
        @"^[\p{L}\p{N}][\p{L}\p{N}_. \-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "template", "deploy", "backup", "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };
    private readonly string _root;
    private readonly string _config;
    private readonly object _gate = new();

    public ConfigWorkspace(string root)
    {
        _root = Path.GetFullPath(root);
        _config = Path.Combine(_root, "config");
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

    private static string? ReadString(JsonObject root, params string[] path)
    {
        JsonNode? current = root;
        foreach (string part in path) current = current?[part];
        return current?.GetValueKind() == JsonValueKind.String ? current.GetValue<string>() : null;
    }

    // Internal typed stores use the same bounded JSON path writer. Public UI
    // patching is intentionally removed; queue inputs are the only task editor contract.
    private static void SetPath(JsonObject root, string path, JsonNode? value)
    {
        string[] parts = path.Split('.');
        JsonObject current = root;
        for (int i = 0; i < parts.Length - 1; i++)
            current = current[parts[i]] as JsonObject ?? (current[parts[i]] = new JsonObject()).AsObject();
        current[parts[^1]] = value;
    }

    private JsonObject ReadObject(string path)
    {
        RejectLink(path);
        if (!File.Exists(path)) throw new ConfigWorkspaceException("CONFIG_UNAVAILABLE", "配置文件不可用");
        return JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new ConfigWorkspaceException("CONFIG_INVALID", "配置文件不是 JSON 对象");
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
public sealed class ConfigWorkspaceException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
