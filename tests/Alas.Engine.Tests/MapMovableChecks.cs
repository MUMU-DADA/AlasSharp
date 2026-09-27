using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static partial class MapMovableChecks
{
    private const string Tiles = "ME MS -- MS ME\n-- ++ -- -- --\n-- -- -- -- --\n-- -- -- -- --\n-- -- -- -- --";
    private static readonly SpawnWave[] Waves = [new(0, Enemy: 2, Siren: 2), new(8, Enemy: 1, Siren: 1), new(17, Siren: 2)];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record MatchCase(string[] Before, string[] Spawn, string[] After, string[] Fleets, int Step);
    private sealed record RoundCase(bool Siren, bool Normal, bool Maze, bool Bounce, int[] Turns, int[] NormalTurns, double Wait);
    private sealed record TrackCase(bool Siren, bool Normal, bool Walls, bool Portal, bool Templates, int Step,
        bool TrackSiren, bool Cleared, int Battle, int SirenCount, string[] Before, string[] AfterSirens, string[] AfterEnemies);
    private sealed record ScanCase(bool Siren, bool Normal, bool Cleared);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static Cell C(string cell) => Cell.Parse(cell);
    private static CampaignState State(bool walls = false, bool portal = false, bool bouncing = false)
    {
        var state = new CampaignState(new("E5", Tiles, ["C3", "C4"], [], Waves,
            walls: [new(C("C2"), C("D2"))], portals: [new(C("A3"), C("E3"))], covered: ["B3"],
            mechanisms: new(bouncingRoutes: [[C("D5"), C("E5")]])));
        state.InitializeMapData(new(Walls: walls, Portals: portal, BouncingEnemy: bouncing));
        state.Fleet1Location = C("C4"); state.Fleet2Location = C("E5");
        state[C("C4")].IsFleet = state[C("E5")].IsFleet = true;
        return state;
    }
    public static async Task RunAsync(string python, string upstream, string artifacts)
    {
        var random = new Random(1947);
        string[] RandomCells(int max) => Enumerable.Range(0, random.Next(max + 1)).Select(_ => new Cell(random.Next(1, 5), random.Next(1, 4)).ToString()).ToArray();
        var matches = Enumerable.Range(0, 450).Select(_ => new MatchCase(RandomCells(3), RandomCells(2), RandomCells(3), RandomCells(1), random.Next(0, 5))).ToList();
        matches.Add(new(["A1", "A1"], ["A1"], ["A1", "A1"], ["A1"], 2));
        var rounds = new List<RoundCase>();
        for (int flags = 0; flags < 16; flags++)
        foreach (int[] turns in new int[][] { [], [1], [2], [2, 3, 2] })
        foreach (double wait in new[] { 0, 1.5, 2.25 })
            rounds.Add(new((flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, (flags & 8) != 0, turns, [1, 3], wait));
        var tracks = new List<TrackCase>();
        foreach (bool siren in new[] { false, true })
        foreach (bool normals in new[] { false, true })
        foreach (bool walls in new[] { false, true })
        foreach (bool portal in new[] { false, true })
        foreach (bool templates in new[] { false, true })
        foreach (bool cleared in new[] { false, true })
        foreach (string? observed in new string?[] { null, "A3", "E2", "C2" })
            tracks.Add(new(true, normals, walls, portal, templates, 2, siren, cleared, cleared ? 1 : 0, cleared && siren ? 1 : 0,
                ["B3"], siren && observed is not null ? [observed] : [], !siren && observed is not null ? [observed] : []));
        string inputs = Path.Combine(artifacts, "movable-input.json"), output = Path.Combine(artifacts, "movable-native.json");
        var scans = new List<ScanCase>();
        foreach (var (siren, normal) in new[] { (true, false), (false, true), (true, true) })
        foreach (bool cleared in new[] { false, true }) scans.Add(new(siren, normal, cleared));
        await File.WriteAllTextAsync(inputs, JsonSerializer.Serialize(new { tiles = Tiles, waves = Waves, matches, rounds, tracks, scans }, Json));
        var process = await new ProcessRunner().RunAsync(python,
            [Path.Combine(AppContext.BaseDirectory, "native_movable_reference.py"), upstream, inputs, output], TimeSpan.FromMinutes(2));
        Check(process.ExitCode == 0, "Native movable oracle failed: " + process.Error);
        var native = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
        foreach (var source in new[] { MapRounds.Source, MovableEnemyMatcher.Source, MapPathfinder.Source, CellState.Source })
            Check(native["sources"]![source.Path]!.GetValue<string>() == source.Sha256, "Native movable source drifted");
        for (int i = 0; i < matches.Count; i++)
        {
            var c = matches[i]; var expected = native["matches"]![i]!;
            var actual = MovableEnemyMatcher.Match(c.Before.Select(C).ToArray(), c.Spawn.Select(C).ToArray(), c.After.Select(C).ToArray(), c.Fleets.Select(C).ToArray(), c.Step);
            Check(actual.Score == expected["score"]!.GetValue<long>() && actual.ColumnSum == expected["columnSum"]!.GetValue<long>(), "Matching objective differs: " + i);
            Check(actual.Before.Length == actual.After.Length && actual.Before.All(c.Before.Concat(c.Spawn).Select(C).Contains), "Invalid matching identities");
        }
        for (int i = 0; i < rounds.Count; i++)
        {
            var c = rounds[i]; var state = State(bouncing: c.Bounce);
            state[C("D4")].IsMaze = true; state[C("D4")].MazeRound = [3, 4, 5];
            var config = new CampaignConfiguration { HasMovableEnemy = c.Siren, HasMovableNormalEnemy = c.Normal,
                HasMaze = c.Maze, HasBouncingEnemy = c.Bounce, MovableEnemyTurns = [.. c.Turns], MovableNormalEnemyTurns = [.. c.NormalTurns], SirenMoveWait = c.Wait };
            state[C("B3")].IsSiren = true; state[C("A1")].IsEnemy = true;
            state.Rounds.Initialize(config);
            for (int j = 0; j < 12; j++)
            {
                if (j > 0)
                {
                    state[C("B3")].IsSiren = j % 4 != 0; state[C("A1")].IsEnemy = j % 5 != 0; state.BattleCount = j / 3;
                    if (j % 3 == 0) state.Rounds.RecordBattle();
                }
                var e = native["rounds"]![i]![j]!;
                Check(state.Rounds.Round == e["round"]!.GetValue<int>() && state.Rounds.WaitSeconds == e["wait"]!.GetValue<double>() &&
                    state.Rounds.EnemyMoved == e["moved"]!.GetValue<bool>() && state.Rounds.MazeChanged == e["maze"]!.GetValue<bool>() &&
                    state.Rounds.MazeActive(C("D4")) == e["active"]!.GetValue<bool>() &&
                    JsonNode.DeepEquals(JsonSerializer.SerializeToNode(state.Rounds.EnemyRounds), e["enemies"]), "Round state differs: " + i + "/" + j);
                state.Rounds.Advance();
            }
        }
        for (int i = 0; i < tracks.Count; i++)
        {
            var c = tracks[i]; var state = State(c.Walls, c.Portal);
            state.BattleCount = c.Battle; state.SirenCount = c.SirenCount;
            foreach (var cell in c.AfterSirens) state[C(cell)].IsSiren = true;
            foreach (var cell in c.AfterEnemies) state[C(cell)].IsEnemy = true;
            var config = new CampaignConfiguration { HasMovableEnemy = true, HasMovableNormalEnemy = c.Normal, HasWall = c.Walls,
                HasPortal = c.Portal, HasAmbush = false, MovableEnemyStep = c.Step };
            state.RefreshFleetPaths(config);
            var costs = state.Cells.Select(g => (g.Cost, g.Cost1, g.Cost2, g.Connection)).ToArray();
            var topology = state.Paths.Connections;
            var camera = new Camera(state);
            new MapMovableScan(state, config, new(state, camera), c.Templates).Track(c.Before.Select(C).ToArray(), c.Cleared, c.TrackSiren);
            Check(ReferenceEquals(topology, state.Paths.Connections) && costs.SequenceEqual(state.Cells.Select(g => (g.Cost, g.Cost1, g.Cost2, g.Connection))), "Tracking modified authoritative fleet paths");
            for (int k = 0; k < state.Cells.Count; k++)
            {
                var g = state.Cells[k]; var e = native["tracks"]![i]![k]!;
                Check(g.IsEnemy == e["enemy"]!.GetValue<bool>() && g.IsSiren == e["siren"]!.GetValue<bool>() && g.IsMovable == e["movable"]!.GetValue<bool>(), "Tracking state differs: " + i + "/" + g.Location);
            }
        }
        for (int i = 0; i < scans.Count; i++)
        {
            var c = scans[i]; var state = State();
            state[C("B3")].IsSiren = true; state[C("A1")].IsEnemy = true;
            var config = new CampaignConfiguration { HasMovableEnemy = c.Siren, HasMovableNormalEnemy = c.Normal, HasAmbush = false };
            state.Rounds.Initialize(config);
            state.RefreshFleetPaths(config);
            var camera = new Camera(state) { ObservedSirens = [C("A3")], ObservedEnemies = [C("B1")] };
            var scan = await new MapMovableScan(state, config, new(state, camera, camera.Clock)).ScanAsync(MovableEnemySnapshot.Capture(state), c.Cleared);
            var e = native["scans"]![i]!;
            Check(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(camera.BeforeScan!.Sirens.Select(c => c.ToString())), e["calls"]![0]!["sirens"]) &&
                JsonNode.DeepEquals(JsonSerializer.SerializeToNode(camera.BeforeScan.Enemies.Select(c => c.ToString())), e["calls"]![0]!["enemies"]),
                "Native full_scan_movable erased the wrong enemy kinds: " + i);
            var expectedCameras = c.Normal || c.Cleared ? state.Map.Cameras.ToHashSet() : new HashSet<Cell>();
            if (!c.Normal) expectedCameras.Add(C("B3"));
            Check(expectedCameras.SetEquals(scan.Scan.Visited), "Native full_scan_movable camera queue/must-scan differs: " + i);
            for (int k = 0; k < state.Cells.Count; k++)
            {
                var g = state.Cells[k]; var cell = e["cells"]![k]!;
                Check(g.IsEnemy == cell["enemy"]!.GetValue<bool>() && g.IsSiren == cell["siren"]!.GetValue<bool>() &&
                    g.IsMovable == cell["movable"]!.GetValue<bool>(), "Full scan tracker order/state differs: " + i + "/" + g.Location);
            }
        }
        var dense = Enumerable.Range(0, 150).Select(i => new Cell(i % 15 + 1, i / 15 + 1)).ToArray();
        var clock = Stopwatch.StartNew();
        var large = MovableEnemyMatcher.Match(dense, [], dense, [], 30);
        clock.Stop();
        Check(large.Before.Length == dense.Length && large.Score == 4500 && clock.Elapsed < TimeSpan.FromSeconds(10), "Dense matching failed or regressed to enumeration");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { MovableEnemyMatcher.Match(dense, dense, dense, dense, 30, cancelled.Token); throw new InvalidOperationException("Cancelled matching ran"); }
        catch (OperationCanceledException) { }
        await MovementChecksAsync();
        await CampaignMapCombatChecks.MovableExecutionChecksAsync();
        Console.WriteLine($"Movable maps: {matches.Count} native match objectives, {rounds.Count * 12} round states, {tracks.Count} tracking traces, {scans.Count} scan compositions; dense matching {clock.ElapsedMilliseconds} ms and movement/campaign/failure checks passed offline.");
    }
}
