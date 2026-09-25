using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Runtime;

namespace Alas.Tasks;

/// <summary>
/// 只读上游任务目录：task.yaml 提供分组及成员，args.json 提供生成任务清单。
/// 宿主核对成员集合一致性；Core 保留完整关系，不另维护任务表。
///
/// 输入（`Input`）：`{ "limit": 20 }` —— 只影响证据里列出的任务条数，不影响统计。
/// 结论：目录读出来即 `Succeeded`（**任务数为 0 也是有效状态**）；宿主读不出来才 `Failed`。
/// </summary>
public sealed class TaskCatalogTask : ITaskRunner
{
    private static readonly HashSet<string> InputFields = new(StringComparer.Ordinal)
    {
        "limit",
    };

    public string Kind => "task_catalog";

    public IReadOnlyList<string> Preconditions(TaskRequest request, TaskContext context)
    {
        var problems = new List<string>();
        if (request.Input is not null)
        {
            foreach (var field in request.Input.Select(pair => pair.Key))
                if (!InputFields.Contains(field))
                    problems.Add($"未知任务目录输入字段: input.{field}");
        }

        if (request.Input?.ContainsKey("limit") == true)
        {
            var limit = request.Input["limit"];
            if (limit?.GetValueKind() != JsonValueKind.Number
                || !TryLimit(limit, out _))
                problems.Add($"input.limit 必须是 1 到 {int.MaxValue} 的整数数值");
        }

        // 目录来自上游仓库，不依赖设备，也不依赖游戏的任何状态。
        return problems;
    }

    public TaskResult Run(TaskRequest request, TaskContext context, CancellationToken token)
    {
        var result = new TaskResult { Id = request.Id, Kind = Kind };
        int limit = 20;
        if (request.Input?["limit"] is JsonNode node
            && node.GetValueKind() == JsonValueKind.Number
            && TryLimit(node, out var parsedLimit))
            limit = parsedLimit;
        try
        {
            var catalog = context.Session.Vision.TaskCatalog();
            var tasks = catalog.GeneratedTasks ?? new List<string>();
            var groups = catalog.SourceGroups ?? new List<string>();
            result.Evidence = new JsonObject
            {
                ["task_source"] = catalog.GeneratedSource,
                ["task_count"] = catalog.GeneratedTaskCount ?? tasks.Count,
                ["task_sample"] = new JsonArray(tasks.Take(limit)
                    .Select(t => (JsonNode)JsonValue.Create(t)!).ToArray()),
                ["group_source"] = catalog.Source,
                ["group_count"] = catalog.SourceGroupCount ?? groups.Count,
                ["groups"] = new JsonArray(groups.Select(g => (JsonNode)JsonValue.Create(g)!).ToArray()),
                ["group_tasks"] = JsonSerializer.SerializeToNode(catalog.Groups),
                ["generated_error"] = catalog.GeneratedError,
            };
            if (catalog.Error is not null || catalog.GeneratedError is not null)
            {
                result.Outcome = TaskOutcome.Failed;
                result.ErrorKind = RuntimeErrorKind.HostUnavailable;
                result.Error = catalog.Error ?? catalog.GeneratedError;
                return result;
            }
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
            var wrapped = RuntimeErrors.Wrap(error, "清点上游任务目录失败");
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
