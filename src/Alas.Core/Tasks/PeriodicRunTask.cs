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
/// 安全语义分两层：本类先要求会话由 CLI 的 `--run --allow-actions` 启动；宿主 op 再检查
/// 输入里的两道闸（`allow_actions=true` + `confirm` 与 `task` 完全一致），且在构造上游对象之前
/// 完成检查，见 `verify_architecture.py` 的 `periodic_run_gate_intact`。这样队列文件不能自行把
/// 默认 dry-run 会话升级成动作会话。会话未授权是前置条件不满足（Skipped，required 时 Failed）；
/// 宿主返回 `denied` → `Failed`，证据保留构造/执行状态；
/// 上游抛错 → `Failed` + `upstream_error`。
///
/// 输入（`Input`）：`{ "task": "reward", "allow_actions": true, "confirm": "reward",
/// "overrides": { "Reward_CollectOil": true } }`
/// `overrides` 通过上游 `config.override()` 作用于本次任务对象；任务自身对 NextRun 等调度状态的
/// 正常写入仍由上游负责。
/// </summary>
public sealed class PeriodicRunTask : ITaskRunner
{
    private static readonly HashSet<string> InputFields = new(StringComparer.Ordinal)
    {
        "task", "allow_actions", "confirm", "overrides", "instance",
    };

    public string Kind => "periodic_run";

    public IReadOnlyList<string> Preconditions(TaskRequest request, TaskContext context)
    {
        var problems = new List<string>();
        if (request.Input is not null)
            foreach (var field in request.Input.Select(pair => pair.Key))
                if (!InputFields.Contains(field))
                    problems.Add($"未知周期任务执行字段: input.{field}");
        if (request.Input?["task"] is not JsonValue taskValue
            || !taskValue.TryGetValue<string>(out var task)
            || string.IsNullOrWhiteSpace(task))
            problems.Add("input.task 为空：执行入口必须指名一个上游任务（如 reward / dorm）");
        if (request.Input?.ContainsKey("overrides") == true
            && request.Input["overrides"] is not null
            && request.Input["overrides"] is not JsonObject)
            problems.Add("input.overrides 必须是 JSON 对象或 null");
        if (request.Input?.ContainsKey("allow_actions") == true
            && (request.Input["allow_actions"] is not JsonValue allowValue
                || !allowValue.TryGetValue<bool>(out _)))
            problems.Add("input.allow_actions 必须是 JSON 布尔值");
        if (request.Input?.ContainsKey("confirm") == true
            && (request.Input["confirm"] is not JsonValue confirmValue
                || !confirmValue.TryGetValue<string>(out _)))
            problems.Add("input.confirm 必须是 JSON 字符串");
        if (context.Options.DryRun || !context.Options.AllowActions)
            problems.Add("会话未授权：执行周期任务需要用 --run --allow-actions 启动队列");
        if (request.Input?.ContainsKey("instance") == true)
        {
            if (request.Input["instance"] is not JsonValue instanceValue ||
                !instanceValue.TryGetValue<string>(out var instance) || string.IsNullOrWhiteSpace(instance))
                problems.Add("input.instance 必须是非空实例名");
            else
            {
                try { _ = new ConfigWorkspace(context.Options.RepoDirectory).Get(instance); }
                catch (Exception error) when (error is ArgumentException or ConfigWorkspaceException or IOException)
                { problems.Add(error.Message); }
            }
        }
        return problems;
    }

    public TaskResult Run(TaskRequest request, TaskContext context, CancellationToken token)
        => RunWithKind(request, context, token, Kind);

