using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static partial class SubmarineChecks
{
    private sealed record Case(string Shape, string Tiles, string Camera, bool Enabled,
        string[] Observed, Dictionary<string, string> Covered, string? Positive);
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Rejects<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    public static async Task RunAsync(string python, string upstream, string artifacts)
    {
        Directory.CreateDirectory(artifacts);
        var cases = new List<Case>();
        string[] possible = ["B2", "F2", "C4", "F5"];
        foreach (string shape in new[] { "G6", "H7" })
        foreach (int count in Enumerable.Range(0, 5))
        foreach (bool enabled in new[] { false, true })
        foreach (string camera in new[] { "A1", "G6", "D3" })
        foreach (int observed in Enumerable.Range(0, Math.Min(count, 2) + 1))
        foreach (string cover in new[] { "none", "is_enemy", "is_fleet", "is_siren", "is_boss", "multiple", "irrelevant" })
        foreach (string? positive in possible.Take(count).Cast<string?>().Prepend(null))
        {
            var size = Cell.Parse(shape);
            var tiles = Enumerable.Repeat("--", size.Column * size.Row).ToArray();
            foreach (var cell in possible.Take(count).Select(Cell.Parse)) tiles[(cell.Row - 1) * size.Column + cell.Column - 1] = "__";
            // Center fallback must avoid land and use map center, even with a distant camera.
            tiles[((size.Row - 1) / 2) * size.Column + (size.Column - 1) / 2] = "++";
            var covered = new Dictionary<string, string>();
            if (count > 0 && cover != "none") covered["B3"] = cover is "multiple" ? "is_enemy" : cover is "irrelevant" ? "is_mystery" : cover;
            if (count > 1 && cover == "multiple") covered["F3"] = "is_fleet";
            cases.Add(new(shape, string.Join('\n', tiles.Chunk(size.Column).Select(row => string.Join(' ', row))), camera,
                enabled, possible.Take(observed).ToArray(), covered, positive));
        }
        string input = Path.Combine(artifacts, "submarine-input.json"), output = Path.Combine(artifacts, "submarine-reference.json");
        await File.WriteAllTextAsync(input, JsonSerializer.Serialize(cases, TaskQueue.Json));
        var oracle = await new ProcessRunner().RunAsync(python,
            [Path.Combine(AppContext.BaseDirectory, "native_submarine_reference.py"), upstream, input, output], TimeSpan.FromMinutes(2));
        Check(oracle.ExitCode == 0, "Native submarine oracle failed: " + oracle.Error);
        var reference = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
        foreach (var source in new[] { MapSubmarineLocator.Source, MapScanner.Source, MapScanner.SelectionSource })
            Check(reference["sources"]![source.Path]!.GetValue<string>() == source.Sha256 &&
                Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(upstream, source.Path)))) == source.Sha256,
                "Submarine reference source drifted: " + source.Path);
        int searches = 0, tiedCenterDifferences = 0;
        var methods = new HashSet<string>();
        for (int index = 0; index < cases.Count; index++)
        {
            var sample = cases[index]; var expected = reference["results"]![index]!;
            var state = State(sample);
            var camera = new Camera(Cell.Parse(sample.Camera), expected["trace"]!.AsArray());
            var actual = await MapSubmarineLocator.LocateAsync(state, sample.Enabled, camera, TimeSpan.FromSeconds(3));
            string? nativeLocation = expected["location"]?.GetValue<string>();
            if (actual.Method == "map_center_assumption")
            {
                // Native np.argsort does not promise an ordering among ties (and varies by SIMD implementation).
                // Check BOTH choices against the whole map's minimal distance, while retaining the native result.
                var center = new Cell((state.Map.Shape.Column - 1) / 2 + 1, (state.Map.Shape.Row - 1) / 2 + 1);
                var nonLand = state.Cells.Where(cell => !cell.IsLand).ToArray();
                int Distance(Cell cell) => Math.Abs(cell.Column - center.Column) + Math.Abs(cell.Row - center.Row);
                int minimum = nonLand.Min(cell => Distance(cell.Location));
                var tied = nonLand.Where(cell => Distance(cell.Location) == minimum).Select(cell => cell.Location).ToArray();
                Check(nativeLocation is not null && actual.Location == tied[0] && tied.Contains(Cell.Parse(nativeLocation)),
                    "Native or C# fallback is not one of the closest non-land cells to map center");
                if (actual.Location?.ToString() != nativeLocation) tiedCenterDifferences++;
            }
            else Check(actual.Location?.ToString() == nativeLocation,
                $"Submarine location differs from native at {index}: {actual.Location} / {nativeLocation}");
            Check(state.SubmarineLocation == actual.Location, "Submarine location was not committed");
            Check(camera.Calls == expected["trace"]!.AsArray().Count && actual.Observations.Count == camera.Calls,
                "Submarine search did not inspect every native candidate or failed to stop at its first match");
            Check(state.Cells.Where(cell => cell.IsSubmarine).Select(cell => cell.Location.ToString())
                .SequenceEqual(expected["observed"]!.AsArray().Select(cell => cell!.GetValue<string>())),
                "Submarine rule inference was recorded as an observed icon");
            Check(actual.Pending is null && !state.MovementInvalidated, "Successful localization retained pending or invalid state");
            searches += camera.Calls; methods.Add(actual.Method);
        }
        Check(methods.SetEquals(["disabled", "no_spawn", "initial_observation", "single_spawn", "covered_spawn",
            "searched_observation", "map_center_assumption"]), "Native submarine branch coverage is incomplete");
        await FailuresAsync(python, artifacts);
        await ReportsAsync(python, artifacts);
        var unverified = CampaignResumeTask.Describe("submarine", "campaign_run", RuleCatalog.Create("campaign_main/campaign_2_1"),
            new(CampaignLoopExit.Ended, 0, null, Submarine: new("searched_observation", new(2, 1),
                [new(new(2, 1), new(2, 2), 5, true)])), true);
        Check(unverified.Outcome == TaskOutcome.Failed && unverified.Evidence!["cleared"]!.GetValue<bool>() == false &&
            unverified.Evidence["submarine"]?["method"]?.GetValue<string>() == "searched_observation",
            "Submarine localization became a settlement or was dropped from the task report");
        await MapViewChecks.SubmarineCameraAsync(upstream, reference["sights"]!.AsArray());
        await CampaignMapInitializerChecks.RunAsync();
        Console.WriteLine($"Submarine localization: {cases.Count} native cases / {searches} camera inspections / {methods.Count} result sources; {tiedCenterDifferences} equal-distance center choices differ from unstable native argsort. Failure and session evidence checks passed offline.");
    }

    private static CampaignState State(Case sample)
    {
        var state = new CampaignState(new MapDefinition(sample.Shape, sample.Tiles, [], [], [new SpawnWave(0)]));
        state.InitializeMapData(new());
        foreach (var cell in sample.Observed) state[Cell.Parse(cell)].IsSubmarine = true;
        foreach (var (cell, flag) in sample.Covered)
        {
            var grid = state[Cell.Parse(cell)];
            switch (flag)
            {
                case "is_enemy": grid.IsEnemy = true; break;
                case "is_fleet": grid.IsFleet = true; break;
                case "is_siren": grid.IsSiren = true; break;
                case "is_boss": grid.IsBoss = true; break;
                case "is_mystery": grid.IsMystery = true; break;
                default: throw new InvalidOperationException("Invalid test observation");
            }
        }
        return state;
    }

    private static async Task FailuresAsync(string python, string artifacts)
    {
        var sample = new Case("C2", "__ -- __\n-- -- --", "B2", true, [], [], null);
        foreach (Exception failure in new Exception[] { new IOException("synthetic swipe failure"),
            new InvalidDataException("synthetic stale view"), new OperationCanceledException("synthetic cancellation") })
        {
            var state = State(sample); var camera = new Camera(new(2, 2), []) { Failure = failure };
            Exception? caught = null;
            try { await MapSubmarineLocator.LocateAsync(state, true, camera, TimeSpan.FromSeconds(3)); }
            catch (Exception error) { caught = error; }
            Check(ReferenceEquals(failure, caught) && state.SubmarineLocation is null && state.MovementInvalidated &&
                state.SubmarineEvidence is { Method: "failed", Pending: not null, Observations.Count: 0 },
                "Failed submarine search guessed a fallback or discarded the failure");
            await Rejects<InvalidOperationException>(() => MapSubmarineLocator.LocateAsync(state, true, camera, TimeSpan.FromSeconds(3)).AsTask());
        }
        var blocked = new Camera(new(2, 2), []) { Block = true };
        await Rejects<TimeoutException>(() => MapSubmarineLocator.LocateAsync(State(sample), true, blocked, TimeSpan.FromMilliseconds(50)).AsTask());
        using var cancel = new CancellationTokenSource(50);
        await Rejects<OperationCanceledException>(() => MapSubmarineLocator.LocateAsync(State(sample), true, blocked, TimeSpan.FromSeconds(3), cancel.Token).AsTask());
        foreach (int corruption in Enumerable.Range(1, 4))
        {
            var state = State(sample);
            var trace = new JsonArray(new JsonObject { ["location"] = "A1", ["camera"] = "B2", ["present"] = false },
                new JsonObject { ["location"] = "C1", ["camera"] = "B2", ["present"] = true });
            var camera = new Camera(new(2, 2), trace) { Corruption = corruption };
            await Rejects<InvalidDataException>(() => MapSubmarineLocator.LocateAsync(state, true, camera, TimeSpan.FromSeconds(3)).AsTask());
            Check(state.SubmarineLocation is null && state.SubmarineEvidence?.Method == "failed",
                "Invalid observation committed a submarine location");
        }

        // Actual session evidence saving must retain a partial localization failure and reset it at task boundaries.
        await using var session = new EngineSession(new("unused-adb", "offline", GameServer.Cn, ".", python));
        session.BeginTask(TimeSpan.FromSeconds(3));
        var execution = session.CreateInMapCampaignExecution(RuleCatalog.Create("campaign_main/campaign_2_1"), new());
        var saved = execution.Context.State;
        saved.InitializeMapData(new());
        foreach (var cell in saved.Cells) cell.IsSubmarineSpawnPoint = false;
        saved[new(1, 1)].IsSubmarineSpawnPoint = saved[new(2, 1)].IsSubmarineSpawnPoint = true;
        var io = new Camera(new(1, 1), []) { Failure = new IOException("synthetic failure") };
        await Rejects<IOException>(() => MapSubmarineLocator.LocateAsync(saved, true, io, TimeSpan.FromSeconds(3)).AsTask());
        string directory = Path.Combine(artifacts, "partial-evidence");
        var evidence = await session.SaveEvidenceAsync(directory, true);
        Check(evidence.SubmarineFile == "submarine-location.json" &&
            JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory, evidence.SubmarineFile)))!["method"]!.GetValue<string>() == "failed",
            "Session lost partial submarine evidence");
        session.BeginTask(TimeSpan.FromSeconds(3));
        Check((await session.SaveEvidenceAsync(Path.Combine(artifacts, "next-task"), false)).SubmarineFile is null,
            "Previous task submarine evidence leaked into the next task");
    }

    private sealed class Camera(Cell position, JsonArray trace) : IMapScanCamera
    {
        public Cell Position { get; private set; } = position;
        public int Calls { get; private set; }
        public Exception? Failure { get; init; }
        public bool Block { get; init; }
        public int Corruption { get; init; }
        public async ValueTask<SubmarineObservation> InspectSubmarineAsync(Cell destination, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Failure is not null) throw Failure;
            if (Block) await Task.Delay(Timeout.Infinite, token);
            Check(Calls < trace.Count, "Unexpected submarine camera inspection");
            var expected = trace[Calls++]!;
            Check(destination.ToString() == expected["location"]!.GetValue<string>(), "Submarine inspection order differs from native");
            Position = Cell.Parse(expected["camera"]!.GetValue<string>());
            return new(Corruption == 1 ? new(99, 99) : destination,
                Corruption == 2 ? new(99, 99) : Position, Corruption == 3 ? 0 : Corruption == 4 ? 10 - Calls : Calls,
                expected["present"]!.GetValue<bool>());
        }
        public ValueTask FocusAsync(Cell destination, CancellationToken token) => throw new InvalidOperationException();
        public ValueTask CenterAsync(double tolerance, CancellationToken token) => throw new InvalidOperationException();
        public ValueTask<MapObservation> ObserveAsync(MapScanMode mode, CancellationToken token) => throw new InvalidOperationException();
        public ValueTask EnsureEdgesAsync(bool skipFirstUpdate, CancellationToken token) => throw new InvalidOperationException();
    }
}
