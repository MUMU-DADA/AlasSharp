using System.Collections.Frozen;
using System.Collections.Immutable;
using Alas.Engine.Runtime;

namespace Alas.Engine.Rules.Main;

public abstract class ChapterTwelveRule : ObservedBossRule
{
    protected static readonly SourceFile ConfigSource = new("campaign/campaign_main/campaign_12_1.py",
        "9e26de1075eb7f7d6c1d3f07bdb9e32691aaed600d78ea332bfa5bb5f4472bea");
    protected static RoadDefinition Road(params string[] groups)
        => new(groups.Select(group => group.Split(' ').Select(Cell.Parse).ToImmutableArray()).ToImmutableArray());
    protected abstract ImmutableArray<RoadDefinition> Roads { get; }
    protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; }
    protected ChapterTwelveRule() => Hooks = new Dictionary<int, BattleHook> { [0] = Battle0, [6] = Battle6 }.ToFrozenDictionary();
    public override CampaignConfiguration Configure(CampaignConfiguration input) => input with
    {
        Vision = (input.Vision ?? MapVisionOverrides.Default) with
        {
            InternalHough = 30, EdgeHough = 30, CoincidentEncourage = 1.2,
            InternalPeaks = new(120, 206, 1.5, 10, 10, 35), EdgePeaks = new(206, 255, null, null, 10, 50, 1000),
            Canny = (75, 100), EdgeColor = (0, 49), HomographyEdgeHough = 210
        },
        SwipeMultipliers = new(new(.977, .995), new(.945, .962), new(.917, .934))
    };
    protected virtual async ValueTask<bool> Battle0(CampaignContext context)
    {
        if (await context.Operations.ClearRoadblocksAsync(Roads)) return true;
        if (await context.Operations.ClearRoadblocksAsync(Roads, potential: true)) return true;
        return await BattleDefaultAsync(context);
    }
    protected virtual async ValueTask<bool> Battle6(CampaignContext context)
    {
        if (ObservedBossBlocked(context) && await context.Operations.ClearRoadblocksAsync(Roads)) return true;
        return await context.Operations.ClearBossForFleetAsync(FleetRoles.BossIndex(context.Config));
    }
}

public sealed class Campaign121 : ChapterTwelveRule
{
    public override string Id => "campaign_main/campaign_12_1";
    public override string StageName => "12-1";
    public override ImmutableArray<SourceFile> Sources => [BaseSource, ConfigSource];
    protected override ImmutableArray<RoadDefinition> Roads { get; } = [Road("F4", "H4")];
    protected override async ValueTask<bool> Battle0(CampaignContext context)
    {
        await context.Operations.PushSecondFleetForwardAsync();
        return await base.Battle0(context);
    }
}

public sealed class Campaign122 : ChapterTwelveRule
{
    public override string Id => "campaign_main/campaign_12_2";
    public override string StageName => "12-2";
    public override ImmutableArray<SourceFile> Sources => [BaseSource, ConfigSource,
        new("campaign/campaign_main/campaign_12_2.py", "2204233433597adc53e799a7692ef80f044da77e4d64f82a38a4c726ed6aaa04")];
    protected override ImmutableArray<RoadDefinition> Roads { get; } = [Road("C4 D5", "B3")];
}

public sealed class Campaign123 : ChapterTwelveRule
{
    public override string Id => "campaign_main/campaign_12_3";
    public override string StageName => "12-3";
    public override ImmutableArray<SourceFile> Sources => [BaseSource, ConfigSource,
        new("campaign/campaign_main/campaign_12_3.py", "230d58959888d0560a272bda23b557918157016d850dc61da0254140d3fa6484")];
    protected override ImmutableArray<RoadDefinition> Roads { get; } = [Road("H1", "J1")];
}

public sealed class Campaign124 : ChapterTwelveRule
{
    public override string Id => "campaign_main/campaign_12_4";
    public override string StageName => "12-4";
    public override ImmutableArray<SourceFile> Sources => [BaseSource, ConfigSource,
        new("campaign/campaign_main/campaign_12_4.py", "7e0ef88a775d8552b52c04345f5f34e9a36850d97df33eeee0fe29a5a5e9ea13")];
    protected override ImmutableArray<RoadDefinition> Roads { get; } = [Road("H3 B6 C5")];
    protected override async ValueTask<bool> Battle0(CampaignContext context)
    {
        if (context.State.BattleCount >= 3) await context.Operations.PickUpAmmoAsync();
        return await base.Battle0(context);
    }
    protected override async ValueTask<bool> Battle6(CampaignContext context)
    {
        await context.Operations.PickUpAmmoAsync();
        return await base.Battle6(context);
    }
}
