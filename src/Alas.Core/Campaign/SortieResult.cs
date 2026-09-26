using System.Text.Json;
using System.Text.Json.Serialization;

namespace Alas.Campaign;

/// <summary>
/// sortie-result/1：一次出击的结果合同（C# 侧的执行表）。
///
/// **这张表与 <c>tools/sortie_contract.py</c> 是同一份规则的两份实现**，必须逐条对齐：
/// 两侧裁决（违例码集合）不一致时，<c>tools/diagnostics/verify_result_contract.py</c>
/// 的跨语言对拍会直接失败。改规则要同时改两边。
///
/// 为什么要有它（路线 R0）：<c>CampaignEnd</c> 只表示"本次出击结束"——上游**撤退也抛它**。
/// 把 `cleared` 写成 `campaign_end` 会静默把撤退记成通关，而且事后从日志里看不出来。
/// 所以把口径变成可执行的不变量：`cleared` 只能由 `outcome` 推出，而
/// `outcome == cleared` 必须带结算证据（胜方战果 + 战果来源 + combat_status +
/// handle_in_stage + 没有撤退 + 真的打过一仗）。
/// </summary>
public static class SortieContract
{
    public const string Version = "sortie-result/1";

    /// <summary>结果词表。<c>ended_unknown</c> 是"结束了但说不出为什么"，是合法结论。</summary>
    public static readonly string[] Outcomes =
    {
        "cleared", "withdrawn", "defeated", "ended_unknown", "error", "incomplete", "refused",
    };

    public static readonly string[] WinRanks = { "S", "A", "B" };
    public static readonly string[] LossRanks = { "C", "D" };

    public static readonly string[] LimitReasons =
    {
        "round_limit", "time_limit", "upstream_returned_without_clear", "stopped_after_map_init",
    };

    /// <summary>打过一仗的步骤名（`cleared` 必须至少有一个且无错误）。</summary>
    public static readonly string[] BattleSteps =
    {
        "execute_a_battle", "auto_search_execute_a_battle",
    };

    /// <summary>真正驱动游戏的操作步；dry-run 里出现任何一个都说明纯度破了。</summary>
    public static readonly string[] ActionSteps =
    {
        "prepare_campaign_navigation", "abort_unfinished", "ensure_campaign_ui",
        "prepare_campaign_run", "enter_map", "handle_map_fleet_lock", "map_init",
        "execute_a_battle", "auto_search_execute_a_battle", "withdraw",
    };

    /// <summary>违例码。与 <c>tools/sortie_contract.py</c> 的 <c>VIOLATION_CODES</c> 同集合。</summary>
    public static class Codes
    {
        public const string ContractVersionMismatch = "contract_version_mismatch";
        public const string OutcomeMissing = "outcome_missing";
        public const string UnknownOutcome = "unknown_outcome";
        public const string ClearedFlagMismatch = "cleared_flag_mismatch";
        public const string ClearedWithoutSettlementEvidence = "cleared_without_settlement_evidence";
        public const string ClearedRequiresWinRank = "cleared_requires_win_rank";
        public const string ClearedRequiresRankSource = "cleared_requires_rank_source";
        public const string ClearedRequiresCombatStatus = "cleared_requires_combat_status";
        public const string ClearedRequiresStageObserved = "cleared_requires_stage_observed";
        public const string ClearedRequiresNoWithdrawal = "cleared_requires_no_withdrawal";
        public const string ClearedRequiresExecutedBattle = "cleared_requires_executed_battle";
        public const string WithdrawnRequiresWithdrawalEvidence = "withdrawn_requires_withdrawal_evidence";
        public const string DefeatedRequiresLossRank = "defeated_requires_loss_rank";
        public const string EndedUnknownRequiresCampaignEnd = "ended_unknown_requires_campaign_end";
        public const string ErrorRequiresFailure = "error_requires_failure";
        public const string ErrorRequiresTraceback = "error_requires_traceback";
        public const string ErrorStepWithoutTraceback = "error_step_without_traceback";
        public const string IncompleteRequiresLimitReason = "incomplete_requires_limit_reason";
        public const string RefusedRequiresReason = "refused_requires_reason";
        public const string FailureFrameNotListed = "failure_frame_not_listed";
        public const string FailureFrameMissingOnDisk = "failure_frame_missing_on_disk";
        public const string DryRunMustNotExecute = "dry_run_must_not_execute";
    }

