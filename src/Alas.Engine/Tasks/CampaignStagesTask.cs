using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Rules;

namespace Alas.Engine.Tasks;

/// <summary>Read-only chapter page observation; stage names do not establish map entry.</summary>
public sealed class CampaignStagesTask : ITaskRunner
{
    public string Kind => "campaign_stages";
    public bool RequiresActions => false;
    public void Validate(JsonObject? input) { _ = Kinds(input); }
    public IReadOnlyList<string> Preconditions(TaskRequest request, TaskCapabilities capabilities)
        => capabilities.HasOcrModels ? [] : ["ocr_models"];

    public async ValueTask<TaskResult> RunAsync(TaskRequest request, TaskContext context, CancellationToken token)
    {
        var kinds = Kinds(request.Input);
        var service = context.Stages ?? throw new NotSupportedException("C# stage observation service is unavailable");
        var stages = await service.ObserveStagesAsync(kinds, token);
        var evidence = JsonSerializer.SerializeToNode(new
        {
            stages.FrameSequence, stages.Chapter, stages.Readings,
            campaignIdentityVerified = false, mapEntered = false
        }, TaskQueue.Json)!.AsObject();
        return new(request.Id, Kind, TaskOutcome.Succeeded, "stage_names_observed", evidence);
    }

    private static StageEntranceKind Kinds(JsonObject? input)
    {
        TaskInput.Fields(input, "entrances");
        if (input?.ContainsKey("entrances") != true) return StageEntranceKind.Normal;
        if (input["entrances"] is not JsonArray values || values.Count == 0)
            throw new ArgumentException("Stage entrances must be a nonempty array");
        StageEntranceKind kinds = 0;
        foreach (var value in values)
            kinds |= value?.GetValue<string>() switch
            {
                "normal" => StageEntranceKind.Normal,
                "half" => StageEntranceKind.Half,
                "blue" => StageEntranceKind.Blue,
                "green" => StageEntranceKind.Green,
                "20240725" => StageEntranceKind.Event20240725,
                _ => throw new ArgumentException("Unknown upstream stage entrance kind")
            };
        return kinds;
    }
}
