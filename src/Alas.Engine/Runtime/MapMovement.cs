using System.Collections.Immutable;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public enum MapMoveOutcome { Committed, Unconfirmed, Interrupted, UnsupportedEncounter, StageReturned }
public enum MapCombatExpectation { None, Enemy, Siren, Boss, Fortress }
public sealed record AmmoPickupEvidence(Cell Location, int ExpectedRecovered, int StockBefore, int StockAfter,
    int FleetBefore, int FleetAfter, long ArrivalSequence, long SettledSequence);
public sealed record MechanismReleaseEvidence(Cell Location, ImmutableArray<Cell> Triggers, ImmutableArray<Cell> Blocks,
    double ConfirmSeconds, long ArrivalSequence);
public sealed record MazeWaitEvidence(Cell WaitingFor, Cell From, Cell To, int RoundBefore, int RoundAfter,
    double ConfirmSeconds, long ArrivalSequence);
public sealed record DecoyArrivalEvidence(int Fleet, Cell From, Cell To, int BattleCount,
    double ConfirmSeconds, long ArrivalSequence);
public sealed record MapMoveResult(MapMoveOutcome Outcome, MapArrivalResult Arrival)
{
    public AmmoPickupEvidence? AmmoPickup { get; init; }
    public MechanismReleaseEvidence? MechanismRelease { get; init; }
}
internal enum MapAction { Move, Reposition, Fight, Mystery, ProbeBoss, ProbeBouncing, Ammo, Visit }

