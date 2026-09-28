using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static class CampaignMapInitializerChecks
{
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    public static async Task RunAsync()
    {
        var map = new MapDefinition("C1", "SP -- ME", ["B1"], ["B1"],
            [new SpawnWave(0, Enemy: 1)], walls: [new MapEdge(new Cell(2, 1), new Cell(3, 1))]);
        var state = new CampaignState(map);
        var camera = new Camera(new MapObservation(
            [new(new(0, 0), new(IsFleet: true, IsCurrentFleet: true)),
             new(new(2, 0), new(IsEnemy: true, EnemyScale: 1))], new(2, 1), new(1, 0)));
        int factories = 0;
        var ready = await CampaignMapInitializer.InitializeAsync(state,
            new CampaignConfiguration { HasWall = true }, new(1, 1, 0, 1), (_, _) =>
            {
                factories++;
                return ValueTask.FromResult<IMapScanCamera>(camera);
            }, TimeSpan.FromSeconds(3));
        Check(factories == 1 && camera.Edges == 1 && camera.Scans == 1 &&
            ready.Fleet1 == new Cell(1, 1) && ready.Fleet2 is null &&
            state.Fleet1Location == ready.Fleet1 && state.FleetIndex == 1 &&
            state[new(3, 1)].IsEnemy && state[new(3, 1)].Cost == MapPathfinder.Unreachable &&
            !state.Paths.Connections[new(2, 1)].Contains(new Cell(3, 1)),
            "Initial map scan did not locate fleet, apply wall topology or preserve observed enemy");

        state = new CampaignState(map);
        camera = new Camera(new MapObservation(
            [new(new(0, 0), new(IsFleet: true))], new(2, 1), new(1, 0)));
        bool rejected = false;
        try
        {
            await CampaignMapInitializer.InitializeAsync(state, new(), new(1, 1, 0, 1),
                (_, _) => ValueTask.FromResult<IMapScanCamera>(camera), TimeSpan.FromSeconds(3));
        }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected && state.Fleet1Location is null && camera.Scans == 1,
            "A fleet without a current marker was guessed after the initial scan");

        map = new MapDefinition("C1", "SP SP ME", ["B1"], ["B1"], [new SpawnWave(0, Enemy: 1)]);
        state = new CampaignState(map);
        camera = new Camera(new MapObservation(
            [new(new(0, 0), new(IsFleet: true, IsCurrentFleet: true)),
             new(new(1, 0), new(IsFleet: true)),
             new(new(2, 0), new(IsEnemy: true, EnemyScale: 1))], new(2, 1), new(1, 0)));
        ready = await CampaignMapInitializer.InitializeAsync(state, new CampaignConfiguration { Fleet2 = 2 }, new(1, 1, 0, 1),
            (_, _) => ValueTask.FromResult<IMapScanCamera>(camera), TimeSpan.FromSeconds(3));
        Check(ready.Fleet1 == new Cell(1, 1) && ready.Fleet2 == new Cell(2, 1) &&
            state.Fleet2Location == ready.Fleet2 && state[new(2, 1)].Cost2 == 0,
            "Two observed fleets were not assigned separate cost fields");

        state = new CampaignState(map);
        ready = await CampaignMapInitializer.InitializeAsync(state,
            new CampaignConfiguration { Fleet2 = 2, FleetOrder = FleetOrder.Fleet1BossFleet2Mob }, new(2, 1, 1, 2),
            (_, _) => ValueTask.FromResult<IMapScanCamera>(camera), TimeSpan.FromSeconds(3));
        Check(ready.Fleet1 == new Cell(2, 1) && ready.Fleet2 == new Cell(1, 1) && state.FleetIndex == 2 &&
            state[new(1, 1)].Cost2 == 0 && state[new(1, 1)].Cost == 0 && state[new(2, 1)].Cost1 == 0,
            "Reversed fleet selection was relabeled as the first fleet or used another fleet's path origin");
        state = new CampaignState(map);
        rejected = false;
        try
        {
            await CampaignMapInitializer.InitializeAsync(state, new CampaignConfiguration { Fleet2 = 2 },
                new(2, 1, 0, 1), (_, _) => throw new InvalidOperationException("Camera must not start"), TimeSpan.FromSeconds(3));
        }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected && !state.IsMapInitialized, "Inconsistent displayed/logical fleet identity initialized the map");

        foreach (SwipePreset? preset in new SwipePreset?[] { null, new(0, 0), new(1, 0), new(-1, 2), new(0, -2) })
        {
            map = new MapDefinition("B1", "SP --", ["A1"], ["A1"], [new SpawnWave(0)], swipePreset: preset);
            state = new CampaignState(map);
            camera = new Camera(new MapObservation(
                [new(new(0, 0), new(IsFleet: true, IsCurrentFleet: true))], new(1, 1), new(0, 0)));
            factories = 0;
            ready = await CampaignMapInitializer.InitializeAsync(state, new(), new(1, 1, 0, 1), (_, _) =>
            {
                factories++;
                return ValueTask.FromResult<IMapScanCamera>(camera);
            }, TimeSpan.FromSeconds(3));
            ViewCell? expected = preset is { } value ? new(value.X, value.Y) : null;
            Check(factories == 1 && camera.Preset == expected && ready.Fleet1 == new Cell(1, 1) &&
                camera.Calls.SequenceEqual(["edges", "focus", "center", "observe"]),
                "Compiled map swipe preset was not passed to the camera edge scan");
        }

        IMapScanCamera basic = new BasicCamera();
        await basic.EnsureEdgesAsync(true, null, default);
        await Rejects<NotSupportedException>(() => basic.EnsureEdgesAsync(true, new ViewCell(1, 0), default).AsTask());
        Check(((BasicCamera)basic).Edges == 1, "Unsupported preset was silently discarded by the camera interface");

        map = new MapDefinition("B1", "SP --", ["A1"], ["A1"], [new SpawnWave(0)], swipePreset: new(1, 0));
        foreach (Exception failure in new Exception[] { new IOException("synthetic swipe failure"),
            new InvalidDataException("synthetic stale frame"), new TimeoutException("synthetic edge timeout"),
            new OperationCanceledException("synthetic cancellation") })
        {
            state = new CampaignState(map);
            camera = new Camera(new([], new(1, 1), default)) { EdgeFailure = failure };
            Exception? actual = null;
            try
            {
                await CampaignMapInitializer.InitializeAsync(state, new(), new(1, 1, 0, 1),
                    (_, _) => ValueTask.FromResult<IMapScanCamera>(camera), TimeSpan.FromSeconds(3));
            }
            catch (Exception error) { actual = error; }
            Check(ReferenceEquals(actual, failure) && camera.Scans == 0 && state.Fleet1Location is null &&
                camera.Calls.SequenceEqual(["edges"]), "Failed initial swipe continued fleet scanning or masked its failure");
        }

        Console.WriteLine("Campaign map initialization: scan, fleet localization, topology and swipe preset passed offline; no device entry or strategy handling.");
    }

    private static async Task Rejects<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private sealed class BasicCamera : IMapScanCamera
    {
        public Cell Position => new(1, 1);
        public int Edges { get; private set; }
        public ValueTask EnsureEdgesAsync(bool skipFirstUpdate, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Edges++; return ValueTask.CompletedTask; }
        public ValueTask FocusAsync(Cell destination, CancellationToken token) => throw new InvalidOperationException();
        public ValueTask CenterAsync(double tolerance, CancellationToken token) => throw new InvalidOperationException();
        public ValueTask<MapObservation> ObserveAsync(MapScanMode mode, CancellationToken token) => throw new InvalidOperationException();
    }

    private sealed class Camera(MapObservation observation) : IMapScanCamera
    {
        public Cell Position { get; private set; } = observation.Camera;
        public int Edges { get; private set; }
        public int Scans { get; private set; }
        public ViewCell? Preset { get; private set; }
        public List<string> Calls { get; } = [];
        public Exception? EdgeFailure { get; init; }
        public ValueTask FocusAsync(Cell destination, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls.Add("focus"); Position = destination; return ValueTask.CompletedTask; }
        public ValueTask CenterAsync(double tolerance, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls.Add("center"); return ValueTask.CompletedTask; }
        public ValueTask<MapObservation> ObserveAsync(MapScanMode mode, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls.Add("observe");
            Scans++;
            return ValueTask.FromResult(observation with { Camera = Position, Mode = mode });
        }
        public ValueTask EnsureEdgesAsync(bool skipFirstUpdate, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Edges++; return ValueTask.CompletedTask; }
        public ValueTask EnsureEdgesAsync(bool skipFirstUpdate, ViewCell? preset, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls.Add("edges"); Preset = preset; Edges++;
            if (EdgeFailure is not null) throw EdgeFailure;
            return ValueTask.CompletedTask;
        }
    }
}
