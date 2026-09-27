using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Rules;

namespace Alas.Engine.Tasks;

internal static class FleetHealthInput
{
    internal static FleetHealthOptions Read(JsonObject input)
    {
        if (!input.TryGetPropertyValue("hpControl", out var node)) return new();
        if (node is not JsonObject value) throw new ArgumentException("hpControl must be an object");
        TaskInput.Fields(value, "lowHpRetreat", "threshold", "balanceWeight");
        foreach (var item in value)
            if (item.Value is null) throw new ArgumentException("hpControl fields cannot be null");
        var options = new FleetHealthOptions {
            UseLowHpRetreat = value["lowHpRetreat"]?.GetValue<bool>() ?? false,
            LowHpRetreatThreshold = value["threshold"]?.Deserialize<double>() ?? .3,
            BalanceWeight = value["balanceWeight"]?.GetValue<string>() ?? "1000, 1000, 1000"
        };
        _ = options.Weights();
        return options;
    }
}