/// <summary>Commits a fleet move only after fresh visual arrival and complete interaction accounting.</summary>
public sealed class MapMovement(CampaignState state, CampaignConfiguration configuration,
    IMapArrivalCamera camera, Func<MapArrivalCheck> createArrival,
    Func<CancellationToken, ValueTask>? waitForInfoBar = null,
    Func<CancellationToken, ValueTask<CampaignWithdrawalEvidence>>? withdraw = null,
    MapMovableScan? movableScan = null, MapScanner? carrierScanner = null)
{
    internal void Invalidate()
    {
        state.MovementInvalidated = true;
        camera.Invalidate();
    }

    public ValueTask<MapMoveResult> MoveAsync(Cell destination, MapArrivalOptions? options = null,
        CancellationToken token = default)
        => MoveCoreAsync(destination, MapAction.Move, options, token);

    internal ValueTask<MapMoveResult> VisitAsync(Cell destination, CancellationToken token = default)
        => MoveCoreAsync(destination, MapAction.Visit, null, token, expectation: MapCombatExpectation.None);

    /// <summary>Native raw goto: handle observed interactions, but supply confirmation alone does not replenish inventory.</summary>
    internal ValueTask<MapMoveResult> RepositionAsync(Cell destination, CancellationToken token = default)
    {
        var grid = state[destination];
        var action = grid.IsEnemy || grid.IsSiren || grid.IsBoss || grid.IsFortress || grid.IsCaughtBySiren
            ? MapAction.Fight : grid.IsMystery ? MapAction.Mystery : MapAction.Reposition;
        return MoveCoreAsync(destination, action, null, token, expectation: MapCombatExpectation.None);
    }

    public ValueTask<MapMoveResult> FightAsync(Cell destination, MapArrivalOptions? options = null,
        CancellationToken token = default, MapCombatExpectation? expectation = null)
        => MoveCoreAsync(destination, MapAction.Fight, options, token, expectation: expectation);

    public ValueTask<MapMoveResult> CollectMysteryAsync(Cell destination, MapArrivalOptions? options = null,
        CancellationToken token = default)
        => MoveCoreAsync(destination, MapAction.Mystery, options, token);

    public ValueTask<MapMoveResult> ProbeBossAsync(Cell destination, MapArrivalOptions? options = null,
        CancellationToken token = default)
        => MoveCoreAsync(destination, MapAction.ProbeBoss, options, token);

    public ValueTask<MapMoveResult> ProbeBouncingAsync(Cell destination, CancellationToken token = default)
        => MoveCoreAsync(destination, MapAction.ProbeBouncing, null, token);

    public ValueTask<MapMoveResult> CollectAmmoAsync(Cell destination, MapArrivalOptions? options = null,
        CancellationToken token = default)
        => MoveCoreAsync(destination, MapAction.Ammo, options, token);

    /// <summary>Native goto's maze waypoint prelude. Raw neighbor taps do not recurse into another maze wait.</summary>
    public async ValueTask<MapMoveResult?> WaitForMazeAsync(Cell waypoint, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!configuration.HasMaze) return null;
        if (!state.IsMapInitialized || state.MovementInvalidated || !state.Rounds.Initialized)
            throw new InvalidOperationException("Maze waiting requires an initialized, usable sortie and rounds");
        state.Rounds.RequireConfiguration(configuration);
        if (state[waypoint].IsMaze && state.MazeRound <= 0)
            throw new InvalidDataException("Maze phase period must be positive");
        if (!state.Rounds.MazeActive(waypoint)) return null;
        var nearby = state[waypoint].MazeNearby;
        if (state.MazeRound <= 0 || nearby is not { Count: > 0 } ||
            nearby.Any(cell => !state.Contains(cell.Location) || !ReferenceEquals(state[cell.Location], cell) || cell.Location == waypoint))
            throw new InvalidDataException("Maze neighbors must belong to this sortie and exclude the active waypoint");
        // Native evaluates active-on once; _goto raises at the next phase change.
        // Preserve its ten-move bound rather than inventing a per-map wait route.
        for (int attempt = 0; attempt < 10; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var choices = nearby.Where(cell => !cell.IsFleet).ToArray();
            if (choices.Any(cell => !cell.IsEnemy)) choices = choices.Where(cell => !cell.IsEnemy).ToArray();
            var target = choices.OrderBy(cell => cell.Cost).FirstOrDefault()
                ?? throw new CampaignScriptException("Active maze waypoint has no unoccupied waiting neighbor");
            var action = target.IsEnemy || target.IsSiren || target.IsBoss || target.IsFortress || target.IsCaughtBySiren
                ? MapAction.Fight : target.IsMystery ? MapAction.Mystery : MapAction.Move;
            var result = await MoveCoreAsync(target.Location, action, null, token, waypoint);
            if (result.Outcome != MapMoveOutcome.Committed) return result;
        }
        return null;
    }

    private async ValueTask<MapMoveResult> MoveCoreAsync(Cell destination, MapAction action,
        MapArrivalOptions? options, CancellationToken token, Cell? mazeWaitFor = null,
        MapCombatExpectation? expectation = null)
    {
        if (!state.IsMapInitialized) throw new InvalidOperationException("Initialize the map before moving a fleet");
        if (state.MovementInvalidated) throw new InvalidOperationException("Map movement state was invalidated; this sortie cannot be reused");
        if (configuration.MysteryHasCarrier && carrierScanner is null)
            throw new NotSupportedException("Carrier mystery movement requires carrier scanning");
        _ = state.Paths.Connections;
        if (!configuration.HasMaze && state.Cells.Any(cell => cell.IsMaze))
            throw new InvalidDataException("Maze map state requires its maze configuration");
        bool dynamic = configuration.HasMovableEnemy || configuration.HasMovableNormalEnemy;
        if (dynamic && (movableScan is null || !state.Rounds.Initialized || state.SpawnStack.IsEmpty) ||
            (configuration.HasBouncingEnemy || configuration.HasMaze) && !state.Rounds.Initialized)
            throw new NotSupportedException("Dynamic movement requires initialized rounds, spawn declarations and movable scanning");
        if (state.FleetIndex is not (1 or 2)) throw new InvalidOperationException("Invalid current fleet index");
        Cell origin = (state.FleetIndex == 1 ? state.Fleet1Location : state.Fleet2Location)
            ?? throw new InvalidOperationException("Current fleet location is unknown");
        if (!state[origin].IsFleet) throw new InvalidOperationException("Current fleet marker is absent from map state");
        bool ammo = action == MapAction.Ammo;
        bool visit = action == MapAction.Visit;
        if (ammo && (waitForInfoBar is null || state.AmmoCount <= 0))
            throw new InvalidOperationException("Ammo pickup requires supply stock and an information-bar wait");
        var target = state[destination];
        bool mechanism = target.IsMechanismTrigger;
        bool caughtCombat = action == MapAction.Fight && target.IsCaughtBySiren;
        bool probeBouncing = action == MapAction.ProbeBouncing;
        if (destination == origin && !visit && !ammo && !caughtCombat && !probeBouncing && !(mechanism && action == MapAction.Move))
            throw new ArgumentException("Destination is the current fleet location", nameof(destination));
        if (target.IsLand) throw new ArgumentException("Destination is land", nameof(destination));
        bool fight = action == MapAction.Fight;
        expectation ??= target.IsBoss ? MapCombatExpectation.Boss : target.IsFortress ? MapCombatExpectation.Fortress :
            target.IsSiren ? MapCombatExpectation.Siren : MapCombatExpectation.Enemy;
        if (!Enum.IsDefined(expectation.Value)) throw new ArgumentOutOfRangeException(nameof(expectation));
        // Native only redispatches an empty result for expected == 'combat'.
        // Boss/siren/fortress calls and raw maze detours retain their own contracts.
        bool decoyCandidate = fight && configuration.HasDecoyEnemy &&
            expectation == MapCombatExpectation.Enemy && mazeWaitFor is null;
        bool mystery = action == MapAction.Mystery;
        bool probeBoss = action == MapAction.ProbeBoss;
        bool enemy = target.IsEnemy || target.IsSiren || target.IsBoss || target.IsFortress || target.IsCaughtBySiren;
        if (fight && (!enemy || target.IsPortal))
            throw new ArgumentException("Combat destination must have an observed enemy", nameof(destination));
        if (mystery && (!target.IsMystery || enemy || target.IsPortal))
            throw new ArgumentException("Mystery destination must have an observed mystery", nameof(destination));
        if (probeBoss && (!target.MayBoss || target.IsPortal))
            throw new ArgumentException("Potential boss destination must be a declared boss spawn", nameof(destination));
        // A cleared overlapping route can unset this cell while another route
        // remains active. Native still visits every member of the selected route.
        if (probeBouncing && (!configuration.HasBouncingEnemy || target.IsPortal ||
            !state.Mechanisms.BouncingRoutes.Any(route => route.Contains(destination) &&
                route.Any(cell => state[cell].MayBouncingEnemy))))
            throw new ArgumentException("Bouncing destination must belong to an enabled uncleared route", nameof(destination));
        if (ammo && (!target.MayAmmo || enemy || target.IsPortal))
            throw new ArgumentException("Supply destination must be a declared ammo tile without an enemy", nameof(destination));
        if (target.IsMechanismBlock ||
            (action is MapAction.Move or MapAction.Reposition or MapAction.Mystery && enemy) ||
            (target.IsMystery && !mystery && !visit) || (target.IsAmmo && !ammo && !visit && action != MapAction.Reposition && !probeBouncing && !mechanism && mazeWaitFor is null) || target.IsCarrier && !fight && !visit ||
            (target.IsFleet && !((visit || ammo || caughtCombat || probeBouncing || mechanism && action == MapAction.Move) && destination == origin)))
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
            options = options with { ConfirmDelay = TimeSpan.FromSeconds(delay), AfterCombatConfirmDelay = TimeSpan.FromSeconds(delay) };
            release = new(destination, triggers.Select(cell => cell.Location).ToImmutableArray(),
                blocks.Select(cell => cell.Location).ToImmutableArray(), delay, 0);
        }

        // Native removes the enemy animation wait after combat but keeps mechanism and base delays.
        var before = dynamic ? MovableEnemySnapshot.Capture(state) : null;
        options ??= MapArrivalOptions.Default;
        bool expectedBoss = mazeWaitFor is null && (probeBoss || fight && expectation == MapCombatExpectation.Boss ||
            (fight && expectation != MapCombatExpectation.None || probeBouncing) && target.MayBoss);
        options = options with { AllowCurrentMarker = !expectedBoss && (options.AllowCurrentMarker || configuration.WalkUseCurrentFleet) };
        if (state.Rounds.Initialized)
        {
            state.Rounds.RequireConfiguration(configuration);
            options ??= MapArrivalOptions.Default;
            double seconds = options.ConfirmDelay.TotalSeconds + state.Rounds.WaitSeconds;
            if (!double.IsFinite(seconds) || seconds >= options.WalkTimeout.TotalSeconds)
                throw new InvalidDataException("Round confirmation delay must fit the movement deadline");
            options = options with { ConfirmDelay = TimeSpan.FromSeconds(seconds), AfterCombatConfirmDelay = options.ConfirmDelay };
        }
        if (decoyCandidate) options = options with { ExpectCombat = true };

        token.ThrowIfCancellationRequested();
        if (state.Health.RetreatTriggered(state.FleetIndex, configuration.Health))
        {
            if (withdraw is null) throw new InvalidOperationException("Low HP retreat requires a withdrawal handler");
            try { state.Withdrawal = await withdraw(token); }
            finally { camera.Invalidate(); }
            throw new CampaignEndedException("Withdraw: low HP");
        }
        MapArrivalResult result;
        try { result = await createArrival().TapAndCheckAsync(destination, options, token); }
        catch { state.MovementInvalidated = true; camera.Invalidate(); throw; }
        if (result.Outcome != MapArrivalOutcome.MarkerConfirmed) state.MovementInvalidated = true;
        if (result.Outcome == MapArrivalOutcome.Unconfirmed) return new(MapMoveOutcome.Unconfirmed, result);
        if (result.Outcome == MapArrivalOutcome.MapInterrupted) return new(MapMoveOutcome.Interrupted, result);
        // Some compiled campaigns dismiss items but return false from
        // handle_mystery_items. Keep the observation without treating it as a mystery.
        var encounters = result.HandledEncounters.Where(kind =>
            kind != MapEncounterKind.ItemPopup || state.Rule?.CountMysteryItems != false).ToArray();
        if (result.Outcome == MapArrivalOutcome.StageReturned)
            return new((fight || visit || probeBoss || probeBouncing) && result.AmbushesConfirmed &&
                result.Combats is [ { Return: CombatReturn.InStage, Rank.IsWinningRank: true } ] &&
                result.HandledEncounters.Count(kind => kind == MapEncounterKind.Combat) == 1 &&
                result.CarriersConfirmed && (result.Carriers.IsEmpty || configuration.MysteryHasCarrier) &&
                encounters.All(kind => kind is MapEncounterKind.Combat or MapEncounterKind.AirRaid or MapEncounterKind.Ambush or MapEncounterKind.CarrierSpawn || visit && kind == MapEncounterKind.ItemPopup)
                ? MapMoveOutcome.StageReturned :
                MapMoveOutcome.UnsupportedEncounter, result);
        bool combatConfirmed = result.Combats.Length == 1 &&
            result.Combats[0] is { Return: CombatReturn.InMap, Rank.IsWinningRank: true };
        bool decoyConfirmed = decoyCandidate && result.Combats.IsEmpty && result.AmmoNotificationFrames.IsEmpty &&
            encounters.All(kind => kind is MapEncounterKind.AirRaid or MapEncounterKind.Ambush);
        bool interactionsConfirmed = action switch
        {
            MapAction.Visit => (result.Combats.IsEmpty || combatConfirmed) &&
                result.HandledEncounters.Count(kind => kind == MapEncounterKind.Combat) == result.Combats.Length &&
                result.HandledEncounters.All(kind => kind is MapEncounterKind.Combat or MapEncounterKind.ItemPopup or
                    MapEncounterKind.AirRaid or MapEncounterKind.Ambush or MapEncounterKind.CarrierSpawn),
            MapAction.Move or MapAction.Reposition or MapAction.Ammo => encounters.All(kind => kind is MapEncounterKind.AirRaid or MapEncounterKind.Ambush or MapEncounterKind.CarrierSpawn) &&
                result.Combats.IsEmpty,
            MapAction.Fight => decoyConfirmed || combatConfirmed &&
                result.HandledEncounters.Count(kind => kind == MapEncounterKind.Combat) == 1 &&
                encounters.All(kind => kind is MapEncounterKind.Combat or MapEncounterKind.AirRaid or MapEncounterKind.Ambush or MapEncounterKind.CarrierSpawn),
            MapAction.Mystery => result.Combats.IsEmpty &&
                (result.HandledEncounters.Contains(MapEncounterKind.ItemPopup) || !result.AmmoNotificationFrames.IsEmpty || !result.Carriers.IsEmpty) &&
                result.HandledEncounters.Count(kind => kind == MapEncounterKind.ItemPopup) <= 1 &&
                result.HandledEncounters.All(kind => kind is MapEncounterKind.ItemPopup or MapEncounterKind.AirRaid or MapEncounterKind.Ambush or MapEncounterKind.CarrierSpawn),
            MapAction.ProbeBoss or MapAction.ProbeBouncing => (result.Combats.IsEmpty &&
                    encounters.All(kind => kind is MapEncounterKind.AirRaid or MapEncounterKind.Ambush or MapEncounterKind.CarrierSpawn) ||
                combatConfirmed && result.HandledEncounters.Count(kind => kind == MapEncounterKind.Combat) == 1 &&
                encounters.All(kind => kind is MapEncounterKind.Combat or MapEncounterKind.AirRaid or MapEncounterKind.Ambush or MapEncounterKind.CarrierSpawn)),
            _ => false
        };
        if (!interactionsConfirmed || !result.AmbushesConfirmed || !result.CarriersConfirmed ||
            !result.Carriers.IsEmpty && !configuration.MysteryHasCarrier || landingGrid.MayAmmo && !result.SupplyClickCompleted)
        {
            state.MovementInvalidated = true;
            camera.Invalidate();
            return new(MapMoveOutcome.UnsupportedEncounter, result);
        }

        bool redispatch = false;
        MapMoveResult committed;
        try
        {
            if (result.SupplyClickCompleted)
            {
                await camera.RefreshImageAsync(token);
                if (camera.FrameSequence <= result.FrameSequence)
                    throw new InvalidDataException("Supply acknowledgement reused the arrival frame");
            }
            bool battled = (fight || visit || probeBoss || probeBouncing) && combatConfirmed;
            // A maze detour is native _goto(expected=''). Native attributes its
            // unplanned battle to sirens only when movable sirens are enabled.
            bool siren = battled && (visit || mazeWaitFor is not null || fight && expectation == MapCombatExpectation.None
                ? configuration.HasMovableEnemy : fight && expectation == MapCombatExpectation.Siren);
            bool cleared = battled && !siren && target.MayEnemy;
            int mysteryCount = checked(state.MysteryCount + result.AmmoNotificationFrames.Length + result.Carriers.Length +
                ((mystery || visit) && state.Rule?.CountMysteryItems != false ? result.HandledEncounters.Count(kind => kind == MapEncounterKind.ItemPopup) : 0));
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
            if (decoyConfirmed)
                state.RecordDecoyArrival(new(state.FleetIndex, origin, landing, state.BattleCount,
                    options.ConfirmDelay.TotalSeconds + 1, result.FrameSequence));
            if (release is not null)
            {
                release = release with { ArrivalSequence = result.FrameSequence,
                    ConfirmSeconds = (battled ? options!.AfterCombatConfirmDelay ?? options.ConfirmDelay : options!.ConfirmDelay).TotalSeconds };
                state.RecordMechanismRelease(release);
            }
            if (result.LastMysteryWasCarrier)
            {
                // Native full_scan_carrier runs after committing arrival, before round updates.
                var previous = state.Cells.Where(cell => cell.IsEnemy).Select(cell => cell.Location).ToHashSet();
                var scan = await carrierScanner!.ScanAsync(state.Progress, TimeSpan.FromMinutes(2), mode: MapScanMode.Carrier,
                    fleet: new FleetScanOptions(configuration.HasDecoyEnemy, configuration.Fleet2 != 0), token: token);
                var added = state.Cells.Where(cell => cell.IsEnemy && !previous.Contains(cell.Location)).Select(cell => cell.Location).ToImmutableArray();
                state.RecordCarrierScan(new(state.CarrierCount, added, scan));
                state.RefreshFleetPaths(configuration);
            }
            if (state.Rounds.Initialized)
            {
                int roundBefore = state.Rounds.Round;
                if (battled) state.Rounds.RecordBattle();
                state.Rounds.Advance();
                if (mazeWaitFor is { } waitingFor)
                    state.RecordMazeWait(new(waitingFor, origin, landing, roundBefore, state.Rounds.Round,
                        (battled ? options!.AfterCombatConfirmDelay ?? options.ConfirmDelay : options!.ConfirmDelay).TotalSeconds,
                        result.FrameSequence));
                if (state.Rounds.EnemyMoved)
                {
                    await movableScan!.ScanAsync(before!, battled, token);
                    redispatch = true;
                }
                else if (state.Rounds.MazeChanged) redispatch = true;
            }
            AmmoPickupEvidence? pickup = null;
            // Native goto raises the round change before pick_up_ammo's inventory update.
            if (ammo && !redispatch)
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
            committed = new(MapMoveOutcome.Committed, result) { AmmoPickup = pickup, MechanismRelease = release };
        }
        catch
        {
            state.MovementInvalidated = true;
            camera.Invalidate();
            throw;
        }
        if (redispatch || decoyConfirmed) throw new MapEnemyMovedException();
        return committed;
    }
}
