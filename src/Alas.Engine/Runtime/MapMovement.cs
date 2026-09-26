using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public enum MapMoveOutcome { Committed, Unconfirmed, Interrupted, UnsupportedEncounter, StageReturned }
public sealed record MapMoveResult(MapMoveOutcome Outcome, MapArrivalResult Arrival);

/// <summary>Commits a fleet move only after fresh visual arrival and complete interaction accounting.</summary>
public sealed class MapMovement(CampaignState state, CampaignConfiguration configuration,
    IMapArrivalCamera camera, Func<MapArrivalCheck> createArrival)
{
    public MapMovement(CampaignState state, CampaignConfiguration configuration,
        IMapArrivalCamera camera, MapArrivalCheck arrival)
        : this(state, configuration, camera, () => arrival) { }

    public ValueTask<MapMoveResult> MoveAsync(Cell destination, MapArrivalOptions? options = null,
        CancellationToken token = default)
        => MoveCoreAsync(destination, false, options, token);

    public ValueTask<MapMoveResult> FightAsync(Cell destination, MapArrivalOptions? options = null,
        CancellationToken token = default)
        => MoveCoreAsync(destination, true, options, token);

    private async ValueTask<MapMoveResult> MoveCoreAsync(Cell destination, bool fight,
        MapArrivalOptions? options, CancellationToken token)
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
        bool enemy = target.IsEnemy || target.IsSiren || target.IsBoss || target.IsFortress || target.IsCaughtBySiren;
        if (fight && (!enemy || target.IsPortal))
            throw new ArgumentException("Combat destination must have an observed enemy", nameof(destination));
        if (target.IsMaze || target.IsMechanismTrigger || target.IsMechanismBlock ||
            (!fight && enemy) || target.IsMystery || target.IsAmmo || target.IsCarrier || target.IsFleet)
            throw new NotSupportedException("Destination requires a map interaction that is not committed by ordinary movement");
        Cell landing = target.IsPortal
            ? target.PortalLink ?? throw new InvalidDataException("Portal has no linked exit") : destination;
        var landingGrid = state[landing];
        if (target.IsPortal &&
            (landingGrid.IsLand || landingGrid.IsFleet || landingGrid.IsEnemy || landingGrid.IsSiren ||
             landingGrid.IsBoss || landingGrid.IsFortress || landingGrid.IsMystery || landingGrid.IsAmmo))
            throw new NotSupportedException("Portal exit requires an interaction that is not committed by ordinary movement");

        var result = await createArrival().TapAndCheckAsync(destination, options, token);
        if (result.Outcome == MapArrivalOutcome.Unconfirmed) return new(MapMoveOutcome.Unconfirmed, result);
        if (result.Outcome == MapArrivalOutcome.MapInterrupted) return new(MapMoveOutcome.Interrupted, result);
        if (result.Outcome == MapArrivalOutcome.StageReturned)
            return new(fight && result.Combats.Length == 1 ? MapMoveOutcome.StageReturned :
                MapMoveOutcome.UnsupportedEncounter, result);
        bool combatConfirmed = result.Combats.Length == 1 &&
            result.Combats[0] is { Return: CombatReturn.InMap, Rank.IsWinningRank: true };
        if (result.HandledEncounters.Any(kind => kind is not (MapEncounterKind.AirRaid or MapEncounterKind.Combat)) ||
            (fight ? !combatConfirmed || result.HandledEncounters.Count(kind => kind == MapEncounterKind.Combat) != 1 :
                result.HandledEncounters.Contains(MapEncounterKind.Combat) || !result.Combats.IsEmpty))
        {
            camera.Invalidate();
            return new(MapMoveOutcome.UnsupportedEncounter, result);
        }

        try
        {
            bool siren = fight && target.IsSiren;
            bool cleared = fight && target.MayEnemy;
            if (fight) state.CommitBattle(siren);
            state[origin].IsFleet = false;
            state.ResetCurrentFleet();
            landingGrid.WipeOut();
            if (cleared) landingGrid.IsCleared = true;
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
