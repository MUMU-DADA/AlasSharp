using System.Collections.Frozen;
using Alas.Engine.Rules.Main;

namespace Alas.Engine.Rules;

/// <summary>Registers compiled rule factories. Missing rules never fall back to Python or an exported plan.</summary>
public static class RuleCatalog
{
    private static readonly FrozenDictionary<string, Func<CampaignRule>> Factories =
        new Dictionary<string, Func<CampaignRule>>(StringComparer.Ordinal)
        {
            ["campaign_main/campaign_1_1"] = static () => new Campaign11(),
            ["campaign_main/campaign_1_2"] = static () => new Campaign12(),
            ["campaign_main/campaign_1_3"] = static () => new Campaign13(),
            ["campaign_main/campaign_1_4"] = static () => new Campaign14(),
            ["campaign_main/campaign_2_1"] = static () => new Campaign21(),
            ["campaign_main/campaign_2_2"] = static () => new Campaign22(),
            ["campaign_main/campaign_2_3"] = static () => new Campaign23(),
            ["campaign_main/campaign_2_4"] = static () => new Campaign24()
        }.ToFrozenDictionary(StringComparer.Ordinal);
    public static IEnumerable<string> Ids => Factories.Keys.Order(StringComparer.Ordinal);
    public static CampaignRule Create(string id) => Factories.TryGetValue(id, out var factory)
        ? factory() : throw new NotSupportedException($"C# campaign rule is not implemented: {id}");
}
