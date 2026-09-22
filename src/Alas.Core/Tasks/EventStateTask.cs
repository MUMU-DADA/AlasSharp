using System.Text.Json.Nodes;
using Alas.Core;
using Alas.Runtime;

namespace Alas.Tasks;

/// <summary>
/// 活动域（R2 第四域）的第一刀：**清点可跑的活动章节**（纯离线，不需要设备）。
///
/// 它回答的是跑活动之前必须先知道的事："现在导出的数据里有哪些活动章节、各自
/// 计划完整度如何"。数据来源是 S0 冻结的上游导出契约（`data/campaign/**`），
/// 与 `alashub campaign` 用的是同一份 —— 所以**没有第二份章节表**，也不认地图名：
/// 筛选条件是"来源目录前缀"，由输入给，不写死在代码里。
///
/// 输入（`Input`）：
/// <code>
/// { "folder_prefix": "event_",   // 默认 event_；按来源目录名筛，不按关名/编号
///   "only_complete": false,      // true = 只要计划完整的（tier A/B）
///   "limit": 50 }                // 只影响证据里列出的条数，不影响统计
/// </code>
///
/// 结论口径：清单读出来了就 `Succeeded`（**"一个活动都没有"是有效状态**，
/// 不是失败）；契约读不出来才 `Failed`。
/// </summary>
public sealed class EventStateTask : ITaskRunner
{
    public string Kind => "event_state";

    public IReadOnlyList<string> Preconditions(TaskRequest request, TaskContext context)
    {
        var problems = new List<string>();
        string data = context.Options.DataDirectory;
        if (string.IsNullOrWhiteSpace(data))
            problems.Add("会话没有配置 DataDirectory（离线章节清点需要上游数据契约目录）");
        else if (!Directory.Exists(Path.Combine(data, "campaign")))
            problems.Add($"数据契约目录里没有 campaign/：{Path.Combine(data, "campaign")}"
                         + "（先跑 tools/export_upstream_data.py 或 sync_all.py）");
        return problems;
    }

    public TaskResult Run(TaskRequest request, TaskContext context, CancellationToken token)
    {
        var result = new TaskResult { Id = request.Id, Kind = Kind };
        string prefix = Text(request, "folder_prefix") ?? "event_";
        bool onlyComplete = Bool(request, "only_complete") ?? false;
        int limit = (int?)Number(request, "limit") ?? 50;

        try
        {
            var catalog = UpstreamData.Catalog.Open(context.Options.DataDirectory);
            var matched = new List<CampaignIndexEntry>();
            int total = 0;
            foreach (var entry in catalog.Campaign.Chapters)
            {
                token.ThrowIfCancellationRequested();
                total++;
                string folder = Folder(entry.Source);
                if (!folder.Contains(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (onlyComplete && !entry.PlanComplete) continue;
                matched.Add(entry);
            }
            var byTier = matched.GroupBy(e => e.Tier ?? "?")
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count());

            var listed = new JsonArray();
            foreach (var entry in matched
                         .OrderBy(e => e.Source, StringComparer.Ordinal)
                         .Take(Math.Max(1, limit)))
                listed.Add(new JsonObject
                {
                    ["chapter"] = Module(entry.Source),
                    ["source"] = entry.Source,
                    ["stage"] = entry.Name,
                    ["tier"] = entry.Tier,
                    ["plan_complete"] = entry.PlanComplete,
                });

            result.Evidence = new JsonObject
            {
                ["folder_prefix"] = prefix,
                ["only_complete"] = onlyComplete,
                ["chapters_total"] = total,
                ["matched"] = matched.Count,
                ["by_tier"] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(byTier)),
                ["listed"] = listed,
                ["data_directory"] = context.Options.DataDirectory,
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
            var wrapped = RuntimeErrors.Wrap(error, "清点活动章节失败");
            result.Outcome = TaskOutcome.Failed;
            result.ErrorKind = wrapped.Kind;
            result.Error = wrapped.Message;
        }
        return result;
    }

    /// <summary>`campaign/event_x/c3.py` → `event_x`（来源目录名）。</summary>
    private static string Folder(string source)
    {
        var parts = source.Replace('\\', '/').Split('/');
        return parts.Length >= 2 ? parts[^2] : source;
    }

    /// <summary>`campaign/event_x/c3.py` → `campaign.event_x.c3`（可执行的完整模块名）。</summary>
    private static string Module(string source)
        => source.Replace('\\', '/').Replace("/", ".").Replace(".py", "");

    private static string? Text(TaskRequest request, string key)
        => request.Input?[key]?.GetValue<string>();

    private static double? Number(TaskRequest request, string key)
        => request.Input?[key] is JsonNode node
           && node.GetValueKind() == System.Text.Json.JsonValueKind.Number
            ? node.GetValue<double>() : null;

    private static bool? Bool(TaskRequest request, string key)
        => request.Input?[key] is JsonNode node
           && node.GetValueKind() is System.Text.Json.JsonValueKind.True
              or System.Text.Json.JsonValueKind.False
            ? node.GetValue<bool>() : null;
}
