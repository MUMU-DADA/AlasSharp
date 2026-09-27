using System.Collections.Frozen;
using System.Collections.Immutable;
using Alas.Engine.Runtime;

namespace Alas.Engine.Rules.Main;

/// <summary>Chapter 15 campaign rules, including the shared strategy mob move.
/// The chapter files own only their upstream hooks; movement validation and UI
/// interaction stay in the common Engine map runtime.</summary>
public abstract class ChapterFifteenRule : CampaignRule
{
    protected static readonly SourceFile BaseSource = new("module/campaign/campaign_base.py",
        "a575fc1a27d5888127139b027d0f0626cb93a3f29b6aab6dbff5060c04755b7f");
    protected static readonly SourceFile ChapterSource = new("campaign/campaign_main/campaign_15_base.py",
        "60f28ffa4c3c6a8255428d99e974f8d7ef7f1c4ea8f3921d4ae5fc48bb2e4ed6");
    protected static readonly EnemyFilter Filter = new("1L > 1M > 1E > 2L > 3L > 2M > 2E > 1C > 2C > 3M > 3E > 3C");
    public override MapDefinition Map => CampaignMapCatalog.Get(Id).Map;
    public override bool CountMysteryItems => false;
    public override CampaignConfiguration Configure(CampaignConfiguration input) => input with
    {
        Vision = (input.Vision ?? MapVisionOverrides.Default) with
        {
            InternalPeaks = new(80, 222, .9, 10, 10, 35),
            EdgePeaks = new(222, 255, null, null, 10, 50, 1000),
            Canny = (50, 100), EdgeColor = (0, 33), HomographyEdgeHough = 180
        },
        SwipeMultipliers = new(new(.993, 1.011), new(.960, .978), new(.932, .949)),
        SwipePredictWithSeaGrids = false, WalkTurningOptimize = false,
        HasMapStory = false, HasFleetStep = false, HasAmbush = true,
        HasMystery = false, HasSiren = false, HasMovableEnemy = false,
        HasFortress = false
    };
    protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; }
    protected ChapterFifteenRule(IReadOnlyDictionary<int, BattleHook> hooks) => Hooks = hooks.ToFrozenDictionary();
    protected override bool UseHookInClearAll(CampaignContext context)
        => (!context.Config.IsClearMode && context.State.BattleCount == 0) ||
           (this is Campaign154 && !context.Config.IsClearMode && context.State.BattleCount == 1) ||
           (this is Campaign153 && context.State.BattleCount == 3) ||
           (this is Campaign154 && context.State.BattleCount is (3 or 6));

    protected static async ValueTask<bool> FilterThenDefault(CampaignContext context, int preserve)
    {
        if (await context.Operations.ClearFilterEnemyAsync(Filter, preserve)) return true;
        return await context.Operations.ClearEnemyAsync();
    }
    protected static bool Accessible(CampaignContext context, string cell)
        => context.State[Cell.Parse(cell)].IsAccessible;
    protected static async ValueTask<bool> MoveThenChosen(CampaignContext context, string origin, string target)
    {
        if (!context.Config.IsClearMode) await context.Operations.MoveMobAsync(Cell.Parse(origin), Cell.Parse(target));
        if (!Accessible(context, target)) return false;
        return await context.Operations.ClearChosenEnemyAsync(Cell.Parse(target));
    }
    protected static async ValueTask<bool> ClearSiren(CampaignContext context, string cell)
        => await context.Operations.ClearChosenEnemyAsync(Cell.Parse(cell), MapCombatExpectation.Siren);
    protected static async ValueTask<bool> ClearBoss(CampaignContext context)
        => await context.Operations.ClearBossForFleetAsync(FleetRoles.BossIndex(context.Config));
    protected static ValueTask<bool> ClearBossDirect(CampaignContext context)
        => context.Operations.ClearBossAsync();
}

