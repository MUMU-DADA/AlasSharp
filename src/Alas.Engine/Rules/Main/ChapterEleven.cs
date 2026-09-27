using System.Collections.Frozen;
using System.Collections.Immutable;
using Alas.Engine.Runtime;

namespace Alas.Engine.Rules.Main;

public abstract class ChapterElevenRule : ObservedBossRule
{
    protected static RoadDefinition Road(params string[] groups)
        => new(groups.Select(group => group.Split(' ').Select(Cell.Parse).ToImmutableArray()).ToImmutableArray());
    protected abstract ImmutableArray<RoadDefinition> Roads { get; }
    protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; }
    protected ChapterElevenRule() => Hooks = new Dictionary<int, BattleHook> { [0] = Battle0, [6] = Battle6 }.ToFrozenDictionary();
    public override CampaignConfiguration Configure(CampaignConfiguration input) => input with
    {
        Vision = (input.Vision ?? MapVisionOverrides.Default) with { InternalHough = 30, EdgeHough = 30, CoincidentEncourage = 1.2 }
    };
    protected virtual async ValueTask<bool> Battle0(CampaignContext context)
    {
        if (await context.Operations.ClearRoadblocksAsync(Roads)) return true;
        if (await context.Operations.ClearRoadblocksAsync(Roads, potential: true)) return true;
        return await BattleDefaultAsync(context);
    }
    protected virtual async ValueTask<bool> Battle6(CampaignContext context)
    {
        if (ObservedBossBlocked(context) && await context.Operations.ClearRoadblocksAsync(Roads)) return true;
        return await context.Operations.ClearBossForFleetAsync(FleetRoles.BossIndex(context.Config));
    }
}

public sealed class Campaign111 : ChapterElevenRule
{
    public override string Id => "campaign_main/campaign_11_1";
    public override string StageName => "11-1";
    public override ImmutableArray<SourceFile> Sources => [BaseSource,
        new("campaign/campaign_main/campaign_11_1.py", "94299261b5e91b968fc9b89727967db2a036afbeae2d9e3a40ba5ac4fcafdd4c")];
    protected override ImmutableArray<RoadDefinition> Roads { get; } = [Road("B2", "B3", "E5", "F4", "G4", "H3")];
}

public sealed class Campaign112 : ChapterElevenRule
{
    public override string Id => "campaign_main/campaign_11_2";
    public override string StageName => "11-2";
    public override ImmutableArray<SourceFile> Sources => [BaseSource,
        new("campaign/campaign_main/campaign_11_2.py", "f07e1889d4bfe2ae5e0b6d789715a4b2377a95616e9ce617006d37e648dccd8e")];
    public override CampaignConfiguration Configure(CampaignConfiguration input)
    {
        var config = base.Configure(input);
        return config with
        {
            Vision = config.Vision! with
            {
                Backend = GridDetectionBackend.Homography,
                Storage = new(new(6, 6), new(new(579.064, 82.271), new(1248.248, 82.271),
                    new(562.795, 616.581), new(1438.283, 616.581))),
                InternalPeaks = new(150, 231, .9, 10, 10, 35), EdgePeaks = new(231, 255, null, null, 10, 50, 1000)
            }
        };
    }
    protected override ImmutableArray<RoadDefinition> Roads { get; } = [Road("C6", "F5", "G5", "H4", "H6")];
    protected override async ValueTask<bool> Battle0(CampaignContext context)
    {
        await context.Operations.PushSecondFleetForwardAsync();
        return await base.Battle0(context);
    }
    public override ValueTask RefocusBossAsync(CampaignContext context) => context.Operations.RefocusBossAsync((-3, -2));
}

public sealed class Campaign113 : ChapterElevenRule
{
    public override string Id => "campaign_main/campaign_11_3";
    public override string StageName => "11-3";
    public override ImmutableArray<SourceFile> Sources => [BaseSource,
        new("campaign/campaign_main/campaign_11_3.py", "25afec92f537cfc4ad3be272b4029a50c13367c68015eae7b473828e6b278b62")];
    public override CampaignConfiguration Configure(CampaignConfiguration input)
    {
        var config = base.Configure(input);
        return config with
        {
            Vision = config.Vision! with
            {
                Backend = GridDetectionBackend.Homography,
                Storage = new(new(5, 4), new(new(133.207, 81.356), new(696.903, 81.356),
                    new(44.566, 406.051), new(705.278, 406.051))),
                InternalPeaks = new(150, 231, .9, 10, 10, 35), EdgePeaks = new(231, 255, 0, 10, 10, 50, 1000)
            }
        };
    }
    protected override ImmutableArray<RoadDefinition> Roads { get; } = [Road("C2", "D6", "F6", "G7")];
    protected override async ValueTask<bool> Battle6(CampaignContext context)
    {
        if (await context.Operations.ClearRoadblocksAsync(Roads)) return true;
        return await context.Operations.ClearBossForFleetAsync(FleetRoles.BossIndex(context.Config));
    }
}

public sealed class Campaign114 : ChapterElevenRule
{
    public override string Id => "campaign_main/campaign_11_4";
    public override string StageName => "11-4";
    public override ImmutableArray<SourceFile> Sources => [BaseSource,
        new("campaign/campaign_main/campaign_11_4.py", "ca512b06ba411af62cac35c0750f5f59c0f8e2eb738e6b31a7a15f9246b4d29a")];
    public override CampaignConfiguration Configure(CampaignConfiguration input) => input with
    {
        Vision = (input.Vision ?? MapVisionOverrides.Default) with
        {
            InternalHough = 35, EdgeHough = 35, CoincidentEncourage = 1.3, HomographyEdgeHough = 210,
            InternalPeaks = new(150, 231, .9, 10, 10, 35), EdgePeaks = new(231, 255, 0, 10, 10, 50, 1000)
        }
    };
    protected override ImmutableArray<RoadDefinition> Roads { get; } = [Road("C3", "H4", "C5 C7", "G6", "G8")];
    private static readonly ImmutableArray<Cell> StepOn = [Cell.Parse("C3")];
    protected override async ValueTask<bool> Battle0(CampaignContext context)
    {
        if (await context.Operations.PositionSecondFleetAsync(StepOn, Roads)) return true;
        return await base.Battle0(context);
    }
    protected override async ValueTask<bool> Battle6(CampaignContext context)
    {
        if (ObservedBossBlocked(context)) return await context.Operations.ClearRoadblocksAsync(Roads);
        return await context.Operations.ClearBossForFleetAsync(FleetRoles.BossIndex(context.Config));
    }
}
