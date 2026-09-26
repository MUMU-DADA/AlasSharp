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
            map.Fleet1Location == new Cell(1, 1),
            "Handled encounter did not resume the same grid tap after relocalization");

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

        Console.WriteLine("Map arrival/movement: fresh-frame confirmation, encounter history, ordinary state commit, long-handler relocalization, air-raid wait and unsupported-interaction rejection passed; no combat or settlement verification.");
    }

    private static async Task MovementChecksAsync(Cell destination, MapArrivalOptions options)
    {
        static CampaignState State()
        {
            var state = new CampaignState(new MapDefinition("C3", "-- -- --\n-- -- --\n-- -- --", [], [], []));
            state.InitializeMapData(new());
            state.Fleet1Location = new(1, 1);
            state[new(1, 1)].IsFleet = state[new(1, 1)].IsCurrentFleet = true;
            return state;
        }

        var clock = new TestClock();
        var state = State();
        var camera = new Camera(clock, [new(true, new(true, true))]);
        var arrival = new MapArrivalCheck(camera, state, camera.InMapAsync, clock);
        var moved = await new MapMovement(state, new(), camera, arrival).MoveAsync(destination, options);
        Check(moved.Outcome == MapMoveOutcome.Committed && moved.Arrival.HandledEncounters.IsEmpty &&
            state.Fleet1Location == destination && !state[new(1, 1)].IsFleet &&
            state[destination].IsFleet && state[destination].IsCurrentFleet &&
            state[destination].Cost == 0 && camera.Taps == 1,
            "Ordinary arrival did not commit fleet and path state exactly once");

        clock = new TestClock(); state = State();
        state.Fleet2Location = new(3, 3);
        state[new(3, 3)].IsFleet = true;
        state.FleetIndex = 2;
        camera = new Camera(clock, [new(true, new(true, true))]);
        arrival = new MapArrivalCheck(camera, state, camera.InMapAsync, clock);
        moved = await new MapMovement(state, new(), camera, arrival).MoveAsync(destination, options);
        Check(moved.Outcome == MapMoveOutcome.Committed && state.Fleet1Location == new Cell(1, 1) &&
            state[new(1, 1)].IsFleet && state.Fleet2Location == destination &&
            !state[new(3, 3)].IsFleet && state[destination].IsCurrentFleet &&
            state[new(1, 1)].Cost1 == 0 && state[destination].Cost2 == 0,
            "Second-fleet movement changed first-fleet state or failed to rebuild both costs");

        clock = new TestClock(); state = State();
        camera = new Camera(clock, [new(true, default)]);
        arrival = new MapArrivalCheck(camera, state, camera.InMapAsync, clock);
        moved = await new MapMovement(state, new(), camera, arrival).MoveAsync(destination,
            new(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1)));
        Check(moved.Outcome == MapMoveOutcome.Unconfirmed && state.Fleet1Location == new Cell(1, 1) &&
            state[new(1, 1)].IsFleet && !state[destination].IsFleet && camera.Invalidated,
            "Unconfirmed movement mutated authoritative fleet state");

        clock = new TestClock(); state = State();
        camera = new Camera(clock, [new(true, default), new(true, default), new(true, new(true, true))]);
        arrival = new MapArrivalCheck(camera, state, camera.InMapAsync, clock,
            new Probe(MapEncounterKind.Combat), new Handler(camera, clock));
        moved = await new MapMovement(state, new(), camera, arrival).MoveAsync(destination, options);
        Check(moved.Outcome == MapMoveOutcome.UnsupportedEncounter &&
            moved.Arrival.HandledEncounters.SequenceEqual([MapEncounterKind.Combat]) &&
            state.Fleet1Location == new Cell(1, 1) && state[new(1, 1)].IsFleet &&
            !state[destination].IsFleet && camera.Invalidated,
            "Handled combat was committed without its battle-state contract");

        clock = new TestClock(); state = State();
        camera = new Camera(clock, [new(true, default), new(true, default), new(true, new(true, true))]);
        arrival = new MapArrivalCheck(camera, state, camera.InMapAsync, clock,
            new Probe(MapEncounterKind.AirRaid), new Handler(camera, clock, MapEncounterKind.AirRaid));
        moved = await new MapMovement(state, new(), camera, arrival).MoveAsync(destination, options);
        Check(moved.Outcome == MapMoveOutcome.Committed &&
            moved.Arrival.HandledEncounters.SequenceEqual([MapEncounterKind.AirRaid]) &&
            state.Fleet1Location == destination && state[destination].IsFleet &&
            camera.Taps == 1 && camera.Relocalizations == 1,
            "Completed air raid did not preserve the original move and commit state");

        clock = new TestClock(); state = State();
        state[destination].IsPortal = true;
        camera = new Camera(clock, [new(true, new(true, true))]);
        arrival = new MapArrivalCheck(camera, state, camera.InMapAsync, clock);
        bool rejected = false;
        try { await new MapMovement(state, new(), camera, arrival).MoveAsync(destination, options); }
        catch (NotSupportedException) { rejected = true; }
        Check(rejected && camera.Taps == 0 && state.Fleet1Location == new Cell(1, 1),
            "Unsupported portal was clicked before its state transition was implemented");

        foreach (var config in new[] { new CampaignConfiguration { HasMovableEnemy = true },
                     new CampaignConfiguration { HasMovableNormalEnemy = true },
                     new CampaignConfiguration { HasMaze = true } })
        {
            clock = new TestClock(); state = State();
            camera = new Camera(clock, [new(true, new(true, true))]);
            arrival = new MapArrivalCheck(camera, state, camera.InMapAsync, clock);
            rejected = false;
            try { await new MapMovement(state, config, camera, arrival).MoveAsync(destination, options); }
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
            if (!Suspended) throw new InvalidOperationException("Camera was not suspended");
            Suspended = false; Relocalizations++; clock.Advance(); FrameSequence++;
            _index = Math.Min(_index + 1, signals.Count - 1);
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
        MapEncounterKind completed = MapEncounterKind.Combat) : IMapEncounterHandler
    {
        public int Calls { get; private set; }
        public bool SawSuspended { get; private set; }
        public ValueTask<bool> HandleAsync(MapEncounterKind encounter, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls++; SawSuspended = camera.Suspended;
            clock.Advance(30);
            return ValueTask.FromResult(encounter == completed);
        }
    }
}
