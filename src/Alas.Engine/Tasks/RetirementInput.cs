using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Alas.Engine.Rules;

namespace Alas.Engine.Tasks;

internal static class RetirementInput
{
    internal static RetirementOptions Read(JsonObject input)
    {
        if (!input.TryGetPropertyValue("retirement", out var node)) return new();
        if (node is not JsonObject value) throw new ArgumentException("retirement must be an object");
        TaskInput.Fields(value, "mode", "keepLimitBreak", "rarities", "amount");
        if (value.Any(p => p.Value is null)) throw new ArgumentException("retirement fields cannot be null");
        var options = new RetirementOptions
        {
            Mode = value["mode"]?.GetValue<string>() switch
            {
                null or "one_click_retire" => RetirementMode.OneClick,
                "old_retire" => RetirementMode.Old,
                "disabled" => RetirementMode.Disabled,
                _ => throw new NotSupportedException("Retirement mode is not ported")
            },
            KeepLimitBreak = value["keepLimitBreak"]?.GetValue<bool>() ?? true,
            Amount = value["amount"]?.GetValue<string>() switch
            {
                null or "retire_all" => 3000,
                "retire_10" => 10,
                _ => throw new ArgumentException("Unknown retirement amount")
            },
            Rarities = value["rarities"] is null ? [ShipRarity.N, ShipRarity.R] :
                value["rarities"] is JsonArray array ? array.Select(r => r?.GetValue<string>() switch
                {
                    "N" => ShipRarity.N,
                    "R" => ShipRarity.R,
                    "SR" => ShipRarity.SR,
                    "SSR" => ShipRarity.SSR,
                    _ => throw new ArgumentException("Unknown retirement rarity")
                }).ToImmutableArray() :
                throw new ArgumentException("retirement rarities must be an array")
        };
        options.Validate(); return options;
    }
}
