using System.Text.Json;
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
    private static readonly HashSet<string> InputFields = new(StringComparer.Ordinal)
    {
        "chapters", "stop_on_failure", "max_seconds", "max_rounds",
        "repeat_until_cleared", "clear_all", "fleet1", "fleet2", "submarine",
    };

    public string Kind => "campaign_batch";

    public IReadOnlyList<string> Preconditions(TaskRequest request, TaskContext context)
    {
        var problems = new List<string>();
        if (request.Input is not null)
            foreach (var field in request.Input.Select(pair => pair.Key))
                if (!InputFields.Contains(field)) problems.Add($"未知战役输入字段: input.{field}");

        if (request.Input?["chapters"] is not JsonArray chapters || chapters.Count == 0)
            problems.Add("input.chapters 必须是非空章节数组");
        else foreach (var item in chapters)
        {
            if (item is not JsonValue value || !value.TryGetValue<string>(out var chapter)
                || string.IsNullOrWhiteSpace(chapter))
            {
                problems.Add("input.chapters 的每一项必须是非空章节模块名字符串");
                continue;
            }
            var parts = chapter.Trim().Split('.');
            if (parts.Length != 3 || parts[0] != "campaign"
                || parts.Any(p => !p.All(c => char.IsLetterOrDigit(c) || c == '_')))
                problems.Add($"章节必须是完整的 campaign 模块名: {chapter}");
        }
        ValidateBoolean(request, "stop_on_failure", problems);
        ValidateBoolean(request, "repeat_until_cleared", problems);
        ValidateBoolean(request, "clear_all", problems);
        ValidatePositiveNumber(request, "max_seconds", problems);
        ValidateInteger(request, "max_rounds", 1, problems);
        ValidateInteger(request, "fleet1", 1, problems);
        ValidateInteger(request, "fleet2", 0, problems);
        ValidateInteger(request, "submarine", 0, problems);
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
                // `error_kind` 要如实：**撤退/战败/没打完不是"上游报错"**。
                // R4 的门槛要求前端能区分"成功与撤退"，如果把撤退也标成 upstream_error，
                // 前端只能靠猜。只有真的报错才给 UpstreamError，限额给 Timeout，其余留 None
                //（结论在 `evidence.batch_outcome` 里，语义由结果合同定义）。
                result.ErrorKind = batch.Outcome switch
                {
                    "error" => RuntimeErrorKind.UpstreamError,
                    "incomplete" => RuntimeErrorKind.Timeout,
                    _ => RuntimeErrorKind.None,
                };
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
                if (item is JsonValue value && value.TryGetValue<string>(out var chapter)
                    && !string.IsNullOrWhiteSpace(chapter))
                    list.Add(chapter.Trim());
        return list;
    }

    private static void ValidateBoolean(TaskRequest request, string key, List<string> problems)
    {
        if (request.Input?.ContainsKey(key) != true) return;
        if (request.Input[key] is JsonValue value && value.TryGetValue<bool>(out _)) return;
        problems.Add($"input.{key} 必须是 JSON 布尔值");
    }

    private static void ValidatePositiveNumber(TaskRequest request, string key, List<string> problems)
    {
        if (request.Input?.ContainsKey(key) != true) return;
        if (TryNumber(request.Input[key], out double number) && number > 0) return;
        problems.Add($"input.{key} 必须是大于 0 的有限数值");
    }

    private static void ValidateInteger(TaskRequest request, string key, int minimum,
                                        List<string> problems)
    {
        if (request.Input?.ContainsKey(key) != true) return;
        if (TryNumber(request.Input[key], out double number) && number >= minimum
            && number <= int.MaxValue && number == Math.Truncate(number)) return;
        problems.Add($"input.{key} 必须是 {minimum} 到 {int.MaxValue} 的整数数值");
    }

    private static bool TryNumber(JsonNode? node, out double number)
    {
        number = 0;
        if (node?.GetValueKind() != JsonValueKind.Number) return false;
        try
        {
            number = node.Deserialize<double>();
            return double.IsFinite(number);
        }
        catch (JsonException) { return false; }
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
