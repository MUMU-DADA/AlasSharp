using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public enum MapMoveOutcome { Committed, Unconfirmed, Interrupted, UnsupportedEncounter }
public sealed record MapMoveResult(MapMoveOutcome Outcome, MapArrivalResult Arrival);

/// <summary>Commits a fleet move only after fresh visual arrival and complete interaction accounting.</summary>
public sealed class MapMovement(CampaignState state, CampaignConfiguration configuration,
    IMapArrivalCamera camera, MapArrivalCheck arrival)
{
    public async ValueTask<MapMoveResult> MoveAsync(Cell destination, MapArrivalOptions? options = null,
        CancellationToken token = default)
    {
        if (!state.IsMapInitialized) throw new InvalidOperationException("Initialize the map before moving a fleet");
        _ = state.Paths.Connections;
        if (configuration.HasMovableEnemy || configuration.HasMovableNormalEnemy || configuration.HasMaze ||
            state.Cells.Any(cell => cell.IsMaze))
            throw new NotSupportedException("Moving-enemy and maze rounds require their map-state transition before fleet movement");
        if (state.FleetIndex is not (1 or 2)) throw new InvalidOperationException("Invalid current fleet index");
        Cell origin = (state.FleetIndex == 1 ? state.Fleet1Location : state.Fleet2Location)
            ?? throw new InvalidOperationException("Current fleet location is unknown");
        if (!state[origin].IsFleet) throw new InvalidOperationException("Current fleet marker is absent from map state");
        if (destination == origin) throw new ArgumentException("Destination is the current fleet location", nameof(destination));
        var target = state[destination];
        if (target.IsLand) throw new ArgumentException("Destination is land", nameof(destination));
        if (target.IsMaze || target.IsMechanismTrigger || target.IsMechanismBlock ||
            target.IsEnemy || target.IsSiren || target.IsBoss || target.IsFortress || target.IsMystery ||
            target.IsAmmo || target.IsCarrier || target.IsCaughtBySiren || target.IsFleet)
            throw new NotSupportedException("Destination requires a map interaction that is not committed by ordinary movement");
        Cell landing = target.IsPortal
            ? target.PortalLink ?? throw new InvalidDataException("Portal has no linked exit") : destination;
        var landingGrid = state[landing];
        if (landingGrid.IsLand || landingGrid.IsFleet || landingGrid.IsEnemy || landingGrid.IsSiren ||
            landingGrid.IsBoss || landingGrid.IsFortress || landingGrid.IsMystery || landingGrid.IsAmmo)
            throw new NotSupportedException("Portal exit requires an interaction that is not committed by ordinary movement");

        var result = await arrival.TapAndCheckAsync(destination, options, token);
        if (result.Outcome == MapArrivalOutcome.Unconfirmed) return new(MapMoveOutcome.Unconfirmed, result);
        if (result.Outcome == MapArrivalOutcome.MapInterrupted) return new(MapMoveOutcome.Interrupted, result);
        if (result.HandledEncounters.Any(kind => kind != MapEncounterKind.AirRaid))
        {
            camera.Invalidate();
            return new(MapMoveOutcome.UnsupportedEncounter, result);
        }

        try
        {
            state[origin].IsFleet = false;
            state.ResetCurrentFleet();
            landingGrid.WipeOut();
            landingGrid.IsFleet = landingGrid.IsCurrentFleet = true;
            if (state.FleetIndex == 1) state.Fleet1Location = landing;
            else state.Fleet2Location = landing;
            state.Paths.ComputeFleetCosts(
                [new(1, state.Fleet1Location), new(2, state.Fleet2Location)], landing, configuration.HasAmbush);
            return new(MapMoveOutcome.Committed, result);
        }
        catch
        {
            camera.Invalidate();
            throw;
        }
    }
}
