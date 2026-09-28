using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tasks;

/// <summary>One independent C# stage-selection, fleet, entry and map-execution graph.</summary>
public sealed class CampaignRunTask : ITaskRunner
{
    public string Kind => "campaign_run";
    public bool RequiresActions => true;

    public void Validate(JsonObject? input)
    {
        TaskInput.Fields(input, "campaign", "fleet1", "fleet2", "submarine", "emotionMode", "fleetLock",
            "fleet1Formation", "fleet2Formation", "fleetOrder", "hpControl", "reachLevel", "retirement",
            "clearMode", "doubleBook", "mapAchievement", "stageIncrease", "ambushEvade", "submarineMode", "submarineDistanceToBoss");
        var id = input?["campaign"]?.GetValue<string>() ??
            throw new ArgumentException("Campaign run requires a compiled campaign rule");
        var rule = RuleCatalog.Create(id);
        if (rule.StageName is null)
            throw new NotSupportedException("Compiled campaign has no selectable stage");
        var plan = Plan(input);
        SubmarineRules.RequireSupported(rule.Configure(new() { Submarine = plan.Submarine,
            SubmarineMode = SubmarineModeInput(input!), SubmarineDistanceToBoss = SubmarineDistanceInput(input!) }));
        _ = EmotionInput.Mode(input!);
        if (input.ContainsKey("fleetLock") && input["fleetLock"] is null)
            throw new ArgumentException("Fleet lock setting cannot be null");
        if (input["fleetLock"] is not null) _ = input["fleetLock"]!.GetValue<bool>();
        _ = Formation(input, "fleet1Formation");
        _ = Formation(input, "fleet2Formation");
        _ = Order(input);
        _ = FleetHealthInput.Read(input);
        _ = FleetLevelInput.Read(input);
        _ = RetirementInput.Read(input);
        _ = Option(input, "clearMode", true);
        _ = Option(input, "doubleBook", false);
        _ = Achievement(input);
        _ = Option(input, "stageIncrease", false);
        _ = Option(input, "ambushEvade", true);
    }

    public IReadOnlyList<string> Preconditions(TaskRequest request, TaskCapabilities capabilities)
        => [.. capabilities.HasOcrModels ? Array.Empty<string>() : ["ocr_models"],
            .. !EmotionInput.Mode(request.Input!).Calculates() || capabilities.HasProfileStore ? Array.Empty<string>() : ["emotion_config"],
            .. Achievement(request.Input!) == MapAchievement.NonStop || capabilities.HasProfileStore ? Array.Empty<string>() : ["achievement_config"]];

