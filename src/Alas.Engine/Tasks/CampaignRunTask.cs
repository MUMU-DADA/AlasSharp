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
            "fleet1Formation", "fleet2Formation", "fleetOrder", "hpControl", "reachLevel", "retirement");
        var id = input?["campaign"]?.GetValue<string>() ??
            throw new ArgumentException("Campaign run requires a compiled campaign rule");
        if (RuleCatalog.Create(id).StageName is null)
            throw new NotSupportedException("Compiled campaign has no selectable stage");
        _ = Plan(input);
        if (input?["emotionMode"]?.GetValue<string>() != "ignore")
            throw new NotSupportedException("Automatic campaign run requires explicit ignore emotion mode until calculation is ported");
        if (input.ContainsKey("fleetLock") && input["fleetLock"] is null)
            throw new ArgumentException("Fleet lock setting cannot be null");
        if (input["fleetLock"] is not null) _ = input["fleetLock"]!.GetValue<bool>();
        _ = Formation(input, "fleet1Formation");
        _ = Formation(input, "fleet2Formation");
        _ = Order(input);
        _ = FleetHealthInput.Read(input);
        _ = FleetLevelInput.Read(input);
        _ = RetirementInput.Read(input);
    }

    public IReadOnlyList<string> Preconditions(TaskRequest request, TaskCapabilities capabilities)
        => capabilities.HasOcrModels ? [] : ["ocr_models"];

    public async ValueTask<TaskResult> RunAsync(TaskRequest request, TaskContext context, CancellationToken token)
    {
        Validate(request.Input);
        var stages = context.Stages ?? throw new NotSupportedException("C# stage observation service is unavailable");
        var fleets = context.Fleets ?? throw new NotSupportedException("C# fleet preparation service is unavailable");
        var entry = context.Entry ?? throw new NotSupportedException("C# campaign entry service is unavailable");
        var autoSearch = context.AutoSearch ?? throw new NotSupportedException("C# auto-search preparation is unavailable");
        var campaign = context.Campaign ?? throw new NotSupportedException("C# campaign execution service is unavailable");
        var rule = RuleCatalog.Create(request.Input!["campaign"]!.GetValue<string>());
        var requestedPlan = Plan(request.Input);
        var requestedConfiguration = new CampaignConfiguration
        {
            EmotionMode = CampaignEmotionMode.Ignore,
            Fleet2 = requestedPlan.Second,
            Submarine = requestedPlan.Submarine,
            Fleet1Formation = Formation(request.Input, "fleet1Formation"),
            Fleet2Formation = Formation(request.Input, "fleet2Formation"),
            FleetOrder = Order(request.Input),
            Health = FleetHealthInput.Read(request.Input),
            Levels = FleetLevelInput.Read(request.Input),
            Retirement = RetirementInput.Read(request.Input),
            UseFleetLock = request.Input["fleetLock"]?.GetValue<bool>() ?? true
        };
        // Config inheritance is authoritative. Apply it before touching the
        // fleet page so the device selection and map state use identical values.
        var effectiveConfiguration = rule.Configure(requestedConfiguration);
        var plan = requestedPlan with
        {
            Second = effectiveConfiguration.Fleet2,
            Submarine = effectiveConfiguration.Submarine
        };
        plan.Validate();
        var configuration = effectiveConfiguration;
        context.Interruptions?.Configure(configuration.Retirement, configuration.EmotionMode);

        var evidence = new JsonObject
        {
            ["campaign"] = rule.Id,
            ["requestedFleetPlan"] = JsonSerializer.SerializeToNode(requestedPlan, TaskQueue.Json),
            ["fleetPlan"] = JsonSerializer.SerializeToNode(plan, TaskQueue.Json),
            ["emotionMode"] = "ignore",
            ["fleetLockRequested"] = configuration.UseFleetLock
        };
        evidence["fleet1Formation"] = CampaignStrategy.FormationName(configuration.Fleet1Formation);
        evidence["fleet2Formation"] = CampaignStrategy.FormationName(configuration.Fleet2Formation);
        evidence["fleetOrder"] = FleetRoles.Name(configuration.FleetOrder);
        string phase = "navigation";
        try
        {
            await context.Navigator.EnsureAsync("page_campaign", context.Timeout, token: token);
            phase = "stage_selection";
            var selection = await new CampaignStageSelector(context.Driver, stages).SelectAsync(rule.StageName!, token);
            evidence["selection"] = JsonSerializer.SerializeToNode(selection, TaskQueue.Json);
            phase = "auto_search";
            var manual = await autoSearch.EnsureManualAsync(token);
            evidence["autoSearch"] = JsonSerializer.SerializeToNode(manual, TaskQueue.Json);
            phase = "map_preparation";
            await new CampaignPreparation(context.Driver, context.Interruptions).OpenFleetAsync(selection.Preparation, token);
            phase = "fleet_setup";
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

    private static FleetFormation Formation(JsonObject input, string name)
        => CampaignStrategy.ParseFormation(input.TryGetPropertyValue(name, out var value)
            ? value?.GetValue<string>() ?? throw new ArgumentException(name + " cannot be null") : "double_line");

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
