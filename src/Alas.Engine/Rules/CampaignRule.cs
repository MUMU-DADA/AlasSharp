using System.Collections.Immutable;
using Alas.Engine.Navigation;
using Alas.Engine.Runtime;

namespace Alas.Engine.Rules;

/// <summary>Compiled rule behavior. No JSON instructions, dynamic method names, or Python execution.</summary>
public abstract class CampaignRule
{
    public delegate ValueTask<bool> BattleHook(CampaignContext context);
    public abstract string Id { get; }
    public virtual string? StageName => null;
    public virtual MapOverlayRules Overlays { get; } = new();
    public abstract MapDefinition Map { get; }
    public abstract ImmutableArray<SourceFile> Sources { get; }
    protected abstract IReadOnlyDictionary<int, BattleHook> Hooks { get; }
    public virtual CampaignConfiguration Configure(CampaignConfiguration input) => input;
    public virtual bool CountMysteryItems => true;
    /// <summary>Compiled map_data_init override, after common declarations and before the initial scan.</summary>
    public virtual void InitializeMapState(CampaignState state, MapInitialization options) { }
    public virtual ValueTask<bool> AllowExperienceAsync(IUiDriver ui, CancellationToken token)
        => ValueTask.FromResult(true);
    public virtual ValueTask RefocusBossAsync(CampaignContext context) => context.Operations.RefocusBossAsync(null);
    public virtual ValueTask<bool> BattleDefaultAsync(CampaignContext context) => context.Operations.ClearEnemyAsync();
    public virtual ValueTask<bool> BattleBossAsync(CampaignContext context) => context.Operations.BruteClearBossAsync();
    /// <summary>Some upstream campaigns select an exact battle hook even while
    /// clear-all is enabled. The default keeps the common full-clear scheduler.</summary>
    protected virtual bool UseHookInClearAll(CampaignContext context)
        => false;

    /// <summary>Port of CampaignBase.battle_function, including all three Config.when variants.</summary>
    public async ValueTask<bool> DispatchAsync(CampaignContext context)
    {
        var state = context.State;
        var operations = context.Operations;
        // Native decorator matches poor-data only when clear-all is false.
        if (context.Config.PoorMapData && !context.Config.ClearAllThisTime)
        {
            if (await operations.BreakSirenCaughtAsync()) return true;
            await operations.ClearMysteriesAsync();
            if (state.BattleCount >= 3) await operations.PickUpAmmoAsync();
            if (state.HasBoss) return await operations.BruteClearBossAsync();
            if (await operations.ClearSirenAsync()) return true;
            return await operations.ClearEnemyAsync();
        }
        if (context.Config.ClearAllThisTime && UseHookInClearAll(context) &&
            Hooks.TryGetValue(state.BattleCount, out var clearHook))
            return await clearHook(context);
        if (context.Config.ClearAllThisTime)
        {
            if (await operations.BreakSirenCaughtAsync()) return true;
            await operations.ClearMysteriesAsync();
            if (state.BattleCount >= 3) await operations.PickUpAmmoAsync();
            if (!state.HasNonBossEnemy) return await BattleBossAsync(context);
            if (context.Config.HasMovableNormalEnemy)
            {
                if (await operations.ClearAnyEnemyBySecondFleetCostAsync()) return true;
                return await BattleDefaultAsync(context);
            }
            if (await operations.ClearBouncingEnemyAsync()) return true;
            if (await operations.ClearSirenAsync()) return true;
            await operations.ClearMechanismAsync();
            return await BattleDefaultAsync(context);
        }
        // Exactly ten candidates; an arbitrarily old hook must not remain selected.
        for (int extra = 0; extra < 10; extra++)
            if (Hooks.TryGetValue(state.BattleCount - extra, out var hook)) return await hook(context);
        return await BattleDefaultAsync(context);
    }
}
