using System.Text.Json.Nodes;
using Alas.Runtime;

namespace Alas.Tasks;

/// <summary>
/// 周期任务域（R2 第五域）的第一刀：**清点上游任务目录**（纯离线，只读）。
///
/// 为什么先做清点：科研/建造/委托/每日这类周期任务的实现要一个任务一个任务地做，
/// 但"上游到底有哪些任务"必须先有个权威来源。本项目**不另维护任务表** ——
/// 所以这里只把宿主返回的上游目录如实报出来。
///
/// **两个来源不许混为一谈**（实测本机）：`task.yaml` 的顶层键是**分组**（9 个），
/// 生成产物里的扁平清单才是**任务**（68 个），交集只有 3 个。第一版验收脚本
/// 把两者当同一集合比对时被当场证伪，所以这里两个字段分开报、各自标明是什么。
///
/// 输入（`Input`）：`{ "limit": 20 }` —— 只影响证据里列出的任务条数，不影响统计。
/// 结论：目录读出来即 `Succeeded`（**任务数为 0 也是有效状态**）；宿主读不出来才 `Failed`。
/// </summary>
public sealed class TaskCatalogTask : ITaskRunner
{
    public string Kind => "task_catalog";

    public IReadOnlyList<string> Preconditions(TaskRequest request, TaskContext context)
    {
        // 没有额外前置条件：目录来自上游仓库，不依赖设备，也不依赖游戏的任何状态。
        return Array.Empty<string>();
    }

    public TaskResult Run(TaskRequest request, TaskContext context, CancellationToken token)
    {
        var result = new TaskResult { Id = request.Id, Kind = Kind };
        int limit = 20;
        if (request.Input?["limit"] is JsonNode node
            && node.GetValueKind() == System.Text.Json.JsonValueKind.Number)
            limit = (int)node.GetValue<double>();
        try
        {
            var catalog = context.Session.Vision.TaskCatalog();
            if (catalog.Error is not null)
            {
                result.Outcome = TaskOutcome.Failed;
                result.ErrorKind = RuntimeErrorKind.HostUnavailable;
                result.Error = catalog.Error;
                return result;
            }
            var tasks = catalog.GeneratedTasks ?? new List<string>();
            var groups = catalog.SourceGroups ?? new List<string>();
            result.Evidence = new JsonObject
            {
                ["task_source"] = catalog.GeneratedSource,
                ["task_count"] = catalog.GeneratedTaskCount ?? tasks.Count,
                ["task_sample"] = new JsonArray(tasks.Take(Math.Max(1, limit))
                    .Select(t => (JsonNode)JsonValue.Create(t)!).ToArray()),
                ["group_source"] = catalog.Source,
                ["group_count"] = catalog.SourceGroupCount ?? groups.Count,
                ["groups"] = new JsonArray(groups.Select(g => (JsonNode)JsonValue.Create(g)!).ToArray()),
                ["generated_error"] = catalog.GeneratedError,
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
            var wrapped = RuntimeErrors.Wrap(error, "清点上游任务目录失败");
            result.Outcome = TaskOutcome.Failed;
            result.ErrorKind = wrapped.Kind;
            result.Error = wrapped.Message;
        }
        return result;
    }
}
