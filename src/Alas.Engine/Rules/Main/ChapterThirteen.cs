using System.Collections.Frozen;
using System.Collections.Immutable;
using Alas.Engine.Runtime;

namespace Alas.Engine.Rules.Main;

public abstract class ChapterThirteenRule : CampaignRule
{
    protected static readonly SourceFile BaseSource = new("module/campaign/campaign_base.py",
        "a575fc1a27d5888127139b027d0f0626cb93a3f29b6aab6dbff5060c04755b7f");
    public override MapDefinition Map => CampaignMapCatalog.Get(Id).Map;
    protected static readonly SourceFile ConfigSource = new("campaign/campaign_main/campaign_13_1.py",
        "88a50a808d86eec6364ebdc01665ec26ecda9ba9435a459e91269b5efb5735d3");
    private static readonly EnemyFilter Filter = new("1L > 1M > 2L > 2M > 3L > 2E > 3E > 2C > 3C > 3M");
    public override CampaignConfiguration Configure(CampaignConfiguration input) => input with
    {
        Vision = (input.Vision ?? MapVisionOverrides.Default) with
        {
            InternalPeaks = new(120, 206, 1.5, 10, 10, 35), EdgePeaks = new(206, 255, null, null, 10, 50, 1000),
            Canny = (75, 100), EdgeColor = (0, 49), HomographyEdgeHough = 210
        },
        SwipeMultipliers = new(new(.994, 1.013), new(.961, .979), new(.933, .950))
    };
    protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; }
    protected ChapterThirteenRule() => Hooks = new Dictionary<int, BattleHook>
    { [0] = Battle0, [5] = Battle5, [6] = SelectedBoss }.ToFrozenDictionary();
    protected virtual ValueTask<bool> Battle0(CampaignContext context) => FilterThenDefault(context, 1);
    protected virtual ValueTask<bool> Battle5(CampaignContext context) => FilterThenDefault(context, 0);
    protected async ValueTask<bool> FilterThenDefault(CampaignContext context, int preserve)
    {
        if (await context.Operations.ClearFilterEnemyAsync(Filter, preserve)) return true;
        return await BattleDefaultAsync(context);
    }
    protected static ValueTask<bool> SelectedBoss(CampaignContext context)
        => context.Operations.ClearBossForFleetAsync(FleetRoles.BossIndex(context.Config));
}

public sealed class Campaign131 : ChapterThirteenRule
{
    public override string Id => "campaign_main/campaign_13_1";
    public override string StageName => "13-1";
    public override ImmutableArray<SourceFile> Sources => [BaseSource, ConfigSource];
}

public sealed class Campaign132 : ChapterThirteenRule
{
    public override string Id => "campaign_main/campaign_13_2";
    public override string StageName => "13-2";
    public override ImmutableArray<SourceFile> Sources => [BaseSource, ConfigSource,
        new("campaign/campaign_main/campaign_13_2.py", "58cdac495f06717c4492f94a40335b738000612f9f766cbf78e8c256e2c3dd69")];
}

public sealed class Campaign133 : ChapterThirteenRule
{
    public override string Id => "campaign_main/campaign_13_3";
    public override string StageName => "13-3";
    public override ImmutableArray<SourceFile> Sources => [BaseSource, ConfigSource,
        new("campaign/campaign_main/campaign_13_3.py", "a132acba4547798e1aafbaae3320d1a89ec7cd909aa1d19175d8db9127b4ec54")];
    protected override async ValueTask<bool> Battle0(CampaignContext context)
    {
        if (await context.Operations.ClearSirenAsync()) return true;
        return await base.Battle0(context);
    }
    protected override async ValueTask<bool> Battle5(CampaignContext context)
    {
        if (await context.Operations.ClearSirenAsync()) return true;
        return await base.Battle5(context);
    }
}

public sealed class Campaign134 : ChapterThirteenRule
{
    public override string Id => "campaign_main/campaign_13_4";
    public override string StageName => "13-4";
    public override ImmutableArray<SourceFile> Sources => [BaseSource, ConfigSource,
        new("campaign/campaign_main/campaign_13_4.py", "56aa89ec8e1eb398228b5efb46d37286922def8f2bd80cce56db7b16651f894e")];
    public override CampaignConfiguration Configure(CampaignConfiguration input)
    {
        var config = base.Configure(input);
        return config with { Vision = config.Vision! with
        {
            InternalHough = 40, EdgeHough = 40, CoincidentEncourage = 1.5,
            InternalPeaks = new(150, 231, .9, 10, 10, 35), EdgePeaks = new(231, 255, 0, 10, 10, 50, 1000)
        } };
    }
    protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; }
    public Campaign134() => Hooks = new Dictionary<int, BattleHook> { [0] = Battle0, [3] = Battle3, [7] = SelectedBoss }.ToFrozenDictionary();
    protected override ValueTask<bool> Battle0(CampaignContext context) => FilterThenDefault(context, 0);
    private async ValueTask<bool> Battle3(CampaignContext context)
    {
        await context.Operations.PickUpAmmoAsync();
        return await FilterThenDefault(context, 0);
    }
}
