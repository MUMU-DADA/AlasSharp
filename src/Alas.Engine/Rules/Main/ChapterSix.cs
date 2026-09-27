using System.Collections.Frozen;
using System.Collections.Immutable;
using Alas.Engine.Runtime;

namespace Alas.Engine.Rules.Main;

/// <summary>Shared bodies of chapter six's native positioning/road/mystery hooks.</summary>
public abstract class ChapterSixRule : ObservedBossRule
{
    protected abstract ImmutableArray<Cell> StepOn { get; }
    protected abstract ImmutableArray<RoadDefinition> BossRoads { get; }
    protected virtual ImmutableArray<RoadDefinition> PositionRoads => BossRoads;
    protected virtual ImmutableArray<RoadDefinition> MysteryRoads => [];
    protected virtual bool ClearPotentialRoads => true;
    protected virtual int BossBattle => 4;
    public override CampaignConfiguration Configure(CampaignConfiguration input) => input with { BossFleet = 1, MysteryHasCarrier = true };
    protected static ImmutableArray<Cell> Grids(params string[] cells) => cells.Select(Cell.Parse).ToImmutableArray();
    protected static RoadDefinition Road(params string[] groups)
        => new(groups.Select(group => Grids(group.Split(' '))).ToImmutableArray());
    protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; }
    protected ChapterSixRule() => Hooks = new Dictionary<int, BattleHook> { [0] = Battle0, [BossBattle] = BattleAtBoss }.ToFrozenDictionary();
    private async ValueTask<bool> Battle0(CampaignContext context)
    {
        if (await context.Operations.PositionSecondFleetAsync(StepOn, PositionRoads)) return true;
        if (await context.Operations.ClearRoadblocksAsync(BossRoads)) return true;
        if (!MysteryRoads.IsEmpty && await context.Operations.ClearRoadblocksAsync(MysteryRoads)) return true;
        await context.Operations.ClearMysteriesAsync();
        if (ClearPotentialRoads)
        {
            if (await context.Operations.ClearRoadblocksAsync(BossRoads, potential: true)) return true;
            if (!MysteryRoads.IsEmpty && await context.Operations.ClearRoadblocksAsync(MysteryRoads, potential: true)) return true;
        }
        return await BattleDefaultAsync(context);
    }
    protected virtual ValueTask BeforeBossAsync(CampaignContext context) => ValueTask.CompletedTask;
    private async ValueTask<bool> BattleAtBoss(CampaignContext context)
    {
        await context.Operations.ClearMysteriesAsync();
        await BeforeBossAsync(context);
        if (ObservedBossBlocked(context)) return await context.Operations.ClearRoadblocksAsync(BossRoads);
        return await context.Operations.ClearBossForFleetAsync(FleetRoles.BossIndex(context.Config));
    }
}

