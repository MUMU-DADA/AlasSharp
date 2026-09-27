using System.Collections.Frozen;
using System.Collections.Immutable;
using Alas.Engine.Runtime;

namespace Alas.Engine.Rules.Main;

public abstract class ChapterSevenRule : ObservedBossRule
{
    protected static ImmutableArray<Cell> Grids(params string[] cells) => cells.Select(Cell.Parse).ToImmutableArray();
    protected static RoadDefinition Road(params string[] groups)
        => new(groups.Select(group => Grids(group.Split(' '))).ToImmutableArray());
}

public sealed class Campaign71 : ChapterSevenRule
{
    public override string Id => "campaign_main/campaign_7_1";
    public override string StageName => "7-1";
    public override ImmutableArray<SourceFile> Sources => [BaseSource,
        new("campaign/campaign_main/campaign_7_1.py", "9ecc5243536b7225e75c3dc8c1b78476f84e778b89c79fed804e9af7609fa8bf")];
    public override CampaignConfiguration Configure(CampaignConfiguration input) => input with
    {
        Submarine = 0, BossFleet = 2,
        Vision = (input.Vision ?? MapVisionOverrides.Default) with
        { InternalHough = 40, EdgeHough = 40, CoincidentEncourage = 1.5, MidHorizontal = new(137, 143), MidVertical = new(140, 146) }
    };
    public override MapOverlayRules Overlays { get; } = new(Ambush: .45, AirRaid: .45);
    protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; }
    public Campaign71() => Hooks = new Dictionary<int, BattleHook> { [0] = Battle0, [5] = Battle5 }.ToFrozenDictionary();
    private async ValueTask<bool> Battle0(CampaignContext context)
    {
        await context.Operations.ClearMysteriesAsync();
        await context.Operations.PushSecondFleetForwardAsync();
        return await BattleDefaultAsync(context);
    }
    private static async ValueTask<bool> Battle5(CampaignContext context)
    {
        await context.Operations.ClearMysteriesAsync();
        await context.Operations.SwitchFleetAsync(FleetRoles.BossIndex(context.Config));
        return await context.Operations.BruteClearBossAsync();
    }
    public override ValueTask RefocusBossAsync(CampaignContext context) => context.Operations.RefocusBossAsync((-3, -2));
}

public sealed class Campaign72 : ChapterSevenRule
{
    public override string Id => "campaign_main/campaign_7_2";
    public override string StageName => "7-2";
    public override ImmutableArray<SourceFile> Sources => [BaseSource,
        new("campaign/campaign_main/campaign_7_2.py", "e9be6924e9f0033c0217f262c8f61bde4ac265549d365b82966c1f0054406a02")];
    public override CampaignConfiguration Configure(CampaignConfiguration input) => input with { Submarine = 0 };
    private static readonly ImmutableArray<RoadDefinition> Roads = [Road("A3", "C3 B4 C5", "F1 G2 G3")];
    private static readonly ImmutableArray<Cell> StepOn = Grids("A3", "G3", "C3", "E3");
    private static readonly EnemySelection Strongest = new(Strongest: true);
    private static readonly Cell A1 = Cell.Parse("A1"), A2 = Cell.Parse("A2"), A3 = Cell.Parse("A3"),
        G3 = Cell.Parse("G3"), H3 = Cell.Parse("H3");
    protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; }
    public Campaign72() => Hooks = new Dictionary<int, BattleHook> { [0] = Battle0, [5] = Battle5 }.ToFrozenDictionary();
    private static IReadOnlyList<Cell>? IgnoredMysteries(CampaignContext context, bool atBoss)
    {
        IReadOnlyList<Cell>? ignore = null;
        if (context.State.Fleet2Location == A3 && (atBoss || context.State[A1].EnemyScale != 3 && context.State.Fleet1Location != A1))
            ignore = [A2];
        if (context.State.Fleet2Location == G3) ignore = [H3];
        return ignore;
    }
    private async ValueTask<bool> Battle0(CampaignContext context)
    {
        if (await context.Operations.PositionSecondFleetAsync(StepOn, Roads)) return true;
        await context.Operations.ClearMysteriesAsync(IgnoredMysteries(context, atBoss: false), nearby: false);
        if (await context.Operations.ClearRoadblocksAsync(Roads, Strongest)) return true;
        if (await context.Operations.ClearEnemyAsync(new EnemySelection([3]))) return true;
        if (await context.Operations.ClearRoadblocksAsync(Roads, Strongest, potential: true)) return true;
        if (await context.Operations.ClearEnemyAsync(Strongest)) return true;
        return await BattleDefaultAsync(context);
    }
    private static async ValueTask<bool> Battle5(CampaignContext context)
    {
        await context.Operations.ClearMysteriesAsync(IgnoredMysteries(context, atBoss: true), nearby: false);
        if (await context.Operations.ClearRoadblocksAsync(Roads)) return true;
        if (context.State.Fleet2Location == A3 && context.State[A2].IsMystery)
        {
            if (context.Config.Fleet2 != 0) await context.Operations.SwitchFleetAsync(2);
            await context.Operations.ClearMysteryAsync(A2);
        }
        if (context.State.Fleet2Location == G3 && context.State[H3].IsMystery)
        {
            if (context.Config.Fleet2 != 0) await context.Operations.SwitchFleetAsync(2);
            await context.Operations.ClearMysteryAsync(H3);
        }
        return await context.Operations.ClearBossForFleetAsync(FleetRoles.BossIndex(context.Config));
    }
}

