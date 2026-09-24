using System.Text.Json.Nodes;
using Alas.Runtime;

namespace Alas.Tasks;

/// <summary>Owns the native loop's lifecycle and evidence; upstream owns all scheduling decisions.</summary>
public sealed class SchedulerRunTask : ITaskRunner
{
    public string Kind => "scheduler_run";

    public IReadOnlyList<string> Preconditions(TaskRequest request, TaskContext context)
    {
        var problems = new List<string>();
        foreach (string field in request.Input?.Select(pair => pair.Key) ?? [])
            if (field is not ("instance" or "allow_actions" or "confirm"))
                problems.Add($"未知调度器字段: input.{field}");
        if (context.Options.DryRun || !context.Options.AllowActions)
            problems.Add("调度器需要动作会话 --run --allow-actions");
        if (context.RunDirectory is null) problems.Add("调度器需要工件目录");
        if (!context.Options.ShouldConfigureDevice) problems.Add("调度器需要当前会话配置设备串号");
        if (request.Input?["instance"] is not JsonValue instanceValue ||
            !instanceValue.TryGetValue<string>(out var instance) || string.IsNullOrWhiteSpace(instance))
            problems.Add("调度器需要有效实例名");
        else if (request.Input?["confirm"] is not JsonValue confirm ||
                 !confirm.TryGetValue<string>(out var confirmed) || confirmed != instance)
            problems.Add("调度器确认必须与实例名一致");
        if (request.Input?["allow_actions"] is not JsonValue allow ||
            !allow.TryGetValue<bool>(out var allowed) || !allowed)
            problems.Add("调度器需要 allow_actions=true");
        return problems;
    }

    public TaskResult Run(TaskRequest request, TaskContext context, CancellationToken token)
    {
        var result = new TaskResult { Id = request.Id, Kind = Kind };
        if (Preconditions(request, context) is { Count: > 0 } problems)
        {
            result.Outcome = TaskOutcome.Refused;
            result.Error = string.Join("; ", problems);
            return result;
        }
        string directory = Path.Combine(context.RunDirectory!, "scheduler-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Exception? stopWriteError = null;
        using var stop = token.Register(() =>
        {
            try { File.WriteAllText(Path.Combine(directory, "stop.request"), "stop"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Interlocked.Exchange(ref stopWriteError, error);
                context.Log.Warn("scheduler", "停止标记写入失败；等待原生边界，不强杀任务");
            }
        });
        try
        {
            var args = (JsonObject)request.Input!.DeepClone();
            args["artifact_directory"] = directory;
            var run = context.Session.Vision.CallTyped<JsonObject>("scheduler_run", args);
            result.Evidence = (JsonObject)run.DeepClone();
            result.Evidence["artifacts"] = Path.GetFileName(directory);
            if (run["decision"]?.GetValue<string>() == "stopped" &&
                run["stop_observed"]?.GetValue<bool>() == true &&
                run["instance"]?.GetValue<string>() == request.Input["instance"]!.GetValue<string>() &&
                token.IsCancellationRequested && stopWriteError is null)
            {
                result.Outcome = TaskOutcome.Skipped;
                result.ErrorKind = RuntimeErrorKind.Cancelled;
                result.StopReason = "cancelled";
                result.Error = "调度器已在上游边界响应停止请求，已执行任务见工件";
            }
            else
            {
                result.Outcome = TaskOutcome.Failed;
                result.ErrorKind = RuntimeErrorKind.UpstreamError;
                result.Error = stopWriteError?.Message ?? run["error"]?.GetValue<string>() ??
                    run["reason"]?.GetValue<string>() ?? "调度器未确认正常边界停止";
            }
        }
        catch (Exception error)
        {
            var wrapped = RuntimeErrors.Wrap(error, "执行原生调度器失败");
            result.Outcome = TaskOutcome.Failed;
            result.ErrorKind = wrapped.Kind;
            result.Error = wrapped.Message;
            result.Evidence = new JsonObject { ["artifacts"] = Path.GetFileName(directory) };
        }
        return result;
    }
}
