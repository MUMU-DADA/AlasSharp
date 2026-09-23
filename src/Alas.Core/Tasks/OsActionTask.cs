using System.Text.Json.Nodes;
using Alas.Runtime;

namespace Alas.Tasks;

/// <summary>
/// 大世界动作的任务入口。目标由上游 Scheduler.Command 解析，执行仍走原生调度器。
/// `succeeded` 只表示原生任务返回成功，不单独证明海域目标或战斗结算。
/// </summary>
public sealed class OsActionTask : ITaskRunner
{
    private readonly PeriodicRunTask _dispatcher = new();

    public string Kind => "os_action";

    public IReadOnlyList<string> Preconditions(TaskRequest request, TaskContext context)
        => _dispatcher.Preconditions(request, context);

    public TaskResult Run(TaskRequest request, TaskContext context, CancellationToken token)
    {
        var result = new TaskResult { Id = request.Id, Kind = Kind };
        string task = request.Input?["task"]?.GetValue<string>() ?? "";
        try
        {
            token.ThrowIfCancellationRequested();
            var plan = context.Session.Vision.CallTyped<PeriodicPlanResult>(
                "periodic_plan", new { task });
            var planEvidence = new JsonObject
            {
                ["task"] = task,
                ["found"] = plan.Found,
                ["scheduler_command"] = plan.SchedulerCommand,
                ["method"] = plan.Method,
                ["lineno"] = plan.LineNumber,
                ["error"] = plan.Error,
            };
            if (plan.Found != true || plan.Method is null
                || !plan.Method.StartsWith("opsi_", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(plan.SchedulerCommand))
            {
                result.Outcome = TaskOutcome.Failed;
                result.ErrorKind = RuntimeErrorKind.Internal;
                result.Error = plan.Found == true
                    ? $"上游任务 {task} 解析为 {plan.Method}，不是大世界动作入口"
                    : plan.Error ?? $"上游任务目录找不到 {task}";
                result.Evidence = new JsonObject { ["os_plan"] = planEvidence };
                return result;
            }

            result = PeriodicRunTask.RunWithKind(request, context, token, Kind,
                                                 plan.Method, plan.SchedulerCommand);
            result.Evidence ??= new JsonObject();
            result.Evidence["os_plan"] = planEvidence;
            if (result.Outcome == TaskOutcome.Succeeded
                && (result.Evidence["target"]?["method"]?.GetValue<string>() != plan.Method
                    || result.Evidence["target"]?["scheduler_command"]?.GetValue<string>()
                       != plan.SchedulerCommand))
            {
                result.Outcome = TaskOutcome.Failed;
                result.ErrorKind = RuntimeErrorKind.UpstreamError;
                result.Error = "原生调度目标与大世界放行目标不一致";
            }
        }
        catch (OperationCanceledException)
        {
            result.Outcome = TaskOutcome.Skipped;
            result.ErrorKind = RuntimeErrorKind.Cancelled;
            result.Error = "调用方取消";
        }
        catch (Exception error)
        {
            var wrapped = RuntimeErrors.Wrap(error, "大世界动作任务失败");
            result.Outcome = TaskOutcome.Failed;
            result.ErrorKind = wrapped.Kind;
            result.Error = wrapped.Message;
        }
        return result;
    }
}