    public async ValueTask<TaskResult> RunAsync(TaskRequest request, TaskContext context, CancellationToken token)
    {
        Validate(request.Input);
        var stages = context.Stages ?? throw new NotSupportedException("C# stage observation service is unavailable");
        var fleets = context.Fleets ?? throw new NotSupportedException("C# fleet preparation service is unavailable");
        var entry = context.Entry ?? throw new NotSupportedException("C# campaign entry service is unavailable");
        var mapPreparation = context.MapPreparation ?? throw new NotSupportedException("C# map preparation is unavailable");
        var campaign = context.Campaign ?? throw new NotSupportedException("C# campaign execution service is unavailable");
        var rule = RuleCatalog.Create(request.Input!["campaign"]!.GetValue<string>());
        var requestedPlan = Plan(request.Input);
        var requestedConfiguration = new CampaignConfiguration
        {
            EmotionMode = EmotionInput.Mode(request.Input),
            Fleet2 = requestedPlan.Second,
            Submarine = requestedPlan.Submarine,
            SubmarineMode = SubmarineModeInput(request.Input),
            SubmarineDistanceToBoss = SubmarineDistanceInput(request.Input),
            Fleet1Formation = Formation(request.Input, "fleet1Formation"),
            Fleet2Formation = Formation(request.Input, "fleet2Formation"),
            FleetOrder = Order(request.Input),
            Health = FleetHealthInput.Read(request.Input),
            Levels = FleetLevelInput.Read(request.Input),
            Retirement = RetirementInput.Read(request.Input),
            UseClearMode = Option(request.Input, "clearMode", true),
            UseDoubleBook = Option(request.Input, "doubleBook", false),
            MapAchievement = Achievement(request.Input),
            StageIncrease = Option(request.Input, "stageIncrease", false),
            AmbushEvade = Option(request.Input, "ambushEvade", true),
            UseFleetLock = request.Input["fleetLock"]?.GetValue<bool>() ?? true
        };
        // Config inheritance is authoritative. Apply it before touching the
        // fleet page so the device selection and map state use identical values.
        var effectiveConfiguration = CampaignObjectives.Apply(rule.Configure(requestedConfiguration));
        SubmarineRules.RequireSupported(effectiveConfiguration);
        var plan = requestedPlan with
        {
            Second = effectiveConfiguration.Fleet2,
            Submarine = effectiveConfiguration.Submarine,
            SubmarineMode = effectiveConfiguration.SubmarineMode,
            IsClearMode = effectiveConfiguration.IsClearMode
        };
        plan.Validate();
        var configuration = effectiveConfiguration;
        context.Interruptions?.Configure(configuration.Retirement, configuration.EmotionMode);

        var evidence = new JsonObject
        {
            ["campaign"] = rule.Id,
            ["requestedFleetPlan"] = JsonSerializer.SerializeToNode(requestedPlan, TaskQueue.Json),
            ["fleetPlan"] = JsonSerializer.SerializeToNode(plan, TaskQueue.Json),
            ["emotionMode"] = configuration.EmotionMode.Name(),
            ["fleetLockRequested"] = configuration.UseFleetLock,
            ["clearModeRequested"] = configuration.UseClearMode,
            ["doubleBookRequested"] = configuration.UseDoubleBook
        };
        evidence["fleet1Formation"] = CampaignStrategy.FormationName(configuration.Fleet1Formation);
        evidence["fleet2Formation"] = CampaignStrategy.FormationName(configuration.Fleet2Formation);
        evidence["fleetOrder"] = FleetRoles.Name(configuration.FleetOrder);
        evidence["mapAchievement"] = configuration.MapAchievement.Name();
        evidence["stageIncrease"] = configuration.StageIncrease;
        evidence["ambushEvade"] = configuration.AmbushEvade;
        evidence["submarineMode"] = configuration.SubmarineMode.Name();
        evidence["submarineDistanceToBoss"] = configuration.SubmarineDistanceToBoss;
        string phase = "achievement_binding";
        try
        {
            if (configuration.MapAchievement != MapAchievement.NonStop)
                await (context.Achievement ?? throw new NotSupportedException("Achievement persistence is unavailable"))
                    .PrepareAchievementAsync(rule, configuration, token);
            phase = "emotion";
            if (configuration.EmotionMode.Calculates())
            {
                var emotion = context.Emotion ?? throw new NotSupportedException("Emotion persistence is unavailable");
                var check = await emotion.PrepareAsync(configuration, rule.Map.ExpectedBattles, false, token)
                    ?? throw new InvalidDataException("Emotion entry check returned no evidence");
                evidence["emotion"] = JsonSerializer.SerializeToNode(check, TaskQueue.Json);
                if (check.DeferredUntil is not null)
                    return new(request.Id, Kind, TaskOutcome.Skipped, "emotion_recovery_required", evidence);
            }
            phase = "navigation";
            await context.Navigator.EnsureAsync("page_campaign", context.Timeout, token: token);
            phase = "stage_selection";
            var selection = await new CampaignStageSelector(context.Driver, stages).SelectAsync(rule.StageName!, token);
            evidence["selection"] = JsonSerializer.SerializeToNode(selection, TaskQueue.Json);
            phase = "map_state";
            var prepared = await mapPreparation.PrepareMapAsync(configuration, context.Timeout, token);
            evidence["mapPreparation"] = JsonSerializer.SerializeToNode(prepared, TaskQueue.Json);
            evidence["autoSearch"] = JsonSerializer.SerializeToNode(prepared.AutoSearch, TaskQueue.Json);
            configuration = CampaignObjectives.Apply(configuration with { IsClearMode = prepared.ClearMode, PreparationInfo = prepared.Info });
            evidence["clearAllThisTime"] = configuration.ClearAllThisTime;
            evidence["hasMapStory"] = configuration.HasMapStory;
            if (CampaignObjectives.Reached(configuration.MapAchievement, prepared.Info))
            {
                phase = "achievement_stop";
                var stopped = await context.Achievement!.StopForAchievementAsync(prepared.Info, context.Timeout, token);
                evidence["mapStop"] = JsonSerializer.SerializeToNode(stopped, TaskQueue.Json);
                return new(request.Id, Kind, TaskOutcome.Skipped, "map_achievement_reached", evidence);
            }
            phase = "map_preparation";
            await new CampaignPreparation(context.Driver, context.Interruptions).OpenFleetAsync(selection.Preparation, token);
            phase = "double_book";
            var book = await mapPreparation.PrepareDoubleBookAsync(configuration, context.Timeout, token);
            evidence["doubleBook"] = JsonSerializer.SerializeToNode(book, TaskQueue.Json);
            configuration = configuration with
            {
                IsDoubleBook = book.Enabled ?? throw new InvalidDataException("Double-book state was not confirmed")
            };
            phase = "fleet_setup";
            plan = plan with { IsClearMode = configuration.IsClearMode };
            evidence["fleetPlan"] = JsonSerializer.SerializeToNode(plan, TaskQueue.Json);
            var setup = await fleets.ConfigureFleetAsync(plan, context.Popups, token);
            evidence["fleetSetup"] = JsonSerializer.SerializeToNode(setup, TaskQueue.Json);
            configuration = configuration with { Submarine = setup.EffectiveSubmarine };
            phase = "map_entry";
            var entered = await entry.EnterFromFleetAsync(token);
            evidence["entry"] = JsonSerializer.SerializeToNode(entered, TaskQueue.Json);
            phase = "map_execution";
            var execution = await campaign.ResumeInMapAsync(rule, configuration, token);
            var result = CampaignResumeTask.Describe(request.Id, Kind, rule, execution, true);
            foreach (var item in evidence)
                result.Evidence![item.Key] = item.Value?.DeepClone();
            return result;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not OperationCanceledException)
        {
            evidence["failedPhase"] = phase;
            evidence["cleared"] = false;
            throw new TaskEvidenceException(phase, evidence, error);
        }
    }

