using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tasks;

/// <summary>Starts a freshly entered map through the independent C# execution graph.</summary>
public sealed class CampaignResumeTask : ITaskRunner
{
    public string Kind => "campaign_resume";
    public bool RequiresActions => true;
    public void Validate(JsonObject? input)
    {
        TaskInput.Fields(input, "campaign", "hpControl", "reachLevel", "retirement", "emotionMode", "configTask");
        string id = input?["campaign"]?.GetValue<string>() ??
            throw new ArgumentException("Campaign resume requires a compiled rule");
        _ = RuleCatalog.Create(id);
        _ = FleetHealthInput.Read(input!);
        _ = FleetLevelInput.Read(input!);
        _ = RetirementInput.Read(input!);
        if (EmotionInput.Mode(input!, CampaignEmotionMode.Ignore).Calculates())
            throw new NotSupportedException("Calculated emotion requires campaign_run to observe double-book state before entry");
        _ = EmotionInput.ConfigTask(input!);
    }
    public IReadOnlyList<string> Preconditions(TaskRequest request, TaskCapabilities capabilities)
        => FleetLevelInput.Read(request.Input!).Enabled && !capabilities.HasOcrModels ? ["ocr_models"] : [];
    public async ValueTask<TaskResult> RunAsync(TaskRequest request, TaskContext context, CancellationToken token)
    {
        Validate(request.Input);
        var service = context.Campaign ?? throw new NotSupportedException("C# campaign execution service is unavailable");
        var rule = RuleCatalog.Create(request.Input!["campaign"]!.GetValue<string>());
        var configuration = rule.Configure(new CampaignConfiguration
        {
            EmotionMode = EmotionInput.Mode(request.Input, CampaignEmotionMode.Ignore), ConfigTask = EmotionInput.ConfigTask(request.Input),
            Health = FleetHealthInput.Read(request.Input), Levels = FleetLevelInput.Read(request.Input), Retirement = RetirementInput.Read(request.Input)
        });
        var result = await service.ResumeInMapAsync(rule, configuration, token);
        return Describe(request.Id, Kind, rule, result, false);
    }

    internal static TaskResult Describe(string id, string kind, CampaignRule rule,
        CampaignResumeResult result, bool identityVerified)
    {
        var terminalCombat = result.StageReturn?.Combats.LastOrDefault();
        bool withdrawn = result.Withdrawal is { StageConfirmed: true, ExitActions: > 0 } withdrawal &&
            withdrawal.StartedSequence >= 0 && withdrawal.StageSequence > withdrawal.StartedSequence;
        var ended = result.Exit == CampaignLoopExit.Ended || withdrawn;
        // A campaign end may also be a withdrawal. Only the fresh combat-to-stage
        // observation from the verified selected campaign can establish settlement.
        bool cleared = result.Withdrawal is null && identityVerified && ended && result.StageReturn is
        {
            Outcome: MapArrivalOutcome.StageReturned,
            Encounter: MapEncounterKind.Combat,
            FrameSequence: > 0,
            FreshFrames: > 0,
            Combats: [ { Return: CombatReturn.InStage,
                Rank: { IsWinningRank: true, Source: CombatRankSource.BattleStatus },
                CapturedFrames: > 0 } ]
        } arrival && terminalCombat?.Rank is { } winningRank && CombatRankProbe.IsRecognized(winningRank) &&
            arrival.HandledEncounters.Count(encounter => encounter == MapEncounterKind.Combat) == 1 &&
            arrival.HandledEncounters.All(encounter => encounter is MapEncounterKind.Combat or MapEncounterKind.AirRaid);
        string outcome = cleared ? "cleared" : withdrawn ? "withdrawn" : ended ? "ended_unknown" : "incomplete";
        var evidence = JsonSerializer.SerializeToNode(new
        {
            campaign = rule.Id, campaignIdentityVerified = identityVerified,
            loopExit = result.Exit.ToString(), result.BattleCount,
            result.InitialFleet,
            result.AmmoPickups,
            result.Health,
            result.Withdrawal,
            result.Levels,
            result.MechanismReleases,
            result.MovableScans,
            result.MazeWaits,
            stageReturn = result.StageReturn, settlementVerified = cleared, cleared,
            sortie = new
            {
                contract = "sortie-result/1",
                outcome,
                cleared,
                campaign_end = ended,
                stop_reason = withdrawn ? "withdraw_" + result.Withdrawal!.Reason : ended ? null : "round_limit",
                steps = cleared ? new[] { new { step = "execute_a_battle" } } : null,
                end_evidence = terminalCombat is null && !withdrawn ? null : new
                {
                    battle_rank = terminalCombat?.Rank?.Rank.ToString(),
                    rank_source = terminalCombat?.Rank?.Source == CombatRankSource.BattleStatus
                        ? "BATTLE_STATUS_" : terminalCombat?.Rank is null ? null : "EXP_INFO_",
                    combat_status = terminalCombat?.Rank?.Source == CombatRankSource.BattleStatus,
                    stage_observed = withdrawn || terminalCombat?.Return == CombatReturn.InStage,
                    withdrawn,
                    expected_end = "in_stage"
                }
            }
        }, TaskQueue.Json)!.AsObject();
        return new(id, kind, cleared ? TaskOutcome.Succeeded : TaskOutcome.Failed,
            cleared ? "sortie_cleared" : withdrawn ? "sortie_withdrawn" : ended ? "sortie_settlement_unverified" : "campaign_loop_exhausted",
            evidence);
    }
}
