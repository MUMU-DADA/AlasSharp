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
        var evidence = JsonSerializer.SerializeToNode(new
        {
            campaign = rule.Id, loopExit = result.Exit.ToString(), result.BattleCount,
            stageReturn = result.StageReturn, settlementVerified = false, cleared = false
        }, TaskQueue.Json)!.AsObject();
        return new(request.Id, Kind, TaskOutcome.Failed,
            result.Exit == CampaignLoopExit.Ended ? "sortie_settlement_unverified" : "campaign_loop_exhausted",
            evidence);
    }
}
