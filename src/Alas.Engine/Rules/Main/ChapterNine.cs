using System.Collections.Frozen;
using System.Collections.Immutable;
using Alas.Engine.Runtime;

namespace Alas.Engine.Rules.Main;

public abstract class ChapterNineRule : ObservedBossRule
{
    protected static RoadDefinition Road(params string[] groups)
        => new(groups.Select(group => group.Split(' ').Select(Cell.Parse).ToImmutableArray()).ToImmutableArray());
    protected abstract ImmutableArray<RoadDefinition> Roads { get; }
    protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; }
    protected ChapterNineRule() => Hooks = new Dictionary<int, BattleHook> { [0] = Battle0, [5] = Battle5 }.ToFrozenDictionary();
    // Identical Config declarations in 9-2/9-3/9-4; 9-1 supplies its own calibration.
    public override CampaignConfiguration Configure(CampaignConfiguration input) => input with
    {
        Vision = (input.Vision ?? MapVisionOverrides.Default) with
        {
            InternalHough = 40, EdgeHough = 40, CoincidentEncourage = 1.5,
            InternalPeaks = new(150, 231, .9, 10, 10, 35), EdgePeaks = new(231, 255, 0, 10, 10, 50, 1000)
        }
    };
    protected virtual async ValueTask<bool> Battle0(CampaignContext context)
    {
        if (await context.Operations.ClearRoadblocksAsync(Roads)) return true;
        if (await context.Operations.ClearRoadblocksAsync(Roads, potential: true)) return true;
        return await BattleDefaultAsync(context);
    }
    protected virtual async ValueTask<bool> Battle5(CampaignContext context)
    {
        if (ObservedBossBlocked(context) && await context.Operations.ClearRoadblocksAsync(Roads)) return true;
        return await context.Operations.ClearBossForFleetAsync(FleetRoles.BossIndex(context.Config));
    }
}

public sealed class Campaign91 : ChapterNineRule
{
    public override string Id => "campaign_main/campaign_9_1";
    public override string StageName => "9-1";
    public override ImmutableArray<SourceFile> Sources => [BaseSource,
        new("campaign/campaign_main/campaign_9_1.py", "5b9c115ff04efbdcaefe297f23a77b19c8a3fb2740ad1c37794a87c984475ade")];
    public override CampaignConfiguration Configure(CampaignConfiguration input) => input with
    {
        Submarine = 0, MapEdgeCorner = "bottom",
        Vision = (input.Vision ?? MapVisionOverrides.Default) with
        {
            Storage = new(new(9, 5), new(new(214.274, 100.482), new(1300.358, 100.482),
                new(79.666, 584.209), new(1505.19, 584.209)))
        }
    };
    protected override ImmutableArray<RoadDefinition> Roads { get; } = [Road("B3", "D3", "D5 E4", "F5", "G5", "G4 H5", "H4", "E3", "F3", "F2", "F1")];
    private static readonly ImmutableArray<Cell> StepOn = [Cell.Parse("F3"), Cell.Parse("E4")];
    protected override async ValueTask<bool> Battle0(CampaignContext context)
    {
        if (await context.Operations.PositionSecondFleetAsync(StepOn, Roads)) return true;
        return await base.Battle0(context);
    }
}

public sealed class Campaign92 : ChapterNineRule
{
    public override string Id => "campaign_main/campaign_9_2";
    public override string StageName => "9-2";
    public override ImmutableArray<SourceFile> Sources => [BaseSource,
        new("campaign/campaign_main/campaign_9_2.py", "17133fe2c4d913d493534833fede34a559ffa4f0e18c24666db76d33e7255319")];
    public override CampaignConfiguration Configure(CampaignConfiguration input)
    {
        var common = base.Configure(input);
        return common with { Submarine = 0, HasMystery = true, Vision = common.Vision! with { HomographyEdgeHough = 210 } };
    }
    protected override ImmutableArray<RoadDefinition> Roads { get; } = [Road("C3", "C2", "C1 D2", "F1", "H1", "H2", "H3", "H4")];
    private static readonly Cell D5 = Cell.Parse("D5"), F4 = Cell.Parse("F4"), F5 = Cell.Parse("F5");
    private static readonly ImmutableArray<double> SecondFleetAtD5 = [
        10, 10, 30, 10, 10, 20, 30, 40, 10,
        10, 10, 10, 10, 10, 30, 10, 50, 10,
        30, 10, 10, 10, 10, 10, 10, 60, 10,
        10, 10, 10, 10, 10, 10, 10, 70, 10,
        10, 30, 10, 10, 10, 10, 10, 10, 10];
    protected override ValueTask<bool> Battle0(CampaignContext context)
    {
        if (context.State.Fleet2Location == D5) context.State.SetWeights(SecondFleetAtD5);
        if (context.State.Fleet2Location == F4) context.State.SetWeights(Map.Weights);
        if (context.State.Fleet2Location == F5) context.State.SetWeights(Map.Weights);
        return base.Battle0(context);
    }
}

public sealed class Campaign93 : ChapterNineRule
{
    public override string Id => "campaign_main/campaign_9_3";
    public override string StageName => "9-3";
    public override ImmutableArray<SourceFile> Sources => [BaseSource,
        new("campaign/campaign_main/campaign_9_3.py", "685fc37167073eb6c0de1e29b45f57035e9cb19829a906ecc5d5ad27f3c48697")];
    protected override ImmutableArray<RoadDefinition> Roads { get; } = [Road("A2", "A3", "C5", "D6", "F2", "F3", "F6", "G6", "H4")];
}

public sealed class Campaign94 : ChapterNineRule
{
    public override string Id => "campaign_main/campaign_9_4";
    public override string StageName => "9-4";
    public override ImmutableArray<SourceFile> Sources => [BaseSource,
        new("campaign/campaign_main/campaign_9_4.py", "69e710530f0d1edf052fef00ecc4db21a52a17f3e9d93d15fb776c22a10dd2b9")];
    protected override ImmutableArray<RoadDefinition> Roads { get; } = [Road("A3", "D6", "E5 F6", "G4", "I4")];
    protected override async ValueTask<bool> Battle5(CampaignContext context)
    {
        // The upstream hook reads the configured role directly. Keep an explicit
        // override observable even when a synthetic/offline composition has no
        // second fleet; normal execution still derives the effective role.
        bool bossIsFirst = context.Config.BossFleet is { } selected ? selected == 1 : FleetRoles.BossIndex(context.Config) == 1;
        if (bossIsFirst) await context.Operations.PickUpAmmoAsync();
        return await base.Battle5(context);
    }
}
