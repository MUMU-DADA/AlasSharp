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
        TaskInput.Fields(value, "lowHpRetreat", "threshold", "balanceWeight", "balance", "balanceThreshold",
            "emergencyRepair", "repairSingleThreshold", "repairMultiThreshold");
        foreach (var item in value)
            if (item.Value is null) throw new ArgumentException("hpControl fields cannot be null");
        var options = new FleetHealthOptions {
            UseLowHpRetreat = value["lowHpRetreat"]?.GetValue<bool>() ?? false,
            LowHpRetreatThreshold = value["threshold"]?.Deserialize<double>() ?? .3,
            BalanceWeight = value["balanceWeight"]?.GetValue<string>() ?? "1000, 1000, 1000",
            UseHpBalance = value["balance"]?.GetValue<bool>() ?? false,
            HpBalanceThreshold = value["balanceThreshold"]?.Deserialize<double>() ?? .2,
            UseEmergencyRepair = value["emergencyRepair"]?.GetValue<bool>() ?? false,
            RepairUseSingleThreshold = value["repairSingleThreshold"]?.Deserialize<double>() ?? .3,
            RepairUseMultiThreshold = value["repairMultiThreshold"]?.Deserialize<double>() ?? .6
        };
        _ = options.Weights();
        return options;
    }
}
