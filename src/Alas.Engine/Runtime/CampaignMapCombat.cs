using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

/// <summary>On-map target selection and movement. Stage return is evidence, not a sortie verdict.</summary>
public sealed class CampaignMapCombat(CampaignState state, CampaignConfiguration configuration,
    MapMovement movement, MapScanner scanner, Func<int, CancellationToken, ValueTask>? waitEmotion = null,
    Func<int, CancellationToken, ValueTask>? switchFleet = null,
    Func<CancellationToken, ValueTask>? ensureEdges = null)
{
    public static readonly SourceFile Source = new("module/map/map.py",
        "187a5ee7d8fbde3c944681216fd2ac75f68036716b17db5a8bb43fdd42de5365");
    public MapArrivalResult? StageReturn { get; private set; }
    private readonly List<AmmoPickupEvidence> _ammoPickups = [];
    public IReadOnlyList<AmmoPickupEvidence> AmmoPickups => _ammoPickups.AsReadOnly();

    public async ValueTask<bool> ClearMechanismAsync(IReadOnlyList<Cell>? grids = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!configuration.HasLandBased) return false;
        if (!state.IsMapInitialized) throw new InvalidOperationException("Initialize the map before selecting a mechanism");
        // Native treats an empty supplied set like no set and chooses all triggers.
        var candidates = grids is { Count: > 0 } ? grids.Select(cell => state[cell]) : state.Cells;
        var target = Order(candidates.Where(grid => grid.IsMechanismTrigger && !grid.IsMechanismBlock && grid.IsAccessible)).FirstOrDefault();
        if (target is null) return false;
        var route = state.Paths.FindRoute(target.Location, FleetRoles.Step(state.FleetIndex, configuration), turningOptimize: configuration.HasAmbush);
        if (!route.IsReachable || route.Waypoints.Count == 0)
            throw new InvalidOperationException("Selected mechanism has no confirmed fleet route");
        foreach (var cell in route.Waypoints)
        {
            await WaitForMazeAsync(cell, token);
            var grid = state[cell];
            // Native goto also handles an enemy or mystery occupying the trigger.
            var result = grid.IsEnemy || grid.IsSiren || grid.IsBoss || grid.IsFortress
                ? await movement.FightAsync(cell, token: token, expectation: MapCombatExpectation.None) : grid.IsMystery
                ? await movement.CollectMysteryAsync(cell, token: token) : await movement.MoveAsync(cell, token: token);
            if (result.Outcome == MapMoveOutcome.StageReturned)
            {
                StageReturn = result.Arrival;
                throw new CampaignEndedException("Winning encounter on mechanism route returned to stage");
            }
            if (result.Outcome != MapMoveOutcome.Committed)
                throw new CampaignScriptException($"Mechanism route to {cell} ended as {result.Outcome}");
        }
        // This is a topology change, not a completed battle. The campaign loop
        // retries selection against refreshed costs without counting a battle.
        throw new MapEnemyMovedException();
    }

    public async ValueTask<bool> PickUpAmmoAsync(CancellationToken token = default)
    {
        if (!state.IsMapInitialized) throw new InvalidOperationException("Initialize the map before picking up ammo");
        token.ThrowIfCancellationRequested();
        var target = state.Cells.FirstOrDefault(grid => grid.MayAmmo);
        if (target is null || state.AmmoCount <= 0 || !target.IsAccessible) return false;
        var route = state.Paths.FindRoute(target.Location, FleetRoles.Step(state.FleetIndex, configuration), turningOptimize: configuration.HasAmbush);
        if (!route.IsReachable || route.Waypoints.Count == 0)
            throw new InvalidOperationException("Selected supply has no confirmed fleet route");
        for (int index = 0; index < route.Waypoints.Count; index++)
        {
            var cell = route.Waypoints[index];
            await WaitForMazeAsync(cell, token);
            bool final = index == route.Waypoints.Count - 1;
            var result = final ? await movement.CollectAmmoAsync(cell, token: token) :
                await movement.MoveAsync(cell, token: token);
            if (result.Outcome != MapMoveOutcome.Committed)
                throw new CampaignScriptException($"Supply route to {cell} ended as {result.Outcome}");
            if (final)
                _ammoPickups.Add(result.AmmoPickup ?? throw new InvalidDataException("Supply move returned no accounting evidence"));
        }
        // Upstream returns None after pickup: this is not a battle action.
        return false;
    }

    public async ValueTask<bool> ClearMysteriesAsync(CancellationToken token = default)
    {
        while (true)
        {
            var target = state.Cells.Where(grid => grid.IsMystery && grid.IsAccessible)
                .OrderBy(grid => grid.Cost).FirstOrDefault();
            if (target is null) return false;
            var route = state.Paths.FindRoute(target.Location, FleetRoles.Step(state.FleetIndex, configuration), turningOptimize: configuration.HasAmbush);
            if (!route.IsReachable || route.Waypoints.Count == 0)
                throw new InvalidOperationException("Selected mystery has no confirmed fleet route");
            for (int index = 0; index < route.Waypoints.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var cell = route.Waypoints[index];
                await WaitForMazeAsync(cell, token);
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
        return candidates.Length == 0 ? ValueTask.FromResult(false) : FightAsync(Order(candidates)[0], token, MapCombatExpectation.Enemy);
    }

    public async ValueTask<bool> ClearBossAsync(CancellationToken token = default)
    {
        var candidates = state.Cells.Where(grid => grid.IsBoss && grid.IsAccessible ||
            grid.MayBoss && grid.IsCaughtBySiren).Distinct().ToArray();
        if (candidates.Length == 0)
            candidates = state.Cells.Where(grid => grid.MayBoss && grid.IsEnemy && grid.IsAccessible).ToArray();
        if (candidates.Length > 0) await FightAsync(Order(candidates)[0], token, MapCombatExpectation.Boss);
        return await ClearPotentialBossAsync(token);
    }

    public ValueTask<bool> ClearSirenAsync(CancellationToken token = default)
    {
        var selected = CampaignTargeting.Siren(state, configuration);
        return selected is null ? ValueTask.FromResult(false) : FightAsync(selected, token,
            selected.IsFortress ? MapCombatExpectation.Fortress : MapCombatExpectation.Siren);
    }

    public ValueTask<bool> ClearAnyEnemyBySecondFleetCostAsync(CancellationToken token = default)
    {
        // CampaignBase's full-clear movable-normal branch passes sort=('cost_2',), without weight.
        var selected = CampaignTargeting.AnyEnemyBySecondFleetCost(state, configuration);
        return selected is null ? ValueTask.FromResult(false) : FightAsync(selected, token,
            selected.IsFortress ? MapCombatExpectation.Fortress : selected.IsSiren ? MapCombatExpectation.Siren : MapCombatExpectation.Enemy);
    }

    public async ValueTask<bool> BreakSirenCaughtAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (FleetRoles.BossIndex(configuration) != 2 || !configuration.HasSiren || !configuration.HasMovableEnemy ||
            !state.Cells.Any(grid => grid.IsCaughtBySiren)) return false;
        if (state.Fleet2Location is not { } second || !state[second].IsCaughtBySiren)
        {
            foreach (var grid in state.Cells) grid.IsCaughtBySiren = false;
            return false;
        }
        if (ensureEdges is null) throw new NotSupportedException("Siren rescue requires camera edge localization");
        await SwitchFleetAsync(2, token);
        await ensureEdges(token);
        await FightAsync(state[second], token, MapCombatExpectation.Enemy);
        // Native does not restore fleet 1 if combat/round change exits the call.
        await SwitchFleetAsync(1, token);
        foreach (var grid in state.Cells) grid.IsCaughtBySiren = false;
        return true;
    }

    public async ValueTask<bool> ClearBouncingEnemyAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!configuration.HasBouncingEnemy) return false;
        var route = state.Mechanisms.BouncingRoutes.FirstOrDefault(cells =>
            cells.Any(cell => state[cell].MayBouncingEnemy && state[cell].IsAccessible));
        if (route.IsDefaultOrEmpty) return false;
        int before = state.BattleCount;
        // Native enumerate(cycle(route)) checks n >= 12 after the action: 13 visits.
        for (int trial = 0; trial <= 12; trial++)
        {
            await WaitEmotionAsync(token);
            var target = route[trial % route.Length];
            var path = state.Paths.FindRoute(target, FleetRoles.Step(state.FleetIndex, configuration), configuration.HasAmbush);
            if (!path.IsReachable || path.Waypoints.Count == 0)
                throw new CampaignScriptException("Bouncing route visit has no confirmed fleet path");
            for (int index = 0; index < path.Waypoints.Count; index++)
            {
                var cell = path.Waypoints[index];
                await WaitForMazeAsync(cell, token);
                var result = index == path.Waypoints.Count - 1
                    ? await movement.ProbeBouncingAsync(cell, token) : await movement.MoveAsync(cell, token: token);
                if (result.Outcome == MapMoveOutcome.StageReturned)
                {
                    StageReturn = result.Arrival;
                    throw new CampaignEndedException("Bouncing encounter returned to stage");
                }
                if (result.Outcome != MapMoveOutcome.Committed)
                    throw new CampaignScriptException($"Bouncing route to {cell} ended as {result.Outcome}");
            }
            if (state.BattleCount <= before) continue;
            foreach (var cell in route) state[cell].MayBouncingEnemy = false;
            await ScanAfterCombatAsync(token);
            return true;
        }
        return false;
    }

    public async ValueTask<bool> BruteClearBossAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var boss = state.Cells.FirstOrDefault(grid => grid.IsBoss);
        if (boss is not null)
        {
            int fleet = FleetRoles.BossIndex(configuration);
            var plan = MapRoadblocks.Find(state, boss.Location, fleet, token);
            if (!plan.Reachable) throw new CampaignScriptException("Boss is separated by non-removable map obstacles");
            if (plan.Enemies.Count > 0)
            {
                if (fleet == 2 && state.Fleet2Location is { } second)
                {
                    var meet = MapRoadblocks.Find(state, second, 1, token);
                    if (meet.Reachable && meet.Enemies.Count > 0)
                        return await ClearRoadblockAsync(meet, token);
                }
                return await ClearRoadblockAsync(plan, token);
            }
            await SwitchFleetAsync(fleet, token);
            return await ClearBossAsync(token);
        }
        if (state.Cells.FirstOrDefault(grid => grid.MayBoss && grid.IsCaughtBySiren) is { } caught)
        {
            await SwitchFleetAsync(2, token);
            return await FightAsync(caught, token, MapCombatExpectation.Enemy);
        }
        return await ClearPotentialBossAsync(token);
    }

    private async ValueTask SwitchFleetAsync(int fleet, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (state.FleetIndex == fleet) return;
        if (switchFleet is null) throw new NotSupportedException("Campaign fleet switching is unavailable");
        await switchFleet(fleet, token);
        if (state.FleetIndex != fleet) throw new InvalidDataException("Campaign fleet switch did not commit its observed identity");
    }

    private ValueTask<bool> ClearRoadblockAsync(MapRoadblockPlan plan, CancellationToken token)
    {
        // Native temporarily removes enemies and can leave hypothetical costs active. Only an
        // actually reachable member of the same minimal set can be the next physical battle.
        var target = Order(plan.Enemies.Select(cell => state[cell]).Where(grid => grid.IsAccessible)).FirstOrDefault()
            ?? throw new CampaignScriptException("No roadblock in the minimum removal set is reachable by the active fleet");
        return FightAsync(target, token, MapCombatExpectation.Enemy);
    }

    private async ValueTask<bool> ClearPotentialBossAsync(CancellationToken token)
    {
        await SwitchFleetAsync(FleetRoles.BossIndex(configuration), token);
        var potential = state.Cells.Where(grid => grid.MayBoss).ToArray();
        var tried = new HashSet<Cell>();
        while (true)
        {
            var current = state.FleetIndex == 1 ? state.Fleet1Location : state.Fleet2Location;
            var target = Order(potential.Where(grid => grid.IsAccessible && grid.Location != current &&
                !tried.Contains(grid.Location))).FirstOrDefault();
            if (target is null) break;
            var route = state.Paths.FindRoute(target.Location, FleetRoles.Step(state.FleetIndex, configuration), turningOptimize: configuration.HasAmbush);
            if (!route.IsReachable || route.Waypoints.Count == 0)
                throw new InvalidOperationException("Potential boss has no confirmed fleet route");
            int previousBattles = state.BattleCount;
            for (int index = 0; index < route.Waypoints.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var cell = route.Waypoints[index];
                await WaitForMazeAsync(cell, token);
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
        foreach (var grid in Order(potential.Where(grid => !grid.IsAccessible)))
        {
            var plan = MapRoadblocks.Find(state, grid.Location, FleetRoles.BossIndex(configuration), token);
            if (!plan.Reachable) continue;
            if (plan.Enemies.Count == 0) throw new InvalidDataException("Boss accessibility differs from roadblock search");
            await SwitchFleetAsync(1, token);
            return await ClearRoadblockAsync(plan, token);
        }
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

    private async ValueTask<bool> FightAsync(CellState target, CancellationToken token, MapCombatExpectation expectation)
    {
        if (!state.IsMapInitialized) throw new InvalidOperationException("Initialize the map before selecting a combat target");
        await WaitEmotionAsync(token);
        var route = state.Paths.FindRoute(target.Location, FleetRoles.Step(state.FleetIndex, configuration), turningOptimize: configuration.HasAmbush);
        if (!route.IsReachable || route.Waypoints.Count == 0)
            throw new InvalidOperationException("Selected combat target has no confirmed fleet route");
        for (int index = 0; index < route.Waypoints.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var cell = route.Waypoints[index];
            await WaitForMazeAsync(cell, token);
            bool final = index == route.Waypoints.Count - 1;
            var result = final ? await movement.FightAsync(cell, token: token, expectation: expectation) :
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

    private async ValueTask WaitEmotionAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (configuration.EmotionMode.Calculates() && configuration.UseFleetLock)
        {
            if (waitEmotion is null) throw new NotSupportedException("Fleet-locked combat requires emotion state");
            // Native clear_chosen_enemy uses the logical index here; combat_preparation uses the displayed index.
            await waitEmotion(state.FleetIndex, token);
        }
    }

    private async ValueTask ScanAfterCombatAsync(CancellationToken token)
    {
        try
        {
            await scanner.ScanAsync(state.Progress, TimeSpan.FromMinutes(2),
                fleet: new FleetScanOptions(configuration.HasDecoyEnemy, configuration.Fleet2 != 0), token: token);
            state.RefreshFleetPaths(configuration);
        }
        catch
        {
            // Keep confirmed combat accounting, but never reuse a partial rescan.
            movement.Invalidate();
            throw;
        }
    }

    private async ValueTask WaitForMazeAsync(Cell waypoint, CancellationToken token)
    {
        var result = await movement.WaitForMazeAsync(waypoint, token);
        if (result is null) return;
        if (result.Outcome == MapMoveOutcome.StageReturned)
        {
            StageReturn = result.Arrival;
            throw new CampaignEndedException("Encounter while waiting for maze returned to stage");
        }
        throw new CampaignScriptException($"Maze wait for {waypoint} ended as {result.Outcome}");
    }
}
