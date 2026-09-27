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
    public static JsonObject FromProfile(EngineProfileSnapshot profile, DateTime now)
    {
        var resources = new JsonArray();
        if (profile.Values["dashboard"] is JsonObject dashboard)
            foreach (var (name, node) in dashboard)
                if (node is JsonObject values && values.ContainsKey("value"))
                    resources.Add(new JsonObject
                    {
                        ["name"] = name, ["value"] = values["value"]?.DeepClone(),
                        ["limit"] = values["limit"]?.DeepClone(), ["total"] = values["total"]?.DeepClone(),
                        ["record"] = values["record"]?.DeepClone(),
                    });
        return new JsonObject
        {
            ["instance"] = profile.Instance, ["revision"] = profile.Revision,
            ["source"] = "configuration", ["observed_at"] = now.ToString("O", CultureInfo.InvariantCulture),
            ["pending"] = new JsonArray(),
            ["waiting"] = new JsonArray(),
            ["resources"] = resources,
            ["device"] = profile.Values["device"]?.DeepClone(),
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
