using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Alas.Runtime;

namespace Alas.Tasks;

/// <summary>
/// 周期任务**执行**任务（`kind = "periodic_run"`）：产品路径上的执行入口。
///
/// 为什么要有它：宿主 op `periodic_run` 只有进程外调用者能用（诊断脚本），
/// 而"跑一个周期任务"是**产品能力** —— 队列/报告/前端只认 `ITaskRunner`，
/// 没有这个任务，用户就没有任何正规入口能跑它。本类只做三件事：
/// 传参、把判定翻译成任务结论、把证据写进工件。
///
/// **安全语义由宿主 op 保证**（两道闸：`allow_actions=true` + `confirm` 与 `task` 完全一致，
/// 且**在构造上游对象之前**检查，见 `verify_architecture.py` 的 `periodic_run_gate_intact`）。
/// 本类不另判一套：`denied` → `Failed`（调用方没满足放行条件，不是"没跑"）；
/// 上游抛错 → `Failed` + `upstream_error`。
///
/// 输入（`Input`）：`{ "task": "reward", "allow_actions": true, "confirm": "reward",
/// "overrides": { "BuyFurniture_Enable": true } }`
/// `overrides` 只作用于本次运行的配置对象（**不写回配置文件**）。
/// </summary>
public sealed class PeriodicRunTask : ITaskRunner
{
    public string Kind => "periodic_run";

    public IReadOnlyList<string> Preconditions(TaskRequest request, TaskContext context)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(request.Input?["task"]?.GetValue<string>()))
            problems.Add("input.task 为空：执行入口必须指名一个上游任务（如 reward / dorm）");
        return problems;
    }

    public TaskResult Run(TaskRequest request, TaskContext context, CancellationToken token)
    {
        string task = request.Input?["task"]?.GetValue<string>() ?? "";
        bool allowActions = request.Input?["allow_actions"]?.GetValue<bool>() ?? false;
        string confirm = request.Input?["confirm"]?.GetValue<string>() ?? "";
        JsonObject? overrides = request.Input?["overrides"] as JsonObject;

        var result = new TaskResult { Id = request.Id, Kind = Kind };
        try
        {
            var run = context.Session.Vision.CallTyped<PeriodicRunResult>("periodic_run", new
            {
                task,
                allow_actions = allowActions,
                confirm,
                overrides,
            });
            result.Evidence = new JsonObject
            {
                ["task"] = task,
                ["allow_actions"] = allowActions,
                ["confirm_matches"] = run.ConfirmMatches,
                ["decision"] = run.Decision,
                ["target"] = run.Target is null ? null : new JsonObject
                {
                    ["module"] = run.Target.Module,
                    ["class"] = run.Target.Class,
                },
                ["constructed"] = run.Constructed,
                ["ran"] = run.Ran,
                ["elapsed_s"] = run.ElapsedSeconds,
                ["returned"] = run.Returned,
                ["reason"] = run.Reason,
                ["error"] = run.Error,
                ["traceback_tail"] = new JsonArray((run.TracebackTail ?? new List<string>())
                    .Select(l => (JsonNode)JsonValue.Create(l)!).ToArray()),
            };
            switch (run.Decision)
            {
                case "ran":
                    result.Outcome = TaskOutcome.Succeeded;
                    break;
                case "denied":
                    // 与放行判定同一口径：被闸门挡下是"调用方没满足条件"，不是"没跑"
                    result.Outcome = TaskOutcome.Failed;
                    result.ErrorKind = RuntimeErrorKind.Internal;
                    result.Error = run.Reason ?? "放行判定未通过（宿主未给出原因）";
                    break;
                default:
                    result.Outcome = TaskOutcome.Failed;
                    // 这里的 error 来自上游任务类本身（构造或 run() 抛的），按上游错误归类
                    result.ErrorKind = RuntimeErrorKind.UpstreamError;
                    result.Error = run.Error ?? "执行失败（宿主未给出原因）";
                    break;
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
            var wrapped = RuntimeErrors.Wrap(error, "执行周期任务失败");
            result.Outcome = TaskOutcome.Failed;
            result.ErrorKind = wrapped.Kind;
            result.Error = wrapped.Message;
        }
        return result;
    }
}

/// <summary>宿主 `periodic_run` 的返回（判定 + 勘察 + 构造/运行结果）。</summary>
public sealed class PeriodicRunResult
{
    [JsonPropertyName("task")] public string? Task { get; set; }
    [JsonPropertyName("decision")] public string? Decision { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("allow_actions")] public bool? AllowActions { get; set; }
    [JsonPropertyName("confirm_matches")] public bool? ConfirmMatches { get; set; }
    [JsonPropertyName("target")] public PeriodicRunTarget? Target { get; set; }
    [JsonPropertyName("constructed")] public bool? Constructed { get; set; }
    [JsonPropertyName("ran")] public bool? Ran { get; set; }
    [JsonPropertyName("elapsed_s")] public double? ElapsedSeconds { get; set; }
    [JsonPropertyName("returned")] public string? Returned { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("traceback_tail")] public List<string>? TracebackTail { get; set; }
}

public sealed class PeriodicRunTarget
{
    [JsonPropertyName("module")] public string? Module { get; set; }
    [JsonPropertyName("class")] public string? Class { get; set; }
}
