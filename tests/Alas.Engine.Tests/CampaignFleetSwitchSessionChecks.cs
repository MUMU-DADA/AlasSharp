using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Imaging;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static class CampaignFleetSwitchSessionChecks
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    public static async Task RunAsync(string python, string upstream, string artifacts)
    {
        string? previous = Environment.GetEnvironmentVariable("ALAS_TEST_ADB_FIXTURE");
        try
        {
            foreach (string mode in new[] { "normal", "reversed", "tap-failure", "camera-failure" })
            {
                bool reversed = mode == "reversed", failure = mode.EndsWith("failure", StringComparison.Ordinal);
                string folder = Path.Combine(artifacts, "switch-session-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                string fixture = Path.Combine(folder, "fixture.json");
                await File.WriteAllTextAsync(fixture, JsonSerializer.Serialize(new
                {
                    first = Path.Combine(artifacts, reversed ? "fleet-2.png" : "fleet-1.png"),
                    second = Path.Combine(artifacts, reversed ? "fleet-1.png" : "fleet-2.png"),
                    advance = true, failTap = mode == "tap-failure"
                }));
                Environment.SetEnvironmentVariable("ALAS_TEST_ADB_FIXTURE", fixture);
                string executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Alas.Engine.Tests.exe" : "Alas.Engine.Tests");
                var options = new EngineSessionOptions(executable, "offline-replay", GameServer.Cn,
                    Path.Combine(upstream, "assets"), python, "org.example.game",
                    ModelDirectory: Path.Combine(upstream, "bin/ocr_models"), AllowActions: true);
                var result = await new TaskQueue([new Probe(upstream, mode)]).RunAsync(
                    [new("switch", "fleet_switch_probe", TimeoutSeconds: 45),
                     new("next", "fleet_switch_probe", new() { ["skip"] = true }, TimeoutSeconds: 45)], options, new(folder));
                Check(result.Tasks[0].Outcome == (failure ? TaskOutcome.Failed : TaskOutcome.Succeeded),
                    "Actual session fleet switch result differs: " + JsonSerializer.Serialize(result.Tasks));
                var files = Directory.GetFiles(result.Directory, "fleet-switch.json", SearchOption.AllDirectories);
                Check(files.Length == 1 && RunReport.Build(result.Directory).ToJson()["evidence_complete"]!.GetValue<bool>(),
                    "Fleet switch evidence was lost, reused by next task, or rejected");
                var record = JsonSerializer.Deserialize<FleetSwitchEvidence[]>(await File.ReadAllTextAsync(files[0]), TaskQueue.Json)!.Single();
                Check(record.Ready == !failure && (record.Selection is not null) == (mode != "tap-failure") &&
                    (record.CameraFrame is not null) == !failure, "Partial physical switch evidence was not retained");
                string original = await File.ReadAllTextAsync(files[0]);
                File.Delete(files[0]);
                Check(!RunReport.Build(result.Directory).ToJson()["evidence_complete"]!.GetValue<bool>(), "Missing switch evidence was accepted");
                await File.WriteAllTextAsync(files[0], JsonSerializer.Serialize(new[] { record with { Ready = true, CameraFrame = null } }, TaskQueue.Json));
                Check(!RunReport.Build(result.Directory).ToJson()["evidence_complete"]!.GetValue<bool>(), "Unconfirmed switch was reported ready");
                await File.WriteAllTextAsync(files[0], original);
            }
        }
        finally { Environment.SetEnvironmentVariable("ALAS_TEST_ADB_FIXTURE", previous); }
        Console.WriteLine("Fleet switch session: four actual queue/session replays passed; real UI CV/HP/OCR, synthetic geometry/markers and ADB; partial evidence/report tamper checks passed.");
    }
    private sealed class Probe(string upstream, string mode) : ITaskRunner
    {
        public string Kind => "fleet_switch_probe";
        public bool RequiresActions => true;
        public void Validate(JsonObject? input) { }
        public IReadOnlyList<string> Preconditions(TaskRequest request, TaskCapabilities capabilities) => [];
        public async ValueTask<TaskResult> RunAsync(TaskRequest request, TaskContext context, CancellationToken token)
        {
            if (request.Input?["skip"]?.GetValue<bool>() == true)
                return new(request.Id, Kind, TaskOutcome.Succeeded, "next_task_without_switch");
            var session = context.Campaign as EngineSession ?? throw new InvalidOperationException("Expected actual EngineSession");
            await session.Driver.ScreenshotAsync(token);
            var state = new CampaignState(new MapDefinition("D1", "SP -- -- SP", [], [], []));
            state.InitializeMapData(new(PoorMapData: true));
            state.Fleet1Location = new(1, 1); state.Fleet2Location = new(4, 1);
            state[new(1, 1)].IsFleet = state[new(1, 1)].IsCurrentFleet = state[new(4, 1)].IsFleet = true;
            state.Health.Commit(1, 1, [.8, 0, 0, .7, 0, 0], new());
            var originalHealth = state.Health.Get(1);
            var source = new Source(session, mode == "camera-failure");
            var geometry = MapViewChecks.Regular(new(262, 227.5));
            var patches = new Markers();
            var recognition = new GridRecognition(patches, new AssetFiles(Path.Combine(upstream, "assets")), GameServer.Cn, new());
            var camera = new MapCamera(state, new(1, 1), new(session.Driver.Frame!, geometry), source, new NoSwipe(),
                recognition, new(new NoSwipe()), new() { Optimize = false });
            var configuration = new CampaignConfiguration { Fleet2 = 2,
                FleetOrder = mode == "reversed" ? FleetOrder.Fleet1BossFleet2Mob : FleetOrder.Fleet1MobFleet2Boss,
                Fleet1Formation = FleetFormation.Diamond, Fleet2Formation = FleetFormation.DoubleLine,
                WaitForFleetSwitchInfoBar = true, Levels = new(120) };
            var switcher = session.CreateFleetSwitcher(camera, configuration);
            try { await switcher.SwitchAsync(2, token); }
            catch
            {
                Check(state.FleetIndex == (mode == "camera-failure" ? 2 : 1), "Failed session rolled back observed identity");
                throw;
            }
            Check(state.FleetIndex == 2 && state[new(4, 1)].IsCurrentFleet &&
                state.Health.Get(2) is { FrameSequence: > 0 } && ReferenceEquals(originalHealth, state.Health.Get(1)) &&
                state.Levels.Evidence(configuration.Levels).Readings is [{ FleetIndex: 2, AfterBattle: false }] &&
                source.Captures >= 3 && patches.Calls > 3, "Session omitted switch recovery, HP, levels, or strategy refresh");
            return new(request.Id, Kind, TaskOutcome.Succeeded, "fleet_switch_observed");
        }
    }
    private sealed class Source(EngineSession session, bool fail) : IMapViewSource
    {
        public int Captures { get; private set; }
        public async ValueTask<MapViewFrame> CaptureAsync(CancellationToken token)
        {
            Captures++;
            if (fail) throw new IOException("Synthetic map geometry failure after fleet selection");
            await session.Driver.ScreenshotAsync(token);
            return new(session.Driver.Frame!, MapViewChecks.Regular(new(262, 227.5)));
        }
        public async ValueTask<ScreenFrame> CaptureImageAsync(CancellationToken token) => (await CaptureAsync(token)).Frame;
    }
    private sealed class Markers : IImagePatchVision
    {
        private long? _firstFrame;
        public int Calls { get; private set; }
        public ValueTask<ImagePatchObservation> MeasurePatchAsync(ScreenFrame frame, ImagePatchRequest request, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); Calls++; _firstFrame ??= frame.Sequence;
            // First fleet/current reading is a transient miss; subsequent images confirm the marker.
            return ValueTask.FromResult(new ImagePatchObservation(frame.Sequence, frame.Sequence == _firstFrame ? 0 :
                request.Measure == PatchMeasure.HsvCount ? 600 : 1));
        }
    }
    private sealed class NoSwipe : IMapSwipeInput, IMapSwipeEvidence
    {
        public ValueTask SwipeAsync(MapSwipeGesture gesture, CancellationToken token) => throw new InvalidOperationException("Unexpected swipe");
        public ValueTask<FleetMarker> FleetAsync(MapViewFrame view, VisibleGrid grid, CancellationToken token) => throw new InvalidOperationException("Old fleet prediction");
        public ValueTask<double?> SimilarityAsync(MapViewFrame before, VisibleGrid oldGrid, MapViewFrame after, VisibleGrid newGrid, CancellationToken token) => throw new InvalidOperationException("Old fleet prediction");
    }
}
