using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Alas.Runtime;

/// <summary>
/// Deployment configuration storage independent of the occupied game host.
/// Schema/defaults are generated from upstream; reads do not deploy, connect,
/// geolocate or restart services. Consumers apply saved settings separately.
/// </summary>
public sealed class DeploySettingsWorkspace
{
    private static readonly JsonObject Definition = LoadDefinition();
    private static readonly Dictionary<string, JsonObject> Fields = Definition["groups"]!.AsArray()
        .SelectMany(group => group!["fields"]!.AsArray()).Concat(Definition["legacy"]!.AsArray())
        .ToDictionary(field => field!["key"]!.GetValue<string>(), field => field!.AsObject(), StringComparer.Ordinal);
    private readonly ConfigWorkspace _configs;
    private readonly string _path;
    private readonly string _platform;
    private readonly Func<bool> _demo;
    private readonly object _gate = new();

    public DeploySettingsWorkspace(string repo, ConfigWorkspace? configs = null, Func<bool>? demo = null)
    {
        _configs = configs ?? new ConfigWorkspace(repo);
        _path = Path.Combine(Path.GetFullPath(repo), "config", "deploy.yaml");
        _platform = OperatingSystem.IsWindows() ? "windows" : "unix";
        _demo = demo ?? (() => Environment.GetEnvironmentVariable("DEMO") == "1");
        _configs.RejectLink(_path);
    }

    public JsonObject Read(string language = "zh-CN")
    {
        lock (_gate)
        {
            if (Definition["translations"]?[language] is not JsonObject translations)
                throw new ArgumentException("不支持的配置语言");
            var values = ReadValues();
            string Translate(string key) => translations[key.Split('.').Last()]?.GetValue<string>() ?? key;
            var groups = Definition["groups"]!.DeepClone().AsArray();
            foreach (JsonObject group in groups.Cast<JsonObject>())
            {
                group["label"] = Translate(group["label"]!.GetValue<string>());
                foreach (JsonObject field in group["fields"]!.AsArray().Cast<JsonObject>())
                {
                    string key = field["key"]!.GetValue<string>();
                    field["label"] = Translate(field["label"]!.GetValue<string>());
                    field["help"] = Translate(field["help"]!.GetValue<string>());
                    field["value"] = key == "Password" ? JsonValue.Create("") : values[key]?.DeepClone() ?? JsonValue.Create("");
                    if (key == "Password") field["type"] = "password";
                }
            }
            return new JsonObject { ["groups"] = groups, ["notice"] = Translate(Definition["notice"]!.GetValue<string>()),
                ["demo"] = _demo() };
        }
    }

    public JsonObject Patch(JsonObject values)
    {
        ArgumentNullException.ThrowIfNull(values);
        lock (_gate)
        {
            EnsureWritable();
            // Validate the complete request before acquiring a write transaction.
            var updates = new JsonObject();
            foreach (var (key, value) in values)
            {
                if (key == "Run" || (key == "Password" && value?.GetValueKind() == JsonValueKind.String && value.GetValue<string>() == ""))
                    continue;
                if (!Fields.TryGetValue(key, out var field)) throw new ArgumentException("未知部署配置项: " + key);
                JsonNode? parsed = ParseValue(field, value);
                if (parsed?.GetValueKind() == JsonValueKind.String &&
                    (parsed.GetValue<string>().Length > 32768 || parsed.GetValue<string>().Any(IsLineBreakOrNull)))
                    throw new ArgumentException(key + " 不能包含换行或过长的值");
                updates[key] = parsed;
            }
            if (updates.Count != 0)
            {
                using var transaction = AcquireTransaction();
                var current = ReadValues();
                foreach (var (key, value) in updates) current[key] = value?.DeepClone();
                WriteValues(current);
            }
            return new JsonObject { ["updated"] = new JsonArray(updates.Select(item => item.Key)
                .Order(StringComparer.Ordinal).Select(key => (JsonNode?)JsonValue.Create(key)).ToArray()) };
        }
    }

