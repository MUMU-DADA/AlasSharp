using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Alas.Runtime;
using Alas.Vision;

namespace Alas.Tasks;

/// <summary>
/// 周期任务域（R2 第六域）的第一刀：**调度状态**（只读，零账号消耗）。
///
/// 数据来自宿主 op `task_schedule`（读上游 `args.json` 的扁平任务表 + 账号配置里的
/// `Scheduler` 段），只表示存储快照，未应用上游默认、锁定字段和配置迁移。
/// 本类传参、校验响应、把结果翻译成任务结论并写入证据；
/// **不重算 NextRun**（那是上游调度器的逻辑）。
///
/// 输入（`Input`）：`{ "only_enabled": true, "limit": 30, "instance": "alas" }`。
/// 结论：跑通即 `Succeeded`；**读不到配置是环境问题 → `Failed`（不是 skipped）** ——
/// 这个语义由宿主 op 定好，本类不另判一套。
/// </summary>
public sealed class TaskScheduleTask : ITaskRunner
{
    private static readonly HashSet<string> InputFields = new(StringComparer.Ordinal)
    {
        "only_enabled", "limit", "instance",
    };

    public string Kind => "task_schedule";

    public IReadOnlyList<string> Preconditions(TaskRequest request, TaskContext context)
    {
        var problems = new List<string>();
        if (request.Input is not null)
            foreach (var field in request.Input.Select(pair => pair.Key))
                if (!InputFields.Contains(field))
                    problems.Add($"未知周期调度输入字段: input.{field}");

        if (request.Input?.ContainsKey("only_enabled") == true
            && (request.Input["only_enabled"] is not JsonValue onlyEnabled
                || !onlyEnabled.TryGetValue<bool>(out _)))
            problems.Add("input.only_enabled 必须是 JSON 布尔值");

        if (request.Input?.ContainsKey("limit") == true)
        {
            var limit = request.Input["limit"];
            if (limit?.GetValueKind() != System.Text.Json.JsonValueKind.Number
                || !TryLimit(limit, out _))
                problems.Add($"input.limit 必须是 1 到 {int.MaxValue} 的整数数值");
        }

        if (request.Input?.ContainsKey("instance") == true)
        {
            if (request.Input["instance"] is not JsonValue value
                || !value.TryGetValue<string>(out var instance)
                || string.IsNullOrWhiteSpace(instance))
                problems.Add("input.instance 必须是非空实例名");
            else
            {
                try
                {
                    if (ConfigWorkspace.ValidateName(instance) != instance)
                        problems.Add("input.instance 必须使用规范实例名");
                }
                catch (ArgumentException) { problems.Add("input.instance 实例名无效"); }
            }
        }

        // 配置缺失属环境问题，由 op 报错 → Failed；这里不把它误标为 skipped。
        return problems;
    }

    public TaskResult Run(TaskRequest request, TaskContext context, CancellationToken token)
    {
        bool onlyEnabled = request.Input?["only_enabled"]?.GetValue<bool>() ?? true;
        int limit = 30;
        if (request.Input?["limit"] is JsonNode node
            && node.GetValueKind() == System.Text.Json.JsonValueKind.Number)
            limit = (int)node.GetValue<double>();

        var result = new TaskResult { Id = request.Id, Kind = Kind };
        try
        {
            var response = context.Session.Vision.CallTyped<JsonObject>(
                "task_schedule", new
                {
                    only_enabled = onlyEnabled,
                    limit,
                    instance = request.Input?["instance"]?.GetValue<string>() ?? "alas",
                });
            result.Evidence = new JsonObject { ["host_response"] = response.DeepClone() };
            TaskScheduleResult schedule;
            try
            {
                schedule = response.Deserialize<TaskScheduleResult>(VisionProtocol.Json)
                    ?? throw new JsonException("调度快照为空");
            }
            catch (JsonException error)
            {
                throw new AlasRuntimeException(RuntimeErrorKind.ContractViolation,
                    $"调度快照字段类型或必需字段不合法: {error.Message}", error);
            }
            if (schedule.Error is not null)
            {
                result.Outcome = TaskOutcome.Failed;
                result.ErrorKind = RuntimeErrorKind.Internal;
                result.Error = schedule.Error;
                return result;
            }
            if (schedule.Instance != (request.Input?["instance"]?.GetValue<string>() ?? "alas"))
                throw new AlasRuntimeException(RuntimeErrorKind.ContractViolation, "调度快照实例身份不一致");
            ValidateSnapshot(schedule, onlyEnabled, limit);
            var listed = new JsonArray();
            foreach (var entry in schedule.Tasks!)
                listed.Add(new JsonObject
                {
                    ["task"] = entry.Task,
                    ["enable"] = entry.Enable,
                    ["next_run"] = entry.NextRun,
                    ["scheduler_present"] = entry.SchedulerPresent,
                });
            result.Evidence = new JsonObject
            {
                ["instance"] = schedule.Instance,
                ["semantics"] = schedule.Semantics,
                ["task_source"] = schedule.TaskSource,
                ["config_source"] = schedule.ConfigSource,
                ["task_count"] = schedule.TaskCount,
                ["enabled_count"] = schedule.EnabledCount,
                ["no_scheduler_count"] = schedule.NoSchedulerCount,
                ["only_enabled"] = onlyEnabled,
                ["listed"] = listed,
                ["no_scheduler_sample"] = new JsonArray(
                    (schedule.NoSchedulerSample ?? new List<string>())
                    .Select(t => (JsonNode)JsonValue.Create(t)!).ToArray()),
            };
            result.Outcome = TaskOutcome.Succeeded;
        }
        catch (OperationCanceledException)
        {
            result.Outcome = TaskOutcome.Skipped;
            result.ErrorKind = RuntimeErrorKind.Cancelled;
            result.Error = "调用方取消";
        }
        catch (Exception error)
        {
            var wrapped = RuntimeErrors.Wrap(error, "读取周期任务调度状态失败");
            result.Outcome = TaskOutcome.Failed;
            result.ErrorKind = wrapped.Kind;
            result.Error = wrapped.Message;
        }
        return result;
    }