public sealed class Campaign151 : ChapterFifteenRule
{
    public override string Id => "campaign_main/campaign_15_1";
    public override string StageName => "15-1";
    public override CampaignConfiguration Configure(CampaignConfiguration input) => base.Configure(input) with { WalkUseCurrentFleet = true };
    public Campaign151() : base(new Dictionary<int, BattleHook> { [0] = Battle0, [1] = context => FilterThenDefault(context, 1),
        [5] = context => FilterThenDefault(context, 0), [6] = ClearBoss }) { }
    private static async ValueTask<bool> Battle0(CampaignContext context)
    {
        if (!context.Config.IsClearMode && await context.Operations.MoveMobAsync(Cell.Parse("B3"), Cell.Parse("C3")) &&
            Accessible(context, "B1"))
            return await context.Operations.ClearChosenEnemyAsync(Cell.Parse("B1"));
        return await FilterThenDefault(context, 1);
    }
    public override ImmutableArray<SourceFile> Sources => [BaseSource, ChapterSource,
        new("campaign/campaign_main/campaign_15_1.py", "0452e20056a855ff3dca609a8e250118174ee0e22be50fab15b7e5ba67a5c0aa")];
}

public sealed class Campaign152 : ChapterFifteenRule
{
    public override string Id => "campaign_main/campaign_15_2";
    public override string StageName => "15-2";
    public Campaign152() : base(new Dictionary<int, BattleHook> { [0] = Battle0, [1] = context => FilterThenDefault(context, 1),
        [5] = context => FilterThenDefault(context, 0), [6] = ClearBoss }) { }
    private static async ValueTask<bool> Battle0(CampaignContext context)
    {
        if (!context.Config.IsClearMode)
        {
            await context.Operations.MoveMobAsync(Cell.Parse("I6"), Cell.Parse("I7"));
            await context.Operations.MoveMobAsync(Cell.Parse("I7"), Cell.Parse("I8"));
            if (Accessible(context, "G7")) return await context.Operations.ClearChosenEnemyAsync(Cell.Parse("G7"));
        }
        return await FilterThenDefault(context, 1);
    }
    public override ImmutableArray<SourceFile> Sources => [BaseSource, ChapterSource,
        new("campaign/campaign_main/campaign_15_2.py", "b0d7072779ff5daab6c5c2df29740127504a0af5bbf770369b0258e3af225db1")];
}

public sealed class Campaign153 : ChapterFifteenRule
{
    public override string Id => "campaign_main/campaign_15_3";
    public override string StageName => "15-3";
    public Campaign153() : base(new Dictionary<int, BattleHook> { [0] = Battle0, [1] = context => FilterThenDefault(context, 1),
        [3] = context => ClearSiren(context, "H5"), [4] = context => FilterThenDefault(context, 1),
        [5] = context => FilterThenDefault(context, 0), [6] = ClearBoss }) { }
    private static async ValueTask<bool> Battle0(CampaignContext context)
    {
        if (!context.Config.IsClearMode && await context.Operations.MoveMobAsync(Cell.Parse("B3"), Cell.Parse("B4")) &&
            Accessible(context, "A1"))
            return await context.Operations.ClearChosenEnemyAsync(Cell.Parse("A1"));
        return await FilterThenDefault(context, 1);
    }
    public override ImmutableArray<SourceFile> Sources => [BaseSource, ChapterSource,
        new("campaign/campaign_main/campaign_15_3.py", "034a88b89d6af7f5ecb08fe70ab30f4d792daf03757174ec1579dc48d0107287")];
}