public sealed class Campaign73 : ChapterSevenRule
{
    public override string Id => "campaign_main/campaign_7_3";
    public override string StageName => "7-3";
    public override ImmutableArray<SourceFile> Sources => [BaseSource,
        new("campaign/campaign_main/campaign_7_3.py", "24e83bdf958da7c2a53c1f4db35eb43c46ae756db181b26d2e4127d07a7695a0")];
    public override CampaignConfiguration Configure(CampaignConfiguration input) => input with
    { Vision = (input.Vision ?? MapVisionOverrides.Default) with { InternalHough = 40, CoincidentEncourage = 1.5 } };
    private static readonly RoadDefinition RoadA1 = Road("A2 B1", "A2 B3", "B3 A4", "B3 C4").Combine(Road("A2", "E1 D2")),
        RoadC6 = Road("B6", "A6", "A4", "B3 C4"), RoadH1 = Road("H2 G1", "G1 F2", "F2 E1"), RoadH5 = Road("H6", "G6", "E6 F5");
    private static readonly ImmutableArray<RoadDefinition> Roads = [RoadA1, RoadC6, RoadH1, RoadH5];
    private static readonly ImmutableArray<Cell> StepOn = Grids("A4", "B3", "E1", "F5");
    protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; }
    public Campaign73() => Hooks = new Dictionary<int, BattleHook> { [0] = Battle0, [5] = Battle5 }.ToFrozenDictionary();
    private async ValueTask<bool> Battle0(CampaignContext context)
    {
        if (await context.Operations.PositionSecondFleetAsync(StepOn, Roads)) return true;
        await context.Operations.ClearMysteriesAsync();
        if (await context.Operations.ClearRoadblocksAsync(Roads)) return true;
        if (await context.Operations.ClearRoadblocksAsync(Roads, potential: true)) return true;
        return await BattleDefaultAsync(context);
    }
    private static async ValueTask<bool> Battle5(CampaignContext context)
    {
        await context.Operations.ClearMysteriesAsync();
        if (context.State.Cells.FirstOrDefault(cell => cell.IsBoss) is { } boss)
        {
            // Native Campaign owns this mapping; it is not a map-name branch in the engine.
            ImmutableArray<RoadDefinition> roads = boss.Location.ToString() switch
            { "A1" => [RoadA1], "C6" => [RoadC6], "H1" => [RoadH1], "H5" => [RoadH5], _ => Roads };
            if (!context.Operations.CheckAccessibility(boss.Location, FleetRoles.BossIndex(context.Config)))
                return await context.Operations.ClearRoadblocksAsync(roads);
        }
        return await context.Operations.ClearBossForFleetAsync(FleetRoles.BossIndex(context.Config));
    }
}

public sealed class Campaign74 : ChapterSevenRule
{
    public override string Id => "campaign_main/campaign_7_4";
    public override string StageName => "7-4";
    public override ImmutableArray<SourceFile> Sources => [BaseSource,
        new("campaign/campaign_main/campaign_7_4.py", "5d51d37f5ca0e2e192c098f67057d4ecd0f727a3ea80dfaa09fa06f858beb6af")];
    public override CampaignConfiguration Configure(CampaignConfiguration input) => input with
    { Vision = (input.Vision ?? MapVisionOverrides.Default) with { InternalHough = 40, CoincidentEncourage = 1.5, HomographyEdgeHough = 210 } };
    private static readonly RoadDefinition RoadA1 = Road("A2", "C3 D2"), RoadG6 = Road("G4");
    private static readonly ImmutableArray<RoadDefinition> Roads = [RoadA1, RoadG6],
        PositionRoads = [Road("A4", "A2", "C3 D2").Combine(RoadG6)];
    private static readonly ImmutableArray<Cell> StepOn = Grids("A4", "A2", "G4", "D2", "C3");
    protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; }
    public Campaign74() => Hooks = new Dictionary<int, BattleHook>
    {
        [0] = context => BeforeBoss(context, supply: false),
        [3] = context => BeforeBoss(context, supply: true),
        [5] = context => context.Operations.ClearBossForFleetAsync(FleetRoles.BossIndex(context.Config))
    }.ToFrozenDictionary();
    private async ValueTask<bool> BeforeBoss(CampaignContext context, bool supply)
    {
        if (await context.Operations.PositionSecondFleetAsync(StepOn, PositionRoads)) return true;
        await context.Operations.ClearMysteriesAsync();
        if (supply && FleetRoles.BossIndex(context.Config) == 1) await context.Operations.PickUpAmmoAsync();
        if (await context.Operations.ClearRoadblocksAsync(Roads)) return true;
        if (await context.Operations.ClearRoadblocksAsync(Roads, potential: true)) return true;
        return await BattleDefaultAsync(context);
    }
}
