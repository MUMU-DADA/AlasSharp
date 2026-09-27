using System.Collections.Frozen;
using System.Collections.Immutable;
using Alas.Engine.Runtime;

namespace Alas.Engine.Rules.Main;

public abstract class ChapterTenRule : ObservedBossRule
{
    protected static RoadDefinition Road(params string[] groups)
        => new(groups.Select(group => group.Split(' ').Select(Cell.Parse).ToImmutableArray()).ToImmutableArray());
    protected abstract ImmutableArray<RoadDefinition> Roads { get; }
    protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; }
    protected ChapterTenRule() => Hooks = new Dictionary<int, BattleHook> { [0] = Battle0, [6] = Battle6 }.ToFrozenDictionary();
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

public sealed class Campaign101 : ChapterTenRule
{
    public override string Id => "campaign_main/campaign_10_1";
    public override string StageName => "10-1";
    public override ImmutableArray<SourceFile> Sources => [BaseSource,
        new("campaign/campaign_main/campaign_10_1.py", "db10d4f3f077852a53ddcfd3449aefdf3d4453f337e51317e79927c61202d020")];
    public override CampaignConfiguration Configure(CampaignConfiguration input) => input with
    {
        Vision = (input.Vision ?? MapVisionOverrides.Default) with { InternalHough = 40, EdgeHough = 40, CoincidentEncourage = 1.5 }
    };
    protected override ImmutableArray<RoadDefinition> Roads { get; } = [Road("B4", "C4", "D4", "E5", "F5", "G5")];
    protected override async ValueTask<bool> Battle0(CampaignContext context)
    {
        await context.Operations.PushSecondFleetForwardAsync();
        return await base.Battle0(context);
    }
}

public sealed class Campaign102 : ChapterTenRule
{
    public override string Id => "campaign_main/campaign_10_2";
    public override string StageName => "10-2";
    public override ImmutableArray<SourceFile> Sources => [BaseSource,
        new("campaign/campaign_main/campaign_10_2.py", "3fa2fabd165e66c7f6481a79137bb1812f2be959b3b22daf3d215511d5cf346b")];
    public override CampaignConfiguration Configure(CampaignConfiguration input) => input with
    {
        Vision = (input.Vision ?? MapVisionOverrides.Default) with
        {
            Backend = GridDetectionBackend.Homography,
            Storage = new(new(7, 6), new(new(471.806, 82.471), new(1249.283, 82.471),
                new(420.72, 615.64), new(1435.387, 615.64))),
            InternalHough = 30, EdgeHough = 30, CoincidentEncourage = 1.3,
            InternalPeaks = new(150, 231, .9, 10, 10, 35), EdgePeaks = new(231, 255, 0, 10, 10, 50, 1000)
        },
        MapEdgeCorner = "bottom"
    };
    protected override ImmutableArray<RoadDefinition> Roads { get; } = [Road("B6", "C6", "D6", "E5", "F4", "G2", "G1 H2", "G3", "G4", "G5", "H5 G6")];
    protected override async ValueTask<bool> Battle0(CampaignContext context)
    {
        await context.Operations.PushSecondFleetForwardAsync();
        return await base.Battle0(context);
    }
}

public sealed class Campaign103 : ChapterTenRule
{
    public override string Id => "campaign_main/campaign_10_3";
    public override string StageName => "10-3";
    public override ImmutableArray<SourceFile> Sources => [BaseSource,
        new("campaign/campaign_main/campaign_10_3.py", "1e2b960b6737a243be4ca6b953d88fc2a2165ecc34a5578860aa05814b1dc409")];
    public override CampaignConfiguration Configure(CampaignConfiguration input) => input with
    {
        Vision = (input.Vision ?? MapVisionOverrides.Default) with
        {
            InternalHough = 35, EdgeHough = 35, CoincidentEncourage = 1.2,
            InternalPeaks = new(150, 231, .9, 10, 10, 35), EdgePeaks = new(231, 255, 0, 10, 10, 50, 1000),
            HomographyEdgeHough = 210
        }
    };
    protected override ImmutableArray<RoadDefinition> Roads { get; } = [Road("E6", "G6", "H6", "E5", "H4", "G4", "F4", "F3")];
    protected override async ValueTask<bool> Battle0(CampaignContext context)
    {
        await context.Operations.PushSecondFleetForwardAsync();
        if (await context.Operations.ClearRoadblocksAsync(Roads)) return true;
        if (await context.Operations.ClearRoadblocksAsync(Roads, potential: true)) return true;
        await context.Operations.ClearMysteriesAsync();
        return await BattleDefaultAsync(context);
    }
}

public sealed class Campaign104 : ChapterTenRule
{
    public override string Id => "campaign_main/campaign_10_4";
    public override string StageName => "10-4";
    public override ImmutableArray<SourceFile> Sources => [BaseSource,
        new("campaign/campaign_main/campaign_10_4.py", "10f84fd754b0dced06c6d29ec3d231170ae221811d50f0c1d1ee7f8bfdcbaa69")];
    public override CampaignConfiguration Configure(CampaignConfiguration input) => input with
    {
        Vision = (input.Vision ?? MapVisionOverrides.Default) with
        {
            InternalHough = 40, EdgeHough = 40, CoincidentEncourage = 1.5,
            InternalPeaks = new(150, 231, .9, 10, 10, 35), EdgePeaks = new(231, 255, 0, 10, 10, 50, 1000)
        },
        MapEdgeCorner = "bottom"
    };
    protected override ImmutableArray<RoadDefinition> Roads { get; } = [Road("D5", "E4", "D3", "C3", "A2", "G4", "H5", "G3", "G2")];
    private static readonly ImmutableArray<RoadDefinition> PositionRoads = [Road("D5 E6", "E4", "D3")];
    private static readonly ImmutableArray<Cell> StepOn = [Cell.Parse("E4"), Cell.Parse("D3"), Cell.Parse("G4"), Cell.Parse("C3")];
    protected override async ValueTask<bool> Battle0(CampaignContext context)
    {
        if (await context.Operations.PositionSecondFleetAsync(StepOn, PositionRoads)) return true;
        return await base.Battle0(context);
    }
    protected override async ValueTask<bool> Battle6(CampaignContext context)
    {
        if (ObservedBossBlocked(context) && await context.Operations.ClearRoadblocksAsync(Roads, potential: true)) return true;
        return await context.Operations.ClearBossForFleetAsync(FleetRoles.BossIndex(context.Config));
    }
}
