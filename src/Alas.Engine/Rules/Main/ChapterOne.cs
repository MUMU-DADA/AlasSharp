using System.Collections.Frozen;
using System.Collections.Immutable;
using Alas.Engine.Runtime;

namespace Alas.Engine.Rules.Main;

public abstract class ChapterOneRule : CampaignRule
{
    protected static readonly SourceFile BaseSource = new("module/campaign/campaign_base.py",
        "a575fc1a27d5888127139b027d0f0626cb93a3f29b6aab6dbff5060c04755b7f");
    protected static readonly SourceFile ConfigurationSource = new("campaign/campaign_main/campaign_1_1.py",
        "0d5cc633b3d0c9f2b96a03e901092c1104637ddef8af90c95985e0546699d370");
    public override CampaignConfiguration Configure(CampaignConfiguration input) => ChapterOneConfiguration.Apply(input);
}

public sealed class Campaign11 : ChapterOneRule
{
    public override string Id => "campaign_main/campaign_1_1";
    public override ImmutableArray<SourceFile> Sources => [BaseSource, ConfigurationSource];
    public override MapDefinition Map { get; } = new("G1", "SP -- -- -- -- ME MB",
        ["D1"], ["D1"], [new(0, Enemy: 1), new(1, Boss: 1)]);
    protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; }
    public Campaign11() => Hooks = new Dictionary<int, BattleHook>
    {
        [0] = BattleDefaultAsync, [1] = static c => c.Operations.ClearBossAsync()
    }.ToFrozenDictionary();
    public override ValueTask RefocusBossAsync(CampaignContext context) => context.Operations.RefocusBossAsync((-3, 0));
}

public sealed class Campaign12 : ChapterOneRule
{
    public override string Id => "campaign_main/campaign_1_2";
    public override ImmutableArray<SourceFile> Sources => [BaseSource, ConfigurationSource,
        new("campaign/campaign_main/campaign_1_2.py", "be2827f79338da75b042baca3b3f6e6b185da4bcee4e773ff9ba7207246d329a")];
    public override MapDefinition Map { get; } = new("E3", """
        SP -- ME Me MB
        -- ++ -- -- ++
        -- -- ME MM ++
        """, ["C1"], ["C1"], [new(0, Enemy: 1, Mystery: 1), new(1, Enemy: 1), new(2, Boss: 1)]);
    protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; }
    public Campaign12() => Hooks = new Dictionary<int, BattleHook> { [0] = Battle0, [2] = Battle2 }.ToFrozenDictionary();
    private async ValueTask<bool> Battle0(CampaignContext context)
    {
        await context.Operations.ClearMysteriesAsync();
        return await BattleDefaultAsync(context);
    }
    private static async ValueTask<bool> Battle2(CampaignContext context)
    {
        await context.Operations.ClearMysteriesAsync();
        return await context.Operations.ClearBossAsync();
    }
}

public sealed class Campaign13 : ChapterOneRule
{
    public override string Id => "campaign_main/campaign_1_3";
    public override ImmutableArray<SourceFile> Sources => [BaseSource, ConfigurationSource,
        new("campaign/campaign_main/campaign_1_3.py", "85c14c3159eb51a60329ccf12e62da81e86a76944e4942edcbb997744baa38ae")];
    public override MapDefinition Map { get; } = new("F3", """
        ++ ++ ++ -- MB --
        -- ME -- ME -- --
        SP -- ++ -- -- MM
        """, ["C1"], ["C1"], [new(0, Enemy: 1, Mystery: 1), new(1, Enemy: 1), new(2, Boss: 1)]);
    protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; }
    public Campaign13() => Hooks = new Dictionary<int, BattleHook> { [0] = Battle0, [2] = Battle2 }.ToFrozenDictionary();
    private async ValueTask<bool> Battle0(CampaignContext context)
    {
        await context.Operations.ClearMysteriesAsync();
        return await BattleDefaultAsync(context);
    }
    private static async ValueTask<bool> Battle2(CampaignContext context)
    {
        await context.Operations.ClearMysteriesAsync();
        return await context.Operations.ClearBossAsync();
    }
}

public sealed class Campaign14 : ChapterOneRule
{
    public override string Id => "campaign_main/campaign_1_4";
    public override ImmutableArray<SourceFile> Sources => [BaseSource, ConfigurationSource,
        new("campaign/campaign_main/campaign_1_4.py", "523e70ab86bd8b2f8fa580cfd64dac41913ac9b2e65d4c32fb638f6bc370369d")];
    public override MapDefinition Map { get; } = new("G3", """
        SP -- ME -- ++ ++ ++
        ++ ++ ME -- MA ++ ++
        ++ ++ ++ ME -- ME MB
        """, ["D1"], ["D1"], [new(0, Enemy: 2), new(1, Enemy: 1), new(2), new(3, Boss: 1)]);
    protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; }
    public Campaign14() => Hooks = new Dictionary<int, BattleHook>
    {
        [0] = BattleDefaultAsync, [3] = static c => c.Operations.ClearBossAsync()
    }.ToFrozenDictionary();
}
