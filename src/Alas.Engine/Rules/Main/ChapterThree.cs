using System.Collections.Frozen;
using System.Collections.Immutable;
using Alas.Engine.Navigation;
using Alas.Engine.Runtime;

namespace Alas.Engine.Rules.Main;

/// <summary>Native campaign_3_base and inherited campaign_3_1 Config; hooks remain compiled methods.</summary>
public abstract class ChapterThreeRule : CampaignRule
{
    protected static readonly SourceFile BaseSource = new("module/campaign/campaign_base.py", "a575fc1a27d5888127139b027d0f0626cb93a3f29b6aab6dbff5060c04755b7f");
    protected static readonly SourceFile ChapterSource = new("campaign/campaign_main/campaign_3_base.py", "f46a8ae43eb2469af165850753066a4f79c36216e8173253a69dfcc9d9f41757");
    protected static readonly SourceFile ConfigurationSource = new("campaign/campaign_main/campaign_3_1.py", "1eba6a7956ce5c92068b9f5fa36ed1adb90f8dd284d81ae1211b179c12a400bc");
    public override MapDefinition Map => CampaignMapCatalog.Get(Id).Map;
    public override CampaignConfiguration Configure(CampaignConfiguration input) => input with
    {
        BossFleet = 1,
        MysteryHasCarrier = true,
        Vision = new(new(120, 206, 1.5, 10, 10, 35), new(206, 255, null, null, 10, 50, 1000),
            (75, 100), (0, 49), 40, 75, 180)
    };
    public override async ValueTask<bool> AllowExperienceAsync(IUiDriver ui, CancellationToken token)
        => !await ui.AppearsAsync(UiAssets.Ui.CAMPAIGN_CHECK, ButtonOffset.Expand(30, 30), token: token);

    protected async ValueTask<bool> AdvanceRescueThenDefault(CampaignContext context, Cell boss, bool mysteries)
    {
        await context.Operations.PushSecondFleetForwardAsync();
        if (await context.Operations.RescueSecondFleetAsync(boss)) return true;
        if (mysteries) await context.Operations.ClearMysteriesAsync();
        return await BattleDefaultAsync(context);
    }
    protected async ValueTask<bool> ClearChapterBoss(CampaignContext context, Cell boss, bool mysteries, bool defaultOnBossFleet = false)
    {
        if (mysteries) await context.Operations.ClearMysteriesAsync();
        int fleet = FleetRoles.BossIndex(context.Config);
        if (!context.Operations.CheckAccessibility(boss, fleet))
        {
            if (defaultOnBossFleet) await context.Operations.SwitchFleetAsync(fleet);
            return await BattleDefaultAsync(context);
        }
        return await context.Operations.ClearBossForFleetAsync(fleet);
    }
}

public sealed class Campaign31 : ChapterThreeRule
{
    public override string Id => "campaign_main/campaign_3_1";
    public override string StageName => "3-1";
    public override ImmutableArray<SourceFile> Sources => [BaseSource, ChapterSource, ConfigurationSource];
    protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; }
    public Campaign31() => Hooks = new Dictionary<int, BattleHook>
    {
        [0] = context => AdvanceRescueThenDefault(context, Cell.Parse("G2"), mysteries: true),
        [3] = context => ClearChapterBoss(context, Cell.Parse("G2"), mysteries: true, defaultOnBossFleet: true)
    }.ToFrozenDictionary();
}
public sealed class Campaign32 : ChapterThreeRule
{
    public override string Id => "campaign_main/campaign_3_2";
    public override string StageName => "3-2";
    public override ImmutableArray<SourceFile> Sources => [BaseSource, ChapterSource, ConfigurationSource,
        new("campaign/campaign_main/campaign_3_2.py", "9c9ac6adde417727990e7774fc2495cee1c228e38750169cc3a2e6a6a967a299")];
    protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; }
    public Campaign32() => Hooks = new Dictionary<int, BattleHook>
    {
        [0] = context => AdvanceRescueThenDefault(context, Cell.Parse("H1"), mysteries: true),
        [3] = context => ClearChapterBoss(context, Cell.Parse("H1"), mysteries: true)
    }.ToFrozenDictionary();
}
public sealed class Campaign33 : ChapterThreeRule
{
    public override string Id => "campaign_main/campaign_3_3";
    public override string StageName => "3-3";
    public override ImmutableArray<SourceFile> Sources => [BaseSource, ChapterSource, ConfigurationSource,
        new("campaign/campaign_main/campaign_3_3.py", "67d9d8f5f646fb1a53189b1d2f8ee5e18f062e6d7bff71eba7b854f29bea03f2")];
    public override CampaignConfiguration Configure(CampaignConfiguration input)
    {
        var config = base.Configure(input);
        return config with { Vision = config.Vision! with { HomographyEdgeHough = 210 } };
    }
    protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; }
    public Campaign33() => Hooks = new Dictionary<int, BattleHook>
    {
        [0] = context => AdvanceRescueThenDefault(context, Cell.Parse("A4"), mysteries: true),
        [3] = context => ClearChapterBoss(context, Cell.Parse("A4"), mysteries: true)
    }.ToFrozenDictionary();
}
public sealed class Campaign34 : ChapterThreeRule
{
    public override string Id => "campaign_main/campaign_3_4";
    public override string StageName => "3-4";
    public override ImmutableArray<SourceFile> Sources => [BaseSource, ChapterSource, ConfigurationSource,
        new("campaign/campaign_main/campaign_3_4.py", "b5c1c1959ba249cd964ed1ff6146deb77bbf64b6ac8f4114ec5336c8fd5d2db4")];
    public override CampaignConfiguration Configure(CampaignConfiguration input)
    {
        var config = base.Configure(input);
        return config with { MysteryHasCarrier = false, Vision = config.Vision! with
        {
            InternalPeaks = new(120, 180, .9, 10, 10, 35),
            EdgePeaks = new(215, 255, null, null, 10, 50, 1000)
        } };
    }
    protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; }
    public Campaign34() => Hooks = new Dictionary<int, BattleHook>
    {
        [0] = context => AdvanceRescueThenDefault(context, Cell.Parse("H3"), mysteries: false),
        [3] = context => ClearChapterBoss(context, Cell.Parse("H3"), mysteries: false)
    }.ToFrozenDictionary();
}
