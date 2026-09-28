using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static partial class MapWalkTimeoutChecks
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    public static async Task RunAsync()
    {
        var state = new CampaignState(new MapDefinition("B1", "SP --", [], [], []));
        state.InitializeMapData(new()); state.Fleet1Location = new(1, 1); state.RefreshFleetPaths(new());
        var clock = new Clock(); var camera = new Camera(clock);
        int recoveries = 0;
        var arrival = new MapArrivalCheck(camera, state, _ => ValueTask.FromResult(true), clock,
            recoverAfterWalkTimeout: token =>
            {
                token.ThrowIfCancellationRequested(); recoveries++; camera.Recover(); return ValueTask.CompletedTask;
            });
        var result = await arrival.TapAndCheckAsync(new(2, 1), new(TimeSpan.FromSeconds(.5), TimeSpan.FromSeconds(20)));
        Check(result.Outcome == MapArrivalOutcome.MarkerConfirmed && recoveries == 1 && camera.Refreshes >= 3 &&
            camera.FrameSequence == result.FrameSequence && !camera.Invalidated && state.Fleet1Location == new Cell(1, 1) && result.RetryTaps == 1,
            "Walk timeout did not recover, retry and confirm without committing before the caller");
        Check(arrival.WalkTimeouts is [{ Fleet: 1, Target: (Column: 2, Row: 1), ObservedFrame: > 0, RecoveredFrame: > 0, RetapCompleted: true }],
            "Walk timeout evidence omitted the observed and recovered frames");
        RunReport.ValidateWalkTimeout(arrival.WalkTimeouts.Single());

        clock = new Clock(); camera = new Camera(clock);
        arrival = new MapArrivalCheck(camera, state, _ => ValueTask.FromResult(true), clock);
        result = await arrival.TapAndCheckAsync(new(2, 1), new(TimeSpan.FromSeconds(.5), TimeSpan.FromSeconds(20)));
        Check(result.Outcome == MapArrivalOutcome.Unconfirmed && camera.Invalidated && arrival.WalkTimeouts is [{ RecoveredFrame: null }],
            "Missing timeout recovery callback was accepted or left the camera usable");

        clock = new Clock(); camera = new Camera(clock) { Cancel = true };
        using var cancelled = new CancellationTokenSource();
        arrival = new MapArrivalCheck(camera, state, _ => ValueTask.FromResult(true), clock,
            recoverAfterWalkTimeout: _ => { recoveries++; return ValueTask.CompletedTask; });
        bool canceled = false;
        try { await arrival.TapAndCheckAsync(new(2, 1), new(TimeSpan.FromSeconds(.5), TimeSpan.FromSeconds(20)), cancelled.Token); }
        catch (OperationCanceledException) { canceled = true; }
        Check(canceled && recoveries == 1, "Caller cancellation was converted into walk timeout recovery");
        await FailuresAsync();
        Console.WriteLine("Walk timeout: recovery/retap, current-marker confirmation and failure boundaries passed; no live device.");
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(double seconds) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }

    private sealed class Camera(Clock clock) : IMapArrivalCamera
    {
        public long FrameSequence { get; private set; } = 1;
        public int Refreshes { get; private set; }
        public bool Invalidated { get; private set; }
        public bool Cancel { get; init; }
        private bool _recovered;
        public ValueTask PrepareTapAsync(Cell destination, CancellationToken token = default) => ValueTask.CompletedTask;
        public ValueTask TapCellAsync(Cell destination, CancellationToken token = default) => ValueTask.CompletedTask;
        public ValueTask RefreshImageAsync(CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); if (Cancel) throw new OperationCanceledException(token); Refreshes++; FrameSequence++; clock.Advance(10); return ValueTask.CompletedTask; }
        public ValueTask<FleetMarker> ReadFleetMarkerAsync(Cell destination, CancellationToken token = default)
            => ValueTask.FromResult(new FleetMarker(_recovered, _recovered));
        public ValueTask<FleetMarker> ReadCenterMarkerAsync(CancellationToken token = default) => throw new InvalidOperationException();
        public ValueTask RelocalizeAsync(CancellationToken token = default) => throw new InvalidOperationException();
        public ValueTask AnchorAtAsync(Cell location, CancellationToken token = default) => throw new InvalidOperationException();
        public void Suspend() { }
        public void Invalidate() => Invalidated = true;
        public void Recover() { FrameSequence++; clock.Advance(1); _recovered = true; }
    }

    private sealed record Sample(int Fleet, string Marker, bool UseCurrent, string Expected, int MissedTaps, int AppearAt);
    public static async Task NativeAsync(string python, string upstream, string artifacts)
    {
        Directory.CreateDirectory(artifacts);
        var samples = new List<Sample>();
        foreach (int fleet in new[] { 1, 2 })
        foreach (string marker in new[] { "fleet", "current" })
        foreach (bool current in new[] { false, true })
        foreach (string expected in new[] { "", "combat", "combat_boss", "mystery" })
        foreach (int missed in new[] { 0, 1, 2 })
        foreach (int at in new[] { 1, 80, 81 })
            samples.Add(new(fleet, marker, current, expected, missed, at));
        string input = Path.Combine(artifacts, "input.json"), output = Path.Combine(artifacts, "native.json");
        await File.WriteAllTextAsync(input, JsonSerializer.Serialize(samples, TaskQueue.Json));
        var process = await new ProcessRunner().RunAsync(python,
            [Path.Combine(AppContext.BaseDirectory, "native_walk_timeout_reference.py"), upstream, input, output], TimeSpan.FromMinutes(2));
        Check(process.ExitCode == 0, "Native walk timeout replay failed: " + process.Error);
        var reference = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
        foreach (var source in new[] { CampaignState.InitializationSource, MapCameraState.Source })
            Check(reference["sources"]![source.Path]!.GetValue<string>() == source.Sha256, "Walk timeout source drift: " + source.Path);
        for (int i = 0; i < samples.Count; i++)
        {
            var sample = samples[i]; var native = reference["results"]![i]!;
            var state = new CampaignState(new MapDefinition("B1", "SP --", [], [], []));
            state.InitializeMapData(new()); state.FleetIndex = sample.Fleet;
            if (sample.Fleet == 1) state.Fleet1Location = new(1, 1); else state.Fleet2Location = new(1, 1);
            state.RefreshFleetPaths(new());
            var camera = new Replay(sample);
            var arrival = new MapArrivalCheck(camera, state, _ => ValueTask.FromResult(true), camera.Clock,
                camera, recoverAfterWalkTimeout: camera.RecoverAsync);
            var result = await arrival.TapAndCheckAsync(new(2, 1), new(TimeSpan.FromSeconds(.5), TimeSpan.FromSeconds(20),
                sample.UseCurrent && sample.Expected != "combat_boss")
                { ExpectCombat = sample.Expected.StartsWith("combat", StringComparison.Ordinal), ExpectMystery = sample.Expected == "mystery" });
            string label = JsonSerializer.Serialize(sample, TaskQueue.Json);
            Check(result.Outcome == MapArrivalOutcome.MarkerConfirmed && !camera.Invalidated, "Native arrival not confirmed: " + label);
            Check(camera.Frames == native["frames"]!.GetValue<int>(), "Native timeout arrival frame differs: " + label + " C#=" + camera.Frames + " native=" + native["frames"]);
            Check(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(camera.Taps), native["taps"]), "Timeout tap order differs: " + label);
            Check(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(camera.RecoveryFrames), native["recoveries"]), "Timeout recovery frame differs: " + label);
            Check(result.RetryTaps == sample.MissedTaps && result.WalkTimeouts.Length == sample.MissedTaps &&
                camera.Initializations == camera.Taps.Count && state.BattleCount == 0 && state.MysteryCount == 0,
                "Retry lost baseline or invented interaction accounting: " + label);
            foreach (var evidence in result.WalkTimeouts) RunReport.ValidateWalkTimeout(evidence);
        }
        await MapViewChecks.WalkTimeoutCameraAsync(upstream);
        await SessionAsync(python, upstream, artifacts);
        Console.WriteLine($"Walk timeout: {samples.Count} actual native _goto traces, camera ordering, session failure artifacts and task isolation passed; synthetic device only.");
    }

    private sealed class Replay(Sample sample) : IMapArrivalCamera, IMapEncounterProbe
    {
        public Clock Clock { get; } = new();
        public int Frames { get; private set; }
        private int _attemptFrames;
        public long FrameSequence => Frames + 1;
        public List<int> Taps { get; } = [];
        public List<int> RecoveryFrames { get; } = [];
        public int Initializations { get; private set; }
        public bool Invalidated { get; private set; }
        public bool Suspended { get; private set; }
        public string Failure { get; set; } = "";
        public Func<CancellationToken, ValueTask>? OnRecovery { get; set; }
        public ValueTask PrepareTapAsync(Cell destination, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Check(!Suspended && !Invalidated, "Retry used an unavailable camera"); return ValueTask.CompletedTask; }
        public ValueTask TapCellAsync(Cell destination, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (Failure == "retap" && Taps.Count > 0) throw new IOException("Synthetic retap failure");
            Taps.Add(Frames); _attemptFrames = 0; return ValueTask.CompletedTask;
        }
        public async ValueTask RefreshImageAsync(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (Failure == "capture") { await Task.Delay(Timeout.InfiniteTimeSpan, token); return; }
            Frames++; _attemptFrames++; Clock.Advance(.25);
            if (Frames > 700) throw new InvalidOperationException("Timeout replay exceeded frame budget");
        }
        public ValueTask<FleetMarker> ReadFleetMarkerAsync(Cell destination, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            bool present = Taps.Count > sample.MissedTaps && _attemptFrames >= sample.AppearAt;
            return ValueTask.FromResult(new FleetMarker(present && sample.Marker == "fleet", present));
        }
        public ValueTask<FleetMarker> ReadCenterMarkerAsync(CancellationToken token = default) => ReadFleetMarkerAsync(default, token);
        public ValueTask RelocalizeAsync(CancellationToken token = default) => throw new InvalidOperationException("Unexpected generic relocalization");
        public ValueTask AnchorAtAsync(Cell location, CancellationToken token = default) => ValueTask.CompletedTask;
        public void Suspend() => Suspended = true;
        public void Invalidate() => Invalidated = true;
        public async ValueTask RecoverAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Check(Suspended, "Recovery did not pause the camera");
            if (OnRecovery is not null) await OnRecovery(token);
            if (Failure == "recovery") throw new IOException("Synthetic recovery failure");
            if (Failure == "cancel") throw new OperationCanceledException();
            RecoveryFrames.Add(Frames);
            if (Failure != "stale") { Frames++; Clock.Advance(.25); }
            Suspended = false;
        }
        public ValueTask RecoverCombatAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); Frames++; Clock.Advance(.25); Suspended = false; return ValueTask.CompletedTask; }
        public ValueTask InitializeAsync(long frameSequence, CancellationToken token)
        { Check(frameSequence == FrameSequence, "Retry baseline uses wrong frame"); Initializations++; return ValueTask.CompletedTask; }
        public ValueTask<MapEncounterKind> InspectAsync(long frameSequence, CancellationToken token) => ValueTask.FromResult(MapEncounterKind.None);
    }
}
