using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tasks;

/// <summary>Opens the fleet preparation page for a compiled stage; does not enter the map.</summary>
public sealed class CampaignFleetPreparationTask : ITaskRunner
{
    public string Kind => "campaign_fleet_prepare";
    public bool RequiresActions => true;
    public void Validate(JsonObject? input)
    {
        TaskInput.Fields(input, "campaign", "fleet1", "fleet2", "submarine");
        string id = input?["campaign"]?.GetValue<string>() ??
            throw new ArgumentException("Fleet preparation requires a compiled campaign rule");
        if (RuleCatalog.Create(id).StageName is null)
            throw new NotSupportedException("Compiled campaign has no stage entrance declaration");
        bool anyFleet = new[] { "fleet1", "fleet2", "submarine" }.Any(field => input?.ContainsKey(field) == true);
        if (anyFleet) ReadPlan(input);
    }
    public IReadOnlyList<string> Preconditions(TaskRequest request, TaskCapabilities capabilities)
        => capabilities.HasOcrModels ? [] : ["ocr_models"];
    public async ValueTask<TaskResult> RunAsync(TaskRequest request, TaskContext context, CancellationToken token)
    {
        Validate(request.Input);
        var stages = context.Stages ?? throw new NotSupportedException("C# stage observation service is unavailable");
        var rule = RuleCatalog.Create(request.Input!["campaign"]!.GetValue<string>());
        await context.Navigator.EnsureAsync("page_campaign", context.Timeout, token: token);
        var selected = await new CampaignStageSelector(context.Driver, stages).SelectAsync(rule.StageName!, token);
        await new CampaignPreparation(context.Driver).OpenFleetAsync(selected.Preparation, token);
        FleetSetupResult? setup = null;
        if (request.Input?.ContainsKey("fleet1") == true)
        {
            var service = context.Fleets ?? throw new NotSupportedException("C# fleet preparation service is unavailable");
            setup = await service.ConfigureFleetAsync(ReadPlan(request.Input), context.Popups, token);
        }
        var evidence = JsonSerializer.SerializeToNode(new
        {
            campaign = rule.Id, selected.Stage, selected.Chapter, selected.OcrFrameSequence,
            selected.Entrance, selected.Preparation,
            stageSelectionVerified = true, fleetPreparationObserved = true,
            fleetSelectionChecked = setup is not null, hardMode = setup?.HardMode,
            fleetSelectionChanged = setup?.Changed, submarineAvailable = setup?.SubmarineAvailable,
            effectiveSubmarine = setup?.EffectiveSubmarine,
            mapEntered = false, cleared = false
        }, TaskQueue.Json)!.AsObject();
        return new(request.Id, Kind, TaskOutcome.Succeeded,
            setup is null ? "fleet_preparation_observed" : "fleet_selection_checked", evidence);
    }

    private static FleetPlan ReadPlan(JsonObject? input)
    {
        if (input?["fleet1"] is null || input["fleet2"] is null || input["submarine"] is null)
            throw new ArgumentException("Fleet selection requires fleet1, fleet2 and submarine together");
        var plan = new FleetPlan(input["fleet1"]!.GetValue<int>(), input["fleet2"]!.GetValue<int>(),
            input["submarine"]!.GetValue<int>());
        plan.Validate();
        return plan;
    }
}
