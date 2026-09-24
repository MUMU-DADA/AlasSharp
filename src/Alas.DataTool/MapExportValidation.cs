using System.Text.Json;
using Alas.Core;

namespace Alas.DataTool;

/// <summary>Offline declaration consistency only; never constructs runtime MAP objects.</summary>
internal static class MapExportValidation
{
    public static bool Check(CampaignIr ir, CampaignIndexEntry entry, string repo)
    {
        var meta = ir.MapMeta;
        if (!meta.Complete || meta.Unresolved.Count != 0 || meta.Calls is null
            || meta.Present != entry.MapPresent
            || entry.MapComplete != (meta.Present && meta.Complete)
            || (!meta.Present && ir.Map.Count != 0)
            || !entry.MapKeys.SequenceEqual(ir.Map.Keys.Order(StringComparer.Ordinal))
            || !ir.Map.Keys.ToHashSet().SetEquals(meta.Origins.Keys)
            || !ir.Map.Keys.ToHashSet().SetEquals(meta.TypedValues.Keys)
            || meta.SourceFiles.Count == 0 || meta.SourceFiles.Distinct().Count() != meta.SourceFiles.Count
            || !meta.SourceFiles.Contains(ir.Source)
            || meta.SourceFiles.Any(p => !SourceExists(repo, p))
            || (meta.DerivedFrom is not null && !meta.SourceFiles.Contains(meta.DerivedFrom)))
            return false;
        foreach (var (name, value) in ir.Map)
        {
            var origin = meta.Origins[name];
            if (origin.Line < 1 || string.IsNullOrWhiteSpace(origin.Expression)
                || !meta.SourceFiles.Contains(origin.Module.Replace('.', '/') + ".py"))
                return false;
            try
            {
                var plain = JsonSerializer.SerializeToElement(Plain(meta.TypedValues[name]));
                if (!JsonElement.DeepEquals(plain, value)) return false;
            }
            catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException
                or FormatException or OverflowException or ArgumentException or InvalidDataException)
            { return false; }
        }
        foreach (var call in meta.Calls)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(call.GetProperty("method").GetString())
                    || !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(Plain(call.GetProperty("typed_args"))),
                                               call.GetProperty("args"))
                    || !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(Plain(call.GetProperty("typed_kwargs"))),
                                               call.GetProperty("kwargs"))) return false;
                var origin = call.GetProperty("origin");
                if (origin.GetProperty("line").GetInt32() < 1
                    || string.IsNullOrWhiteSpace(origin.GetProperty("expression").GetString())
                    || !meta.SourceFiles.Contains(origin.GetProperty("module").GetString()!.Replace('.', '/') + ".py"))
                    return false;
            }
            catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException
                or FormatException or OverflowException or ArgumentException or InvalidDataException)
            { return false; }
        }
        return true;
    }

    internal static bool SourceExists(string repo, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Split('/').Contains("..")) return false;
        return File.Exists(Path.Combine(repo, relative.Replace('/', Path.DirectorySeparatorChar)));
    }

    internal static object? Plain(JsonElement node)
    {
        string kind = node.GetProperty("type").GetString() ?? "";
        switch (kind)
        {
            case "grid":
                var location = node.GetProperty("location").EnumerateArray().Select(p => p.GetInt32()).ToArray();
                if (location.Length != 2 || location[0] is < 0 or > 25 || location[1] < 0)
                    throw new InvalidDataException("Invalid symbolic grid");
                return $"{(char)('A' + location[0])}{location[1] + 1}";
            case "reference":
                if (node.GetProperty("kind").GetString() != "class")
                    throw new InvalidDataException("Unknown MAP reference kind");
                return new Dictionary<string, object?> {
                    ["$ref"] = node.GetProperty("module").GetString() + "." + node.GetProperty("name").GetString(),
                    ["kind"] = "class" };
            case "list": case "tuple": case "set": case "frozenset":
                return node.GetProperty("items").EnumerateArray().Select(Plain).ToArray();
            case "dict":
                var result = new Dictionary<string, object?>();
                foreach (var item in node.GetProperty("items").EnumerateArray())
                {
                    object? key = Plain(item.GetProperty("key"));
                    string encoded = key is string text ? text : JsonSerializer.Serialize(key);
                    if (!result.TryAdd(encoded, Plain(item.GetProperty("value"))))
                        throw new InvalidDataException("Duplicate MAP dictionary key");
                }
                return result;
            case "NoneType":
                if (node.GetProperty("value").ValueKind != JsonValueKind.Null)
                    throw new InvalidDataException("Invalid null");
                return null;
            case "bool": return node.GetProperty("value").GetBoolean();
            case "str":
                return node.GetProperty("value").GetString() ?? throw new InvalidDataException("Invalid string");
            case "int": case "float":
                var number = node.GetProperty("value");
                if (number.ValueKind != JsonValueKind.Number || !double.IsFinite(number.GetDouble())
                    || (kind == "int" && number.GetRawText().IndexOfAny(['.', 'e', 'E']) >= 0))
                    throw new InvalidDataException("Invalid number");
                return number;
            default: throw new InvalidDataException("Unknown MAP value type");
        }
    }
}
