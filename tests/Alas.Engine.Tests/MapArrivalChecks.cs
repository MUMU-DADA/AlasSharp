using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static class MapArrivalChecks
{
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    public static async Task RunAsync()
    {
        var map = new CampaignState(new MapDefinition("C3", "-- -- --\n-- -- --\n-- -- --", [], [], []));
        map.Fleet1Location = new(1, 1);
        var destination = new Cell(2, 2);
        var options = new MapArrivalOptions(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(3));

        var clock = new TestClock();
        var steady = new Camera(clock, [new(true, new(true, false))]);
        var checker = new MapArrivalCheck(steady, map, steady.InMapAsync, clock);
        var reached = await checker.TapAndCheckAsync(destination, options);
        Check(reached is { Outcome: MapArrivalOutcome.MarkerConfirmed, FreshFrames: 4, FrameSequence: 5 } &&
            steady.Taps == 1 && steady.MarkerReads == 4 && !steady.Invalidated && map.Fleet1Location == new Cell(1, 1),
            "Fleet-marker confirmation changed sortie state or accepted a single frame");
        bool repeated = false;
        try { await checker.TapAndCheckAsync(destination, options); }
        catch (InvalidOperationException) { repeated = true; }
        Check(repeated && steady.Taps == 1, "Arrival checker sent a second tap without a new movement attempt");

        clock = new TestClock();
        var flicker = new Camera(clock, [new(true, new(true, false)), new(true, new(true, false)),
            new(true, default), new(true, new(true, false))]);
        reached = await new MapArrivalCheck(flicker, map, flicker.InMapAsync, clock)
            .TapAndCheckAsync(destination, options);
        Check(reached is { Outcome: MapArrivalOutcome.MarkerConfirmed, FreshFrames: 7 },
            "Arrival marker disappearance did not restart confirmation");

        clock = new TestClock();
        var interrupted = new Camera(clock, [new(false, default)]);
        reached = await new MapArrivalCheck(interrupted, map, interrupted.InMapAsync, clock)
            .TapAndCheckAsync(destination, options);
        Check(reached is { Outcome: MapArrivalOutcome.MapInterrupted, FreshFrames: 1 } &&
            reached.Encounter == MapEncounterKind.UnknownPage && interrupted.MarkerReads == 0 &&
            interrupted.Invalidated && map.Fleet1Location == new Cell(1, 1),
            "A non-map frame became an arrival or was read as a map grid");
        bool blocked = false;
        try { await interrupted.TapCellAsync(destination); } catch (InvalidOperationException) { blocked = true; }
        Check(blocked && interrupted.Taps == 1, "Interrupted arrival allowed another click on stale geometry");

        clock = new TestClock();
        var currentOnly = new Camera(clock, [new(true, new(false, true))]);
        reached = await new MapArrivalCheck(currentOnly, map, currentOnly.InMapAsync, clock)
            .TapAndCheckAsync(destination, new(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1)));
        Check(reached.Outcome == MapArrivalOutcome.Unconfirmed && currentOnly.Invalidated &&
            map.Fleet1Location == new Cell(1, 1),
            "Current-fleet marker alone confirmed an ordinary destination");

        clock = new TestClock();
        currentOnly = new Camera(clock, [new(true, new(false, true))]);
        map.SubmarineLocation = new(2, 1);
        reached = await new MapArrivalCheck(currentOnly, map, currentOnly.InMapAsync, clock)
            .TapAndCheckAsync(destination, options);
        Check(reached is { Outcome: MapArrivalOutcome.MarkerConfirmed, FreshFrames: 4 },
            "Submarine-covered destination did not use the current-fleet marker");
        map.SubmarineLocation = null;

        clock = new TestClock();
        currentOnly = new Camera(clock, [new(true, new(false, true))]);
        reached = await new MapArrivalCheck(currentOnly, map, currentOnly.InMapAsync, clock)
            .TapAndCheckAsync(destination, options with { AllowCurrentMarker = true });
        Check(reached.Outcome == MapArrivalOutcome.MarkerConfirmed,
            "Configured current-fleet marker did not confirm arrival");

        clock = new TestClock();
        var stale = new Camera(clock, [new(true, new(true, true))]) { ReuseSequence = true };
        bool rejected = false;
        try { await new MapArrivalCheck(stale, map, stale.InMapAsync, clock).TapAndCheckAsync(destination, options); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Arrival check accepted a stale screenshot");

        clock = new TestClock();
        var recovering = new Camera(clock, [new(true, default), new(true, default), new(true, new(true, true))]);
        var encounter = new Probe(MapEncounterKind.Combat);
        var handler = new Handler(recovering, clock);
        reached = await new MapArrivalCheck(recovering, map, recovering.InMapAsync, clock, encounter, handler)
            .TapAndCheckAsync(destination, options);
        Check(reached is { Outcome: MapArrivalOutcome.MarkerConfirmed, FreshFrames: 5 } &&
            recovering.Taps == 1 && recovering.Relocalizations == 1 && !recovering.Invalidated &&
            encounter.Initializations == 2 && handler.Calls == 1 && handler.SawSuspended &&
            reached.HandledEncounters.SequenceEqual([MapEncounterKind.Combat]) &&
            reached.Combats is [ { Return: CombatReturn.InMap, Rank: { Rank: CombatRank.S } } ] &&
            map.Fleet1Location == new Cell(1, 1),
            "Handled encounter did not resume the same grid tap after relocalization");

        clock = new TestClock();
        var stageCamera = new Camera(clock, [new(false, default)]);
        reached = await new MapArrivalCheck(stageCamera, map, stageCamera.InMapAsync, clock,
            new Probe(MapEncounterKind.Combat), new Handler(stageCamera, clock, combatReturn: CombatReturn.InStage))
            .TapAndCheckAsync(destination, options);
        Check(reached is { Outcome: MapArrivalOutcome.StageReturned, FreshFrames: 1 } &&
            reached.Combats is [ { Return: CombatReturn.InStage, Rank: { Rank: CombatRank.S } } ] &&
            stageCamera.Relocalizations == 0 && stageCamera.Invalidated && map.Fleet1Location == new Cell(1, 1),
            "Stage return attempted map relocalization or lost the battle evidence");

        clock = new TestClock();
        var focused = new Camera(clock, [new(true, new(true, true))]) { FocusBeforeTap = true };
        var focusedProbe = new Probe(MapEncounterKind.None, () => focused.Taps);
        reached = await new MapArrivalCheck(focused, map, focused.InMapAsync, clock, focusedProbe)
            .TapAndCheckAsync(destination, options);
        Check(reached is { Outcome: MapArrivalOutcome.MarkerConfirmed, FrameSequence: 6 } &&
            focusedProbe.FirstInitializedSequence == 2 && focusedProbe.TapsAtFirstInitialization == 0 &&
            focused.Prepared == 1 && focused.Taps == 1,
            "Encounter baseline was not captured after offscreen focus and before grid tap");

        clock = new TestClock();
        var pending = new Camera(clock, [new(true, default)]);
        reached = await new MapArrivalCheck(pending, map, pending.InMapAsync, clock,
            new Probe(MapEncounterKind.Ambush)).TapAndCheckAsync(destination, options);
        Check(reached is { Outcome: MapArrivalOutcome.MapInterrupted, Encounter: MapEncounterKind.Ambush,
            FreshFrames: 1 } && pending.Invalidated && pending.MarkerReads == 0,
            "Unhandled encounter was treated as arrival or left old geometry usable");

        await MapEncounterProbeChecks.RunAsync();
        await MovementChecksAsync(destination, options);

        Console.WriteLine("Map arrival/movement: fresh-frame confirmation, ordinary/portal and synthetic combat state commits, stage return, air-raid wait and unsupported-interaction rejection passed; no real combat or settlement verification.");
    }

    private static async Task MovementChecksAsync(Cell destination, MapArrivalOptions options)
    {
        static CampaignState State(bool portal = false)
        {
            var state = new CampaignState(new MapDefinition("C3", "-- -- --\n-- -- --\n-- -- --", [], [], [],
                portals: portal ? [new MapEdge(new Cell(2, 2), new Cell(3, 2))] : null));
            state.InitializeMapData(new(Portals: portal));
            state.Fleet1Location = new(1, 1);
            state[new(1, 1)].IsFleet = state[new(1, 1)].IsCurrentFleet = true;
            return state;
        }

        var clock = new TestClock();
        var state = State();
        var camera = new Camera(clock, [new(true, new(true, true))]);
        var arrival = new MapArrivalCheck(camera, state, camera.InMapAsync, clock);
        var moved = await new MapMovement(state, new(), camera, () => arrival).MoveAsync(destination, options);
        Check(moved.Outcome == MapMoveOutcome.Committed && moved.Arrival.HandledEncounters.IsEmpty &&
            state.Fleet1Location == destination && !state[new(1, 1)].IsFleet &&
            state[destination].IsFleet && state[destination].IsCurrentFleet &&
            state[destination].Cost == 0 && camera.Taps == 1,
            "Ordinary arrival did not commit fleet and path state exactly once");

        clock = new TestClock(); state = State();
        camera = new Camera(clock, [new(true, new(true, true))]);
        int arrivalInstances = 0;
        var movement = new MapMovement(state, new(), camera, () =>
        {
            arrivalInstances++;
            return new MapArrivalCheck(camera, state, camera.InMapAsync, clock);
        });
        var next = new Cell(3, 2);
        moved = await movement.MoveAsync(destination, options);
        var secondMove = await movement.MoveAsync(next, options);
        Check(moved.Outcome == MapMoveOutcome.Committed && secondMove.Outcome == MapMoveOutcome.Committed &&
            arrivalInstances == 2 && camera.Taps == 2 && state.Fleet1Location == next &&
            !state[destination].IsFleet && state[next].IsCurrentFleet && state[next].Cost == 0,
            "Reusable movement did not create a fresh arrival check for each grid tap");

        clock = new TestClock(); state = State();
        state.Fleet2Location = new(3, 3);
        state[new(3, 3)].IsFleet = true;
        state.FleetIndex = 2;
        camera = new Camera(clock, [new(true, new(true, true))]);
        arrival = new MapArrivalCheck(camera, state, camera.InMapAsync, clock);
        moved = await new MapMovement(state, new(), camera, () => arrival).MoveAsync(destination, options);
        Check(moved.Outcome == MapMoveOutcome.Committed && state.Fleet1Location == new Cell(1, 1) &&
            state[new(1, 1)].IsFleet && state.Fleet2Location == destination &&
            !state[new(3, 3)].IsFleet && state[destination].IsCurrentFleet &&
            state[new(1, 1)].Cost1 == 0 && state[destination].Cost2 == 0,
            "Second-fleet movement changed first-fleet state or failed to rebuild both costs");

        clock = new TestClock(); state = State();
        camera = new Camera(clock, [new(true, default)]);
        arrival = new MapArrivalCheck(camera, state, camera.InMapAsync, clock);
        moved = await new MapMovement(state, new(), camera, () => arrival).MoveAsync(destination,
            new(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1)));
        Check(moved.Outcome == MapMoveOutcome.Unconfirmed && state.Fleet1Location == new Cell(1, 1) &&
            state[new(1, 1)].IsFleet && !state[destination].IsFleet && camera.Invalidated,
            "Unconfirmed movement mutated authoritative fleet state");

        clock = new TestClock(); state = State();
        camera = new Camera(clock, [new(true, default), new(true, default), new(true, new(true, true))]);
        arrival = new MapArrivalCheck(camera, state, camera.InMapAsync, clock,
            new Probe(MapEncounterKind.Combat), new Handler(camera, clock));
        moved = await new MapMovement(state, new(), camera, () => arrival).MoveAsync(destination, options);
        Check(moved.Outcome == MapMoveOutcome.UnsupportedEncounter &&
            moved.Arrival.HandledEncounters.SequenceEqual([MapEncounterKind.Combat]) &&
            state.Fleet1Location == new Cell(1, 1) && state[new(1, 1)].IsFleet &&
            !state[destination].IsFleet && camera.Invalidated,
            "Handled combat was committed without its battle-state contract");

        clock = new TestClock(); state = State();
        state[destination].IsEnemy = state[destination].MayEnemy = true;
        camera = new Camera(clock, [new(true, default), new(true, default), new(true, new(true, true))]);
        int combatCalls = 0;
        arrival = new MapArrivalCheck(camera, state, camera.InMapAsync, clock,
            new Probe(MapEncounterKind.Combat), new MapCombatHandler(token =>
            {
                token.ThrowIfCancellationRequested();
                combatCalls++;
                clock.Advance(30);
                return ValueTask.FromResult(new CombatFlowResult(CombatReturn.InMap,
                    new(CombatRank.S, CombatRankSource.BattleStatus, UiAssets.Combat.BATTLE_STATUS_S.Id),
                    false, false, 5));
            }));
        moved = await new MapMovement(state, new(), camera, () => arrival).FightAsync(destination, options);
        Check(moved.Outcome == MapMoveOutcome.Committed && state.Progress.Battle == 1 && state.AmmoCount == 2 &&
            state.Fleet1Location == destination && !state[new(1, 1)].IsFleet &&
            state[destination].IsFleet && state[destination].IsCleared && !state[destination].IsEnemy &&
            moved.Arrival.Combats is [ { Rank: { Rank: CombatRank.S } } ] && combatCalls == 1,
            "Confirmed enemy combat did not commit battle and fleet state together");

        clock = new TestClock(); state = State();
        state[destination].IsSiren = state[destination].MaySiren = true;
        camera = new Camera(clock, [new(true, default), new(true, default), new(true, new(true, true))]);
        arrival = new MapArrivalCheck(camera, state, camera.InMapAsync, clock,
            new Probe(MapEncounterKind.Combat), new Handler(camera, clock));
        moved = await new MapMovement(state, new(), camera, () => arrival).FightAsync(destination, options);
        Check(moved.Outcome == MapMoveOutcome.Committed && state.Progress is { Battle: 1, Siren: 1 } &&
            state.AmmoCount == 2 && !state[destination].IsSiren && !state[destination].IsCleared,
            "Siren combat did not update its own counter or clear the target");

        clock = new TestClock(); state = State();
        state[destination].IsEnemy = true;
        camera = new Camera(clock, [new(true, new(true, true))]);
        arrival = new MapArrivalCheck(camera, state, camera.InMapAsync, clock);
        moved = await new MapMovement(state, new(), camera, () => arrival).FightAsync(destination, options);
        Check(moved.Outcome == MapMoveOutcome.UnsupportedEncounter && state.Progress.Battle == 0 &&
            state.AmmoCount == 3 && state[destination].IsEnemy && state.Fleet1Location == new Cell(1, 1),
            "Enemy-grid marker without combat evidence changed authoritative state");

        foreach (var rank in new CombatRank?[] { null, CombatRank.C })
        {
            clock = new TestClock(); state = State();
            state[destination].IsEnemy = true;
            camera = new Camera(clock, [new(true, default), new(true, default), new(true, new(true, true))]);
            arrival = new MapArrivalCheck(camera, state, camera.InMapAsync, clock,
                new Probe(MapEncounterKind.Combat), new Handler(camera, clock, rank: rank));
            moved = await new MapMovement(state, new(), camera, () => arrival).FightAsync(destination, options);
            Check(moved.Outcome == MapMoveOutcome.UnsupportedEncounter && state.Progress.Battle == 0 &&
                state.AmmoCount == 3 && state.Fleet1Location == new Cell(1, 1) && state[destination].IsEnemy,
                "Combat with missing or losing rank changed authoritative map state");
        }

        clock = new TestClock(); state = State();
        state[destination].IsEnemy = true;
        camera = new Camera(clock, [new(true, default)]);
        arrival = new MapArrivalCheck(camera, state, camera.InMapAsync, clock,
            new Probe(MapEncounterKind.Combat), new MapCombatHandler(_ => throw new IOException("synthetic combat failure")));
        bool combatFailed = false;
        try { await new MapMovement(state, new(), camera, () => arrival).FightAsync(destination, options); }
        catch (IOException) { combatFailed = true; }
        Check(combatFailed && camera.Invalidated && state.Progress.Battle == 0 && state.AmmoCount == 3 &&
            state[destination].IsEnemy && state.Fleet1Location == new Cell(1, 1),
            "Failed C# combat left a usable camera or committed map state");

        clock = new TestClock(); state = State();
        state[destination].IsBoss = true;
        camera = new Camera(clock, [new(false, default)]);
        arrival = new MapArrivalCheck(camera, state, camera.InMapAsync, clock,
            new Probe(MapEncounterKind.Combat), new Handler(camera, clock, combatReturn: CombatReturn.InStage));
        moved = await new MapMovement(state, new(), camera, () => arrival).FightAsync(destination, options);
        Check(moved.Outcome == MapMoveOutcome.StageReturned && state.Progress.Battle == 0 &&
            state.Fleet1Location == new Cell(1, 1) && moved.Arrival.Combats is [ { Return: CombatReturn.InStage } ],
            "Boss stage return was treated as a map move or discarded its settlement evidence");

        foreach (var rank in new CombatRank?[] { null, CombatRank.C })
        {
            clock = new TestClock(); state = State();
            state[destination].IsBoss = true;
            camera = new Camera(clock, [new(false, default)]);
            arrival = new MapArrivalCheck(camera, state, camera.InMapAsync, clock,
                new Probe(MapEncounterKind.Combat), new Handler(camera, clock,
                    combatReturn: CombatReturn.InStage, rank: rank));
            moved = await new MapMovement(state, new(), camera, () => arrival).FightAsync(destination, options);
            Check(moved.Outcome == MapMoveOutcome.UnsupportedEncounter && state.Progress.Battle == 0 &&
                state.Fleet1Location == new Cell(1, 1) && moved.Arrival.Combats.Length == 1,
                "Stage return with missing or losing rank was accepted as a completed boss fight");
        }

        clock = new TestClock(); state = State();
        camera = new Camera(clock, [new(true, default), new(true, default), new(true, new(true, true))]);
        arrival = new MapArrivalCheck(camera, state, camera.InMapAsync, clock,
            new Probe(MapEncounterKind.AirRaid), new Handler(camera, clock, MapEncounterKind.AirRaid));
        moved = await new MapMovement(state, new(), camera, () => arrival).MoveAsync(destination, options);
        Check(moved.Outcome == MapMoveOutcome.Committed &&
            moved.Arrival.HandledEncounters.SequenceEqual([MapEncounterKind.AirRaid]) &&
            state.Fleet1Location == destination && state[destination].IsFleet &&
            camera.Taps == 1 && camera.Relocalizations == 1,
            "Completed air raid did not preserve the original move and commit state");

        clock = new TestClock(); state = State(portal: true);
        camera = new Camera(clock, [new(true, new(true, true))]);
        arrival = new MapArrivalCheck(camera, state, camera.InMapAsync, clock);
        moved = await new MapMovement(state, new(), camera, () => arrival).MoveAsync(destination, options);
        var exit = new Cell(3, 2);
        Check(moved.Outcome == MapMoveOutcome.Committed && state.Fleet1Location == exit &&
            state[exit].IsFleet && state[exit].IsCurrentFleet && !state[destination].IsFleet &&
            state[exit].Cost == 0 && camera.Anchored == exit && camera.Relocalizations == 4 &&
            camera.CenterReads == 4 && camera.MarkerReads == 0 && camera.Taps == 1,
            "Portal arrival did not verify the center marker and commit the linked exit");

        clock = new TestClock(); state = State(portal: true);
        camera = new Camera(clock, [new(true, default)]);
        arrival = new MapArrivalCheck(camera, state, camera.InMapAsync, clock);
        moved = await new MapMovement(state, new(), camera, () => arrival).MoveAsync(destination,
            new(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1)));
        Check(moved.Outcome == MapMoveOutcome.Unconfirmed && state.Fleet1Location == new Cell(1, 1) &&
            !state[exit].IsFleet && camera.Anchored is null && camera.Invalidated,
            "Unconfirmed portal changed fleet position or anchored the camera");

        clock = new TestClock(); state = State();
        state[destination].IsPortal = true;
        camera = new Camera(clock, [new(true, new(true, true))]);
        arrival = new MapArrivalCheck(camera, state, camera.InMapAsync, clock);
        bool rejected = false;
        try { await new MapMovement(state, new(), camera, () => arrival).MoveAsync(destination, options); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected && camera.Taps == 0 && state.Fleet1Location == new Cell(1, 1),
            "Portal without a linked exit was clicked");

        foreach (var config in new[] { new CampaignConfiguration { HasMovableEnemy = true },
                     new CampaignConfiguration { HasMovableNormalEnemy = true },
                     new CampaignConfiguration { HasMaze = true } })
        {
            clock = new TestClock(); state = State();
            camera = new Camera(clock, [new(true, new(true, true))]);
            arrival = new MapArrivalCheck(camera, state, camera.InMapAsync, clock);
            rejected = false;
            try { await new MapMovement(state, config, camera, () => arrival).MoveAsync(destination, options); }
            catch (NotSupportedException) { rejected = true; }
            Check(rejected && camera.Taps == 0 && state.Fleet1Location == new Cell(1, 1),
                "An unported map-round mode was clicked before its state transition was implemented");
        }
    }

    private sealed class TestClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(double seconds = 0.25) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }
    private sealed record Signal(bool InMap, FleetMarker Marker);
    private sealed class Camera(TestClock clock, IReadOnlyList<Signal> signals) : IMapArrivalCamera
    {
        private int _index = -1;
        public long FrameSequence { get; private set; } = 1;
        public int Taps { get; private set; }
        public int Prepared { get; private set; }
        public int MarkerReads { get; private set; }
        public int CenterReads { get; private set; }
        public Cell? Anchored { get; private set; }
        public bool ReuseSequence { get; init; }
        public bool FocusBeforeTap { get; init; }
        public bool Invalidated { get; private set; }
        public bool Suspended { get; private set; }
        public int Relocalizations { get; private set; }
        public void Invalidate() => Invalidated = true;
        public void Suspend() => Suspended = true;
        public ValueTask PrepareTapAsync(Cell destination, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (Invalidated || Suspended) throw new InvalidOperationException("Camera is not localized");
            Prepared++;
            if (FocusBeforeTap) FrameSequence++;
            return ValueTask.CompletedTask;
        }
        public ValueTask RelocalizeAsync(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Suspended = false; Relocalizations++; clock.Advance(); FrameSequence++;
            _index = Math.Min(_index + 1, signals.Count - 1);
            return ValueTask.CompletedTask;
        }
        public ValueTask AnchorAtAsync(Cell location, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (Suspended || Invalidated) throw new InvalidOperationException("Camera is not localized");
            Anchored = location;
            return ValueTask.CompletedTask;
        }
        public ValueTask TapCellAsync(Cell destination, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (Invalidated || Suspended) throw new InvalidOperationException("Camera is not localized");
            Taps++;
            return ValueTask.CompletedTask;
        }
        public ValueTask RefreshImageAsync(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); clock.Advance();
            _index = Math.Min(_index + 1, signals.Count - 1);
            if (!ReuseSequence) FrameSequence++;
            return ValueTask.CompletedTask;
        }
        public ValueTask<bool> InMapAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(signals[_index].InMap); }
        public ValueTask<FleetMarker> ReadFleetMarkerAsync(Cell destination, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); MarkerReads++; return ValueTask.FromResult(signals[_index].Marker); }
        public ValueTask<FleetMarker> ReadCenterMarkerAsync(CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); CenterReads++; return ValueTask.FromResult(signals[_index].Marker); }
    }
    private sealed class Probe(MapEncounterKind first, Func<int>? taps = null) : IMapEncounterProbe
    {
        private bool _used;
        public int Initializations { get; private set; }
        public long FirstInitializedSequence { get; private set; }
        public int TapsAtFirstInitialization { get; private set; } = -1;
        public ValueTask InitializeAsync(long frameSequence, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Initializations++ == 0)
            {
                FirstInitializedSequence = frameSequence;
                TapsAtFirstInitialization = taps?.Invoke() ?? -1;
            }
            return ValueTask.CompletedTask;
        }
        public ValueTask<MapEncounterKind> InspectAsync(long frameSequence, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (_used) return ValueTask.FromResult(MapEncounterKind.None);
            _used = true;
            return ValueTask.FromResult(first);
        }
    }
    private sealed class Handler(Camera camera, TestClock clock,
        MapEncounterKind completed = MapEncounterKind.Combat, CombatReturn combatReturn = CombatReturn.InMap,
        CombatRank? rank = CombatRank.S) : IMapEncounterHandler
    {
        public int Calls { get; private set; }
        public bool SawSuspended { get; private set; }
        public ValueTask<MapEncounterHandling> HandleAsync(MapEncounterKind encounter, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls++; SawSuspended = camera.Suspended;
            clock.Advance(30);
            if (encounter != completed) return ValueTask.FromResult(new MapEncounterHandling(MapEncounterContinuation.Unhandled));
            CombatFlowResult? result = encounter == MapEncounterKind.Combat
                ? new(combatReturn, rank is { } value ? new(value, CombatRankSource.BattleStatus,
                    UiAssets.Combat.BATTLE_STATUS_S.Id) : null, false, false, 1) : null;
            return ValueTask.FromResult(new MapEncounterHandling(encounter == MapEncounterKind.Combat &&
                combatReturn == CombatReturn.InStage ? MapEncounterContinuation.InStage : MapEncounterContinuation.InMap,
                result));
        }
    }
}
