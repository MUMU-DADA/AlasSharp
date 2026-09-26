using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

/// <summary>On-map target selection and movement. Stage return is evidence, not a sortie verdict.</summary>
public sealed class CampaignMapCombat(CampaignState state, CampaignConfiguration configuration,
    MapMovement movement, MapScanner scanner)
{
    public static readonly SourceFile Source = new("module/map/map.py",
        "187a5ee7d8fbde3c944681216fd2ac75f68036716b17db5a8bb43fdd42de5365");
    public MapArrivalResult? StageReturn { get; private set; }

    public async ValueTask<bool> ClearMysteriesAsync(CancellationToken token = default)
    {
        while (true)
        {
            var target = state.Cells.Where(grid => grid.IsMystery && grid.IsAccessible)
                .OrderBy(grid => grid.Cost).FirstOrDefault();
            if (target is null) return false;
            var route = state.Paths.FindRoute(target.Location, turningOptimize: configuration.HasAmbush);
            if (!route.IsReachable || route.Waypoints.Count == 0)
                throw new InvalidOperationException("Selected mystery has no confirmed fleet route");
            for (int index = 0; index < route.Waypoints.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var cell = route.Waypoints[index];
                var result = index == route.Waypoints.Count - 1
                    ? await movement.CollectMysteryAsync(cell, token: token)
                    : await movement.MoveAsync(cell, token: token);
                if (result.Outcome != MapMoveOutcome.Committed)
                    throw new CampaignScriptException($"Mystery route to {cell} ended as {result.Outcome}");
            }
        }
    }

    public ValueTask<bool> ClearEnemyAsync(CancellationToken token = default)
    {
        var candidates = state.Cells.Where(grid => grid.IsEnemy && !grid.IsBoss && grid.IsAccessible).ToArray();
        bool strongest = configuration.EnemyPriority == EnemyScalePriority.StrongestFirst ||
            configuration.EnemyPriority == EnemyScalePriority.Default && configuration.ClearAllThisTime;
        if (strongest) candidates = FirstPresentScale(candidates, [3, 2, 1, 0]);
        else if (configuration.EnemyPriority == EnemyScalePriority.WeakestFirst)
            candidates = FirstPresentScale(candidates, [1, 2, 3, 0]);
        return candidates.Length == 0 ? ValueTask.FromResult(false) : FightAsync(Order(candidates)[0], token);
    }

    public async ValueTask<bool> ClearBossAsync(CancellationToken token = default)
    {
        var candidates = state.Cells.Where(grid => grid.IsBoss && grid.IsAccessible ||
            grid.MayBoss && grid.IsCaughtBySiren).Distinct().ToArray();
        if (candidates.Length == 0)
            candidates = state.Cells.Where(grid => grid.MayBoss && grid.IsEnemy && grid.IsAccessible).ToArray();
        if (candidates.Length > 0) await FightAsync(Order(candidates)[0], token);
        return await ClearPotentialBossAsync(token);
    }

    private async ValueTask<bool> ClearPotentialBossAsync(CancellationToken token)
    {
        var potential = state.Cells.Where(grid => grid.MayBoss).ToArray();
        var tried = new HashSet<Cell>();
        while (true)
        {
            var current = state.FleetIndex == 1 ? state.Fleet1Location : state.Fleet2Location;
            var target = Order(potential.Where(grid => grid.IsAccessible && grid.Location != current &&
                !tried.Contains(grid.Location))).FirstOrDefault();
            if (target is null) break;
            var route = state.Paths.FindRoute(target.Location, turningOptimize: configuration.HasAmbush);
            if (!route.IsReachable || route.Waypoints.Count == 0)
                throw new InvalidOperationException("Potential boss has no confirmed fleet route");
            int previousBattles = state.BattleCount;
            for (int index = 0; index < route.Waypoints.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var cell = route.Waypoints[index];
                bool final = index == route.Waypoints.Count - 1;
                var options = potential.Length == 1
                    ? new MapArrivalOptions(TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(20)) : null;
                var result = final ? await movement.ProbeBossAsync(cell, options, token) :
                    await movement.MoveAsync(cell, token: token);
                if (result.Outcome == MapMoveOutcome.StageReturned)
                {
                    StageReturn = result.Arrival;
                    throw new CampaignEndedException("Winning boss search returned to stage; sortie settlement remains unverified");
                }
                if (result.Outcome != MapMoveOutcome.Committed)
                    throw new CampaignScriptException($"Potential boss route to {cell} ended as {result.Outcome}");
            }
            tried.Add(target.Location);
            if (state.BattleCount > previousBattles)
            {
                await ScanAfterCombatAsync(token);
                return true;
            }
        }
        if (potential.Any(grid => !grid.IsAccessible && grid.Location !=
            (state.FleetIndex == 1 ? state.Fleet1Location : state.Fleet2Location)))
            throw new NotSupportedException("Potential boss roadblock clearing is not yet ported");
        return false;
    }

    private static CellState[] FirstPresentScale(CellState[] candidates, int[] priority)
    {
        foreach (int scale in priority)
        {
            var selected = candidates.Where(grid => grid.EnemyScale == scale).ToArray();
            if (selected.Length > 0) return selected;
        }
        return [];
    }

    private static CellState[] Order(IEnumerable<CellState> candidates)
        => candidates.OrderBy(grid => grid.Weight).ThenBy(grid => grid.Cost).ToArray();

    private async ValueTask<bool> FightAsync(CellState target, CancellationToken token)
    {
        if (!state.IsMapInitialized) throw new InvalidOperationException("Initialize the map before selecting a combat target");
        var route = state.Paths.FindRoute(target.Location, turningOptimize: configuration.HasAmbush);
        if (!route.IsReachable || route.Waypoints.Count == 0)
            throw new InvalidOperationException("Selected combat target has no confirmed fleet route");
        for (int index = 0; index < route.Waypoints.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var cell = route.Waypoints[index];
            bool final = index == route.Waypoints.Count - 1;
            var result = final ? await movement.FightAsync(cell, token: token) :
                await movement.MoveAsync(cell, token: token);
            if (result.Outcome == MapMoveOutcome.StageReturned)
            {
                StageReturn = result.Arrival;
                throw new CampaignEndedException("Winning combat returned to stage; sortie settlement remains unverified");
            }
            if (result.Outcome != MapMoveOutcome.Committed)
                throw new CampaignScriptException($"Map movement to {cell} ended as {result.Outcome}");
        }
        await ScanAfterCombatAsync(token);
        return true;
    }

    private async ValueTask ScanAfterCombatAsync(CancellationToken token)
    {
        await scanner.ScanAsync(state.Progress, TimeSpan.FromMinutes(2),
            fleet: new FleetScanOptions(Fleet2Enabled: configuration.Fleet2 != 0), token: token);
        var current = state.FleetIndex == 1 ? state.Fleet1Location : state.Fleet2Location;
        if (current is not { } location) throw new InvalidDataException("Combat completed without a fleet location");
        state.Paths.ComputeFleetCosts([new(1, state.Fleet1Location), new(2, state.Fleet2Location)],
            location, configuration.HasAmbush);
    }
}