    /// <summary>
    /// 给一份结果做合同裁决。返回**按规则顺序**的违例码（可能重复的码只按规则各出现一次）。
    /// 跨语言对拍只比这个集合，不比文案。
    /// </summary>
    public static List<string> Violations(SortieResult result, string? artifactRoot = null)
    {
        var problems = new List<string>();
        if (result is null) return new List<string> { Codes.OutcomeMissing };

        if (result.Contract is not null && result.Contract != Version)
            problems.Add(Codes.ContractVersionMismatch);

        string? outcome = result.Outcome;
        bool cleared = result.Cleared == true;
        bool dryRun = result.DryRun;
        bool campaignEnd = result.CampaignEnd == true;
        string reason = result.Reason ?? result.EndReason ?? "";
        var steps = result.Steps ?? new List<Dictionary<string, JsonElement>>();
        SortieEndEvidence? evidence = result.EndEvidence;

        if (outcome is null)
        {
            if (!dryRun) problems.Add(Codes.OutcomeMissing);
        }
        else if (!Outcomes.Contains(outcome, StringComparer.Ordinal))
        {
            problems.Add(Codes.UnknownOutcome);
        }

        if (cleared != (outcome == "cleared"))
            problems.Add(Codes.ClearedFlagMismatch);

        if (outcome == "cleared")
        {
            if (evidence is null || !HasContent(evidence))
            {
                // campaign_end 单独不能通关：这条就是 R0 明确要堵的口子。
                problems.Add(Codes.ClearedWithoutSettlementEvidence);
            }
            else
            {
                if (evidence.BattleRank is null ||
                    !WinRanks.Contains(evidence.BattleRank, StringComparer.Ordinal))
                    problems.Add(Codes.ClearedRequiresWinRank);
                if (string.IsNullOrEmpty(evidence.RankSource))
                    problems.Add(Codes.ClearedRequiresRankSource);
                if (evidence.CombatStatus != true)
                    problems.Add(Codes.ClearedRequiresCombatStatus);
                if (evidence.StageObserved != true)
                    problems.Add(Codes.ClearedRequiresStageObserved);
                if (evidence.Withdrawn == true)
                    problems.Add(Codes.ClearedRequiresNoWithdrawal);
            }
            bool battleExecuted = steps.Any(s =>
                StepName(s) is string name && BattleSteps.Contains(name, StringComparer.Ordinal)
                && string.IsNullOrEmpty(StepError(s)));
            if (!battleExecuted) problems.Add(Codes.ClearedRequiresExecutedBattle);
        }

        if (outcome == "withdrawn")
        {
            bool withdrew = evidence?.Withdrawn == true;
            if (!(withdrew || reason.Contains("withdraw", StringComparison.OrdinalIgnoreCase)))
                problems.Add(Codes.WithdrawnRequiresWithdrawalEvidence);
        }

        if (outcome == "defeated")
        {
            string? rank = evidence?.BattleRank;
            if (rank is null || !LossRanks.Contains(rank, StringComparer.Ordinal))
                problems.Add(Codes.DefeatedRequiresLossRank);
        }

        if (outcome == "ended_unknown" && !campaignEnd)
            problems.Add(Codes.EndedUnknownRequiresCampaignEnd);

        if (outcome == "error")
        {
            var failures = FailingSteps(steps);
            SortieFailure? top = HasContent(result.Failure) ? result.Failure : null;
            if (top is null && failures.Count == 0)
            {
                problems.Add(Codes.ErrorRequiresFailure);
            }
            else
            {
                top ??= failures[0];
                if (top.TracebackTail is null || top.TracebackTail.Count == 0)
                    problems.Add(Codes.ErrorRequiresTraceback);
            }
        }

        foreach (var step in steps)
        {
            if (!string.IsNullOrEmpty(StepError(step)) &&
                !(step.TryGetValue("traceback_tail", out var tail) &&
                  tail.ValueKind == JsonValueKind.Array && tail.GetArrayLength() > 0))
            {
                problems.Add(Codes.ErrorStepWithoutTraceback);
                break;
            }
        }

        if (outcome == "incomplete" &&
            (result.StopReason is null || !LimitReasons.Contains(result.StopReason, StringComparer.Ordinal)))
            problems.Add(Codes.IncompleteRequiresLimitReason);

        if (outcome == "refused" && string.IsNullOrEmpty(reason))
            problems.Add(Codes.RefusedRequiresReason);

        var frames = DeclaredFrames(result, steps);
        var listed = result.FailureFrames ?? new List<string>();
        foreach (var frame in frames)
        {
            if (!listed.Contains(frame, StringComparer.Ordinal))
                problems.Add(Codes.FailureFrameNotListed);
            if (!FrameExists(frame, artifactRoot))
                problems.Add(Codes.FailureFrameMissingOnDisk);
        }

        if (dryRun)
        {
            foreach (var step in steps)
            {
                if (StepName(step) is string name &&
                    ActionSteps.Contains(name, StringComparer.Ordinal))
                {
                    problems.Add(Codes.DryRunMustNotExecute);
                    break;
                }
            }
        }

        return problems;
    }

