using System.Text.Json.Nodes;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Tasks;

public sealed class ObserveTask : ITaskRunner
{
    public string Kind => "observe";
    public bool RequiresActions => false;
    public void Validate(JsonObject? input) => TaskInput.Fields(input);
    public IReadOnlyList<string> Preconditions(TaskRequest request, TaskCapabilities capabilities) => [];
    public async ValueTask<TaskResult> RunAsync(TaskRequest request, TaskContext context, CancellationToken token)
    {
        var pages = await new PageObserver(context.Driver, UpstreamPages.Create()).ObserveAsync(token);
        return new(request.Id, Kind, TaskOutcome.Succeeded, "observed", new JsonObject
        { ["pages"] = new JsonArray(pages.Select(p => JsonValue.Create(p)).ToArray()), ["meaning"] = "single_frame_observation" });
    }
}
public sealed class NavigateTask : ITaskRunner
{
    public string Kind => "navigate";
    public bool RequiresActions => true;
    public void Validate(JsonObject? input)
    {
        TaskInput.Fields(input, "page");
        string page = input?["page"]?.GetValue<string>() ?? throw new ArgumentException("Navigation requires page");
        if (UpstreamPages.Create()[page].Check is null) throw new ArgumentException("Destination has no checker");
    }
    public IReadOnlyList<string> Preconditions(TaskRequest request, TaskCapabilities capabilities) => [];
    public async ValueTask<TaskResult> RunAsync(TaskRequest request, TaskContext context, CancellationToken token)
    {
        var observed = await context.Navigator.EnsureAsync(request.Input!["page"]!.GetValue<string>(), context.Timeout, token: token);
        return new(request.Id, Kind, TaskOutcome.Succeeded, "destination_observed", new JsonObject
        { ["page"] = observed.Page, ["switched"] = observed.Switched, ["meaning"] = "destination_page_observed" });
    }
}
