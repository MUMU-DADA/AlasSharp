using System.Text.Json;
using System.Text.Json.Serialization;

namespace Alas.Engine.Contracts;

/// <summary>
/// sortie-result/1: the single result contract owned by the Engine.
/// The Python contract is an offline oracle only; product result validation lives here.
/// </summary>
public static class SortieContract
{
    public const string Version = "sortie-result/1";
    public static readonly string[] Outcomes =
        ["cleared", "withdrawn", "defeated", "ended_unknown", "error", "incomplete", "refused"];
    public static readonly string[] WinRanks = ["S", "A", "B"];
    public static readonly string[] LossRanks = ["C", "D"];
    public static readonly string[] LimitReasons =
        ["round_limit", "time_limit", "upstream_returned_without_clear", "stopped_after_map_init"];
    public static readonly string[] BattleSteps = ["execute_a_battle", "auto_search_execute_a_battle"];
    public static readonly string[] ActionSteps =
        ["prepare_campaign_navigation", "abort_unfinished", "ensure_campaign_ui", "prepare_campaign_run",
         "enter_map", "handle_map_fleet_lock", "map_init", "execute_a_battle",
         "auto_search_execute_a_battle", "withdraw"];

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

    public static List<string> Violations(SortieResult? result, string? artifactRoot = null)
    {
        if (result is null) return [Codes.OutcomeMissing];
        var problems = new List<string>();
        if (result.Contract is not null && result.Contract != Version)
            problems.Add(Codes.ContractVersionMismatch);

        string? outcome = result.Outcome;
        bool cleared = result.Cleared == true;
        bool dryRun = result.DryRun;
        bool campaignEnd = result.CampaignEnd == true;
        string reason = result.Reason ?? result.EndReason ?? "";
        var steps = result.Steps ?? [];
        var evidence = result.EndEvidence;
        if (outcome is null)
        {
            if (!dryRun) problems.Add(Codes.OutcomeMissing);
        }
        else if (!Outcomes.Contains(outcome, StringComparer.Ordinal)) problems.Add(Codes.UnknownOutcome);
        if (cleared != (outcome == "cleared")) problems.Add(Codes.ClearedFlagMismatch);

        if (outcome == "cleared")
        {
            if (!HasContent(evidence)) problems.Add(Codes.ClearedWithoutSettlementEvidence);
            else
            {
                if (evidence!.BattleRank is null || !WinRanks.Contains(evidence.BattleRank, StringComparer.Ordinal))
                    problems.Add(Codes.ClearedRequiresWinRank);
                if (string.IsNullOrEmpty(evidence.RankSource)) problems.Add(Codes.ClearedRequiresRankSource);
                if (evidence.CombatStatus != true) problems.Add(Codes.ClearedRequiresCombatStatus);
                if (evidence.StageObserved != true) problems.Add(Codes.ClearedRequiresStageObserved);
                if (evidence.Withdrawn == true) problems.Add(Codes.ClearedRequiresNoWithdrawal);
            }
            if (!steps.Any(step => StepName(step) is { } name && BattleSteps.Contains(name, StringComparer.Ordinal)
                                   && string.IsNullOrEmpty(StepError(step))))
                problems.Add(Codes.ClearedRequiresExecutedBattle);
        }
        if (outcome == "withdrawn")
        {
            bool withdrew = evidence?.Withdrawn == true;
            if (!(withdrew || reason.Contains("withdraw", StringComparison.OrdinalIgnoreCase)))
                problems.Add(Codes.WithdrawnRequiresWithdrawalEvidence);
        }
        if (outcome == "defeated" && (evidence?.BattleRank is not { } rank || !LossRanks.Contains(rank, StringComparer.Ordinal)))
            problems.Add(Codes.DefeatedRequiresLossRank);
        if (outcome == "ended_unknown" && !campaignEnd) problems.Add(Codes.EndedUnknownRequiresCampaignEnd);
        if (outcome == "error")
        {
            var failures = FailingSteps(steps);
            var top = HasContent(result.Failure) ? result.Failure : failures.FirstOrDefault();
            if (top is null) problems.Add(Codes.ErrorRequiresFailure);
            else if (top.TracebackTail is not { Count: > 0 }) problems.Add(Codes.ErrorRequiresTraceback);
        }
        foreach (var step in steps)
        {
            if (!string.IsNullOrEmpty(StepError(step)) &&
                !(step.TryGetValue("traceback_tail", out var tail) && tail.ValueKind == JsonValueKind.Array && tail.GetArrayLength() > 0))
            {
                problems.Add(Codes.ErrorStepWithoutTraceback);
                break;
            }
        }
        if (outcome == "incomplete" && (result.StopReason is null || !LimitReasons.Contains(result.StopReason, StringComparer.Ordinal)))
            problems.Add(Codes.IncompleteRequiresLimitReason);
        if (outcome == "refused" && string.IsNullOrEmpty(reason)) problems.Add(Codes.RefusedRequiresReason);

        var frames = DeclaredFrames(result, steps);
        var listed = result.FailureFrames ?? [];
        foreach (var frame in frames)
        {
            if (!listed.Contains(frame, StringComparer.Ordinal)) problems.Add(Codes.FailureFrameNotListed);
            if (!FrameExists(frame, artifactRoot)) problems.Add(Codes.FailureFrameMissingOnDisk);
        }
        if (dryRun && steps.Any(step => StepName(step) is { } name && ActionSteps.Contains(name, StringComparer.Ordinal)))
            problems.Add(Codes.DryRunMustNotExecute);
        return problems;
    }

