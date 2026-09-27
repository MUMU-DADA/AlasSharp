using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static partial class MapMechanismChecks
{
    private const string Tiles = "-- -- -- -- --\n-- -- -- -- --\n-- -- ++ -- --\n-- -- -- -- --\n-- -- -- -- --";
    private sealed record Sample(string Direction, string Start, bool Enabled, double Wait, string[]? Selection, string Tiles, int[] Weights);
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    public static async Task RunAsync(string python, string upstream, string artifacts)
    {
        var samples = new List<Sample>();
        foreach (string direction in new[] { "up", "down", "left", "right" })
        foreach (string start in new[] { "A1", "B3", "E5" })
        foreach (bool enabled in new[] { false, true })
        foreach (double wait in new[] { 0, 2, 3.25 })
        foreach (string[]? selection in new string[]?[] { null, [], ["C2", "D3"], ["B3"] })
            samples.Add(new(direction, start, enabled, wait, selection, Tiles, Enumerable.Range(0, 25).Select(i => 1 + i % 4).ToArray()));
        string inputs = Path.Combine(artifacts, "mechanism-input.json"), output = Path.Combine(artifacts, "mechanism-native.json");
        await File.WriteAllTextAsync(inputs, JsonSerializer.Serialize(samples, TaskJson));
        var result = await new ProcessRunner().RunAsync(python,
            [Path.Combine(AppContext.BaseDirectory, "native_mechanism_reference.py"), upstream, inputs, output], TimeSpan.FromMinutes(1));
        Check(result.ExitCode == 0, "Native mechanism oracle failed: " + result.Error);
        var reference = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
        foreach (var source in new[] { CampaignMapCombat.Source, CampaignState.InitializationSource, CellState.Source, MapPathfinder.Source })
            Check(reference["sources"]![source.Path]!.GetValue<string>() == source.Sha256, "Mechanism source drifted");
        for (int i = 0; i < samples.Count; i++)
        {
            var sample = samples[i]; var expected = reference["results"]![i]!;
            var state = State(sample.Direction, sample.Start);
            for (int j = 0; j < state.Cells.Count; j++) { state.Cells[j].Weight = sample.Weights[j]; state.Cells[j].MechanismWait = sample.Wait; }
            var config = new CampaignConfiguration { HasLandBased = sample.Enabled, HasAmbush = false, EmotionMode = CampaignEmotionMode.Ignore };
            state.RefreshFleetPaths(config);
            var camera = new Camera(state);
            var combat = Combat(state, config, camera);
            bool moved = false;
            try { Check(!await combat.ClearMechanismAsync(sample.Selection?.Select(Cell.Parse).ToArray()), "Mechanism counted as battle"); }
            catch (MapEnemyMovedException) { moved = true; }
            Check(moved == expected["moved"]!.GetValue<bool>() && camera.Frames == expected["frames"]!.GetValue<int>() &&
                camera.Taps.Select(c => c.ToString()).SequenceEqual(expected["taps"]!.AsArray().Select(n => n!.GetValue<string>())) &&
                state.Fleet1Location!.Value.ToString() == expected["fleet"]!.GetValue<string>() && state.BattleCount == 0,
                "Native mechanism action/confirmation differs: " + i);
            for (int j = 0; j < state.Cells.Count; j++)
            {
                var cell = expected["cells"]![j]!;
                Check(state.Cells[j].IsMechanismTrigger == cell["trigger"]!.GetValue<bool>() &&
                    state.Cells[j].IsMechanismBlock == cell["block"]!.GetValue<bool>() && state.Cells[j].Cost == cell["cost"]!.GetValue<int>(),
                    "Native group release or path costs differ: " + i);
            }
            Check(!await combat.ClearMechanismAsync(sample.Selection?.Select(Cell.Parse).ToArray()) &&
                state.MechanismReleases.Count == (moved ? 1 : 0), "Completed mechanism was repeated or unrecorded");
        }
        await FailureChecksAsync();
        await CampaignMapCombatChecks.MechanismExecutionChecksAsync();
        Console.WriteLine($"Mechanisms: {samples.Count} actual native selection/goto/timer/group/path traces; failure, occupied-trigger and campaign retries passed offline.");
    }
    private static readonly JsonSerializerOptions TaskJson = new(JsonSerializerDefaults.Web);
    private static CampaignState State(string direction = "right", string start = "A1")
    {
        var state = new CampaignState(new("E5", Tiles, ["A1"], [], [],
            mechanisms: new(landBased: [new(new(3, 3), Enum.Parse<MapDirection>(direction, true))])));
        state.InitializeMapData(new(LandBased: true));
        state.Fleet1Location = Cell.Parse(start);
        state[state.Fleet1Location.Value].IsFleet = state[state.Fleet1Location.Value].IsCurrentFleet = true;
        state.RefreshFleetPaths(new());
        return state;
    }
    private static MapMovement Movement(CampaignState state, CampaignConfiguration config, Camera camera)
        => new(state, config, camera, () => new MapArrivalCheck(camera, state, camera.InMapAsync, camera.Clock));
    private static CampaignMapCombat Combat(CampaignState state, CampaignConfiguration config, Camera camera)
        => new(state, config, Movement(state, config, camera), new MapScanner(state, camera, camera.Clock));
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance() => _ticks += TimeSpan.FromSeconds(.25).Ticks;
    }
    private sealed class Camera(CampaignState state) : IMapArrivalCamera, IMapScanCamera
    {
        public Clock Clock { get; } = new();
        public Cell Position { get; private set; } = state.Fleet1Location!.Value;
        public long FrameSequence { get; private set; } = 1;
        public List<Cell> Taps { get; } = [];
        public int Frames { get; private set; }
        public bool Invalidated { get; private set; }
        public string? Failure { get; init; }
        public Action? OnFrame { get; init; }
        public void Invalidate() => Invalidated = true;
        public void Suspend() { }
        public ValueTask PrepareTapAsync(Cell destination, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); if (Invalidated) throw new InvalidOperationException(); return ValueTask.CompletedTask; }
        public ValueTask TapCellAsync(Cell destination, CancellationToken token = default)
        { if (Failure == "click") throw new IOException("Synthetic tap failure"); Taps.Add(destination); return ValueTask.CompletedTask; }
        public ValueTask RefreshImageAsync(CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Clock.Advance(); Frames++; if (Failure != "stale") FrameSequence++; OnFrame?.Invoke(); return ValueTask.CompletedTask; }
        public ValueTask<FleetMarker> ReadFleetMarkerAsync(Cell destination, CancellationToken token = default)
            => ValueTask.FromResult(Failure == "timeout" ? new FleetMarker(false, false) : new FleetMarker(true, true));
        public ValueTask<FleetMarker> ReadCenterMarkerAsync(CancellationToken token = default) => ReadFleetMarkerAsync(Position, token);
        public ValueTask<bool> InMapAsync(CancellationToken token) => ValueTask.FromResult(Failure != "page");
        public ValueTask RelocalizeAsync(CancellationToken token = default) => RefreshImageAsync(token);
        public ValueTask AnchorAtAsync(Cell location, CancellationToken token = default) { Position = location; return ValueTask.CompletedTask; }
        public ValueTask FocusAsync(Cell destination, CancellationToken token) => AnchorAtAsync(destination, token);
        public ValueTask CenterAsync(double tolerance, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<MapObservation> ObserveAsync(MapScanMode mode, CancellationToken token)
            => ValueTask.FromResult(new MapObservation([], Position, new(0, 0), mode));
        public ValueTask EnsureEdgesAsync(bool skipFirstUpdate, CancellationToken token) => ValueTask.CompletedTask;
    }
}
