using System.Collections.Frozen;
using System.Collections.Immutable;
using Alas.Engine.Runtime;

namespace Alas.Engine.Rules.Main;

public abstract class ChapterFourteenRule : CampaignRule
{
    protected static readonly SourceFile BaseSource = new("module/campaign/campaign_base.py",
        "a575fc1a27d5888127139b027d0f0626cb93a3f29b6aab6dbff5060c04755b7f");
    protected static readonly SourceFile ChapterSource = CampaignMapCombat.PickupSource;
    protected static readonly EnemyFilter Filter = new("1T > 1L > 1E > 1M > 2T > 2L > 2E > 2M > 3T > 3L > 3E > 3M");
    public override MapDefinition Map => CampaignMapCatalog.Get(Id).Map;
    public override bool CountMysteryItems => false;
    public override CampaignConfiguration Configure(CampaignConfiguration input) => input with
    {
        Vision = (input.Vision ?? MapVisionOverrides.Default) with { EdgeColor = (0, 12), HomographyEdgeHough = 210 },
        SwipeMultipliers = new(new(1.006, 1.025), new(.973, .991), new(.944, .961)),
        SwipePredictWithSeaGrids = false, WalkTurningOptimize = false,
        HasMapStory = false, HasFleetStep = false, HasAmbush = true
    };
    protected static RoadDefinition Road(params string[] groups)
        => new(groups.Select(group => group.Split(' ').Select(Cell.Parse).ToImmutableArray()).ToImmutableArray());
    protected async ValueTask<bool> FilterThenDefault(CampaignContext context, int preserve)
    {
        if (await context.Operations.ClearFilterEnemyAsync(Filter, preserve)) return true;
        return await BattleDefaultAsync(context);
    }
    protected static async ValueTask<bool> FlareThenBoss(CampaignContext context, Cell flare)
    {
        int fleet = FleetRoles.BossIndex(context.Config);
        await context.Operations.SwitchFleetAsync(fleet);
        await context.Operations.PickUpFlareAsync(flare);
        return await context.Operations.ClearBossForFleetAsync(fleet);
    }
}

public abstract class ChapterFourteenLighthouseRule : ChapterFourteenRule
{
    protected abstract Cell LightHouse { get; }
    protected abstract Cell Flare { get; }
    protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; }
    protected ChapterFourteenLighthouseRule() => Hooks = new Dictionary<int, BattleHook>
    { [0] = context => PickThenFilter(context, 1), [5] = context => PickThenFilter(context, 0),
      [6] = context => FlareThenBoss(context, Flare) }.ToFrozenDictionary();
    private async ValueTask<bool> PickThenFilter(CampaignContext context, int preserve)
    {
        await context.Operations.PickUpLightHouseAsync(LightHouse);
        return await FilterThenDefault(context, preserve);
    }
}

public sealed class Campaign141 : ChapterFourteenLighthouseRule
{
    public override string Id => "campaign_main/campaign_14_1";
    public override string StageName => "14-1";
    protected override Cell LightHouse => Cell.Parse("E3");
    protected override Cell Flare => Cell.Parse("C5");
    public override ImmutableArray<SourceFile> Sources => [BaseSource, ChapterSource,
        new("campaign/campaign_main/campaign_14_1.py", "084151d1a6b547463742722102cabbd04a6664765188cb1cb2c13fe8a1bb8ecf")];
}

