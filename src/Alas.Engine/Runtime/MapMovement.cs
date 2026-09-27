using System.Collections.Immutable;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public enum MapMoveOutcome { Committed, Unconfirmed, Interrupted, UnsupportedEncounter, StageReturned }
public sealed record AmmoPickupEvidence(Cell Location, int ExpectedRecovered, int StockBefore, int StockAfter,
    int FleetBefore, int FleetAfter, long ArrivalSequence, long SettledSequence);
public sealed record MechanismReleaseEvidence(Cell Location, ImmutableArray<Cell> Triggers, ImmutableArray<Cell> Blocks,
    double ConfirmSeconds, long ArrivalSequence);
public sealed record MapMoveResult(MapMoveOutcome Outcome, MapArrivalResult Arrival)
{
    public AmmoPickupEvidence? AmmoPickup { get; init; }
    public MechanismReleaseEvidence? MechanismRelease { get; init; }
}
internal enum MapAction { Move, Fight, Mystery, ProbeBoss, Ammo }

/// <summary>Commits a fleet move only after fresh visual arrival and complete interaction accounting.</summary>
public sealed class MapMovement(CampaignState state, CampaignConfiguration configuration,
    IMapArrivalCamera camera, Func<MapArrivalCheck> createArrival,
    Func<CancellationToken, ValueTask>? waitForInfoBar = null,
    Func<CancellationToken, ValueTask<CampaignWithdrawalEvidence>>? withdraw = null)
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

    public ValueTask<MapMoveResult> CollectAmmoAsync(Cell destination, MapArrivalOptions? options = null,
        CancellationToken token = default)
        => MoveCoreAsync(destination, MapAction.Ammo, options, token);

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
        bool ammo = action == MapAction.Ammo;
        if (ammo && (waitForInfoBar is null || state.AmmoCount <= 0))
            throw new InvalidOperationException("Ammo pickup requires supply stock and an information-bar wait");
        var target = state[destination];
        bool mechanism = target.IsMechanismTrigger;
        if (destination == origin && !ammo && !(mechanism && action == MapAction.Move))
            throw new ArgumentException("Destination is the current fleet location", nameof(destination));
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
        if (ammo && (!target.MayAmmo || enemy || target.IsPortal))
            throw new ArgumentException("Supply destination must be a declared ammo tile without an enemy", nameof(destination));
        if (target.IsMaze || target.IsMechanismBlock ||
            (action is MapAction.Move or MapAction.Mystery && enemy) ||
            (target.IsMystery && !mystery) || (target.IsAmmo && !ammo && !mechanism) || target.IsCarrier ||
            (target.IsFleet && !((ammo || mechanism && action == MapAction.Move) && destination == origin)))
            throw new NotSupportedException("Destination requires a map interaction that is not committed by ordinary movement");
        Cell landing = target.IsPortal
            ? target.PortalLink ?? throw new InvalidDataException("Portal has no linked exit") : destination;
        var landingGrid = state[landing];
        if (target.IsPortal &&
            (landingGrid.IsLand || landingGrid.IsFleet || landingGrid.IsEnemy || landingGrid.IsSiren ||
             landingGrid.IsBoss || landingGrid.IsFortress || landingGrid.IsMystery || landingGrid.IsAmmo ||
             landingGrid.IsMechanismTrigger || landingGrid.IsMechanismBlock))
            throw new NotSupportedException("Portal exit requires an interaction that is not committed by ordinary movement");

        // Native _goto waits for the trigger animation before wipe_out releases
        // the whole linked group. Apply this to every landing, including combat
        // or a route's intermediate stop, not only explicit clear_mechanism calls.
        MechanismReleaseEvidence? release = null;
        if (mechanism)
        {
            if (target.IsPortal || target.MechanismTrigger is not { Count: > 0 } triggers || target.MechanismBlock is not { } blocks ||
                !triggers.Any(cell => ReferenceEquals(cell, target)) ||
                triggers.Concat(blocks).Any(cell => !state.Contains(cell.Location) || !ReferenceEquals(state[cell.Location], cell)))
                throw new InvalidDataException("Mechanism links must belong to this map and include the landing trigger");
            options ??= MapArrivalOptions.Default;
            double wait = configuration.HasLandBased ? target.MechanismWait : 0;
            double delay = options.ConfirmDelay.TotalSeconds + wait;
            if (options.ConfirmDelay < TimeSpan.Zero || !double.IsFinite(wait) || wait < 0 ||
                !double.IsFinite(delay) || delay >= options.WalkTimeout.TotalSeconds)
                throw new InvalidDataException("Mechanism confirmation delay must fit the movement deadline");
            options = options with { ConfirmDelay = TimeSpan.FromSeconds(delay) };
            release = new(destination, triggers.Select(cell => cell.Location).ToImmutableArray(),
                blocks.Select(cell => cell.Location).ToImmutableArray(), delay, 0);
        }

        token.ThrowIfCancellationRequested();
        if (state.Health.RetreatTriggered(state.FleetIndex, configuration.Health))
        {
            if (withdraw is null) throw new InvalidOperationException("Low HP retreat requires a withdrawal handler");
            try { state.Withdrawal = await withdraw(token); }
            finally { camera.Invalidate(); }
            throw new CampaignEndedException("Withdraw: low HP");
        }
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
            MapAction.Move or MapAction.Ammo => result.HandledEncounters.All(kind => kind == MapEncounterKind.AirRaid) &&
                result.Combats.IsEmpty,
            MapAction.Fight => combatConfirmed &&
                result.HandledEncounters.Count(kind => kind == MapEncounterKind.Combat) == 1 &&
                result.HandledEncounters.All(kind => kind is MapEncounterKind.Combat or MapEncounterKind.AirRaid),
            MapAction.Mystery => result.Combats.IsEmpty &&
                (result.HandledEncounters.Contains(MapEncounterKind.ItemPopup) || !result.AmmoNotificationFrames.IsEmpty) &&
                result.HandledEncounters.Count(kind => kind == MapEncounterKind.ItemPopup) <= 1 &&
                result.HandledEncounters.All(kind => kind is MapEncounterKind.ItemPopup or MapEncounterKind.AirRaid),
            MapAction.ProbeBoss => (result.Combats.IsEmpty &&
                    result.HandledEncounters.All(kind => kind == MapEncounterKind.AirRaid) ||
                combatConfirmed && result.HandledEncounters.Count(kind => kind == MapEncounterKind.Combat) == 1 &&
                    result.HandledEncounters.All(kind => kind is MapEncounterKind.Combat or MapEncounterKind.AirRaid)),
            _ => false
        };
        if (!interactionsConfirmed || landingGrid.MayAmmo && !result.SupplyClickCompleted)
        {
            camera.Invalidate();
            return new(MapMoveOutcome.UnsupportedEncounter, result);
        }

        try
        {
            if (result.SupplyClickCompleted)
            {
                await camera.RefreshImageAsync(token);
                if (camera.FrameSequence <= result.FrameSequence)
                    throw new InvalidDataException("Supply acknowledgement reused the arrival frame");
            }
            bool battled = fight || probeBoss && combatConfirmed;
            bool siren = battled && target.IsSiren;
            bool cleared = battled && target.MayEnemy;
            int mysteryCount = checked(state.MysteryCount + result.AmmoNotificationFrames.Length +
                (mystery ? result.HandledEncounters.Count(kind => kind == MapEncounterKind.ItemPopup) : 0));
            if (battled) state.CommitBattle(siren);
            state[origin].IsFleet = false;
            state.ResetCurrentFleet();
            landingGrid.WipeOut();
            if (cleared) landingGrid.IsCleared = true;
            landingGrid.IsFleet = landingGrid.IsCurrentFleet = true;
            state.MysteryCount = mysteryCount;
            if (state.FleetIndex == 1) state.Fleet1Location = landing;
            else state.Fleet2Location = landing;
            state.RefreshFleetPaths(configuration);
            if (release is not null)
            {
                release = release with { ArrivalSequence = result.FrameSequence };
                state.RecordMechanismRelease(release);
            }
            AmmoPickupEvidence? pickup = null;
            if (ammo)
            {
                long beforeWait = camera.FrameSequence;
                await waitForInfoBar!(token);
                await camera.RefreshImageAsync(token);
                if (camera.FrameSequence <= beforeWait)
                    throw new InvalidDataException("Supply settling did not produce a fresh map image");
                int stockBefore = state.AmmoCount, fleetBefore = state.FleetAmmo;
                int expectedRecovered = state.CommitAmmoPickup();
                pickup = new(destination, expectedRecovered, stockBefore, state.AmmoCount,
                    fleetBefore, state.FleetAmmo, result.FrameSequence, camera.FrameSequence);
            }
            return new(MapMoveOutcome.Committed, result) { AmmoPickup = pickup, MechanismRelease = release };
        }
        catch
        {
            camera.Invalidate();
            throw;
        }
    }
}
