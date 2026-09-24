using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Alas.Runtime;

/// <summary>
/// Read-only configuration projection for an instance that has no live native observation.
/// Display ordering follows AzurPilot RuntimeService.overview and parse_task_priority;
/// these lists never select a task or replace the native scheduler's hoarding/priority rules.
/// </summary>
public static class InstanceOverview
{
    public static JsonObject FromConfig(ConfigSnapshot config, DateTime now)
    {
        string current = now.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
        string priority = config.Values["General"]?["YukikazeTaskManager"]?["TaskPriorityAdjustment"]?.GetValue<string>() ?? "";
        var order = ParsePriority(priority).Select((name, index) => (name, index))
            .ToDictionary(item => item.name, item => item.index, StringComparer.Ordinal);
        var pending = new List<JsonObject>();
        var waiting = new List<JsonObject>();
        foreach (var (name, value) in config.Values)
        {
            if (value is not JsonObject groups || groups["Scheduler"] is not JsonObject scheduler ||
                scheduler["Enable"] is not JsonValue enabled || !enabled.TryGetValue<bool>(out bool isEnabled) || !isEnabled)
                continue;
            string nextRun = scheduler["NextRun"]?.GetValue<string>() ?? "";
            var item = new JsonObject { ["name"] = name, ["next_run"] = nextRun };
            (string.CompareOrdinal(nextRun.Replace('T', ' '), current) <= 0 ? pending : waiting).Add(item);
        }
        var resources = new JsonArray();
        if (config.Values["Dashboard"] is JsonObject dashboard)
            foreach (var (name, node) in dashboard)
                if (node is JsonObject values && values.ContainsKey("Value"))
                    resources.Add(new JsonObject
                    {
                        ["name"] = name, ["value"] = values["Value"]?.DeepClone(),
                        ["limit"] = values["Limit"]?.DeepClone(), ["total"] = values["Total"]?.DeepClone(),
                        ["record"] = values["Record"]?.DeepClone(),
                    });
        return new JsonObject
        {
            ["instance"] = config.Instance, ["revision"] = config.Revision,
            ["source"] = "configuration", ["observed_at"] = now.ToString("O", CultureInfo.InvariantCulture),
            ["pending"] = new JsonArray(pending.OrderBy(item => order.GetValueOrDefault(item["name"]!.GetValue<string>(), order.Count))
                .Select(item => (JsonNode)item).ToArray()),
            ["waiting"] = new JsonArray(waiting.OrderBy(item => item["next_run"]!.GetValue<string>(), StringComparer.Ordinal)
                .Select(item => (JsonNode)item).ToArray()),
            ["resources"] = resources,
            ["emulator"] = config.Values["Alas"]?["Emulator"]?.DeepClone(),
        };
    }

    private static IEnumerable<string> ParsePriority(string text)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in Regex.Replace(text, "[＞﹥›˃ᐳ❯]", ">").Split(['\r', '\n']))
            foreach (string part in line.Split('#', 2)[0].Split('>'))
            {
                string name = part.Trim();
                if (name.Length > 0 && seen.Add(name)) yield return name;
            }
    }

    public static string Status(string instance, JsonObject active, JsonObject? report)
    {
        if (active["instance"]?.GetValue<string>() != instance) return "stopped";
        if (active["status"]?.GetValue<string>() == "running") return "running";
        return active["status"]?.GetValue<string>() == "failed" || report?["queue_outcome"]?.GetValue<string>() == "failed"
            ? "error" : "stopped";
    }
}
