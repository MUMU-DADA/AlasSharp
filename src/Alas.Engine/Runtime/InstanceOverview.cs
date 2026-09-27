using System.Globalization;
using System.Text.Json.Nodes;

namespace Alas.Engine.Runtime;

/// <summary>
/// Read-only configuration projection for an instance that has no live Engine observation.
/// Queue entries are supplied by EngineControlWorkspace; configuration only contributes
/// resources and emulator identity.  No upstream scheduler state is interpreted here.
/// </summary>
public static class InstanceOverview
{
    public static JsonObject FromConfig(ConfigSnapshot config, DateTime now)
    {
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
            ["pending"] = new JsonArray(),
            ["waiting"] = new JsonArray(),
            ["resources"] = resources,
            ["emulator"] = config.Values["Alas"]?["Emulator"]?.DeepClone(),
        };
    }

    public static string Status(string instance, JsonObject active, JsonObject? report)
    {
        if (active["instance"]?.GetValue<string>() != instance) return "stopped";
        if (active["status"]?.GetValue<string>() == "running") return "running";
        return active["status"]?.GetValue<string>() == "failed" || report?["queue_outcome"]?.GetValue<string>() == "failed"
            ? "error" : "stopped";
    }
}
