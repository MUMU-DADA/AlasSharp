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
        TaskInput.Fields(input, "campaign");
        string id = input?["campaign"]?.GetValue<string>() ??
            throw new ArgumentException("Fleet preparation requires a compiled campaign rule");
        if (RuleCatalog.Create(id).StageName is null)
            throw new NotSupportedException("Compiled campaign has no stage entrance declaration");
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
        var evidence = JsonSerializer.SerializeToNode(new
        {
            campaign = rule.Id, selected.Stage, selected.Chapter, selected.OcrFrameSequence,
            selected.Entrance, selected.Preparation,
            stageSelectionVerified = true, fleetPreparationObserved = true,
            mapEntered = false, cleared = false
        }, TaskQueue.Json)!.AsObject();
        return new(request.Id, Kind, TaskOutcome.Succeeded, "fleet_preparation_observed", evidence);
    }
}
