using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static class MapScannerChecks
{
    public static async Task RunAsync()
    {
        int checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
            checks++;
        }
        async Task Throws<T>(Func<Task> action, string message) where T : Exception
        {
            try
            {
                var pending = action();
                if (await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(5))) != pending)
                    throw new InvalidOperationException("Test watchdog expired: " + message);
                await pending;
            }
            catch (T) { checks++; return; }
            throw new InvalidOperationException(message);
        }
        var budget = TimeSpan.FromSeconds(2);
        var map = new MapDefinition("B2", "SP ME\nMB --", ["A1", "B1"], [], [new(0, 1)]);
        CampaignState State(bool poor = false)
        {
            var state = new CampaignState(map);
            state.InitializeMapData(new(PoorMapData: poor));
            return state;
        }
        static MapObservation Empty(FakeCamera cam, MapScanMode mode) => new([], cam.Position, new(0, 0), mode);
        static MapObservation Enemy(FakeCamera cam, MapScanMode mode) => new(
            [new(new(2 - cam.Position.Column, 1 - cam.Position.Row), new(IsEnemy: true))], cam.Position, new(0, 0), mode);
        static MapObservation Conflict(FakeCamera cam, MapScanMode mode) => new([
            new(new(1 - cam.Position.Column, 1 - cam.Position.Row), new(IsBoss: true)),
            new(new(2 - cam.Position.Column, 1 - cam.Position.Row), new(IsBoss: true))], cam.Position, new(0, 0), mode);

        var state = State();
        var camera = new FakeCamera(new(2, 1), Enemy);
        var result = await new MapScanner(state, camera).ScanAsync(new(), budget);
        Check(result.StoppedEarly && result.Visited.SequenceEqual([new Cell(2, 1)]) && state[new(2, 1)].IsEnemy,
            "Scanner did not stop after observing all expected spawns");
        Check(camera.Focused.SequenceEqual([new Cell(2, 1)]) && camera.Tolerances.SequenceEqual([0.25]),
            "Nearest camera or native centering tolerance differs");

        var requiredCamera = new FakeCamera(new(2, 1), (cam, mode) => cam.Position == new Cell(2, 1) ? Enemy(cam, mode) : Empty(cam, mode));
        var required = await new MapScanner(State(), requiredCamera).ScanAsync(new(), budget,
            [new(2, 1)], [new(1, 1), new(1, 1)]);
        Check(!required.StoppedEarly && required.Visited.SequenceEqual([new Cell(2, 1), new Cell(1, 1)]),
            "Required view was skipped, or duplicated while joining the queue");

        var complete = State();
        complete[new(2, 1)].IsEnemy = true;
        complete[new(1, 1)].IsFleet = complete[new(1, 1)].IsCurrentFleet = true;
        var completeCamera = new FakeCamera(new(1, 1), Empty);
        var early = await new MapScanner(complete, completeCamera).ScanAsync(new(), budget);
        Check(early.StoppedEarly && completeCamera.Focused.Count == 0 && !complete[new(1, 1)].IsCurrentFleet && complete[new(1, 1)].IsFleet,
            "Pre-scan early exit or current-fleet reset differs");

        var conflictState = State();
        int observations = 0;
        var conflictCamera = new FakeCamera(new(1, 1), (cam, mode) => ++observations <= 2 ? Conflict(cam, mode) : Enemy(cam, mode));
        conflictCamera.Recover = cam => cam.Position = new(2, 1);
        var conflict = await new MapScanner(conflictState, conflictCamera).ScanAsync(new(), budget,
            mustScan: [new(1, 1)]);
        Check(conflict.RejectedViews == 2 && conflictCamera.EdgeUpdates.SequenceEqual([false, false]) && !conflictState.HasBoss,
            "Rejected views mutated state or skipped native edge recovery");
        Check(conflictCamera.Focused.SequenceEqual([new Cell(1, 1), new Cell(2, 1), new Cell(2, 1), new Cell(1, 1)]) &&
            conflict.Visited.SequenceEqual([new Cell(2, 1), new Cell(1, 1)]),
            "Recovery did not retain pending views and reselect from the recovered position");

        var oneConflict = new FakeCamera(new(1, 1), (cam, mode) => new(
            [new(new(0, 0), new(IsBoss: true))], cam.Position, new(0, 0), mode));
        var accepted = await new MapScanner(State(poor: true), oneConflict).ScanAsync(new(), budget, [new(1, 1)]);
        Check(accepted.RejectedViews == 0 && accepted.Visited.Count == 1 && oneConflict.EdgeUpdates.Count == 0,
            "A single conflicting prediction should still accept the view");

        var modeCamera = new FakeCamera(new(1, 1), (cam, _) => Empty(cam, MapScanMode.Carrier));
        await Throws<InvalidDataException>(() => new MapScanner(State(), modeCamera).ScanAsync(new(), budget).AsTask(),
            "Mismatched observation mode was accepted");
        var positionCamera = new FakeCamera(new(1, 1), (_, mode) => new([], new(2, 2), new(0, 0), mode));
        await Throws<InvalidDataException>(() => new MapScanner(State(), positionCamera).ScanAsync(new(), budget).AsTask(),
            "Mismatched observation camera was accepted");

        var fleetState = new CampaignState(new MapDefinition("C1", "ME MB --", ["A1"], [], [new(0, 3, Boss: 1)]));
        fleetState.InitializeMapData(new());
        fleetState.Fleet1Location = new(1, 1);
        var fleetCamera = new FakeCamera(new(1, 1), (cam, mode) => new([
            new(new(0, 0), new(IsEnemy: true)),
            new(new(1, 0), new(IsBoss: true, IsCaughtBySiren: true)),
            new(new(2, 0), new(IsEnemy: true))], cam.Position, new(0, 0), mode));
        await new MapScanner(fleetState, fleetCamera).ScanAsync(new(), budget, fleet: new(true, true));
        Check(fleetCamera.Modes.SequenceEqual([MapScanMode.Decoy]) && fleetState[new(3, 1)].IsEnemy,
            "Decoy scan did not use native merge mode");
        Check(fleetState.Fleet2Location == new Cell(2, 1) && !fleetState[new(1, 1)].IsEnemy &&
            fleetState[new(2, 1)].IsBoss && fleetState[new(2, 1)].IsCaughtBySiren,
            "Fleet cleanup lost the boss/caught exception or failed to infer the second fleet");

        var poorState = new CampaignState(new MapDefinition("B1", "-- --", ["A1", "B1"], [], []));
        poorState.InitializeMapData(new(PoorMapData: true));
        var poor = await new MapScanner(poorState, new FakeCamera(new(2, 1), Empty)).ScanAsync(new(), budget, queue: []);
        Check(!poor.StoppedEarly && poor.Predictions.Count == 0 && poor.Visited.SequenceEqual([new Cell(2, 1), new Cell(1, 1)]),
            "Poor-data scan should exhaust default cameras without an empty-table census");

        var predictionState = new CampaignState(new MapDefinition("B1", "ME SP", ["B1"], [], [new(0, 1)], covered: ["A1"]));
        predictionState.InitializeMapData(new());
        var prediction = await new MapScanner(predictionState, new FakeCamera(new(2, 1), Empty)).ScanAsync(new(), budget);
        Check(prediction.Predictions.SequenceEqual([new Cell(1, 1)]) && predictionState[new(1, 1)].IsEnemy,
            "Scan completion did not run occluded-spawn prediction");

        var invalidCamera = new FakeCamera(new(1, 1), Empty);
        await Throws<ArgumentOutOfRangeException>(() => new MapScanner(State(), invalidCamera).ScanAsync(new(), budget,
            mustScan: [new(3, 1)]).AsTask(), "Out-of-map required camera accepted");
        await Throws<InvalidOperationException>(() => new MapScanner(new CampaignState(map), invalidCamera).ScanAsync(new(), budget).AsTask(),
            "Non-poor scan accepted an unloaded spawn table");
        Check(invalidCamera.Focused.Count == 0, "Invalid scan moved a camera");

        var blockedCamera = new FakeCamera(new(1, 1), Empty) { BlockObservation = true };
        var blockedScanner = new MapScanner(State(poor: true), blockedCamera);
        using var cancellation = new CancellationTokenSource();
        var first = blockedScanner.ScanAsync(new(), budget, token: cancellation.Token).AsTask();
        await blockedCamera.Observing.Task.WaitAsync(budget);
        await Throws<TimeoutException>(() => blockedScanner.ScanAsync(new(), TimeSpan.FromMilliseconds(50)).AsTask(),
            "A queued scan did not include semaphore waiting in its deadline");
        Check(blockedCamera.Focused.Count == 1, "Timed-out queued scan moved a camera");
        cancellation.Cancel();
        await Throws<OperationCanceledException>(() => first, "Caller cancellation became timeout");
        blockedCamera.BlockObservation = false;
        Check((await blockedScanner.ScanAsync(new(), budget)).Visited.Count == 2,
            "Cancellation or queued timeout leaked the scan semaphore");

        var deadlineCamera = new FakeCamera(new(1, 1), Empty) { BlockObservation = true };
        var deadlineScanner = new MapScanner(State(poor: true), deadlineCamera);
        await Throws<TimeoutException>(() => deadlineScanner.ScanAsync(new(), TimeSpan.FromMilliseconds(50)).AsTask(),
            "Cooperative camera timeout was not surfaced");
        deadlineCamera.BlockObservation = false;
        Check((await deadlineScanner.ScanAsync(new(), budget)).Visited.Count == 2, "Deadline failure leaked the scan semaphore");
        Console.WriteLine($"Map scanner: {checks} checks passed; synthetic camera only, no device or settlement evidence.");
    }

    private sealed class FakeCamera(Cell initial, Func<FakeCamera, MapScanMode, MapObservation> observe) : IMapScanCamera
    {
        public Cell Position { get; set; } = initial;
        public List<Cell> Focused { get; } = [];
        public List<double> Tolerances { get; } = [];
        public List<bool> EdgeUpdates { get; } = [];
        public List<MapScanMode> Modes { get; } = [];
        public Action<FakeCamera>? Recover { get; set; }
        public bool BlockObservation { get; set; }
        public TaskCompletionSource Observing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask FocusAsync(Cell destination, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Position = destination;
            Focused.Add(destination);
            return ValueTask.CompletedTask;
        }
        public ValueTask CenterAsync(double tolerance, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Tolerances.Add(tolerance);
            return ValueTask.CompletedTask;
        }
        public async ValueTask<MapObservation> ObserveAsync(MapScanMode mode, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Modes.Add(mode);
            Observing.TrySetResult();
            if (BlockObservation) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return observe(this, mode);
        }
        public ValueTask EnsureEdgesAsync(bool skipFirstUpdate, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            EdgeUpdates.Add(skipFirstUpdate);
            Recover?.Invoke(this);
            return ValueTask.CompletedTask;
        }
    }
}