    internal static TaskResult RunWithKind(TaskRequest request, TaskContext context,
                                           CancellationToken token, string kind,
                                           string? expectedMethod = null,
                                           string? expectedCommand = null)
    {
        string task = request.Input?["task"]?.GetValue<string>() ?? "";
        bool allowActions = request.Input?["allow_actions"]?.GetValue<bool>() ?? false;
        string confirm = request.Input?["confirm"]?.GetValue<string>() ?? "";
        JsonObject? overrides = request.Input?["overrides"] as JsonObject;

        var result = new TaskResult { Id = request.Id, Kind = kind };
        if (context.Options.DryRun || !context.Options.AllowActions)
        {
            result.Outcome = TaskOutcome.Refused;
            result.ErrorKind = RuntimeErrorKind.None;
            result.Error = "会话未授权：执行周期任务需要用 --run --allow-actions 启动队列";
            result.Evidence = new JsonObject
            {
                ["task"] = task,
                ["allow_actions"] = allowActions,
                ["session_dry_run"] = context.Options.DryRun,
                ["session_allow_actions"] = context.Options.AllowActions,
                ["decision"] = "denied",
                ["constructed"] = false,
                ["ran"] = false,
                ["reason"] = result.Error,
            };
            return result;
        }
        try
        {
            var run = context.Session.Vision.CallTyped<PeriodicRunResult>("periodic_run", new
            {
                task,
                instance = request.Input?["instance"]?.GetValue<string>(),
                allow_actions = allowActions,
                confirm,
                overrides,
                expected_method = expectedMethod,
                expected_scheduler_command = expectedCommand,
            });
            result.Evidence = new JsonObject
            {
                ["task"] = run.Task,
                ["instance"] = run.Instance,
                ["allow_actions"] = run.AllowActions,
                ["confirm_matches"] = run.ConfirmMatches,
                ["decision"] = run.Decision,
                ["target"] = run.Target is null ? null : new JsonObject
                {
                    ["module"] = run.Target.Module,
                    ["class"] = run.Target.Class,
                    ["scheduler_command"] = run.Target.SchedulerCommand,
                    ["method"] = run.Target.Method,
                },
                ["constructed"] = run.Constructed,
                ["ran"] = run.Ran,
                ["native_success"] = run.NativeSuccess,
                ["elapsed_s"] = run.ElapsedSeconds,
                ["returned"] = run.Returned,
                ["exit_code"] = run.ExitCode,
                ["reason"] = run.Reason,
                ["error"] = run.Error,
                ["traceback_tail"] = new JsonArray((run.TracebackTail ?? new List<string>())
                    .Select(l => (JsonNode)JsonValue.Create(l)!).ToArray()),
                ["native_error_dir"] = run.NativeErrorDirectory,
                ["native_error_log"] = run.NativeErrorLog,
                ["failure_frames"] = new JsonArray((run.FailureFrames ?? new List<string>())
                    .Select(path => (JsonNode)JsonValue.Create(path)!).ToArray()),
            };
            switch (run.Decision)
            {
                case "ran":
                    var violations = SuccessfulDispatchViolations(run, task,
                        request.Input?["instance"]?.GetValue<string>() ?? "alas",
                        expectedMethod, expectedCommand);
                    if (violations.Count == 0)
                        result.Outcome = TaskOutcome.Succeeded;
                    else
                    {
                        result.Outcome = TaskOutcome.Failed;
                        result.ErrorKind = RuntimeErrorKind.ContractViolation;
                        result.Error = $"原生调度成功响应不一致: {string.Join(", ", violations)}";
                        if (!string.IsNullOrWhiteSpace(run.Error)) result.Error += $"; {run.Error}";
                        result.Evidence["response_violations"] = new JsonArray(violations
                            .Select(field => (JsonNode)JsonValue.Create(field)!).ToArray());
                    }
                    break;
                case "denied":
                    // 与放行判定同一口径：被闸门挡下是"调用方没满足条件"，不是"没跑"
                    result.Outcome = TaskOutcome.Failed;
                    result.ErrorKind = RuntimeErrorKind.Internal;
                    result.Error = run.Reason ?? "放行判定未通过（宿主未给出原因）";
                    break;
                case "failed":
                    result.Outcome = TaskOutcome.Failed;
                    result.ErrorKind = RuntimeErrorKind.UpstreamError;
                    result.Error = run.Error ?? "上游原生调度器报告失败";
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

    private static List<string> SuccessfulDispatchViolations(PeriodicRunResult run, string task,
        string instance, string? expectedMethod, string? expectedCommand)
    {
        // A consistent native return proves dispatch only, never a sortie clear.
        var violations = new List<string>();
        if (run.Task != task) violations.Add("task");
        if (run.Instance != instance) violations.Add("instance");
        if (run.AllowActions != true) violations.Add("allow_actions");
        if (run.ConfirmMatches != true) violations.Add("confirm_matches");
        if (run.Constructed != true) violations.Add("constructed");
        if (run.Ran != true) violations.Add("ran");
        if (run.NativeSuccess != true) violations.Add("native_success");
        if (run.Target?.Module != "alas") violations.Add("target.module");
        if (run.Target?.Class != "AzurLaneAutoScript") violations.Add("target.class");
        if (string.IsNullOrWhiteSpace(run.Target?.Method)
            || expectedMethod is not null && run.Target.Method != expectedMethod)
            violations.Add("target.method");
        if (string.IsNullOrWhiteSpace(run.Target?.SchedulerCommand)
            || expectedCommand is not null && run.Target.SchedulerCommand != expectedCommand)
            violations.Add("target.scheduler_command");
        if (!string.IsNullOrWhiteSpace(run.Error)) violations.Add("error");
        return violations;
    }
}

/// <summary>宿主 `periodic_run` 的返回（判定 + 勘察 + 构造/运行结果）。</summary>
public sealed class PeriodicRunResult
{
    [JsonPropertyName("instance")] public string? Instance { get; set; }
    [JsonPropertyName("task")] public string? Task { get; set; }
    [JsonPropertyName("decision")] public string? Decision { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("allow_actions")] public bool? AllowActions { get; set; }
    [JsonPropertyName("confirm_matches")] public bool? ConfirmMatches { get; set; }
    [JsonPropertyName("target")] public PeriodicRunTarget? Target { get; set; }
    [JsonPropertyName("constructed")] public bool? Constructed { get; set; }
    [JsonPropertyName("ran")] public bool? Ran { get; set; }
    [JsonPropertyName("native_success")] public bool? NativeSuccess { get; set; }
    [JsonPropertyName("elapsed_s")] public double? ElapsedSeconds { get; set; }
    [JsonPropertyName("returned")] public string? Returned { get; set; }
    [JsonPropertyName("exit_code")] public string? ExitCode { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("traceback_tail")] public List<string>? TracebackTail { get; set; }
    [JsonPropertyName("native_error_dir")] public string? NativeErrorDirectory { get; set; }
    [JsonPropertyName("native_error_log")] public string? NativeErrorLog { get; set; }
    [JsonPropertyName("failure_frames")] public List<string>? FailureFrames { get; set; }
}

public sealed class PeriodicRunTarget
{
    [JsonPropertyName("module")] public string? Module { get; set; }
    [JsonPropertyName("class")] public string? Class { get; set; }
    [JsonPropertyName("scheduler_command")] public string? SchedulerCommand { get; set; }
    [JsonPropertyName("method")] public string? Method { get; set; }
}