public class Campaign154 : ChapterFifteenRule
{
    public override string Id => "campaign_main/campaign_15_4";
    public override string StageName => "15-4";
    public Campaign154() : base(new Dictionary<int, BattleHook> { [0] = Battle0, [1] = Battle1, [2] = context => FilterThenDefault(context, 0),
        [3] = Battle3, [4] = Battle4, [6] = context => ClearSiren(context, "D3"),
        [7] = context => FilterThenDefault(context, 0), [8] = ClearBoss }) { }
    public override CampaignConfiguration Configure(CampaignConfiguration input) => base.Configure(input) with
    { SwipeMultipliers = new(new(1.055, 1.075), new(1.020, 1.039), new(.990, 1.008)) };
    private static async ValueTask<bool> Battle0(CampaignContext context)
    {
        if (!context.Config.IsClearMode && await context.Operations.MoveMobAsync(Cell.Parse("J8"), Cell.Parse("K8")) &&
            Accessible(context, "K9"))
            return await context.Operations.ClearChosenEnemyAsync(Cell.Parse("K9"));
        return await FilterThenDefault(context, 0);
    }
    protected static async ValueTask<bool> Battle1(CampaignContext context)
    {
        if (!context.Config.IsClearMode && Accessible(context, "A1"))
            return await context.Operations.ClearChosenEnemyAsync(Cell.Parse("A1"));
        return await FilterThenDefault(context, 0);
    }
    protected static async ValueTask<bool> Battle3(CampaignContext context)
    {
        if (!context.Config.IsClearMode)
        {
            int fleet = FleetRoles.BossIndex(context.Config);
            await context.Operations.SwitchFleetAsync(fleet);
            _ = await ClearSiren(context, "H5");
            await context.Operations.SwitchFleetAsync(1);
            return true;
        }
        await context.Operations.PickUpAmmoAsync();
        _ = await ClearSiren(context, "H5");
        return true;
    }
    protected static async ValueTask<bool> Battle4(CampaignContext context)
    {
        await context.Operations.PickUpAmmoAsync();
        return await FilterThenDefault(context, 0);
    }
    public override ImmutableArray<SourceFile> Sources => [BaseSource, ChapterSource,
        new("campaign/campaign_main/campaign_15_4.py", "72db3b288e44b6204f200eaac170e94548e4aa03d4e50933c4f9058d3513d137")];
}

public sealed class Campaign154121 : Campaign154
{
    public override string Id => "campaign_main/campaign_15_4" + "_121";
    public override string StageName => "15-4-121";
    protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; } = new Dictionary<int, BattleHook>
    { [0] = Battle0, [1] = Battle1, [2] = context => FilterThenDefault(context, 0), [3] = Battle3,
      [4] = context => FilterThenDefault(context, 0), [6] = Battle6, [7] = context => FilterThenDefault(context, 0),
      [8] = ClearBossDirect }.ToFrozenDictionary();
    private static async ValueTask<bool> Battle0(CampaignContext context)
    {
        if (!context.Config.IsClearMode && await context.Operations.MoveMobAsync(Cell.Parse("J8"), Cell.Parse("K8")) &&
            Accessible(context, "K9"))
            return await context.Operations.ClearChosenEnemyAsync(Cell.Parse("K9"));
        return await FilterThenDefault(context, 0);
    }
    private static new async ValueTask<bool> Battle1(CampaignContext context)
    {
        if (!context.Config.IsClearMode && Accessible(context, "A1"))
            return await context.Operations.ClearChosenEnemyAsync(Cell.Parse("A1"));
        return await FilterThenDefault(context, 0);
    }
    private static new async ValueTask<bool> Battle3(CampaignContext context)
    {
        await context.Operations.PickUpAmmoAsync();
        _ = await ClearSiren(context, "H5");
        return true;
    }
    private static async ValueTask<bool> Battle6(CampaignContext context)
    {
        int fleet = FleetRoles.BossIndex(context.Config);
        await context.Operations.SwitchFleetAsync(fleet);
        _ = await ClearSiren(context, "D3");
        await context.Operations.SwitchFleetAsync(1);
        return true;
    }
    public override ImmutableArray<SourceFile> Sources => [BaseSource, ChapterSource,
        new("campaign/campaign_main/campaign_15_4.py", "72db3b288e44b6204f200eaac170e94548e4aa03d4e50933c4f9058d3513d137"),
        new("campaign/campaign_main/campaign_15_4" + "_121.py", "4752e912c3621475e588e3c4cb69271807a774d6070212ff81a699b1eeee21f8")];
}
