using System.Collections.Frozen;
using System.Collections.Immutable;
using Alas.Engine.Runtime;

namespace Alas.Engine.Rules.Main;

public abstract class ChapterFourRule : ObservedBossRule
{
    protected static readonly SourceFile ConfigurationSource = new("campaign/campaign_main/campaign_4_1.py", "8beb54fa69d13b36269fed0130c1d97ad3808b81a3cd19cb225087e28020a81a");
    public override MapOverlayRules Overlays { get; } = new(.3, .25, .65);
    public override CampaignConfiguration Configure(CampaignConfiguration input) => input with
    {
        BossFleet = 1, MysteryHasCarrier = true,
        Vision = new(new(120, 222, 1.5, 10, 10, 35), new(222, 255, null, null, 10, 50, 1000),
            (100, 150), (0, 33), 75, 75, 210)
    };
    protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; }
    protected ChapterFourRule() => Hooks = new Dictionary<int, BattleHook>
    { [0] = MysteriesThenDefault, [3] = MysteriesThenObservedBoss }.ToFrozenDictionary();
}
public sealed class Campaign41 : ChapterFourRule
{
    public override string Id => "campaign_main/campaign_4_1";
    public override string StageName => "4-1";
    public override ImmutableArray<SourceFile> Sources => [BaseSource, ConfigurationSource];
}
public sealed class Campaign42 : ChapterFourRule
{
    public override string Id => "campaign_main/campaign_4_2";
    public override string StageName => "4-2";
    public override ImmutableArray<SourceFile> Sources => [BaseSource, ConfigurationSource,
        new("campaign/campaign_main/campaign_4_2.py", "b003edec23ab6bfb838af6dbab5b816c98832f6aea27c1240816d05bb8717ee7")];
}
public sealed class Campaign43 : ChapterFourRule
{
    public override string Id => "campaign_main/campaign_4_3";
    public override string StageName => "4-3";
    public override ImmutableArray<SourceFile> Sources => [BaseSource, ConfigurationSource,
        new("campaign/campaign_main/campaign_4_3.py", "9453e049475fa4d44dc20bf6704f7525a0e8edfd6438e8728eb01ab7d59609ee")];
    public override CampaignConfiguration Configure(CampaignConfiguration input)
    {
        var config = base.Configure(input);
        return config with { Vision = config.Vision! with { InternalHough = 40, EdgeHough = 40 } };
    }
}
public sealed class Campaign44 : ChapterFourRule
{
    public override string Id => "campaign_main/campaign_4_4";
    public override string StageName => "4-4";
    public override ImmutableArray<SourceFile> Sources => [BaseSource, ConfigurationSource,
        new("campaign/campaign_main/campaign_4_4.py", "4410a8d7101c45dbc473c1aa5839da1fe0936221467157339bc183b5f3ab5135")];
    private static readonly ImmutableArray<RoadDefinition> MainRoad = [new([
        [Cell.Parse("B6")], [Cell.Parse("C4"), Cell.Parse("D6")], [Cell.Parse("C5"), Cell.Parse("D4")],
        [Cell.Parse("D3"), Cell.Parse("C2")], [Cell.Parse("C2"), Cell.Parse("D1")]])];
    public override CampaignConfiguration Configure(CampaignConfiguration input) => base.Configure(input) with { MysteryHasCarrier = false };
    protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; }
    public Campaign44() => Hooks = new Dictionary<int, BattleHook> { [0] = Battle0, [4] = Battle4 }.ToFrozenDictionary();
    private static async ValueTask<bool> ClearRoads(CampaignContext context)
        => await context.Operations.ClearRoadblocksAsync(MainRoad) || await context.Operations.ClearRoadblocksAsync(MainRoad, potential: true);
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
