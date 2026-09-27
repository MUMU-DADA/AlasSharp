using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed partial class CampaignMapCombat
{
    /// <summary>Native fleet_2_push_forward: order by active cost before switching to fleet 2.</summary>
    public async ValueTask<bool> PushSecondFleetForwardAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (FleetRoles.BossIndex(configuration) != 2) return false;
        if (!state.IsMapInitialized || state.Fleet2Location is not { } second || state.Fleet1Location is not { } first)
            throw new InvalidOperationException("Second-fleet advance requires initialized fleet locations");
        var grids = Order(state.Cells.Where(grid => !grid.IsLand));
        if (state[second].Weight <= grids[0].Weight)
        {
            await SwitchFleetAsync(1, token);
            return false;
        }
        var target = grids.FirstOrDefault(grid => grid.IsAccessible2 && grid.IsSea && grid.Location != first && grid.Location != second);
        if (target is null || state[second].Weight <= target.Weight) return false;
        await SwitchFleetAsync(2, token);
        await RepositionFleetAsync(target.Location, token);
        // No finally: native goto can raise a round change before this restoration.
        await SwitchFleetAsync(1, token);
        return true;
    }

    /// <summary>Native fleet_2_step_on. A successful relocation returns false; only clearing a roadblock returns true.</summary>
    public async ValueTask<bool> PositionSecondFleetAsync(IReadOnlyList<Cell> cells, IReadOnlyList<RoadDefinition> roads,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (configuration.Fleet2 == 0) return false;
        if (!state.IsMapInitialized || state.Fleet1Location is null || state.Fleet2Location is not { } second)
            throw new InvalidOperationException("Second-fleet positioning requires initialized fleet locations");
        var targets = cells.Select(cell => state[cell]).ToArray();
        if (targets.Any(grid => grid.Location == second)) return false;
        bool allCleared = targets.All(grid => grid.IsCleared);
        foreach (var target in targets)
        {
            if (target.IsEnemy || !allCleared && target.IsCleared || !CheckAccessibility(target.Location, 2)) continue;
            await SwitchFleetAsync(2, token);
            await RepositionFleetAsync(target.Location, token);
            await SwitchFleetAsync(1, token);
            return false;
        }
        await SwitchFleetAsync(1, token);
        bool cleared = await ClearRoadblocksAsync(roads, token: token);
        await ClearMysteriesAsync(token);
        return cleared;
    }

    private async ValueTask RepositionFleetAsync(Cell target, CancellationToken token)
    {
        var route = state.Paths.FindRoute(target, FleetRoles.Step(state.FleetIndex, configuration), configuration.HasAmbush);
        if (!route.IsReachable || route.Waypoints.Count == 0)
            throw new CampaignScriptException("Fleet repositioning has no confirmed route");
        foreach (var cell in route.Waypoints)
        {
            await WaitForMazeAsync(cell, token);
            var result = await movement.RepositionAsync(cell, token);
            if (result.Outcome == MapMoveOutcome.StageReturned)
            {
                StageReturn = result.Arrival;
                throw new CampaignEndedException("Encounter during fleet repositioning returned to stage");
            }
            if (result.Outcome != MapMoveOutcome.Committed)
                throw new CampaignScriptException($"Fleet repositioning to {cell} ended as {result.Outcome}");
        }
    }

    public ValueTask<bool> RescueSecondFleetAsync(Cell destination, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (FleetRoles.BossIndex(configuration) != 2) return ValueTask.FromResult(false);
        if (!state.IsMapInitialized) throw new InvalidOperationException("Initialize the map before fleet rescue");
        var plan = MapRoadblocks.Find(state, destination, 2, token);
        // Native select_grids defaults to weight/cost, with no scale preference.
        // Keep actual fleet costs: hypothetical roadblock search must not leak into physical targeting.
        var target = Order(plan.Enemies.Select(cell => state[cell]).Where(grid => grid.IsAccessible)).FirstOrDefault();
        return target is null ? ValueTask.FromResult(false) : FightAsync(target, token, MapCombatExpectation.Enemy);
    }
}
