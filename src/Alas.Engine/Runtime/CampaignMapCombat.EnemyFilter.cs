using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed partial class CampaignMapCombat
{
    public ValueTask<bool> ClearFilterEnemyAsync(EnemyFilter filter, int preserve = 0, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!state.IsMapInitialized) throw new InvalidOperationException("Initialize the map before selecting a combat target");
        ArgumentNullException.ThrowIfNull(filter);
        if (configuration.HasMovableNormalEnemy) return ClearAnyEnemyBySecondFleetCostAsync(token);
        if (configuration.EnemyPriority == EnemyScalePriority.StrongestFirst)
        {
            filter = EnemyFilter.StrongestFirst;
            preserve = 0;
        }
        else if (configuration.EnemyPriority == EnemyScalePriority.WeakestFirst) filter = EnemyFilter.WeakestFirst;
        // Unlike clear_enemy, native includes overlapping boss flags and does
        // not inject the full-clear scale preference into this ordered filter.
        var candidates = Order(state.Cells.Where(grid => grid.IsEnemy && grid.IsAccessible));
        var target = filter.Apply(candidates, preserve).FirstOrDefault();
        return target is null ? ValueTask.FromResult(false) : FightAsync(target, token, MapCombatExpectation.Enemy);
    }
}