public sealed class Campaign61 : ChapterSixRule
{
    public override string Id => "campaign_main/campaign_6_1";
    public override string StageName => "6-1";
    public override ImmutableArray<SourceFile> Sources => [BaseSource,
        new("campaign/campaign_main/campaign_6_1.py", "2ba713dd8c2b1e114687fb8c0d2fed0166f44227af68549016640805771980c8")];
    protected override ImmutableArray<Cell> StepOn { get; } = Grids("E3", "C3", "G4", "D2");
    protected override ImmutableArray<RoadDefinition> BossRoads { get; } = [Road("C1", "C2", "C3 D2", "D4", "E4", "E3 F4", "F3", "G3", "H3", "G4 H4")];
    protected override ImmutableArray<RoadDefinition> MysteryRoads { get; } = [Road("C4 D5", "D4", "G2", "G1 H2")];
    public override CampaignConfiguration Configure(CampaignConfiguration input)
    {
        var config = base.Configure(input);
        return config with { Vision = (config.Vision ?? MapVisionOverrides.Default) with
        { InternalHough = 40, CoincidentEncourage = 1.5, HomographyEdgeHough = 240 } };
    }
}
public sealed class Campaign62 : ChapterSixRule
{
    public override string Id => "campaign_main/campaign_6_2";
    public override string StageName => "6-2";
    public override ImmutableArray<SourceFile> Sources => [BaseSource,
        new("campaign/campaign_main/campaign_6_2.py", "c53f551bfebe3423258ed0559024d4c643e2e197f27625bcfebef91805dd2a75")];
    protected override ImmutableArray<Cell> StepOn { get; } = Grids("C4", "F4", "A5", "H4", "F6");
    protected override ImmutableArray<RoadDefinition> BossRoads { get; } = [Road("B6", "A6", "A5", "A4", "B4", "C4", "D4", "D5", "E5", "F5", "F6", "F4", "G4", "H4")];
    protected override bool ClearPotentialRoads => false;
    public override CampaignConfiguration Configure(CampaignConfiguration input)
    {
        var config = base.Configure(input);
        return config with { Vision = (config.Vision ?? MapVisionOverrides.Default) with
        { InternalHough = 40, CoincidentEncourage = 1.5, HomographyEdgeHough = 210 } };
    }
}
public sealed class Campaign63 : ChapterSixRule
{
    public override string Id => "campaign_main/campaign_6_3";
    public override string StageName => "6-3";
    public override ImmutableArray<SourceFile> Sources => [BaseSource,
        new("campaign/campaign_main/campaign_6_3.py", "5ad3bbe8dcccc26a4749842c5a518162b4779160d28a6a8d58c668ec410ec069")];
    protected override ImmutableArray<Cell> StepOn { get; } = Grids("E4", "G2", "C3", "C4", "F3");
    protected override ImmutableArray<RoadDefinition> BossRoads { get; } = [Road(
        "A1", "B1 A2", "B2", "C2 B3", "B3 C3", "B4 C3", "C3 C4", "C4 D3", "D4",
        "G3 H4", "G3 G4", "G3", "F3 G4", "F4", "E4 F5", "E4 E5", "E4 D5")];
    protected override ImmutableArray<RoadDefinition> PositionRoads { get; } = [Road(
        "A1", "B1 A2", "B2", "C2 B3", "B3 C3", "B4 C3", "C3 C4", "C4 D3", "D4",
        "G3 H4", "G3 G4", "G3", "F3 G4", "F4", "E4 F5", "E4 E5", "E4 D5",
        "D1", "C1", "B1 C2", "E1", "F1", "G1 F2", "G2 F2", "F3 G2")];
    protected override ImmutableArray<RoadDefinition> MysteryRoads { get; } = [Road("F5 G4", "H4 G5", "H2")];
    public override CampaignConfiguration Configure(CampaignConfiguration input)
    {
        var config = base.Configure(input);
        return config with { Vision = (config.Vision ?? MapVisionOverrides.Default) with { InternalHough = 40, CoincidentEncourage = 1.5 } };
    }
}
public sealed class Campaign64 : ChapterSixRule
{
    public override string Id => "campaign_main/campaign_6_4";
    public override string StageName => "6-4";
    public override ImmutableArray<SourceFile> Sources => [BaseSource,
        new("campaign/campaign_main/campaign_6_4.py", "006d7f7fedd736e992b2b544a37f8f5fb7293896270363686d2f9f49daeb567c")];
    protected override int BossBattle => 5;
    protected override ImmutableArray<Cell> StepOn { get; } = Grids("C2", "C3", "D4", "F3", "G4");
    protected override ImmutableArray<RoadDefinition> BossRoads { get; } = [Road(
        "A5 B6", "A4 B5 B6", "C4", "C5", "C3 D4", "D3", "C5 D3", "B1 B2", "B1 C2", "C1 C2", "C2 D1", "C2 D2",
        "H3 G4", "G3 G4", "F3 G4", "F3 F4", "F2 F3 E4", "E2 F3 E4", "E3")];
    protected override async ValueTask BeforeBossAsync(CampaignContext context)
    {
        if (context.Config.BossFleet == 1) await context.Operations.PickUpAmmoAsync();
    }
}
