using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static partial class MapMazeChecks
{
    private sealed record Sample(int Round, int Step, string Start, string? Second, bool CurrentOnly);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static Cell C(string value) => Cell.Parse(value);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static CampaignState State(string start = "A1", string? second = null)
    {
        var state = new CampaignState(new("G1", "-- -- -- -- -- -- --", ["D1"], [], [new(0)],
            mechanisms: new(mazes: [[C("D1")], [C("A1")], [C("G1")]])));
        state.InitializeMapData(new(Maze: true));
        state.Fleet1Location = C(start); state.Fleet2Location = second is null ? null : C(second);
        state[C(start)].IsFleet = state[C(start)].IsCurrentFleet = true;
        if (second is not null) state[C(second)].IsFleet = true;
        return state;
    }
    private static MapMovement Movement(CampaignState state, CampaignConfiguration config, Camera camera)
        => new(state, config, camera, () => new(camera, state, _ => ValueTask.FromResult(camera.Failure != "page"), camera.Clock),
            movableScan: new(state, config, new(state, camera, camera.Clock)));
    public static async Task RunAsync(string python, string upstream, string artifacts)
    {
        var samples = new List<Sample>();
        for (int round = 0; round < 9; round++)
        foreach (int step in new[] { 0, 2, 3 })
        foreach (string start in new[] { "A1", "B1", "F1", "G1" })
        foreach (string? second in new string?[] { null, "C1" })
        foreach (bool current in new[] { false, true }) samples.Add(new(round, step, start, second, current));
        string inputs = Path.Combine(artifacts, "maze-input.json"), output = Path.Combine(artifacts, "maze-native.json");
        await File.WriteAllTextAsync(inputs, JsonSerializer.Serialize(samples, Json));
        var result = await new ProcessRunner().RunAsync(python,
            [Path.Combine(AppContext.BaseDirectory, "native_maze_reference.py"), upstream, inputs, output], TimeSpan.FromMinutes(2));
        Check(result.ExitCode == 0, "Native maze oracle failed: " + result.Error);
        var native = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
        foreach (var source in new[] { MapRounds.Source, MapPathfinder.Source, MapScanner.SelectionSource })
            Check(native["sources"]![source.Path]!.GetValue<string>() == source.Sha256, "Maze source drifted");
        for (int i = 0; i < samples.Count; i++)
        {
            var c = samples[i]; var e = native["results"]![i]!; var state = State(c.Start, c.Second);
            var config = new CampaignConfiguration { HasMaze = true, HasAmbush = false, HasFleetStep = c.Step != 0,
                Fleet1Step = c.Step == 0 ? 3 : c.Step, Fleet2 = c.Second is null ? 0 : 2, WalkUseCurrentFleet = c.CurrentOnly };
            state.Rounds.Initialize(config); for (int r = 0; r < c.Round; r++) state.Rounds.Advance();
            state.RefreshFleetPaths(config);
            var camera = new Camera { CurrentOnly = c.CurrentOnly }; var movement = Movement(state, config, camera);
            bool changed = false;
            try
            {
                var path = state.Paths.FindRoute(C("D1"), FleetRoles.Step(1, config));
                foreach (var waypoint in path.Waypoints)
                {
                    Check(await movement.WaitForMazeAsync(waypoint) is null, "Maze wait did not finish or redispatch");
                    Check((await movement.MoveAsync(waypoint)).Outcome == MapMoveOutcome.Committed, "Maze waypoint was not confirmed");
                }
            }
            catch (MapEnemyMovedException) { changed = true; }
            Check(changed == e["moved"]!.GetValue<bool>() && state.Rounds.Round == e["round"]!.GetValue<int>() &&
                state.Fleet1Location!.Value.ToString() == e["fleet"]!.GetValue<string>() && camera.Frames == e["frames"]!.GetValue<int>() &&
                camera.Taps.Select(c => c.ToString()).SequenceEqual(e["taps"]!.AsArray().Select(n => n!.GetValue<string>())) &&
                state.Cells.Select(g => g.Cost).SequenceEqual(e["costs"]!.AsArray().Select(n => n!.GetValue<int>())),
                "Native maze goto/timer/fleet/cost differs: " + i);
            Check(state.MazeWaits.Count == e["waits"]!.AsArray().Count && !state.MovementInvalidated && !camera.Invalidated && state.BattleCount == 0,
                "Maze phase was treated as a failed camera or battle: " + i);
            for (int j = 0; j < state.MazeWaits.Count; j++)
            {
                var actual = state.MazeWaits[j]; var expected = e["waits"]![j]!;
                Check(actual.From.ToString() == expected["origin"]!.GetValue<string>() && actual.To.ToString() == expected["target"]!.GetValue<string>() &&
                    actual.RoundBefore == expected["before"]!.GetValue<int>() && actual.RoundAfter == expected["after"]!.GetValue<int>() &&
                    actual.ConfirmSeconds == (actual.RoundAfter % 3 == 0 ? 1.5 : .5) && actual.ArrivalSequence > 1,
                    "Maze wait evidence differs: " + i);
            }
        }
        await FailureChecksAsync();
        await CampaignMapCombatChecks.MazeExecutionChecksAsync();
        Console.WriteLine($"Maze: {samples.Count} actual native goto/_goto phase, neighbor, step, timer and fleet traces; failures and campaign redispatch passed offline.");
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance() => _ticks += TimeSpan.FromSeconds(.25).Ticks;
    }
    private sealed class Camera : IMapArrivalCamera, IMapScanCamera
    {
        public Clock Clock { get; } = new();
        public Cell Position { get; private set; } = C("A1");
        public long FrameSequence { get; private set; } = 1;
        public int Frames { get; private set; }
        public List<Cell> Taps { get; } = [];
        public bool CurrentOnly { get; init; }
        public bool Invalidated { get; private set; }
        public string? Failure { get; init; }
        public CancellationTokenSource? Cancellation { get; init; }
        public void Invalidate() => Invalidated = true;
        public void Suspend() { }
        public ValueTask PrepareTapAsync(Cell destination, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); if (Invalidated) throw new InvalidOperationException(); return ValueTask.CompletedTask; }
        public ValueTask TapCellAsync(Cell destination, CancellationToken token = default)
        { if (Failure == "click") throw new IOException("Synthetic maze tap failure"); Taps.Add(destination); return ValueTask.CompletedTask; }
        public ValueTask RefreshImageAsync(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); Clock.Advance(); Frames++; if (Failure != "stale") FrameSequence++;
            if (Failure == "cancel") Cancellation!.Cancel();
            return ValueTask.CompletedTask;
        }
        public ValueTask<FleetMarker> ReadFleetMarkerAsync(Cell destination, CancellationToken token = default)
            => ValueTask.FromResult(Failure == "timeout" ? new FleetMarker(false, false) : new FleetMarker(!CurrentOnly, true));
        public ValueTask<FleetMarker> ReadCenterMarkerAsync(CancellationToken token = default) => ReadFleetMarkerAsync(Position, token);
        public ValueTask RelocalizeAsync(CancellationToken token = default) => RefreshImageAsync(token);
        public ValueTask AnchorAtAsync(Cell location, CancellationToken token = default) { Position = location; return ValueTask.CompletedTask; }
        public ValueTask FocusAsync(Cell destination, CancellationToken token) => AnchorAtAsync(destination, token);
        public ValueTask CenterAsync(double tolerance, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<MapObservation> ObserveAsync(MapScanMode mode, CancellationToken token)
            => ValueTask.FromResult(new MapObservation([], Position, new(0, 0), mode));
        public ValueTask EnsureEdgesAsync(bool skipFirstUpdate, CancellationToken token) => ValueTask.CompletedTask;
    }
}