    public JsonObject ReadStartup(string instance)
    {
        lock (_gate) return Startup(ValidateInstance(instance, false), ReadValues());
    }

    public JsonObject SetStartup(string instance, bool enabled)
    {
        lock (_gate)
        {
            EnsureWritable();
            string name = ValidateInstance(instance, true);
            using var transaction = AcquireTransaction();
            var values = ReadValues();
            var runs = ParseRun(values["Run"]);
            if (enabled && !runs.Contains(name, StringComparer.Ordinal)) runs.Add(name);
            if (!enabled) runs.RemoveAll(item => item == name);
            values["Run"] = runs.Count == 0 ? null : new JsonArray(runs.Select(item => (JsonNode?)JsonValue.Create(item)).ToArray())
                .ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            WriteValues(values);
            return Startup(name, ReadValues());
        }
    }

    private string ValidateInstance(string instance, bool mustExist)
    {
        string name = ConfigWorkspace.ValidateName(instance);
        if (name.Length == 0 || name.IndexOfAny(".\\/:*?\"'<>|".ToCharArray()) >= 0 ||
            name.StartsWith("template", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("实例名无效");
        _configs.RejectLink(Path.Combine(Path.GetDirectoryName(_path)!, name + ".json"));
        if (mustExist) _configs.Get(name);
        return name;
    }

    private static JsonObject Startup(string name, JsonObject values)
    {
        var runs = ParseRun(values["Run"]);
        return new JsonObject { ["instance"] = name, ["enabled"] = runs.Contains(name, StringComparer.Ordinal),
            ["run"] = new JsonArray(runs.Select(item => (JsonNode?)JsonValue.Create(item)).ToArray()),
            ["raw"] = values["Run"]?.DeepClone() };
    }

    private static List<string> ParseRun(JsonNode? value)
    {
        if (value is null || value.GetValueKind() == JsonValueKind.False) return [];
        JsonArray? items = value as JsonArray;
        if (items is null)
        {
            string text = PythonText(value).Trim();
            if (text.Length == 0 || text.Equals("null", StringComparison.OrdinalIgnoreCase)) return [];
            if (text.StartsWith('[') && text.EndsWith(']'))
            {
                try { items = JsonNode.Parse(text) as JsonArray; }
                catch (JsonException) { }
            }
            items ??= new JsonArray(text.Trim('[', ']').Split(',').Select(item => (JsonNode?)JsonValue.Create(item)).ToArray());
        }
        return items.Select(item => PythonText(item).Trim(' ', '\t', '\r', '\n', '\'', '"'))
            .Where(item => item.Length != 0).Distinct(StringComparer.Ordinal).ToList();
    }

    private void EnsureWritable()
    {
        if (_demo()) throw new ConfigWorkspaceException("FORBIDDEN", "演示模式下不能修改部署设置");
    }

    private JsonObject ReadValues()
    {
        var values = Definition["defaults"]![_platform]!.DeepClone().AsObject();
        foreach (var (key, value) in ParseYaml(ReadText())) values[key] = value?.DeepClone();
        return values;
    }

    private string ReadText()
    {
        _configs.RejectLink(_path);
        if (!File.Exists(_path)) return "";
        if (new FileInfo(_path).Length > 2_000_000) throw new ConfigWorkspaceException("CONFIG_INVALID", "部署配置文件过大");
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static IEnumerable<string> Lines(string text) => Regex.Split(text, "\r\n|[\n\r\v\f\u001c-\u001e\u0085\u2028\u2029]");
    private static bool IsLineBreakOrNull(char c) => c is '\0' or '\n' or '\r' or '\v' or '\f' or '\u001c' or '\u001d' or '\u001e' or '\u0085' or '\u2028' or '\u2029';

    private static JsonObject ParseYaml(string text)
    {
        var values = new JsonObject();
        foreach (string raw in Lines(text))
        {
            string line = raw.Trim('\n', '\r', '\t', ' ').Replace('\\', '/');
            if (line.StartsWith('#')) continue;
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            string value = line[(colon + 1)..].Trim('\n', '\r', '\t', '\'', ' ');
            if (value.Length == 0) continue;
            values[line[..colon]] = value.ToLowerInvariant() switch
            {
                "null" => null, "true" => JsonValue.Create(true), "false" => JsonValue.Create(false),
                _ when Regex.IsMatch(value, @"^\p{Nd}+$") => JsonNode.Parse(BigInteger.Parse(NormalizeDigits(value), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)),
                _ => JsonValue.Create(value),
            };
        }
        return values;
    }

    private void WriteValues(JsonObject values)
    {
        // Preserve existing comments and unknown keys. Missing template fields
        // are appended so an older deployment template cannot discard a save.
        string original = ReadText();
        string template = original.Length == 0 ? Definition["templates"]![_platform]!.GetValue<string>() : original;
        var remaining = new HashSet<string>(values.Select(item => item.Key), StringComparer.Ordinal);
        var output = new StringBuilder();
        foreach (string line in Lines(template.Replace('\\', '/').TrimEnd('\r', '\n')))
        {
            string trimmed = line.TrimStart(' ', '\t');
            int colon = trimmed.IndexOf(':');
            string key = colon >= 0 ? trimmed[..colon] : "";
            if (!trimmed.StartsWith('#') && values.TryGetPropertyValue(key, out var value))
            {
                output.Append(line.AsSpan(0, line.Length - trimmed.Length)).Append(key).Append(": ").Append(YamlText(value)).Append('\n');
                remaining.Remove(key);
            }
            else output.Append(line).Append('\n');
        }
        foreach (string key in remaining.Order(StringComparer.Ordinal)) output.Append(key).Append(": ").Append(YamlText(values[key])).Append('\n');
        string serialized = output.ToString();
        if (Encoding.UTF8.GetByteCount(serialized) > 2_000_000) throw new ArgumentException("部署配置文件过大");
        _configs.RejectLink(_path);
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, serialized, new UTF8Encoding(false));
            // Windows readers outside Core can briefly deny delete sharing.
            // Retry the same atomic replacement, never fall back to truncation.
            DateTime deadline = DateTime.UtcNow.AddSeconds(2);
            while (true)
            {
                _configs.RejectLink(_path);
                try { File.Move(temporary, _path, true); break; }
                catch (Exception error) when (OperatingSystem.IsWindows() &&
                    error is IOException or UnauthorizedAccessException &&
                    (error.HResult & 0xffff) is 5 or 32 or 33 && DateTime.UtcNow < deadline)
                { Thread.Sleep(25); }
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private FileStream AcquireTransaction()
    {
        string path = _path + ".lock";
        _configs.RejectLink(path);
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(20); }
        }
    }

    private static JsonNode? ParseValue(JsonObject field, JsonNode? value)
    {
        string key = field["key"]!.GetValue<string>();
        string type = field["type"]!.GetValue<string>();
        if (type == "bool")
        {
            if (value?.GetValueKind() is JsonValueKind.True or JsonValueKind.False) return value.DeepClone();
            throw new ArgumentException(key + " 必须是布尔值");
        }
        if (type == "int")
        {
            BigInteger number;
            if (value?.GetValueKind() == JsonValueKind.Number)
            {
                string json = value.ToJsonString();
                if (!BigInteger.TryParse(json, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                {
                    if (!double.TryParse(json, NumberStyles.Float, CultureInfo.InvariantCulture, out double real) || !double.IsFinite(real))
                        throw new ArgumentException(key + " 必须是整数");
                    number = new BigInteger(real);
                }
            }
            else if (value?.GetValueKind() == JsonValueKind.String && Regex.IsMatch(value.GetValue<string>().Trim(), @"^[+-]?\p{Nd}(?:_?\p{Nd})*$"))
                number = BigInteger.Parse(NormalizeDigits(value.GetValue<string>().Trim().Replace("_", "")), CultureInfo.InvariantCulture);
            else throw new ArgumentException(key + " 必须是整数");
            if (number.Sign < 0) throw new ArgumentException(key + " 不能小于 0");
            return JsonNode.Parse(number.ToString(CultureInfo.InvariantCulture));
        }
        string text = value is null ? "" : PythonText(value).Trim();
        if (type == "select")
        {
            if (!field["options"]!.AsArray().Any(option => option!.GetValue<string>() == text)) throw new ArgumentException(key + " 的值无效");
        }
        if (type == "nullable_string" && text.Length == 0) return null;
        if (type == "cdn")
            return text.ToLowerInvariant() switch { "" or "false" => JsonValue.Create(false), "true" => JsonValue.Create(true), "null" => null, _ => JsonValue.Create(text) };
        return JsonValue.Create(text);
    }

    private static string NormalizeDigits(string text) => string.Concat(text.Select(c => char.IsDigit(c) ? ((int)char.GetNumericValue(c)).ToString(CultureInfo.InvariantCulture) : c.ToString()));
    private static string YamlText(JsonNode? value) => value?.GetValueKind() switch
        { null or JsonValueKind.Null => "null", JsonValueKind.True => "true", JsonValueKind.False => "false", _ => PythonText(value) };

    private static string PythonText(JsonNode? value) => value?.GetValueKind() switch
    {
        null or JsonValueKind.Null => "None", JsonValueKind.True => "True", JsonValueKind.False => "False",
        JsonValueKind.String => value.GetValue<string>(),
        JsonValueKind.Array => "[" + string.Join(", ", value.AsArray().Select(PythonRepr)) + "]",
        JsonValueKind.Object => "{" + string.Join(", ", value.AsObject().Select(item => PythonRepr(JsonValue.Create(item.Key)) + ": " + PythonRepr(item.Value))) + "}",
        _ => PythonNumber(value!),
    };

    private static string PythonNumber(JsonNode value)
    {
        string raw = value.ToJsonString();
        if (raw.IndexOfAny(['.', 'e', 'E']) < 0) return raw;
        double number = double.Parse(raw, CultureInfo.InvariantCulture);
        if (double.IsInfinity(number)) return number < 0 ? "-inf" : "inf";
        string shortest = number.ToString("R", CultureInfo.InvariantCulture).ToLowerInvariant();
        string sign = shortest.StartsWith('-') ? "-" : "";
        shortest = shortest.TrimStart('-');
        string[] parts = shortest.Split('e');
        int power = parts.Length == 2 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
        int point = parts[0].IndexOf('.');
        if (point < 0) point = parts[0].Length;
        string digits = parts[0].Replace(".", "");
        int leading = digits.Length - digits.TrimStart('0').Length;
        digits = digits.TrimStart('0').TrimEnd('0');
        if (digits.Length == 0) return sign + "0.0";
        int exponent = point + power - leading - 1;
        if (exponent is < -4 or >= 16)
            return sign + digits[0] + (digits.Length == 1 ? "" : "." + digits[1..]) + "e" +
                (exponent >= 0 ? "+" : "-") + Math.Abs(exponent).ToString("D2", CultureInfo.InvariantCulture);
        int position = exponent + 1;
        return sign + (position <= 0 ? "0." + new string('0', -position) + digits :
            digits.Length <= position ? digits.PadRight(position, '0') + ".0" : digits.Insert(position, "."));
    }

    private static string PythonRepr(JsonNode? value)
    {
        if (value?.GetValueKind() != JsonValueKind.String) return PythonText(value);
        string text = value.GetValue<string>();
        char quote = text.Contains('\'') && !text.Contains('"') ? '"' : '\'';
        return quote + text.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t")
            .Replace(quote.ToString(), "\\" + quote) + quote;
    }

    private static JsonObject LoadDefinition()
    {
        using var stream = typeof(DeploySettingsWorkspace).Assembly.GetManifestResourceStream("Alas.Core.Runtime.Resources.deploy-settings.json")
            ?? throw new InvalidOperationException("部署设置导出资源缺失");
        return JsonNode.Parse(stream)!.AsObject();
    }
}
