using System.Text.Json.Nodes;
using Alas.Core;

namespace Alas.Tasks;

/// <summary>
/// 把"清点结果"变成**可执行的队列文件**（R2 的数据面：发现 → 生成 → 执行 → 报告）。
///
/// 将具备 Campaign/MAP 的导出候选翻译为普通队列，避免手写任务列表；
/// 候选不保证当前活动开放、账号可达或原生加载成功。生成队列照样能被
/// `Alas.Server queue` 跑、被 `--resume` 续、被 `Alas.Server report` 复核，不需要新机制。
///
/// 纪律：筛选规则复用 <see cref="EventStateTask.Select"/>（**只有一处**），
/// 本类只负责"翻译成任务"，不新增任何判据，也不写死关卡名。
/// </summary>
public static class TaskQueuePlanner
{
    /// <summary>生成队列文档；返回 (选中章节数, 文档)。</summary>
    public static (int Count, JsonObject Document) Build(string dataDirectory, string prefix,
                                                         bool onlyComplete, int limit,
                                                         int maxRounds, double maxSeconds,
                                                         bool dryRun, bool captureAfter = false)
    {
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "limit 必须为正整数");
        if (maxRounds <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxRounds), maxRounds, "maxRounds 必须为正整数");
        if (!double.IsFinite(maxSeconds) || maxSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxSeconds), maxSeconds, "maxSeconds 必须为有限正数");

        var catalog = UpstreamData.Catalog.Open(dataDirectory);
        var matched = EventStateTask.Select(catalog, prefix, onlyComplete, default, out int total);
        var selected = matched
            .OrderBy(e => e.Source, StringComparer.Ordinal)
            .Take(limit)
            .ToList();

        var tasks = new JsonArray();
        foreach (var entry in selected)
        {
            string module = EventStateTask.Module(entry.Source);
            tasks.Add(new JsonObject
            {
                // 任务 id 用完整模块名（含包名）→ 全局唯一，断点续跑与工件命名都靠它
                ["id"] = module,
                ["kind"] = "campaign_batch",
                ["input"] = new JsonObject
                {
                    ["chapters"] = new JsonArray(module),
                    ["max_rounds"] = maxRounds,
                    ["max_seconds"] = maxSeconds,
                },
                // 默认不 required：一个活动关跑不动不该把整条队列停掉，
                // 但失败仍会按"失败即停"停下（要跑完请显式 --continue-on-error）。
                ["required"] = false,
            });
            if (captureAfter)
                tasks.Add(new JsonObject
                {
                    ["id"] = $"{module}:post",
                    ["kind"] = "account_state",
                    ["input"] = new JsonObject { ["capture"] = true },
                    ["required"] = true,
                });
        }

        var document = new JsonObject
        {
            ["generated_by"] = "Alas.Server plan-queue",
            ["folder_prefix"] = prefix,
            ["only_complete"] = onlyComplete,
            ["limit"] = limit,
            ["dry_run"] = dryRun,
            ["capture_after"] = captureAfter,
            ["chapters_total"] = total,
            ["matched"] = matched.Count,
            ["tasks"] = tasks,
        };
        return (selected.Count, document);
    }
}
