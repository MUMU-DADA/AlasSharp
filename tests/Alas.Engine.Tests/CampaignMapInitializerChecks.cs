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
            new CampaignConfiguration { HasWall = true }, (_, _) =>
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
            await CampaignMapInitializer.InitializeAsync(state, new(),
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
        ready = await CampaignMapInitializer.InitializeAsync(state, new CampaignConfiguration { Fleet2 = 2 },
            (_, _) => ValueTask.FromResult<IMapScanCamera>(camera), TimeSpan.FromSeconds(3));
        Check(ready.Fleet1 == new Cell(1, 1) && ready.Fleet2 == new Cell(2, 1) &&
            state.Fleet2Location == ready.Fleet2 && state[new(2, 1)].Cost2 == 0,
            "Two observed fleets were not assigned separate cost fields");

        map = new MapDefinition("B1", "SP --", ["A1"], [], [], swipePreset: new(1, 0));
        state = new CampaignState(map);
        factories = 0; rejected = false;
        try
        {
            await CampaignMapInitializer.InitializeAsync(state, new(), (_, _) =>
            {
                factories++;
                throw new InvalidOperationException("Camera must not start");
            }, TimeSpan.FromSeconds(3));
        }
        catch (NotSupportedException) { rejected = true; }
        Check(rejected && factories == 0 && !state.IsMapInitialized,
            "Unported initial swipe preset started an incomplete sortie initialization");
        Console.WriteLine("Campaign map initialization: scan, fleet localization and topology passed offline; no device entry or strategy handling.");
    }

    private sealed class Camera(MapObservation observation) : IMapScanCamera
    {
        public Cell Position { get; private set; } = observation.Camera;
        public int Edges { get; private set; }
        public int Scans { get; private set; }
        public ValueTask FocusAsync(Cell destination, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Position = destination; return ValueTask.CompletedTask; }
        public ValueTask CenterAsync(double tolerance, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
        public ValueTask<MapObservation> ObserveAsync(MapScanMode mode, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Scans++;
            return ValueTask.FromResult(observation with { Camera = Position, Mode = mode });
        }
        public ValueTask EnsureEdgesAsync(bool skipFirstUpdate, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Edges++; return ValueTask.CompletedTask; }
    }
}
