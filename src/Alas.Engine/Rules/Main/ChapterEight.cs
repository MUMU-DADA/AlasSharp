using System.Collections.Frozen;
using System.Collections.Immutable;
using Alas.Engine.Runtime;

namespace Alas.Engine.Rules.Main;

public abstract class ChapterEightRule : ObservedBossRule
{
    protected static readonly SourceFile ConfigSource = new("campaign/campaign_main/campaign_8_1.py",
        "f8fd77314bce70742429a1d3aa283c479a8b677536258f83c260dfd246d281f7");
    protected static ImmutableArray<Cell> Grids(params string[] cells) => cells.Select(Cell.Parse).ToImmutableArray();
    protected static RoadDefinition Road(params string[] groups)
        => new(groups.Select(group => Grids(group.Split(' '))).ToImmutableArray());
    public override CampaignConfiguration Configure(CampaignConfiguration input) => input with
    {
        Vision = (input.Vision ?? MapVisionOverrides.Default) with
        {
            InternalHough = 35, EdgeHough = 35, CoincidentEncourage = 1.3,
            InternalPeaks = new(120, 206, .9, 10, 10, 35),
            EdgePeaks = new(206, 255, 0, 10, 10, 50, 1000), EdgeColor = (0, 49)
        },
        SwipeMultipliers = new(new(1.127, 1.148), new(1.090, 1.110), new(1.058, 1.077))
    };
    protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; }
    protected ChapterEightRule() => Hooks = new Dictionary<int, BattleHook> { [0] = Battle0, [4] = Battle4 }.ToFrozenDictionary();
    protected abstract ValueTask<bool> Battle0(CampaignContext context);
    protected virtual ValueTask<bool> Battle4(CampaignContext context) => context.Operations.BruteClearBossAsync();
}

public sealed class Campaign81 : ChapterEightRule
{
    public override string Id => "campaign_main/campaign_8_1";
    public override string StageName => "8-1";
    public override ImmutableArray<SourceFile> Sources => [BaseSource, ConfigSource];
    protected override async ValueTask<bool> Battle0(CampaignContext context)
    {
        await context.Operations.PushSecondFleetForwardAsync();
        return await BattleDefaultAsync(context);
    }
}

public sealed class Campaign82 : ChapterEightRule
{
    public override string Id => "campaign_main/campaign_8_2";
    public override string StageName => "8-2";
    public override ImmutableArray<SourceFile> Sources => [BaseSource, ConfigSource,
        new("campaign/campaign_main/campaign_8_2.py", "08ac28a3958d61e059ce9e2690a67a7a88f0e0b6012bd2d8e82bef2b3f10bc08")];
    public override CampaignConfiguration Configure(CampaignConfiguration input)
    {
        var inherited = base.Configure(input);
        return inherited with { Vision = inherited.Vision! with { HomographyEdgeHough = 210 } };
    }
    private static readonly ImmutableArray<RoadDefinition> Roads = [
        Road("A2 B1", "B1 B2 B3", "A2 B2 C2", "B3 C2", "D3"),
        Road("F1 G2 H3", "F1 G2 G3", "F2 G2 H3", "F2 G3", "E3")];
    private static readonly ImmutableArray<RoadDefinition> MysteryRoad = [Road("A4", "A2 B3")],
        Middle = [Road("E5", "D5 E4", "D3").Combine(Road("H4", "H3", "F1 G2 G3", "F2 G3", "E3"))];
    private static readonly ImmutableArray<Cell> StepOn = Grids("D3", "E3");
    protected override async ValueTask<bool> Battle0(CampaignContext context)
    {
        if (await context.Operations.PositionSecondFleetAsync(StepOn, Middle)) return true;
        await context.Operations.ClearMysteriesAsync();
        if (await context.Operations.ClearRoadblocksAsync(Roads)) return true;
        if (context.State.MysteryCount < 1 && await context.Operations.ClearRoadblocksAsync(MysteryRoad)) return true;
        if (await context.Operations.ClearRoadblocksAsync(Roads, potential: true)) return true;
        if (await context.Operations.ClearFirstRoadblocksAsync(Roads)) return true;
        return await BattleDefaultAsync(context);
    }
}

public sealed class Campaign83 : ChapterEightRule
{
    public override string Id => "campaign_main/campaign_8_3";
    public override string StageName => "8-3";
    public override ImmutableArray<SourceFile> Sources => [BaseSource, ConfigSource,
        new("campaign/campaign_main/campaign_8_3.py", "9a16fc19c2cf663e89c1fde8edce98a5334a03b0ebcd1ecef3c1fda339e43040")];
    // Upstream keeps these roads separate so a boss-wave enemy cannot block every route.
    private static readonly ImmutableArray<RoadDefinition> Roads = [Road("D5", "B5 C6"), Road("F3", "F1 G2 H3"),
        Road("A4", "A3"), Road("F1", "E1", "D1", "C1"), Road("D6", "E6", "F6"), Road("H3 G4", "G4 H4", "H4 G5")];
    private static readonly ImmutableArray<RoadDefinition> MysteryRoad = [Road("B2 C1")], Middle = [Road("D5", "F3")];
    private static readonly ImmutableArray<Cell> StepOn = Grids("D5", "F3");
    protected override async ValueTask<bool> Battle0(CampaignContext context)
    {
        if (await context.Operations.PositionSecondFleetAsync(StepOn, Middle)) return true;
        await context.Operations.ClearMysteriesAsync();
        if (await context.Operations.ClearRoadblocksAsync(Roads)) return true;
        if (await context.Operations.ClearRoadblocksAsync(Roads, potential: true)) return true;
        if (await context.Operations.ClearRoadblocksAsync(MysteryRoad)) return true;
        if (await context.Operations.ClearFirstRoadblocksAsync(Roads)) return true;
        return await BattleDefaultAsync(context);
    }
}

public sealed class Campaign84 : ChapterEightRule
{
    public override string Id => "campaign_main/campaign_8_4";
    public override string StageName => "8-4";
    public override ImmutableArray<SourceFile> Sources => [BaseSource, ConfigSource,
        new("campaign/campaign_main/campaign_8_4.py", "170ce2a6e845e6d7f9c9e2102893c6e617a166886f9a8004ec0eb8ebcd5fc950")];
    public override MapOverlayRules Overlays { get; } = new(Ambush: .45, AirRaid: .45);
    private static readonly ImmutableArray<RoadDefinition> Roads = [
        Road("G7", "E7").Combine(Road("G5", "E5", "C5 D6", "D6 C7")), Road("G5", "F4 G4", "F4 G3"), Road("G7 H6")];
    protected override async ValueTask<bool> Battle0(CampaignContext context)
    {
        await context.Operations.PushSecondFleetForwardAsync();
        await context.Operations.ClearMysteriesAsync();
        if (await context.Operations.ClearRoadblocksAsync(Roads)) return true;
        if (await context.Operations.ClearRoadblocksAsync(Roads, potential: true)) return true;
        if (await context.Operations.ClearFirstRoadblocksAsync(Roads)) return true;
        return await BattleDefaultAsync(context);
    }
    protected override async ValueTask<bool> Battle4(CampaignContext context)
    {
        await context.Operations.ClearMysteriesAsync();
        return await context.Operations.BruteClearBossAsync();
    }
}
