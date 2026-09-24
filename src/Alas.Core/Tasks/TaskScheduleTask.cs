using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Alas.Runtime;

namespace Alas.Tasks;

/// <summary>
/// 周期任务域（R2 第六域）的第一刀：**调度状态**（只读，零账号消耗）。
///
/// 为什么先做它（`docs/tasks.md` 的已注册任务）：周期任务的动作要真机，但"哪些任务开着、
/// 下次什么时候跑"完全在配置里 —— R4 前端与"跑之前先知道会跑什么"都需要它。
///
/// 数据来自宿主 op `task_schedule`（读上游 `args.json` 的扁平任务表 + 账号配置里的
/// `Scheduler` 段）。本类只做三件事：传参、把结果翻译成任务结论、把关键数字写进证据；
/// **不重算 NextRun**（那是上游调度器的逻辑）。
///
/// 输入（`Input`）：`{ "only_enabled": true, "limit": 30 }`
/// 结论：跑通即 `Succeeded`；**读不到配置是环境问题 → `Failed`（不是 skipped）** ——
/// 这个语义由宿主 op 定好，本类不另判一套。
/// </summary>
public sealed class TaskScheduleTask : ITaskRunner
{
    private static readonly HashSet<string> InputFields = new(StringComparer.Ordinal)
    {
        "only_enabled", "limit", "config_path",
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

        if (request.Input?.ContainsKey("config_path") == true
            && request.Input["config_path"] is not null
            && (request.Input["config_path"] is not JsonValue configPath
                || !configPath.TryGetValue<string>(out var path)
                || string.IsNullOrWhiteSpace(path)))
            problems.Add("input.config_path 必须是非空字符串或 null");

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
            var schedule = context.Session.Vision.CallTyped<TaskScheduleResult>(
                "task_schedule", new
                {
                    only_enabled = onlyEnabled,
                    limit,
                    // 显式配置路径（多份账号配置 / 边界验收用假配置）；不给则用标准位置。
                    config_path = request.Input?["config_path"]?.GetValue<string>(),
                });
            if (schedule.Error is not null)
            {
                result.Outcome = TaskOutcome.Failed;
                result.ErrorKind = RuntimeErrorKind.Internal;
                result.Error = schedule.Error;
                return result;
            }
            var listed = new JsonArray();
            foreach (var entry in schedule.Tasks ?? new List<TaskScheduleEntry>())
                listed.Add(new JsonObject
                {
                    ["task"] = entry.Task,
                    ["enable"] = entry.Enable,
                    ["next_run"] = entry.NextRun,
                    ["scheduler_present"] = entry.SchedulerPresent,
                });
            result.Evidence = new JsonObject
            {
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
    [JsonPropertyName("task_source")] public string? TaskSource { get; set; }
    [JsonPropertyName("config_source")] public string? ConfigSource { get; set; }
    [JsonPropertyName("task_count")] public int? TaskCount { get; set; }
    [JsonPropertyName("enabled_count")] public int? EnabledCount { get; set; }
    [JsonPropertyName("no_scheduler_count")] public int? NoSchedulerCount { get; set; }
    [JsonPropertyName("tasks")] public List<TaskScheduleEntry>? Tasks { get; set; }
    [JsonPropertyName("no_scheduler_sample")] public List<string>? NoSchedulerSample { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

public sealed class TaskScheduleEntry
{
    [JsonPropertyName("task")] public string? Task { get; set; }
    [JsonPropertyName("enable")] public bool? Enable { get; set; }
    [JsonPropertyName("next_run")] public string? NextRun { get; set; }
    [JsonPropertyName("scheduler_present")] public bool? SchedulerPresent { get; set; }
}
