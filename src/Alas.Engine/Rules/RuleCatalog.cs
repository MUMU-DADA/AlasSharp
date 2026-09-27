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
            ["campaign_main/campaign_2_4"] = static () => new Campaign24(),
            ["campaign_main/campaign_3_1"] = static () => new Campaign31(),
            ["campaign_main/campaign_3_2"] = static () => new Campaign32(),
            ["campaign_main/campaign_3_3"] = static () => new Campaign33(),
            ["campaign_main/campaign_3_4"] = static () => new Campaign34(),
            ["campaign_main/campaign_4_1"] = static () => new Campaign41(),
            ["campaign_main/campaign_4_2"] = static () => new Campaign42(),
            ["campaign_main/campaign_4_3"] = static () => new Campaign43(),
            ["campaign_main/campaign_4_4"] = static () => new Campaign44(),
            ["campaign_main/campaign_5_1"] = static () => new Campaign51(),
            ["campaign_main/campaign_5_2"] = static () => new Campaign52(),
            ["campaign_main/campaign_5_3"] = static () => new Campaign53(),
            ["campaign_main/campaign_5_4"] = static () => new Campaign54(),
            ["campaign_main/campaign_6_1"] = static () => new Campaign61(),
            ["campaign_main/campaign_6_2"] = static () => new Campaign62(),
            ["campaign_main/campaign_6_3"] = static () => new Campaign63(),
            ["campaign_main/campaign_6_4"] = static () => new Campaign64(),
            ["campaign_main/campaign_7_1"] = static () => new Campaign71(),
            ["campaign_main/campaign_7_2"] = static () => new Campaign72(),
            ["campaign_main/campaign_7_3"] = static () => new Campaign73(),
            ["campaign_main/campaign_7_4"] = static () => new Campaign74(),
            ["campaign_main/campaign_8_1"] = static () => new Campaign81(),
            ["campaign_main/campaign_8_2"] = static () => new Campaign82(),
            ["campaign_main/campaign_8_3"] = static () => new Campaign83(),
            ["campaign_main/campaign_8_4"] = static () => new Campaign84(),
            ["campaign_main/campaign_9_1"] = static () => new Campaign91(),
            ["campaign_main/campaign_9_2"] = static () => new Campaign92(),
            ["campaign_main/campaign_9_3"] = static () => new Campaign93(),
            ["campaign_main/campaign_9_4"] = static () => new Campaign94(),
            ["campaign_main/campaign_10_1"] = static () => new Campaign101(),
            ["campaign_main/campaign_10_2"] = static () => new Campaign102(),
            ["campaign_main/campaign_10_3"] = static () => new Campaign103(),
            ["campaign_main/campaign_10_4"] = static () => new Campaign104(),
            ["campaign_main/campaign_11_1"] = static () => new Campaign111(),
            ["campaign_main/campaign_11_2"] = static () => new Campaign112(),
            ["campaign_main/campaign_11_3"] = static () => new Campaign113(),
            ["campaign_main/campaign_11_4"] = static () => new Campaign114(),
            ["campaign_main/campaign_12_1"] = static () => new Campaign121(),
            ["campaign_main/campaign_12_2"] = static () => new Campaign122(),
            ["campaign_main/campaign_12_3"] = static () => new Campaign123(),
            ["campaign_main/campaign_12_4"] = static () => new Campaign124()
        }.ToFrozenDictionary(StringComparer.Ordinal);
    public static IEnumerable<string> Ids => Factories.Keys.Order(StringComparer.Ordinal);
    public static CampaignRule Create(string id) => Factories.TryGetValue(id, out var factory)
        ? factory() : throw new NotSupportedException($"C# campaign rule is not implemented: {id}");
}
