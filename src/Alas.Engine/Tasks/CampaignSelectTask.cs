using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tasks;

/// <summary>Selects a compiled stage and confirms map preparation, without entering the map.</summary>
public sealed class CampaignSelectTask : ITaskRunner
{
    public string Kind => "campaign_select";
    public bool RequiresActions => true;
    public void Validate(JsonObject? input)
    {
        TaskInput.Fields(input, "campaign");
        string id = input?["campaign"]?.GetValue<string>() ??
            throw new ArgumentException("Campaign selection requires a compiled rule");
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
        var selection = await new CampaignStageSelector(context.Driver, stages).SelectAsync(rule.StageName!, token);
        var evidence = JsonSerializer.SerializeToNode(new
        {
            campaign = rule.Id, selection.Stage, selection.Chapter, selection.OcrFrameSequence,
            selection.Entrance, selection.Preparation,
            stageSelectionVerified = true, mapEntered = false, cleared = false
        }, TaskQueue.Json)!.AsObject();
        return new(request.Id, Kind, TaskOutcome.Succeeded, "map_preparation_observed", evidence);
    }
}
