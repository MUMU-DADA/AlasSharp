using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

internal static class CampaignTargeting
{
    // Native unions use object-hash sets. Equal-priority ties have no portable
    // order; retain declaration order within each group for deterministic runs.
    public static CellState? Siren(CampaignState state, CampaignConfiguration configuration)
    {
        if (!configuration.HasSiren && !configuration.HasFortress) return null;
        return state.Cells.Where(grid => grid.IsSiren).Concat(configuration.HasFortress
                ? state.Cells.Where(grid => grid.IsFortress) : []).Distinct().Where(grid => grid.IsAccessible)
            .OrderBy(grid => grid.Weight).ThenBy(grid => configuration.Fleet2 != 0 ? grid.Cost2 : grid.Cost).FirstOrDefault();
    }
    public static CellState? AnyEnemyBySecondFleetCost(CampaignState state, CampaignConfiguration configuration)
        => state.Cells.Where(grid => grid.IsEnemy && !grid.IsBoss)
            .Concat(configuration.HasSiren ? state.Cells.Where(grid => grid.IsSiren) : [])
            .Concat(configuration.HasFortress ? state.Cells.Where(grid => grid.IsFortress) : [])
            .Distinct().Where(grid => grid.IsAccessible).OrderBy(grid => grid.Cost2).FirstOrDefault();
}
