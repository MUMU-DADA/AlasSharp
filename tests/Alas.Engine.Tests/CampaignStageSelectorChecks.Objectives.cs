using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static partial class CampaignStageSelectorChecks
{
    public static async Task ObjectiveTaskChecksAsync()
    {
        var runner = new CampaignRunTask();
        var input = new JsonObject { ["campaign"] = "campaign_main/campaign_1_1", ["fleet1"] = 1,
            ["fleet2"] = 0, ["submarine"] = 0, ["emotionMode"] = "ignore", ["mapAchievement"] = "map_3_stars" };
        var request = new TaskRequest("goal", runner.Kind, input);
        Check(runner.Preconditions(request, new(true, true)).SequenceEqual(["achievement_config"]), "Achievement bypassed binding precondition");
        foreach (string key in new[] { "mapAchievement", "stageIncrease" })
        {
            var malformed = input.DeepClone().AsObject(); malformed[key] = null;
            await Throws<ArgumentException>(() => { runner.Validate(malformed); return Task.CompletedTask; }, "Null objective option accepted");
        }
        foreach (bool reached in new[] { false, true })
        foreach (bool fail in new[] { false, true })
        {
            var driver = new Driver { Chapter = 1 };
            var fleet = new FleetService(); var navigator = new Navigator();
            var campaign = new ObjectiveCampaign(); var achievement = new ObjectiveService { Fail = fail };
            var info = new CampaignMapInfo(1, .99, true, true, reached, true, true);
            try
            {
                var result = await runner.RunAsync(request, new(driver, navigator, null!, TimeSpan.FromSeconds(60),
                    Campaign: campaign, Stages: new Stages(driver), Fleets: fleet, Entry: new EntryService(driver),
                    MapPreparation: new PreparationService { Info = info }, Achievement: achievement), default);
                Check(achievement.Prepared && achievement.Stopped == reached && (campaign.Configuration is null) == reached,
                    "Achievement decision failed to bypass sortie execution");
                if (reached)
                    Check(result is { Outcome: TaskOutcome.Skipped, Reason: "map_achievement_reached" } &&
                        !driver.ClickedAssets.Contains("MAP_PREPARATION") && result.Evidence?["sortie"] is null,
                        "Reached objective entered map or claimed sortie settlement");
                else
                    Check(campaign.Configuration is { ClearAllThisTime: true, HasMapStory: false } &&
                        result.Evidence?["clearAllThisTime"]?.GetValue<bool>() == true,
                        "Missing star did not reach full-clear campaign dispatch");
            }
            catch (TaskEvidenceException error) when (reached && fail)
            {
                Check(error.Phase == "achievement_stop" && error.InnerException is IOException &&
                    !driver.ClickedAssets.Contains("MAP_PREPARATION") && campaign.Configuration is null,
                    "Stop failure entered sortie or lost phase evidence");
            }
        }
        // Chapter defaults are applied again at execution; preparation observations remain authoritative.
        var execution = new CampaignExecution(new ObjectiveRule(), new()
        {
            MapAchievement = MapAchievement.ThreeStars, PreparationInfo = new(1, .99, true, false, false, false, true)
        }, (ICampaignOperations)null!);
        Check(execution.Context.Config is { ClearAllThisTime: true, HasMapStory: false }, "Repeated chapter Config erased observed objectives");
    }
    private sealed class ObjectiveCampaign : ICampaignExecutionService
    {
        public CampaignConfiguration? Configuration { get; private set; }
        public ValueTask<CampaignResumeResult> ResumeInMapAsync(CampaignRule rule, CampaignConfiguration configuration, CancellationToken token)
        { Configuration = configuration; return ValueTask.FromResult(new CampaignResumeResult(CampaignLoopExit.Exhausted, 0, null)); }
    }
    private sealed class ObjectiveService : ICampaignAchievementService
    {
        public bool Prepared { get; private set; }
        public bool Stopped { get; private set; }
        public bool Fail { get; init; }
        public ValueTask PrepareAchievementAsync(CampaignRule rule, CampaignConfiguration configuration, CancellationToken token)
        { Prepared = true; return ValueTask.CompletedTask; }
        public ValueTask<CampaignStopEvidence> StopForAchievementAsync(CampaignMapInfo info, TimeSpan timeout, CancellationToken token)
        {
            Check(Prepared, "Stop preceded binding"); Stopped = true;
            if (Fail) throw new IOException("Synthetic stop failure");
            return ValueTask.FromResult(new CampaignStopEvidence("campaign_main/campaign_1_1", "map_3_stars", info,
                1, 2, Disabled: true, Persisted: true));
        }
    }
    private sealed class ObjectiveRule : CampaignRule
    {
        public override string Id => "test/objective";
        public override MapDefinition Map => new("A1", "SP", [], [], []);
        public override ImmutableArray<SourceFile> Sources => [];
        protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; } = new Dictionary<int, BattleHook>();
        public override CampaignConfiguration Configure(CampaignConfiguration input) => input with { HasMapStory = true, ClearAllThisTime = false, PreparationInfo = null };
    }
}
