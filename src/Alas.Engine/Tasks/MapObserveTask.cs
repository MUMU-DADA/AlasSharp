using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tasks;

/// <summary>Read-only detector inspection. Success proves a local observation, never sortie completion.</summary>
public sealed class MapObserveTask : ITaskRunner
{
    public string Kind => "map_observe";
    public bool RequiresActions => false;
    public void Validate(JsonObject? input)
    {
        TaskInput.Fields(input, "campaign");
        string id = input?["campaign"]?.GetValue<string>() ?? throw new ArgumentException("Map observation requires a compiled campaign rule");
        _ = RuleCatalog.Create(id);
    }
    public IReadOnlyList<string> Preconditions(TaskRequest request, TaskCapabilities capabilities) => [];
    public async ValueTask<TaskResult> RunAsync(TaskRequest request, TaskContext context, CancellationToken token)
    {
        Validate(request.Input);
        var service = context.Map ?? throw new NotSupportedException("Map observation service is unavailable");
        var rule = RuleCatalog.Create(request.Input!["campaign"]!.GetValue<string>());
        var observed = await service.ObserveMapAsync(rule, token);
        var geometry = observed.View.Geometry;
        var evidence = JsonSerializer.SerializeToNode(new
        {
            frame = observed.View.Frame.Sequence, campaign = request.Input["campaign"]!.GetValue<string>(),
            center = geometry.Center, center_offset = geometry.CenterOffset, shape = geometry.Shape,
            edges = geometry.Edges, swipe_scale = geometry.SwipeBase,
            cells = observed.Cells, geometry = geometry.Grids,
            global_position_verified = false, settlement_verified = false
        }, TaskQueue.Json)!.AsObject();
        return new(request.Id, Kind, TaskOutcome.Succeeded, "map-observed", evidence);
    }
}
