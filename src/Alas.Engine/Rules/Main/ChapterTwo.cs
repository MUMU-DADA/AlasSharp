using System.Collections.Frozen;
using System.Collections.Immutable;
using Alas.Engine.Navigation;
using Alas.Engine.Runtime;

namespace Alas.Engine.Rules.Main;

/// <summary>Native campaign_2_base override and inherited campaign_2_1 Config.</summary>
public abstract class ChapterTwoRule : CampaignRule
{
    protected static readonly SourceFile BaseSource = new("module/campaign/campaign_base.py", "a575fc1a27d5888127139b027d0f0626cb93a3f29b6aab6dbff5060c04755b7f");
    protected static readonly SourceFile ChapterSource = new("campaign/campaign_main/campaign_2_base.py", "2a187ac121ddc2a37a27061a5ee96dad14f3ee1d9fd536097796822e5bec81ef");
    protected static readonly SourceFile ConfigurationSource = new("campaign/campaign_main/campaign_2_1.py", "94af491afe4d79abdf1938d80926cfce047e476f8552ee65aad2f4e370470c11");
    public override MapDefinition Map => CampaignMapCatalog.Get(Id).Map;
    public override CampaignConfiguration Configure(CampaignConfiguration input) => input with
    {
        BossFleet = 1,
        Vision = new(new(120, 206, 1.5, 10, 10, 35), new(206, 255, null, null, 10, 50, 1000),
            (75, 100), (0, 49), 40, 40, 180) { CoincidentEncourage = 1.5 }
    };
    public override async ValueTask<bool> AllowExperienceAsync(IUiDriver ui, CancellationToken token)
        => !await ui.AppearsAsync(UiAssets.Ui.CAMPAIGN_CHECK, ButtonOffset.Expand(30, 30), token: token);
    protected async ValueTask<bool> ClearMysteriesThenDefault(CampaignContext context)
    {
        await context.Operations.ClearMysteriesAsync();
        return await BattleDefaultAsync(context);
    }
    protected async ValueTask<bool> ClearMysteriesThenBoss(CampaignContext context, Cell boss)
    {
        await context.Operations.ClearMysteriesAsync();
        int fleet = FleetRoles.BossIndex(context.Config);
        if (!context.Operations.CheckAccessibility(boss, fleet)) return await BattleDefaultAsync(context);
        return await context.Operations.ClearBossForFleetAsync(fleet);
    }
}

public sealed class Campaign21 : ChapterTwoRule
{
    public override string Id => "campaign_main/campaign_2_1";
    public override string StageName => "2-1";
    public override ImmutableArray<SourceFile> Sources => [BaseSource, ChapterSource, ConfigurationSource];
    protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; }
    public Campaign21() => Hooks = new Dictionary<int, BattleHook>
    { [0] = ClearMysteriesThenDefault, [2] = context => ClearMysteriesThenBoss(context, Cell.Parse("D4")) }.ToFrozenDictionary();
    public override ValueTask RefocusBossAsync(CampaignContext context) => context.Operations.RefocusBossAsync((0, -2));
}
public sealed class Campaign22 : ChapterTwoRule
{
    public override string Id => "campaign_main/campaign_2_2";
    public override string StageName => "2-2";
    public override ImmutableArray<SourceFile> Sources => [BaseSource, ChapterSource, ConfigurationSource,
        new("campaign/campaign_main/campaign_2_2.py", "88f5bb8ccd102eef08cf4d38302b9addf5ef12492e84cf853d7973f5371baf67")];
    protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; }
    public Campaign22() => Hooks = new Dictionary<int, BattleHook>
    { [0] = ClearMysteriesThenDefault, [3] = context => ClearMysteriesThenBoss(context, Cell.Parse("D1")) }.ToFrozenDictionary();
}
public sealed class Campaign23 : ChapterTwoRule
{
    public override string Id => "campaign_main/campaign_2_3";
    public override string StageName => "2-3";
    public override ImmutableArray<SourceFile> Sources => [BaseSource, ChapterSource, ConfigurationSource,
        new("campaign/campaign_main/campaign_2_3.py", "637403236d3a874eaeab3b145d0c2c669d5c961773e3e55c1e237f69e1eae3f4")];
    protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; }
    public Campaign23() => Hooks = new Dictionary<int, BattleHook>
    { [0] = ClearMysteriesThenDefault, [3] = context => ClearMysteriesThenBoss(context, Cell.Parse("E1")) }.ToFrozenDictionary();
}
public sealed class Campaign24 : ChapterTwoRule
{
    public override string Id => "campaign_main/campaign_2_4";
    public override string StageName => "2-4";
    public override ImmutableArray<SourceFile> Sources => [BaseSource, ChapterSource, ConfigurationSource,
        new("campaign/campaign_main/campaign_2_4.py", "7bb3d0d6fff5606f59f200d5c239e6656fdf9240672cd0fe0b1e792ac3c05b9c")];
    private static readonly ImmutableArray<RoadDefinition> MainRoad = [new([[Cell.Parse("F2"), Cell.Parse("F1")], [Cell.Parse("F2"), Cell.Parse("E1")]])];
    public override CampaignConfiguration Configure(CampaignConfiguration input)
    {
        var config = base.Configure(input);
        return config with { Vision = config.Vision! with { MidHorizontal = new(118, 124), MidVertical = new(118, 124) } };
    }
    protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; }
    public Campaign24() => Hooks = new Dictionary<int, BattleHook> { [0] = Battle0, [3] = Battle3 }.ToFrozenDictionary();
    private async ValueTask<bool> Battle0(CampaignContext context)
    {
        if (await context.Operations.ClearRoadblocksAsync(MainRoad)) return true;
        if (await context.Operations.ClearRoadblocksAsync(MainRoad, potential: true)) return true;
        return await BattleDefaultAsync(context);
    }
    private async ValueTask<bool> Battle3(CampaignContext context)
    {
        int fleet = FleetRoles.BossIndex(context.Config);
        if (!context.Operations.CheckAccessibility(Cell.Parse("G1"), fleet))
        {
            if (await context.Operations.ClearRoadblocksAsync(MainRoad)) return true;
            return await BattleDefaultAsync(context);
        }
        return await context.Operations.ClearBossForFleetAsync(fleet);
    }
}
