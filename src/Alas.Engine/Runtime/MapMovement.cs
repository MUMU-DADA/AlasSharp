using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public enum MapMoveOutcome { Committed, Unconfirmed, Interrupted, UnsupportedEncounter, StageReturned }
public sealed record MapMoveResult(MapMoveOutcome Outcome, MapArrivalResult Arrival);
internal enum MapAction { Move, Fight, Mystery, ProbeBoss }

/// <summary>Commits a fleet move only after fresh visual arrival and complete interaction accounting.</summary>
public sealed class MapMovement(CampaignState state, CampaignConfiguration configuration,
    IMapArrivalCamera camera, Func<MapArrivalCheck> createArrival)
{
    public ValueTask<MapMoveResult> MoveAsync(Cell destination, MapArrivalOptions? options = null,
        CancellationToken token = default)
        => MoveCoreAsync(destination, MapAction.Move, options, token);

    public ValueTask<MapMoveResult> FightAsync(Cell destination, MapArrivalOptions? options = null,
        CancellationToken token = default)
        => MoveCoreAsync(destination, MapAction.Fight, options, token);

    public ValueTask<MapMoveResult> CollectMysteryAsync(Cell destination, MapArrivalOptions? options = null,
        CancellationToken token = default)
        => MoveCoreAsync(destination, MapAction.Mystery, options, token);

    public ValueTask<MapMoveResult> ProbeBossAsync(Cell destination, MapArrivalOptions? options = null,
        CancellationToken token = default)
        => MoveCoreAsync(destination, MapAction.ProbeBoss, options, token);

    private async ValueTask<MapMoveResult> MoveCoreAsync(Cell destination, MapAction action,
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
        bool fight = action == MapAction.Fight;
        bool mystery = action == MapAction.Mystery;
        bool probeBoss = action == MapAction.ProbeBoss;
        bool enemy = target.IsEnemy || target.IsSiren || target.IsBoss || target.IsFortress || target.IsCaughtBySiren;
        if (fight && (!enemy || target.IsPortal))
            throw new ArgumentException("Combat destination must have an observed enemy", nameof(destination));
        if (mystery && (!target.IsMystery || enemy || target.IsPortal))
            throw new ArgumentException("Mystery destination must have an observed mystery", nameof(destination));
        if (probeBoss && (!target.MayBoss || target.IsPortal))
            throw new ArgumentException("Potential boss destination must be a declared boss spawn", nameof(destination));
        if (target.IsMaze || target.IsMechanismTrigger || target.IsMechanismBlock ||
            (action is MapAction.Move or MapAction.Mystery && enemy) ||
            (target.IsMystery && !mystery) || target.IsAmmo || target.IsCarrier || target.IsFleet)
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
            return new((fight || probeBoss) &&
                result.Combats is [ { Return: CombatReturn.InStage, Rank.IsWinningRank: true } ] &&
                result.HandledEncounters.Count(kind => kind == MapEncounterKind.Combat) == 1 &&
                result.HandledEncounters.All(kind => kind is MapEncounterKind.Combat or MapEncounterKind.AirRaid)
                ? MapMoveOutcome.StageReturned :
                MapMoveOutcome.UnsupportedEncounter, result);
        bool combatConfirmed = result.Combats.Length == 1 &&
            result.Combats[0] is { Return: CombatReturn.InMap, Rank.IsWinningRank: true };
        bool interactionsConfirmed = action switch
        {
            MapAction.Move => result.HandledEncounters.All(kind => kind == MapEncounterKind.AirRaid) &&
                result.Combats.IsEmpty,
            MapAction.Fight => combatConfirmed &&
                result.HandledEncounters.Count(kind => kind == MapEncounterKind.Combat) == 1 &&
                result.HandledEncounters.All(kind => kind is MapEncounterKind.Combat or MapEncounterKind.AirRaid),
            MapAction.Mystery => result.Combats.IsEmpty &&
                result.HandledEncounters.Count(kind => kind == MapEncounterKind.ItemPopup) == 1 &&
                result.HandledEncounters.All(kind => kind is MapEncounterKind.ItemPopup or MapEncounterKind.AirRaid),
            MapAction.ProbeBoss => (result.Combats.IsEmpty &&
                    result.HandledEncounters.All(kind => kind == MapEncounterKind.AirRaid) ||
                combatConfirmed && result.HandledEncounters.Count(kind => kind == MapEncounterKind.Combat) == 1 &&
                    result.HandledEncounters.All(kind => kind is MapEncounterKind.Combat or MapEncounterKind.AirRaid)),
            _ => false
        };
        if (!interactionsConfirmed)
        {
            camera.Invalidate();
            return new(MapMoveOutcome.UnsupportedEncounter, result);
        }

        try
        {
            bool battled = fight || probeBoss && combatConfirmed;
            bool siren = battled && target.IsSiren;
            bool cleared = battled && target.MayEnemy;
            int mysteryCount = mystery ? checked(state.MysteryCount + 1) : state.MysteryCount;
            if (battled) state.CommitBattle(siren);
            state[origin].IsFleet = false;
            state.ResetCurrentFleet();
            landingGrid.WipeOut();
            if (cleared) landingGrid.IsCleared = true;
            landingGrid.IsFleet = landingGrid.IsCurrentFleet = true;
            state.MysteryCount = mysteryCount;
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
