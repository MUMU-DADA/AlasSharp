using System.Collections.Frozen;
using System.Collections.Immutable;
using Alas.Engine.Runtime;

namespace Alas.Engine.Rules.Main;

public abstract class ChapterFiveRule : ObservedBossRule
{
    protected static readonly SourceFile ConfigurationSource = new("campaign/campaign_main/campaign_5_1.py", "9ac436429bb565ab82ed6a23c9469b7fcff9fefd97d4e5c85c8390bafbf2eb15");
    public override CampaignConfiguration Configure(CampaignConfiguration input) => input with
    {
        BossFleet = 1, MysteryHasCarrier = true,
        Vision = new(new(120, 206, 1.5, 10, 10, 35), new(206, 255, null, null, 10, 50, 1000),
            (75, 100), (0, 49), 75, 75, 180)
    };
    protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; }
    protected ChapterFiveRule() => Hooks = new Dictionary<int, BattleHook>
    { [0] = MysteriesThenDefault, [4] = MysteriesThenObservedBoss }.ToFrozenDictionary();
}
public sealed class Campaign51 : ChapterFiveRule
{
    public override string Id => "campaign_main/campaign_5_1";
    public override string StageName => "5-1";
    public override ImmutableArray<SourceFile> Sources => [BaseSource, ConfigurationSource];
}
public sealed class Campaign52 : ChapterFiveRule
{
    public override string Id => "campaign_main/campaign_5_2";
    public override string StageName => "5-2";
    public override ImmutableArray<SourceFile> Sources => [BaseSource, ConfigurationSource,
        new("campaign/campaign_main/campaign_5_2.py", "cd37c9551eee15383cabd3855356cbdde66addf2c4afabd3d25f8bda9c2b9ed0")];
}
public sealed class Campaign53 : ChapterFiveRule
{
    public override string Id => "campaign_main/campaign_5_3";
    public override string StageName => "5-3";
    public override ImmutableArray<SourceFile> Sources => [BaseSource, ConfigurationSource,
        new("campaign/campaign_main/campaign_5_3.py", "990bad331d40436860c7d7d06918e7fe2ee23d91a2ac60d3908918aa529b324c")];
    public override CampaignConfiguration Configure(CampaignConfiguration input)
    {
        var config = base.Configure(input);
        return config with { Vision = config.Vision! with { HomographyEdgeHough = 210 } };
    }
}
public sealed class Campaign54 : ChapterFiveRule
{
    public override string Id => "campaign_main/campaign_5_4";
    public override string StageName => "5-4";
    public override ImmutableArray<SourceFile> Sources => [BaseSource, ConfigurationSource,
        new("campaign/campaign_main/campaign_5_4.py", "6813c00caef5fd01eb07e7ab86db31661ef9b8defab290db7aaded5d48e0ee54")];
    private static readonly ImmutableArray<RoadDefinition> CenterRoad = [new([
        [Cell.Parse("C2")], [Cell.Parse("E2"), Cell.Parse("F1")], [Cell.Parse("C4")], [Cell.Parse("F5")],
        [Cell.Parse("F3"), Cell.Parse("G2"), Cell.Parse("H3")], [Cell.Parse("F3"), Cell.Parse("G4"), Cell.Parse("H3")]])];
    private static readonly ImmutableArray<RoadDefinition> RingRoad = [new([
        [Cell.Parse("E2"), Cell.Parse("F1")], [Cell.Parse("F1"), Cell.Parse("G2"), Cell.Parse("H3")],
        [Cell.Parse("F5"), Cell.Parse("G4"), Cell.Parse("H3")], [Cell.Parse("F3"), Cell.Parse("G2"), Cell.Parse("H3")],
        [Cell.Parse("F3"), Cell.Parse("G4"), Cell.Parse("H3")]])];
    public override CampaignConfiguration Configure(CampaignConfiguration input)
    {
        var config = base.Configure(input);
        return config with { MysteryHasCarrier = false, Vision = config.Vision! with
        { InternalHough = 40, EdgeHough = 40, HomographyEdgeHough = 210 } };
    }
    protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; }
    public Campaign54() => Hooks = new Dictionary<int, BattleHook> { [0] = Battle0, [4] = Battle4 }.ToFrozenDictionary();
    private static async ValueTask<bool> ClearRoads(CampaignContext context)
        => await context.Operations.ClearRoadblocksAsync(CenterRoad) || await context.Operations.ClearRoadblocksAsync(RingRoad, potential: true);
    private async ValueTask<bool> Battle0(CampaignContext context)
    {
        if (await ClearRoads(context)) return true;
        return await BattleDefaultAsync(context);
    }
    private static async ValueTask<bool> Battle4(CampaignContext context)
    {
        if (ObservedBossBlocked(context) && await ClearRoads(context)) return true;
        return await context.Operations.ClearBossForFleetAsync(FleetRoles.BossIndex(context.Config));
    }
}
