using System.Text.Json.Nodes;
using Alas.Runtime;

namespace Alas.Tasks;

/// <summary>
/// 战役批量域：把"跑哪几关、怎么打"变成一份任务输入模型，接到通用任务队列上。
///
/// 输入（`Input`，全部可选除 `chapters`）：
/// <code>
/// {
///   "chapters": ["campaign.campaign_main.campaign_1_1", ...],  // 完整模块名，禁用同名兜底
///   "stop_on_failure": true,        // 默认 true：某关失败就停
///   "max_seconds": 1500, "max_rounds": 20,
///   "repeat_until_cleared": true,   // 循环打到通关
///   "clear_all": false,             // 上游两套战斗流程二选一
///   "fleet1": 1, "fleet2": 0, "submarine": 0
/// }
/// </code>
///
/// 这里**不做任何逐地图适配**：章节差异全部由上游 `MAP`/`Config`/`Campaign.run()` 消费；
/// 本类只负责把输入翻译成上游调用参数，并把结果合同裁决搬进任务结果。
/// </summary>
public sealed class CampaignBatchTask : ITaskRunner
{
    public string Kind => "campaign_batch";

    public IReadOnlyList<string> Preconditions(TaskRequest request, TaskContext context)
    {
        var problems = new List<string>();
        var chapters = Chapters(request);
        if (chapters.Count == 0)
            problems.Add("input.chapters 为空：战役任务必须给出至少一个完整章节模块名");
        foreach (var chapter in chapters)
        {
            var parts = chapter.Split('.');
            if (parts.Length != 3 || parts[0] != "campaign"
                || parts.Any(p => !p.All(c => char.IsLetterOrDigit(c) || c == '_')))
                problems.Add($"章节必须是完整的 campaign 模块名: {chapter}");
        }
        if (!context.Options.DryRun && !context.Options.AllowActions)
            problems.Add("真跑需要 allow_actions（会话级安全联锁）");
        return problems;
    }

    public TaskResult Run(TaskRequest request, TaskContext context, CancellationToken token)
    {
        var chapters = Chapters(request);
        var settings = Settings(request, context.Options);
        var runner = new CampaignBatchRunner(context.Session)
        {
            StopOnFailure = Bool(request, "stop_on_failure") ?? true,
        };
        var batch = runner.Run(chapters, token, settings);

        var result = new TaskResult
        {
            Id = request.Id,
            Kind = Kind,
            StopReason = batch.StopReason,
            Evidence = Evidence(batch),
        };
        if (batch.DryRun)
        {
            result.Outcome = batch.Stages.Any(s => s.Result is null)
                ? TaskOutcome.Failed : TaskOutcome.DryRun;
            if (result.Outcome == TaskOutcome.Failed)
            {
                result.ErrorKind = RuntimeErrorKind.UpstreamError;
                result.Error = batch.Stages.First(s => s.Result is null).Error;
            }
            return result;
        }
        switch (batch.Outcome)
        {
            case "cleared":
                result.Outcome = TaskOutcome.Succeeded;
                break;
            case "refused":
                result.Outcome = TaskOutcome.Refused;
                result.ErrorKind = RuntimeErrorKind.ChapterRefused;
                result.Error = batch.Stages.FirstOrDefault(s => s.Failed)?.Error ?? "章节被拒绝执行";
                break;
            case "cancelled":
                result.Outcome = TaskOutcome.Skipped;
                result.ErrorKind = RuntimeErrorKind.Cancelled;
                result.Error = "调用方取消：停在关卡边界";
                break;
            default:
                result.Outcome = TaskOutcome.Failed;
                result.ErrorKind = batch.ErrorKind != RuntimeErrorKind.None
                    ? batch.ErrorKind : RuntimeErrorKind.UpstreamError;
                result.Error = batch.Stages.FirstOrDefault(s => s.Failed)?.Error
                               ?? $"批次未通关: {batch.Outcome}";
                break;
        }
        return result;
    }

    /// <summary>把批次的逐关结论放进证据里（任务结果不复制业务对象，只给结构化事实）。</summary>
    private static JsonObject Evidence(CampaignBatchResult batch)
    {
        var stages = new JsonArray();
        foreach (var stage in batch.Stages)
            stages.Add(new JsonObject
            {
                ["chapter"] = stage.Chapter,
                ["stage"] = stage.Stage,
                ["outcome"] = stage.Outcome,
                ["cleared"] = stage.Cleared,
                ["failed"] = stage.Failed,
                ["skipped"] = stage.Skipped,
                ["error_kind"] = RuntimeErrors.Name(stage.ErrorKind),
                ["error"] = stage.Error,
                ["contract_violations"] = new JsonArray(
                    stage.ContractViolations.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()),
                ["artifact"] = stage.ArtifactPath,
            });
        return new JsonObject
        {
            ["batch_outcome"] = batch.Outcome,
            ["cleared"] = batch.Cleared,
            ["stopped_early"] = batch.StoppedEarly,
            ["index_artifact"] = batch.IndexPath,
            ["stages"] = stages,
        };
    }

    private static List<string> Chapters(TaskRequest request)
    {
        var list = new List<string>();
        if (request.Input?["chapters"] is JsonArray array)
            foreach (var item in array)
                if (item?.GetValue<string>() is { Length: > 0 } chapter)
                    list.Add(chapter.Trim());
        return list;
    }

    private static CampaignRunSettings Settings(TaskRequest request, SessionOptions options)
    {
        var settings = CampaignRunSettings.From(options);
        settings.MaxSeconds = Number(request, "max_seconds") ?? settings.MaxSeconds;
        settings.MaxRounds = (int?)Number(request, "max_rounds") ?? settings.MaxRounds;
        settings.RepeatUntilCleared = Bool(request, "repeat_until_cleared") ?? settings.RepeatUntilCleared;
        settings.ClearAll = Bool(request, "clear_all") ?? settings.ClearAll;
        settings.Fleet1 = (int?)Number(request, "fleet1") ?? settings.Fleet1;
        settings.Fleet2 = (int?)Number(request, "fleet2") ?? settings.Fleet2;
        settings.SubmarineFleet = (int?)Number(request, "submarine") ?? settings.SubmarineFleet;
        return settings;
    }

    private static double? Number(TaskRequest request, string key)
        => request.Input?[key] is JsonNode node && node.GetValueKind() == System.Text.Json.JsonValueKind.Number
            ? node.GetValue<double>() : null;

    private static bool? Bool(TaskRequest request, string key)
        => request.Input?[key] is JsonNode node && node.GetValueKind()
            is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False
            ? node.GetValue<bool>() : null;
}
