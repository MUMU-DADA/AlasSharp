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
        TaskInput.Fields(input, "campaign");
        string id = input?["campaign"]?.GetValue<string>() ??
            throw new ArgumentException("Campaign resume requires a compiled rule");
        _ = RuleCatalog.Create(id);
    }
    public IReadOnlyList<string> Preconditions(TaskRequest request, TaskCapabilities capabilities) => [];
    public async ValueTask<TaskResult> RunAsync(TaskRequest request, TaskContext context, CancellationToken token)
    {
        Validate(request.Input);
        var service = context.Campaign ?? throw new NotSupportedException("C# campaign execution service is unavailable");
        var rule = RuleCatalog.Create(request.Input!["campaign"]!.GetValue<string>());
        var result = await service.ResumeInMapAsync(rule,
            new CampaignConfiguration { EmotionMode = CampaignEmotionMode.Ignore }, token);
        return Describe(request.Id, Kind, rule, result, false);
    }

    internal static TaskResult Describe(string id, string kind, CampaignRule rule,
        CampaignResumeResult result, bool identityVerified)
    {
        var terminalCombat = result.StageReturn?.Combats.LastOrDefault();
        var ended = result.Exit == CampaignLoopExit.Ended;
        // A campaign end may also be a withdrawal. Only the fresh combat-to-stage
        // observation from the verified selected campaign can establish settlement.
        bool cleared = identityVerified && ended && result.StageReturn is
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
        string outcome = cleared ? "cleared" : ended ? "ended_unknown" : "incomplete";
        var evidence = JsonSerializer.SerializeToNode(new
        {
            campaign = rule.Id, campaignIdentityVerified = identityVerified,
            loopExit = result.Exit.ToString(), result.BattleCount,
            stageReturn = result.StageReturn, settlementVerified = cleared, cleared,
            sortie = new
            {
                contract = "sortie-result/1",
                outcome,
                cleared,
                campaign_end = ended,
                stop_reason = ended ? null : "round_limit",
                steps = cleared ? new[] { new { step = "execute_a_battle" } } : null,
                end_evidence = terminalCombat is null ? null : new
                {
                    battle_rank = terminalCombat.Rank?.Rank.ToString(),
                    rank_source = terminalCombat.Rank?.Source == CombatRankSource.BattleStatus
                        ? "BATTLE_STATUS_" : terminalCombat.Rank is null ? null : "EXP_INFO_",
                    combat_status = terminalCombat.Rank?.Source == CombatRankSource.BattleStatus,
                    stage_observed = terminalCombat.Return == CombatReturn.InStage,
                    withdrawn = false,
                    expected_end = "in_stage"
                }
            }
        }, TaskQueue.Json)!.AsObject();
        return new(id, kind, cleared ? TaskOutcome.Succeeded : TaskOutcome.Failed,
            cleared ? "sortie_cleared" : ended ? "sortie_settlement_unverified" : "campaign_loop_exhausted",
            evidence);
    }
}
