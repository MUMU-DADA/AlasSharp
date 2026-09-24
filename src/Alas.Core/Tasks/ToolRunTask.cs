using System.Text.Json.Nodes;
using Alas.Runtime;

namespace Alas.Tasks;

/// <summary>Native standalone tools from get_available_func(), with normal queue artifacts.</summary>
public sealed class ToolRunTask : ITaskRunner
{
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
        { "task", "instance", "allow_actions", "confirm" };

    public string Kind => "tool_run";

    public IReadOnlyList<string> Preconditions(TaskRequest request, TaskContext context)
    {
        var problems = new List<string>();
        foreach (var field in request.Input?.Select(pair => pair.Key) ?? [])
            if (!Fields.Contains(field)) problems.Add($"未知工具执行字段: input.{field}");
        foreach (var field in new[] { "task", "instance", "confirm" })
            if (request.Input?[field] is not JsonValue value ||
                !value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
                problems.Add($"input.{field} 必须是非空字符串");
        if (request.Input?["allow_actions"] is not JsonValue allow ||
            !allow.TryGetValue<bool>(out var allowed) || !allowed)
            problems.Add("工具执行需要 input.allow_actions=true");
        if (context.Options.DryRun || !context.Options.AllowActions)
            problems.Add("会话未授权：执行工具需要 --run --allow-actions");
        return problems;
    }

    public TaskResult Run(TaskRequest request, TaskContext context, CancellationToken token)
    {
        var result = new TaskResult { Id = request.Id, Kind = Kind };
        // Keep the authorization guard even when called without TaskQueue.
        if (Preconditions(request, context) is { Count: > 0 } problems)
        {
            result.Outcome = TaskOutcome.Refused;
            result.Error = string.Join("; ", problems);
            return result;
        }
        if (token.IsCancellationRequested)
        {
            result.Outcome = TaskOutcome.Skipped;
            result.ErrorKind = RuntimeErrorKind.Cancelled;
            result.Error = "调用方取消：工具尚未开始";
            return result;
        }
        try
        {
            var run = context.Session.Vision.CallTyped<JsonObject>("tool_run", request.Input);
            result.Evidence = (JsonObject)run.DeepClone();
            // A native return is dispatch evidence, never a campaign-clear verdict.
            if (run["decision"]?.GetValue<string>() == "ran" &&
                run["native_success"]?.GetValue<bool>() == true &&
                run["constructed"]?.GetValue<bool>() == true && run["ran"]?.GetValue<bool>() == true &&
                run["task"]?.GetValue<string>() == request.Input!["task"]!.GetValue<string>() &&
                run["instance"]?.GetValue<string>() == request.Input!["instance"]!.GetValue<string>())
                result.Outcome = TaskOutcome.Succeeded;
            else
            {
                result.Outcome = TaskOutcome.Failed;
                result.ErrorKind = RuntimeErrorKind.UpstreamError;
                result.Error = run["error"]?.GetValue<string>() ?? run["reason"]?.GetValue<string>()
                    ?? "上游工具执行合同未确认成功";
            }
        }
        catch (Exception error)
        {
            var wrapped = RuntimeErrors.Wrap(error, "上游工具执行失败");
            result.Outcome = TaskOutcome.Failed;
            result.ErrorKind = wrapped.Kind;
            result.Error = wrapped.Message;
        }
        return result;
    }
}
