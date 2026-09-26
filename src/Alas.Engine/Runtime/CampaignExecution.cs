using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

/// <summary>Loop termination only. Neither value proves a successful sortie settlement.</summary>
public enum CampaignLoopExit { Ended, Exhausted }

/// <summary>Direct port of CampaignBase execution, independent of the old plan and mixed vision host.</summary>
public sealed class CampaignExecution
{
    private readonly CampaignRule _rule;
    private int _running;
    public CampaignContext Context { get; }
    public CampaignExecution(CampaignRule rule, CampaignConfiguration configuration, ICampaignOperations operations)
    {
        _rule = rule;
        Context = new CampaignContext(new CampaignState(rule.Map), rule.Configure(configuration), operations);
    }

    public CampaignExecution(CampaignRule rule, CampaignConfiguration configuration,
        Func<CampaignState, CampaignConfiguration, ICampaignOperations> createOperations)
    {
        ArgumentNullException.ThrowIfNull(createOperations);
        _rule = rule;
        var state = new CampaignState(rule.Map);
        var effective = rule.Configure(configuration);
        Context = new CampaignContext(state, effective, createOperations(state, effective));
    }

    public async ValueTask<bool> ExecuteBattleAsync()
    {
        int previous = Context.State.BattleCount;
        bool result = false;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                result = await _rule.DispatchAsync(Context);
                break;
            }
            catch (MapEnemyMovedException)
            {
                if (Context.State.BattleCount <= previous) continue;
                result = true;
                break;
            }
        }
        if (!result)
        {
            if (Context.Config.HandleError) await Context.Operations.WithdrawAsync();
            else throw new CampaignScriptException("No combat executed.");
        }
        return result;
    }

    public async ValueTask<CampaignLoopExit> RunAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) != 0)
            throw new InvalidOperationException("A campaign execution belongs to a single sortie and cannot be reused");
        var operations = Context.Operations;
        var state = Context.State;
        await operations.CheckEmotionAsync(_rule.Map.ExpectedBattles);
        await operations.EnterMapAsync();
        if (!state.AutoSearch)
        {
            await operations.HandleFleetLockAsync();
            await operations.InitializeMapAsync(_rule.Map);
        }
        else
        {
            state.BattleCount = 0;
            await operations.ResetLevelsAsync();
            await operations.ReadLevelsAsync();
        }
        for (int round = 0; round < 20; round++)
        {
            try
            {
                if (!state.AutoSearch) await ExecuteBattleAsync();
                else
                {
                    await operations.AutoSearchMoveAsync();
                    await operations.AutoSearchCombatAsync(state.FleetIndex);
                    state.BattleCount++;
                }
            }
            catch (CampaignEndedException) { return CampaignLoopExit.Ended; }
        }
        if (!Context.Config.HandleError) throw new CampaignScriptException("Battle function exhausted.");
        try { await operations.WithdrawAsync(); }
        catch (CampaignEndedException) { }
        return CampaignLoopExit.Exhausted;
    }
}
