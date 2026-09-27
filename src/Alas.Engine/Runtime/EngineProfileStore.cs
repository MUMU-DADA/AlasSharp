using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

/// <summary>
/// Engine-owned profile storage. Profiles contain device identity and typed
/// Engine state; they do not mirror the retired upstream configuration tree.
/// </summary>
public sealed partial class EngineProfileStore
{
    private static readonly Regex NamePattern = new(
        @"^[\p{L}\p{N}][\p{L}\p{N}_. \-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "template", "deploy", "backup", "profiles", "import", "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };
    private readonly string _root;
    private readonly string _profiles;
    private readonly object _gate = new();

    public EngineProfileStore(string root)
    {
        _root = Path.GetFullPath(root);
        _profiles = Path.Combine(_root, "profiles");
        EnsureDirectory(_profiles);
        RejectLink(_root);
    }

    public IReadOnlyList<EngineInstance> List()
    {
        lock (_gate)
        {
            var result = new List<EngineInstance>();
            foreach (string file in Directory.EnumerateFiles(_profiles, "*.json", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (!TryValidateName(name)) continue;
                try
                {
                    var profile = ReadRaw(name, out string revision);
                    result.Add(new EngineInstance(name, revision,
                        ReadString(profile, "device", "serial"),
                        ReadString(profile, "device", "server")));
                }
                catch (IOException) { }
                catch (JsonException) { }
                catch (EngineProfileException) { }
            }
            return result;
        }
    }

    public EngineProfileSnapshot Get(string instance)
    {
        lock (_gate)
        {
            string name = ValidateName(instance);
            var profile = ReadRaw(name, out string revision);
            return new EngineProfileSnapshot(name, revision, profile);
        }
    }

    public EngineProfileSnapshot Create(string instance, string? source = null, string? importFile = null)
    {
        lock (_gate)
        {
            string name = ValidateName(instance);
            string target = ProfilePath(name);
            RejectLink(target);
            if (File.Exists(target)) throw new EngineProfileException("ALREADY_EXISTS", "同名 Engine profile 已存在");
            if (source is not null && importFile is not null)
                throw new ArgumentException("source 与 import_file 不能同时提供");
            JsonObject profile = importFile is not null
                ? ReadImport(importFile)
                : source is null
                    ? DefaultProfile()
                    : ReadRaw(ValidateName(source), out _);
            ValidateProfile(profile);
            WriteNew(name, profile);
            return Get(name);
        }
    }

    private JsonObject ReadImport(string importFile)
    {
        string name = ValidateName(importFile);
        string path = Path.Combine(_profiles, "import", name + ".json");
        RejectLink(path);
        if (!File.Exists(path)) throw new EngineProfileException("NOT_FOUND", "导入 profile 不存在");
        var profile = ReadObject(path);
        ValidateProfile(profile);
        return profile;
    }

    public void Delete(string instance, string revision)
    {
        lock (_gate)
        {
            string name = ValidateName(instance);
            using var transaction = AcquireTransaction(name);
            ReadRaw(name, out string currentRevision);
            if (currentRevision != revision)
                throw new EngineProfileException("CONFLICT", "Engine profile 已变化，请重新加载后删除");
            string backup = Path.Combine(_profiles, "backup");
            EnsureDirectory(backup);
            string target = Path.Combine(backup, name + "-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + ".json");
            RejectLink(target);
            File.Move(ProfilePath(name), target);
        }
    }

    public EngineProfileImport Import(string instance, string content)
    {
        string name = ValidateName(instance);
        if (string.IsNullOrEmpty(content) || content.Length > 2_000_000)
            throw new ArgumentException("导入 profile 必须是长度不超过 2000000 的 JSON 文本");
        var profile = JsonNode.Parse(content) as JsonObject
            ?? throw new EngineProfileException("INVALID_PROFILE", "导入 profile 必须是 JSON 对象");
        ValidateProfile(profile);
        lock (_gate)
        {
            string directory = Path.Combine(_profiles, "import");
            EnsureDirectory(directory);
            string path = Path.Combine(directory, name + ".json");
            RejectLink(path);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, profile.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return new EngineProfileImport(name, File.GetLastWriteTimeUtc(path));
        }
    }

    public IReadOnlyList<EngineProfileImport> ListImports()
    {
        lock (_gate)
        {
            string directory = Path.Combine(_profiles, "import");
            RejectLink(directory);
            if (!Directory.Exists(directory)) return [];
            var result = new List<EngineProfileImport>();
            foreach (string path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                if (!TryValidateName(name) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                try
                {
                    ValidateProfile(ReadObject(path));
                    result.Add(new EngineProfileImport(name, File.GetLastWriteTimeUtc(path)));
                }
                catch (Exception error) when (error is IOException or JsonException or EngineProfileException) { }
            }
            return result;
        }
    }

    private JsonObject ReadRaw(string name, out string revision)
    {
        string path = ProfilePath(name);
        RejectLink(path);
        if (!File.Exists(path)) throw new EngineProfileException("NOT_FOUND", "找不到 Engine profile");
        byte[] bytes = File.ReadAllBytes(path);
        revision = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var profile = JsonNode.Parse(bytes) as JsonObject
            ?? throw new EngineProfileException("INVALID_PROFILE", "Engine profile 必须是 JSON 对象");
        ValidateProfile(profile);
        return profile;
    }

    private static JsonObject DefaultProfile()
    {
        string now = DateTimeOffset.UtcNow.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        return new JsonObject
        {
            ["device"] = new JsonObject { ["serial"] = "", ["package"] = "", ["server"] = "cn" },
            ["dashboard"] = new JsonObject(),
            ["campaign"] = new JsonObject
            {
                ["emotion"] = new JsonObject
                {
                    ["fleets"] = new JsonArray(
                        new JsonObject { ["value"] = 100, ["recordedAt"] = now, ["control"] = "keep_exp_bonus", ["recovery"] = "not_in_dormitory", ["oath"] = false },
                        new JsonObject { ["value"] = 100, ["recordedAt"] = now, ["control"] = "keep_exp_bonus", ["recovery"] = "not_in_dormitory", ["oath"] = false }),
                    ["nextRun"] = null,
                },
                ["achievement"] = new JsonObject { ["event"] = "campaign_main", ["stage"] = "campaign_1_1", ["enabled"] = true },
            },
        };
    }

    private static void ValidateProfile(JsonObject profile)
    {
        if (profile["device"] is not JsonObject device)
            throw new EngineProfileException("INVALID_PROFILE", "Engine profile 缺少 device 对象");
        foreach (string key in new[] { "serial", "package", "server" })
            if (device[key] is not JsonValue value || value.GetValueKind() != JsonValueKind.String)
                throw new EngineProfileException("INVALID_PROFILE", $"device.{key} 必须是字符串");
        if (profile["campaign"] is not JsonObject campaign)
            throw new EngineProfileException("INVALID_PROFILE", "Engine profile 缺少 campaign 对象");
        if (campaign["emotion"] is not JsonObject emotion || emotion["fleets"] is not JsonArray fleets || fleets.Count != 2)
            throw new EngineProfileException("INVALID_PROFILE", "campaign.emotion.fleets 必须包含两支舰队");
        if (campaign["achievement"] is not JsonObject achievement ||
            achievement["event"] is not JsonValue || achievement["stage"] is not JsonValue || achievement["enabled"] is not JsonValue)
            throw new EngineProfileException("INVALID_PROFILE", "campaign.achievement 字段不完整");
    }

    private void WriteNew(string name, JsonObject profile)
    {
        string target = ProfilePath(name);
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, profile.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            File.Move(temporary, target);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void WriteRaw(string name, JsonObject profile)
    {
        ValidateProfile(profile);
        string target = ProfilePath(name);
        RejectLink(target);
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, profile.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private FileStream AcquireTransaction(string name)
    {
        string path = ProfilePath(name) + ".lock";
        RejectLink(path);
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(20); }
        }
    }

    private string ProfilePath(string name) => Path.Combine(_profiles, ValidateName(name) + ".json");

    internal static string ValidateName(string value)
        => TryValidateName(value) ? value.Trim().TrimEnd(' ', '.') : throw new ArgumentException("实例名无效");

    internal static GameServer ParseServer(string value) => value switch
    {
        "cn" => GameServer.Cn, "en" => GameServer.En, "jp" => GameServer.Jp, "tw" => GameServer.Tw,
        _ => throw new EngineProfileException("INVALID_PROFILE", "device.server 无效")
    };

    private static bool TryValidateName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        string name = value.Trim().TrimEnd(' ', '.');
        return name.Length > 0 && NamePattern.IsMatch(name) && !ReservedNames.Contains(name.Split('.')[0]);
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
        if (!File.Exists(path)) throw new EngineProfileException("PROFILE_UNAVAILABLE", "Engine profile 不可用");
        return JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new EngineProfileException("PROFILE_INVALID", "Engine profile 不是 JSON 对象");
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
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                if (string.Equals(current, _profiles, StringComparison.OrdinalIgnoreCase) && Directory.Exists(current) &&
                    new DirectoryInfo(current).ResolveLinkTarget(false) is { FullName: var target } &&
                    IsWithin(Path.GetFullPath(target), Directory.GetParent(_root)?.FullName ?? _root)) continue;
                throw new EngineProfileException("PATH_LINK", "profile 路径不能使用链接");
            }
        }
    }

    private static bool IsWithin(string path, string root)
        => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
           path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

public sealed record EngineInstance(string Instance, string Revision, string? Serial, string? Server)
{
    public string Status { get; init; } = "stopped";
    public string? CurrentTask { get; init; }
}
public sealed record EngineProfileImport(string Name, DateTimeOffset ModifiedAt);
public sealed record EngineProfileSnapshot(string Instance, string Revision, JsonObject Values);
public sealed class EngineProfileException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