    /// <summary>
    /// 对象是否**真的带了信息**（字段全空视为没有）。与 Python 侧 <c>_has_content</c> 同一谓词 ——
    /// 不用"对象非 null"判断，否则 <c>{"battle_rank": null}</c> 这种边角在两侧结论会分叉。
    /// </summary>
    private static bool HasContent(SortieEndEvidence? evidence)
        => evidence is not null && (evidence.BattleRank is not null || evidence.RankSource is not null
            || evidence.CombatStatus is not null || evidence.StageObserved is not null
            || evidence.ExpectedEnd is not null || evidence.Withdrawn is not null
            || (evidence.CallPath is not null && evidence.CallPath.Count > 0));

    private static bool HasContent(SortieFailure? failure)
        => failure is not null && (failure.Step is not null || failure.Error is not null
            || failure.Frame is not null
            || (failure.TracebackTail is not null && failure.TracebackTail.Count > 0));

    /// <summary>失败帧存在性：绝对路径直接查；相对路径要有 artifactRoot 才能判；判不了就不算违例。</summary>
    private static bool FrameExists(string frame, string? artifactRoot)    {
        try
        {
            string path = frame;
            if (!Path.IsPathRooted(path))
            {
                if (artifactRoot is null) return true;   // 未知，不冤枉也不放过
                path = Path.Combine(artifactRoot, path);
            }
            return File.Exists(path);
        }
        catch (Exception)
        {
            return true;   // 非法路径形态交给上层的路径规则，不在合同里判
        }
    }

    private static List<SortieFailure> FailingSteps(List<Dictionary<string, JsonElement>> steps)
    {
        var failures = new List<SortieFailure>();
        foreach (var step in steps)
        {
            string? error = StepError(step);
            if (string.IsNullOrEmpty(error)) continue;
            failures.Add(new SortieFailure
            {
                Step = StepName(step),
                Error = error,
                TracebackTail = ReadStringArray(step, "traceback_tail"),
                Frame = ReadString(step, "failure_frame"),
            });
        }
        return failures;
    }

    private static List<string> DeclaredFrames(SortieResult result,
                                               List<Dictionary<string, JsonElement>> steps)
    {
        var frames = new List<string>();
        string? top = result.Failure?.Frame;
        if (!string.IsNullOrEmpty(top)) frames.Add(top);
        foreach (var step in steps)
        {
            string? frame = ReadString(step, "failure_frame");
            if (!string.IsNullOrEmpty(frame)) frames.Add(frame);
        }
        return frames;
    }

    private static string? StepName(Dictionary<string, JsonElement> step)
        => ReadString(step, "step");

