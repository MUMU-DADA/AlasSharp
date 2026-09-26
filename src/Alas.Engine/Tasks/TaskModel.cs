using System.Text.Json.Nodes;
using Alas.Engine.Navigation;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tasks;

public enum TaskOutcome { Succeeded, Failed, Skipped, Refused, DryRun }
public sealed record TaskRequest(string Id, string Kind, JsonObject? Input = null, bool Required = false,
    string[]? DependsOn = null, double TimeoutSeconds = 120);
public sealed record TaskResult(string Id, string Kind, TaskOutcome Outcome, string Reason,
    JsonObject? Evidence = null, string? Error = null, string[]? FailureFrames = null, double ElapsedSeconds = 0);
public sealed record TaskContext(IUiDriver Driver, IPageNavigator Navigator, IPopupHandler Popups, TimeSpan Timeout,
    IMapObservationService? Map = null);

/// <summary>Each business domain owns its input schema and completion evidence.</summary>
public interface ITaskRunner
{
    string Kind { get; }
    bool RequiresActions { get; }
    void Validate(JsonObject? input);
    IReadOnlyList<string> Preconditions(TaskRequest request, TaskCapabilities capabilities);
    ValueTask<TaskResult> RunAsync(TaskRequest request, TaskContext context, CancellationToken token);
}
public sealed record TaskCapabilities(bool AllowActions, bool HasOcrModels);

internal static class TaskInput
{
    public static void Fields(JsonObject? input, params string[] allowed)
    {
        if (input is not null && input.Any(p => !allowed.Contains(p.Key, StringComparer.Ordinal)))
            throw new ArgumentException("Unknown task input field");
    }
    public static bool Boolean(JsonObject? input, string name, bool fallback = false)
        => input?[name] is null ? fallback : input[name]!.GetValue<bool>();
}