    private static void ValidateSnapshot(TaskScheduleResult snapshot, bool onlyEnabled, int limit)
    {
        if (snapshot.Semantics != "stored_config" || snapshot.Tasks is null
            || snapshot.TaskCount is not >= 0 || snapshot.EnabledCount is not >= 0
            || snapshot.NoSchedulerCount is not >= 0 || snapshot.ListedCount is not >= 0
            || (long)snapshot.EnabledCount.Value + snapshot.NoSchedulerCount.Value > snapshot.TaskCount
            || snapshot.ListedCount != (onlyEnabled ? snapshot.EnabledCount : snapshot.TaskCount)
            || snapshot.Tasks.Count != Math.Min(snapshot.ListedCount.Value, limit))
            throw new AlasRuntimeException(RuntimeErrorKind.ContractViolation, "调度存储快照来源或计数不完整/矛盾");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in snapshot.Tasks)
            if (entry is null || string.IsNullOrWhiteSpace(entry.Task) || !names.Add(entry.Task)
                || (!entry.SchedulerPresent && (entry.Enable is not null || entry.NextRun is not null))
                || (onlyEnabled && entry.Enable is not true))
                throw new AlasRuntimeException(RuntimeErrorKind.ContractViolation, "调度存储快照条目缺失或启用状态矛盾");
        int visibleEnabled = snapshot.Tasks.Count(entry => entry.Enable is true);
        int visibleMissing = snapshot.Tasks.Count(entry => !entry.SchedulerPresent);
        if (visibleEnabled > snapshot.EnabledCount || visibleMissing > snapshot.NoSchedulerCount
            || (long)snapshot.EnabledCount.Value - visibleEnabled + snapshot.NoSchedulerCount.Value - visibleMissing
                > snapshot.TaskCount.Value - snapshot.Tasks.Count)
            throw new AlasRuntimeException(RuntimeErrorKind.ContractViolation, "调度存储快照条目与计数矛盾");
    }

    private static bool TryLimit(JsonNode node, out int limit)
    {
        limit = 0;
        try
        {
            double value = node.Deserialize<double>();
            if (!double.IsFinite(value) || value < 1 || value > int.MaxValue
                || value != Math.Truncate(value)) return false;
            limit = (int)value;
            return true;
        }
        catch (JsonException) { return false; }
    }
}

/// <summary>宿主 `task_schedule` 的返回（只读快照）。</summary>
public sealed class TaskScheduleResult
{
    [JsonPropertyName("instance")] public string? Instance { get; set; }
    [JsonPropertyName("semantics")] public string? Semantics { get; set; }
    [JsonPropertyName("task_source")] public string? TaskSource { get; set; }
    [JsonPropertyName("config_source")] public string? ConfigSource { get; set; }
    [JsonPropertyName("task_count")] public int? TaskCount { get; set; }
    [JsonPropertyName("enabled_count")] public int? EnabledCount { get; set; }
    [JsonPropertyName("no_scheduler_count")] public int? NoSchedulerCount { get; set; }
    [JsonPropertyName("listed_count")] public int? ListedCount { get; set; }
    [JsonPropertyName("tasks")] public List<TaskScheduleEntry>? Tasks { get; set; }
    [JsonPropertyName("no_scheduler_sample")] public List<string>? NoSchedulerSample { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

public sealed class TaskScheduleEntry
{
    [JsonPropertyName("task")] public string? Task { get; set; }
    [JsonPropertyName("enable"), JsonRequired] public bool? Enable { get; set; }
    [JsonPropertyName("next_run")] public string? NextRun { get; set; }
    [JsonPropertyName("scheduler_present"), JsonRequired] public bool SchedulerPresent { get; set; }
}