    private static string SubmarineDistanceInput(JsonObject input) => input.TryGetPropertyValue("submarineDistanceToBoss", out var value)
        ? value?.GetValue<string>() ?? throw new ArgumentException("submarineDistanceToBoss cannot be null") : "2_grid_to_boss";

    private static SubmarineMode SubmarineModeInput(JsonObject input) => SubmarineRules.Parse(
        input.TryGetPropertyValue("submarineMode", out var value)
            ? value?.GetValue<string>() ?? throw new ArgumentException("submarineMode cannot be null") : "do_not_use");

    private static FleetFormation Formation(JsonObject input, string name)
        => CampaignStrategy.ParseFormation(input.TryGetPropertyValue(name, out var value)
            ? value?.GetValue<string>() ?? throw new ArgumentException(name + " cannot be null") : "double_line");

    internal static bool Option(JsonObject input, string name, bool fallback) => input.TryGetPropertyValue(name, out var value)
        ? value?.GetValue<bool>() ?? throw new ArgumentException(name + " cannot be null") : fallback;
    private static MapAchievement Achievement(JsonObject input) => CampaignObjectives.Parse(input.TryGetPropertyValue("mapAchievement", out var value)
        ? value?.GetValue<string>() ?? throw new ArgumentException("mapAchievement cannot be null") : "non_stop");

    private static FleetOrder Order(JsonObject input) => FleetRoles.Parse(input.TryGetPropertyValue("fleetOrder", out var value)
        ? value?.GetValue<string>() ?? throw new ArgumentException("fleetOrder cannot be null") : "fleet1_mob_fleet2_boss");

    private static FleetPlan Plan(JsonObject? input)
    {
        if (input?["fleet1"] is null || input["fleet2"] is null || input["submarine"] is null)
            throw new ArgumentException("Campaign run requires fleet1, fleet2 and submarine");
        var plan = new FleetPlan(input["fleet1"]!.GetValue<int>(), input["fleet2"]!.GetValue<int>(),
            input["submarine"]!.GetValue<int>());
        plan.Validate();
        return plan;
    }
}
