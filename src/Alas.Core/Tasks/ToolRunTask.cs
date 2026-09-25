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
            var arguments = (JsonObject)request.Input!.DeepClone();
            arguments["device_configured"] = context.Options.ShouldConfigureDevice;
            var run = context.Session.Vision.CallTyped<JsonObject>("tool_run", arguments);
            result.Evidence = (JsonObject)run.DeepClone();
            // A native return is dispatch evidence, never a campaign-clear verdict.
            if (Text(run, "decision") == "ran")
            {
                var violations = SuccessfulDispatchViolations(run,
                    request.Input!["task"]!.GetValue<string>(), request.Input!["instance"]!.GetValue<string>());
                if (violations.Count == 0)
                    result.Outcome = TaskOutcome.Succeeded;
                else
                {
                    result.Outcome = TaskOutcome.Failed;
                    result.ErrorKind = RuntimeErrorKind.ContractViolation;
                    result.Error = $"原生工具成功响应不一致: {string.Join(", ", violations)}";
                    if (Text(run, "error") is { Length: > 0 } error) result.Error += $"; {error}";
                    result.Evidence["response_violations"] = new JsonArray(violations
                        .Select(field => (JsonNode)JsonValue.Create(field)!).ToArray());
                }
            }
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

    private static string? Text(JsonObject? value, string key) =>
        value?[key] is JsonValue node && node.TryGetValue<string>(out var text) ? text : null;

    private static bool IsTrue(JsonObject? value, string key) =>
        value?[key] is JsonValue node && node.TryGetValue<bool>(out var flag) && flag;

    private static List<string> SuccessfulDispatchViolations(JsonObject run, string task, string instance)
    {
        var violations = new List<string>();
        if (Text(run, "task") != task) violations.Add("task");
        if (Text(run, "instance") != instance) violations.Add("instance");
        foreach (var field in new[] { "allow_actions", "confirm_matches", "constructed", "ran", "native_success" })
            if (!IsTrue(run, field)) violations.Add(field);

        // The native registry owns the method mapping; never maintain a C# tool table.
        var plan = run["plan"] as JsonObject;
        if (Text(plan, "task") != task) violations.Add("plan.task");
        if (!IsTrue(plan, "found")) violations.Add("plan.found");
        if (!IsTrue(plan, "skip_first_screenshot")) violations.Add("plan.skip_first_screenshot");
        string? method = Text(plan, "method");
        if (string.IsNullOrWhiteSpace(method)) violations.Add("plan.method");
        var target = run["target"] as JsonObject;
        if (Text(target, "module") != "alas") violations.Add("target.module");
        if (Text(target, "class") != "AzurLaneAutoScript") violations.Add("target.class");
        if (string.IsNullOrWhiteSpace(Text(target, "method")) || Text(target, "method") != method)
            violations.Add("target.method");
        if (run["error"] is not null && (Text(run, "error") is not { } error || !string.IsNullOrWhiteSpace(error)))
            violations.Add("error");
        return violations;
    }
}