    public static string Describe(SortieResult result, string? artifactRoot = null)
    {
        var violations = Violations(result, artifactRoot);
        return violations.Count == 0
            ? $"合规 outcome={result.Outcome ?? "<无>"} cleared={result.Cleared == true}"
            : $"违例 {violations.Count} 项: {string.Join(", ", violations)}";
    }

    private static bool HasContent(SortieEndEvidence? evidence) => evidence is not null &&
        (evidence.BattleRank is not null || evidence.RankSource is not null || evidence.CombatStatus is not null ||
         evidence.StageObserved is not null || evidence.ExpectedEnd is not null || evidence.Withdrawn is not null ||
         evidence.CallPath is { Count: > 0 });
    private static bool HasContent(SortieFailure? failure) => failure is not null &&
        (failure.Step is not null || failure.Error is not null || failure.Frame is not null || failure.TracebackTail is { Count: > 0 });
    private static bool FrameExists(string frame, string? artifactRoot)
    {
        try
        {
            string path = Path.IsPathRooted(frame) ? frame : artifactRoot is null ? frame : Path.Combine(artifactRoot, frame);
            return artifactRoot is null && !Path.IsPathRooted(frame) || File.Exists(path);
        }
        catch (Exception) { return true; }
    }
    private static List<SortieFailure> FailingSteps(List<Dictionary<string, JsonElement>> steps)
        => steps.Where(step => !string.IsNullOrEmpty(StepError(step))).Select(step => new SortieFailure
        {
            Step = StepName(step), Error = StepError(step), TracebackTail = ReadStringArray(step, "traceback_tail"),
            Frame = ReadString(step, "failure_frame")
        }).ToList();
    private static List<string> DeclaredFrames(SortieResult result, List<Dictionary<string, JsonElement>> steps)
        => new[] { result.Failure?.Frame }.Concat(steps.Select(step => ReadString(step, "failure_frame")))
            .Where(frame => !string.IsNullOrEmpty(frame)).Select(frame => frame!).ToList();
    private static string? StepName(Dictionary<string, JsonElement> step) => ReadString(step, "step");
    private static string? StepError(Dictionary<string, JsonElement> step) => ReadString(step, "error");
    private static string? ReadString(Dictionary<string, JsonElement> step, string key)
        => step.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static List<string> ReadStringArray(Dictionary<string, JsonElement> step, string key)
        => step.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToList()
            : [];
}

public sealed class SortieResult
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
    [JsonPropertyName("refused")] public bool? Refused { get; set; }
    [JsonPropertyName("stopped_early")] public bool? StoppedEarly { get; set; }
    [JsonPropertyName("elapsed_s")] public double? ElapsedSeconds { get; set; }
    [JsonPropertyName("end_evidence")] public SortieEndEvidence? EndEvidence { get; set; }
    [JsonPropertyName("failure")] public SortieFailure? Failure { get; set; }
    [JsonPropertyName("failure_frames")] public List<string>? FailureFrames { get; set; }
    [JsonPropertyName("contract_violations")] public List<string>? ContractViolations { get; set; }
    [JsonPropertyName("steps")] public List<Dictionary<string, JsonElement>>? Steps { get; set; }
    [JsonPropertyName("shadow_observation")] public JsonElement? ShadowObservation { get; set; }
}

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

public sealed class SortieFailure
{
    [JsonPropertyName("step")] public string? Step { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("traceback_tail")] public List<string>? TracebackTail { get; set; }
    [JsonPropertyName("frame")] public string? Frame { get; set; }
}
