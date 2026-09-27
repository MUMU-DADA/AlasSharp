using System.Text.Json.Nodes;
using Alas.Engine.Rules;

namespace Alas.Engine.Tasks;

internal static class FleetLevelInput
{
    internal static FleetLevelOptions Read(JsonObject input)
    {
        if (!input.TryGetPropertyValue("reachLevel", out var value)) return new();
        var options = new FleetLevelOptions(value?.GetValue<int>() ?? throw new ArgumentException("reachLevel cannot be null"));
        options.Validate();
        return options;
    }
}
