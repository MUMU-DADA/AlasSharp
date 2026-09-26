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
        var result = await service.ResumeInMapAsync(rule, token);
        var terminalCombat = result.StageReturn?.Combats.LastOrDefault();
        var ended = result.Exit == CampaignLoopExit.Ended;
        var evidence = JsonSerializer.SerializeToNode(new
        {
            campaign = rule.Id, campaignIdentityVerified = false,
            loopExit = result.Exit.ToString(), result.BattleCount,
            stageReturn = result.StageReturn, settlementVerified = false, cleared = false,
            sortie = new
            {
                contract = "sortie-result/1",
                outcome = ended ? "ended_unknown" : "incomplete",
                cleared = false,
                campaign_end = ended,
                stop_reason = ended ? null : "round_limit",
                end_evidence = terminalCombat is null ? null : new
                {
                    battle_rank = terminalCombat.Rank?.Rank.ToString(),
                    rank_source = terminalCombat.Rank?.Source == CombatRankSource.BattleStatus
                        ? "BATTLE_STATUS_" : terminalCombat.Rank is null ? null : "EXP_INFO_",
                    combat_status = terminalCombat.Rank?.Source == CombatRankSource.BattleStatus,
                    stage_observed = terminalCombat.Return == CombatReturn.InStage,
                    expected_end = "in_stage"
                }
            }
        }, TaskQueue.Json)!.AsObject();
        return new(request.Id, Kind, TaskOutcome.Failed,
            result.Exit == CampaignLoopExit.Ended ? "sortie_settlement_unverified" : "campaign_loop_exhausted",
            evidence);
    }
}