    private static string? StepError(Dictionary<string, JsonElement> step)
        => ReadString(step, "error");

    private static string? ReadString(Dictionary<string, JsonElement> step, string key)
        => step.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static List<string> ReadStringArray(Dictionary<string, JsonElement> step, string key)
    {
        var items = new List<string>();
        if (!step.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.Array)
            return items;
        foreach (var item in value.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String) items.Add(item.GetString()!);
        return items;
    }

    /// <summary>一行摘要，给 CLI 与工件用。</summary>
    public static string Describe(SortieResult result, string? artifactRoot = null)
    {
        var violations = Violations(result, artifactRoot);
        return violations.Count == 0
            ? $"合规 outcome={result.Outcome ?? "<无>"} cleared={result.Cleared == true}"
            : $"违例 {violations.Count} 项: {string.Join(", ", violations)}";
    }
}

/// <summary>
/// 一次出击的结果。字段名就是 sortie-result/1 的 JSON 字段名，不做二次映射 ——
/// 生产方（Python）与消费方（C#）看到的是同一份文档。
/// </summary>
public class SortieResult
{
    [JsonPropertyName("contract")] public string? Contract { get; set; }
    [JsonPropertyName("chapter")] public string? Chapter { get; set; }
    [JsonPropertyName("stage")] public string? Stage { get; set; }
    [JsonPropertyName("dry_run")] public bool DryRun { get; set; }
    [JsonPropertyName("outcome")] public string? Outcome { get; set; }
    [JsonPropertyName("cleared")] public bool? Cleared { get; set; }
    [JsonPropertyName("campaign_end")] public bool? CampaignEnd { get; set; }
    [JsonPropertyName("end_reason")] public string? EndReason { get; set; }
    [JsonPropertyName("stop_reason")] public string? StopReason { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    /// <summary>`refused` 的安全联锁标记（与 `outcome=refused` 同时出现）。</summary>
    [JsonPropertyName("refused")] public bool? Refused { get; set; }
    [JsonPropertyName("stopped_early")] public bool? StoppedEarly { get; set; }
    [JsonPropertyName("elapsed_s")] public double? ElapsedSeconds { get; set; }
    [JsonPropertyName("end_evidence")] public SortieEndEvidence? EndEvidence { get; set; }
    [JsonPropertyName("failure")] public SortieFailure? Failure { get; set; }
    [JsonPropertyName("failure_frames")] public List<string>? FailureFrames { get; set; }
    /// <summary>生产方自报的违例码；消费方不信它，自己再判一次。</summary>
    [JsonPropertyName("contract_violations")] public List<string>? ContractViolations { get; set; }
    [JsonPropertyName("steps")] public List<Dictionary<string, JsonElement>>? Steps { get; set; }
    /// <summary>Optional current-run observation; never used to judge the sortie outcome.</summary>
    [JsonPropertyName("shadow_observation")] public JsonElement? ShadowObservation { get; set; }
}

/// <summary>通关证据链。缺任何一项都不能判 `cleared`。</summary>
public sealed class SortieEndEvidence
{
    [JsonPropertyName("battle_rank")] public string? BattleRank { get; set; }
    [JsonPropertyName("rank_source")] public string? RankSource { get; set; }
    [JsonPropertyName("combat_status")] public bool? CombatStatus { get; set; }
    [JsonPropertyName("stage_observed")] public bool? StageObserved { get; set; }
    [JsonPropertyName("expected_end")] public string? ExpectedEnd { get; set; }
    [JsonPropertyName("withdrawn")] public bool? Withdrawn { get; set; }
    [JsonPropertyName("call_path")] public List<string>? CallPath { get; set; }
}

/// <summary>失败的可定位信息：哪一步、什么错、调用栈尾部、现场帧。</summary>
public sealed class SortieFailure
{
    [JsonPropertyName("step")] public string? Step { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("traceback_tail")] public List<string>? TracebackTail { get; set; }
    [JsonPropertyName("frame")] public string? Frame { get; set; }
}
