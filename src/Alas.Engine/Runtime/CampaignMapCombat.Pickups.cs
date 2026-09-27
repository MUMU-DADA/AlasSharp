using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed partial class CampaignMapCombat
{
    public static readonly SourceFile PickupSource = new("campaign/campaign_main/campaign_14_base.py",
        "2fda008e66082218b3f318084f51cb7145ade18ea342fcc02abcd643126c0cb3");

    public async ValueTask<bool> PickUpFlareAsync(Cell destination, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!state.IsMapInitialized) throw new InvalidOperationException("Initialize the map before collecting a flare");
        var grid = state[destination];
        // Native marks the route constraint even when inaccessible or already picked.
        grid.IsFlare = true;
        if (state.PickedFlares.Contains(destination) || !grid.IsAccessible) return false;
        await MoveFleetAsync(destination, token);
        state.RecordFlare(destination);
        return false;
    }

    public async ValueTask<bool> PickUpLightHouseAsync(Cell destination, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!state.IsMapInitialized) throw new InvalidOperationException("Initialize the map before visiting a lighthouse");
        if (state.PickedLightHouses.Contains(destination) || !state[destination].IsAccessible) return false;
        if (waitForInfoBar is null) throw new NotSupportedException("Lighthouse visit requires information-bar waiting");
        await MoveFleetAsync(destination, token);
        // Native records arrival before waiting for the notification to disappear.
        state.RecordLightHouse(destination);
        try { await waitForInfoBar(token); }
        catch { movement.Invalidate(); throw; }
        return false;
    }

    /// <summary>Explicit native goto with empty expected; route and observations belong to this sortie.</summary>
    public async ValueTask MoveFleetAsync(Cell destination, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!state.IsMapInitialized) throw new InvalidOperationException("Initialize the map before moving a fleet");
        var current = state.FleetIndex == 1 ? state.Fleet1Location : state.Fleet2Location;
        var route = current == destination ? new MapRoute(true, [destination], [destination]) :
            state.Paths.FindRoute(destination, FleetRoles.Step(state.FleetIndex, configuration), configuration.HasAmbush);
        if (!route.IsReachable || route.Waypoints.Count == 0)
            throw new CampaignScriptException("Explicit fleet movement has no confirmed route");
        foreach (var cell in route.Waypoints)
        {
            await WaitForMazeAsync(cell, token);
            var result = await movement.VisitAsync(cell, token);
            if (result.Outcome == MapMoveOutcome.StageReturned)
            {
                StageReturn = result.Arrival;
                throw new CampaignEndedException("Encounter during explicit movement returned to stage");
            }
            if (result.Outcome != MapMoveOutcome.Committed)
                throw new CampaignScriptException($"Explicit movement to {cell} ended as {result.Outcome}");
        }
    }
}