public sealed class Campaign142 : ChapterFourteenRule
{
    public override string Id => "campaign_main/campaign_14_2";
    public override string StageName => "14-2";
    public override ImmutableArray<SourceFile> Sources => [BaseSource, ChapterSource,
        new("campaign/campaign_main/campaign_14_2.py", "b14478a97fa4d98d208dfd3bc2111830ae2d083ba148e235740c357989ab2227")];
    private static readonly ImmutableArray<RoadDefinition> Roads = [Road("A4 B5"), Road("G6 G7 H6", "G6 G7 H7")];
    protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; }
    public Campaign142() => Hooks = new Dictionary<int, BattleHook>
    { [0] = Battle0, [5] = context => FilterThenDefault(context, 0),
      [6] = context => context.Operations.ClearBossForFleetAsync(FleetRoles.BossIndex(context.Config)) }.ToFrozenDictionary();
    private async ValueTask<bool> Battle0(CampaignContext context)
    {
        if (context.State.PickedFlares.Count == 0 && context.State[Cell.Parse("H7")].IsAccessible && context.State[Cell.Parse("A5")].IsAccessible)
        {
            int fleet = FleetRoles.BossIndex(context.Config);
            await context.Operations.SwitchFleetAsync(fleet);
            await context.Operations.PickUpFlareAsync(Cell.Parse("H7"));
            await context.Operations.SwitchFleetAsync(fleet);
            await context.Operations.PickUpFlareAsync(Cell.Parse("A5"));
            await context.Operations.SwitchFleetAsync(fleet);
            await context.Operations.MoveFleetAsync(Cell.Parse("D6"));
            await context.Operations.SwitchFleetAsync(1);
        }
        if (await context.Operations.ClearRoadblocksAsync(Roads, new EnemySelection(Weakest: true))) return true;
        return await FilterThenDefault(context, 1);
    }
}

public sealed class Campaign143 : ChapterFourteenLighthouseRule
{
    public override string Id => "campaign_main/campaign_14_3";
    public override string StageName => "14-3";
    protected override Cell LightHouse => Cell.Parse("J7");
    protected override Cell Flare => Cell.Parse("D5");
    public override ImmutableArray<SourceFile> Sources => [BaseSource, ChapterSource,
        new("campaign/campaign_main/campaign_14_3.py", "0e29a7a8601a29c14cdc3f421783bc4265f0d7cd00fe2df17feda46e72a339c8")];
}

public sealed class Campaign144 : ChapterFourteenRule
{
    public override string Id => "campaign_main/campaign_14_4";
    public override string StageName => "14-4";
    public override ImmutableArray<SourceFile> Sources => [BaseSource, ChapterSource,
        new("campaign/campaign_main/campaign_14_4.py", "d89007908fd1e8ee9ae9bd16f791732d13a35aeba85f7bd47e9cfaa516f80efa")];
    public override CampaignConfiguration Configure(CampaignConfiguration input) => base.Configure(input) with { WalkUseCurrentFleet = true };
    // Native OVERRIDE is a full may_enemy replacement in non-clear mode, preserving may_ambush.
    private static readonly FrozenSet<Cell> EnemySpawns = "A1 G1 B2 C2 F2 H2 D3 D4 G4 K4 C5 D5 F5 K5 B6 C6 G6 C7 I7 B8 E8 G8 H8 I8 J9"
        .Split(' ').Select(Cell.Parse).ToFrozenSet();
    public override void InitializeMapState(CampaignState state, MapInitialization options)
    {
        base.InitializeMapState(state, options);
        if (!options.ClearMode)
            foreach (var cell in state.Cells) cell.MayEnemy = EnemySpawns.Contains(cell.Location);
    }
    private static readonly ImmutableArray<RoadDefinition> Roads = [Road("B8"), Road("H8 I8 J9")];
    protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; }
    public Campaign144() => Hooks = new Dictionary<int, BattleHook>
    { [0] = context => Battle(context, false, 1), [3] = context => Battle(context, true, 1),
      [6] = context => Battle(context, true, 0), [7] = context => FlareThenBoss(context, Cell.Parse("A5")) }.ToFrozenDictionary();
    private async ValueTask<bool> Battle(CampaignContext context, bool supply, int preserve)
    {
        await context.Operations.PickUpLightHouseAsync(Cell.Parse("A9"));
        if (supply)
        {
            await context.Operations.PickUpAmmoAsync();
            await context.Operations.PickUpFlareAsync(Cell.Parse("H9"));
        }
        if (await context.Operations.ClearRoadblocksAsync(Roads, new EnemySelection(Weakest: false))) return true;
        return await FilterThenDefault(context, preserve);
    }
}
