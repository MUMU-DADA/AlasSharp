using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Alas.Runtime;

namespace Alas.Tasks;

/// <summary>
/// 周期任务执行的**放行判定**任务（`kind = "periodic_preflight"`）：
/// 把两道闸的判定结果与勘察结论**留成工件**，但**不执行任何游戏动作**。
///
/// 为什么值得独立成任务：队列/报告/前端只认 `ITaskRunner`；而"放行/拒绝"这个决定本身
/// 就该有记录 —— 事后能回答"当时为什么允许/拒绝跑这个任务"。
///
/// 输入（`Input`）：`{ "task": "reward", "allow_actions": true, "confirm": "reward" }`
/// 结论：**两闸都过 + 任务名在真上游找得到 → `Succeeded`（allowed）**；
/// 其余一律 `Failed` —— 拒绝不是"没跑"（skipped），是调用方没满足放行条件，
/// 记成 skipped 会让"被闸门挡下"看起来像"没事干"。
/// </summary>
public sealed class PeriodicPreflightTask : ITaskRunner
{
    private static readonly HashSet<string> InputFields = new(StringComparer.Ordinal)
    {
        "task", "allow_actions", "confirm",
    };

    public string Kind => "periodic_preflight";

    public IReadOnlyList<string> Preconditions(TaskRequest request, TaskContext context)
    {
        var problems = new List<string>();
        if (request.Input is not null)
            foreach (var field in request.Input.Select(pair => pair.Key))
                if (!InputFields.Contains(field))
                    problems.Add($"未知周期任务放行字段: input.{field}");
        if (request.Input?["task"] is not JsonValue taskValue
            || !taskValue.TryGetValue<string>(out var task)
            || string.IsNullOrWhiteSpace(task))
            problems.Add("input.task 为空：放行判定必须针对一个具体的上游任务名（如 reward）");
        if (request.Input?.ContainsKey("allow_actions") == true
            && (request.Input["allow_actions"] is not JsonValue allowValue
                || !allowValue.TryGetValue<bool>(out _)))
            problems.Add("input.allow_actions 必须是 JSON 布尔值");
        if (request.Input?.ContainsKey("confirm") == true
            && (request.Input["confirm"] is not JsonValue confirmValue
                || !confirmValue.TryGetValue<string>(out _)))
            problems.Add("input.confirm 必须是 JSON 字符串");
        return problems;
    }

    public TaskResult Run(TaskRequest request, TaskContext context, CancellationToken token)
    {
        string task = request.Input?["task"]?.GetValue<string>() ?? "";
        bool allowActions = request.Input?["allow_actions"]?.GetValue<bool>() ?? false;
        string confirm = request.Input?["confirm"]?.GetValue<string>() ?? "";

        var result = new TaskResult { Id = request.Id, Kind = Kind };
        try
        {
            var decision = context.Session.Vision.CallTyped<PeriodicPreflightResult>(
                "periodic_preflight",
                new { task, allow_actions = allowActions, confirm });
            var plan = decision.Plan;
            result.Evidence = new JsonObject
            {
                ["task"] = task,
                ["allow_actions"] = allowActions,
                ["confirm_matches"] = decision.ConfirmMatches,
                ["executes"] = decision.Executes ?? false,
                ["decision"] = decision.Decision,
                ["reason"] = decision.Reason,
                ["plan"] = plan is null ? null : new JsonObject
                {
                    ["found"] = plan.Found,
                    ["lineno"] = plan.LineNumber,
                    ["imports"] = new JsonArray((plan.Imports ?? new List<string>())
                        .Select(i => (JsonNode)JsonValue.Create(i)!).ToArray()),
                    ["calls_run"] = plan.CallsRun,
                },
            };
            if (string.Equals(decision.Decision, "allowed", StringComparison.Ordinal))
            {
                result.Outcome = TaskOutcome.Succeeded;
            }
            else
            {
                // 被闸门挡下是"调用方没满足条件"，不是"没跑"：记 Failed 并原样带上原因
                result.Outcome = TaskOutcome.Failed;
                result.ErrorKind = RuntimeErrorKind.Internal;
                result.Error = decision.Reason ?? "放行判定未通过（宿主未给出原因）";
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
            var wrapped = RuntimeErrors.Wrap(error, "放行判定失败");
            result.Outcome = TaskOutcome.Failed;
            result.ErrorKind = wrapped.Kind;
            result.Error = wrapped.Message;
        }
        return result;
    }
}

/// <summary>宿主 `periodic_preflight` 的返回：判定 + 原因 + 勘察结果（永不执行）。</summary>
public sealed class PeriodicPreflightResult
{
    [JsonPropertyName("task")] public string? Task { get; set; }
    [JsonPropertyName("decision")] public string? Decision { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("allow_actions")] public bool? AllowActions { get; set; }
    [JsonPropertyName("confirm_matches")] public bool? ConfirmMatches { get; set; }
    [JsonPropertyName("executes")] public bool? Executes { get; set; }
    [JsonPropertyName("plan")] public PeriodicPlanResult? Plan { get; set; }
}
