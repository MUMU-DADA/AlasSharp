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
            interrupted.MarkerReads == 0 && interrupted.Invalidated && map.Fleet1Location == new Cell(1, 1),
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

        Console.WriteLine("Map arrival: fresh-frame confirmation, marker reset, interruption, timeout and submarine/current marker checks passed; no combat or settlement verification.");
    }

    private sealed class TestClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance() => _ticks += TimeSpan.FromSeconds(0.25).Ticks;
    }
    private sealed record Signal(bool InMap, FleetMarker Marker);
    private sealed class Camera(TestClock clock, IReadOnlyList<Signal> signals) : IMapArrivalCamera
    {
        private int _index = -1;
        public long FrameSequence { get; private set; } = 1;
        public int Taps { get; private set; }
        public int MarkerReads { get; private set; }
        public bool ReuseSequence { get; init; }
        public bool Invalidated { get; private set; }
        public void Invalidate() => Invalidated = true;
        public ValueTask TapCellAsync(Cell destination, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); if (Invalidated) throw new InvalidOperationException("Camera invalidated"); Taps++; return ValueTask.CompletedTask; }
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
}
