using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Imaging;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static partial class MapWalkTimeoutChecks
{
    private static async Task SessionAsync(string python, string upstream, string artifacts)
    {
        string? previous = Environment.GetEnvironmentVariable("ALAS_TEST_MAP_FIXTURE");
        Environment.SetEnvironmentVariable("ALAS_TEST_MAP_FIXTURE", Path.Combine(artifacts, "in-map.png"));
        try
        {
            string executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Alas.Engine.Tests.exe" : "Alas.Engine.Tests");
            var runner = new TimeoutRunner(Path.Combine(upstream, "assets"));
            var queue = await new TaskQueue([runner]).RunAsync([new("success", runner.Kind), new("timeout", runner.Kind), new("next", runner.Kind)],
                new(executable, "offline-map", GameServer.Cn, Path.Combine(upstream, "assets"), python,
                    ApplicationPackage: "org.example.game", AllowActions: true),
                new(Path.Combine(artifacts, "reports"), ContinueOnFailure: true));
            Check(queue.Tasks is [{ Outcome: TaskOutcome.Succeeded }, { Outcome: TaskOutcome.Failed, Reason: "IOException" }, { Outcome: TaskOutcome.Succeeded }],
                "Actual session missed timeout recovery: " + string.Join(';', queue.Tasks.Select(task => task.Error)));
            Check(runner.Invalidated && queue.Tasks[1].FailureFrames is ["failure.png"], "Timeout failure left usable map or lost failure frame");
            for (int i = 0; i < queue.Tasks.Count; i++)
                Check(queue.Tasks[i].Evidence!["boundary"]!["actionAttempts"]!.GetValue<int>() == (i == 0 ? 2 : i == 1 ? 1 : 0),
                    "Session retry input count changed or leaked across tasks");
            var files = Directory.GetFiles(queue.Directory, "walk-timeouts.json", SearchOption.AllDirectories);
            Check(files.Length == 2 && Complete(), "Timeout evidence missing or leaked across tasks");
            string failedPath = "";
            foreach (string path in files)
            {
                var evidence = JsonSerializer.Deserialize<WalkTimeoutEvidence[]>(await File.ReadAllTextAsync(path), TaskQueue.Json)!;
                Check(evidence.Length == 1, "Session timeout list leaked previous-task records");
                if (!evidence[0].RetapCompleted) failedPath = path;
                else Check(evidence[0].RecoveredFrame > evidence[0].ObservedFrame && evidence[0].RetapFrame >= evidence[0].RecoveredFrame,
                    "Actual successful retap missing recovery frame");
            }
            Check(failedPath.Length > 0, "Session failure phase was not retained");
            string original = await File.ReadAllTextAsync(failedPath);
            var records = JsonSerializer.Deserialize<WalkTimeoutEvidence[]>(original, TaskQueue.Json)!;
            Check(records is [{ ObservedFrame: > 0, RecoveredFrame: null, RetapFrame: null, RetapCompleted: false }],
                "Recovery transport failure claimed a new frame or retap");
            try
            {
                File.Delete(failedPath); Check(!Complete(), "Missing walk timeout evidence accepted");
                foreach (string corrupt in new[] { "[]", "[null]", "{", JsonSerializer.Serialize(new[] { records[0] with { RetapCompleted = true } }, TaskQueue.Json) })
                { await File.WriteAllTextAsync(failedPath, corrupt); Check(!Complete(), "Corrupt timeout evidence accepted"); }
            }
            finally { await File.WriteAllTextAsync(failedPath, original); }
            bool Complete() => RunReport.Build(queue.Directory).ToJson()["evidence_complete"]!.GetValue<bool>();
        }
        finally { Environment.SetEnvironmentVariable("ALAS_TEST_MAP_FIXTURE", previous); }
    }

    private sealed class TimeoutRunner(string assets) : ITaskRunner
    {
        public string Kind => "timeout_session_probe";
        public bool RequiresActions => true;
        public bool Invalidated { get; private set; }
        public void Validate(JsonObject? input) { }
        public IReadOnlyList<string> Preconditions(TaskRequest request, TaskCapabilities capabilities) => [];
        public async ValueTask<TaskResult> RunAsync(TaskRequest request, TaskContext context, CancellationToken token)
        {
            if (request.Id != "next")
            {
                var session = (EngineSession)context.Campaign!;
                var state = new CampaignState(new MapDefinition("C3", "SP -- --\n-- -- --\n-- -- --", [], [], []));
                state.InitializeMapData(new()); state.Fleet1Location = new(1, 1);
                var config = new CampaignConfiguration { HasAmbush = false }; state.RefreshFleetPaths(config);
                var io = new TimeoutIo(session, request.Id == "timeout");
                var camera = new MapCamera(state, new(2, 2), await io.CaptureAsync(token), io, io,
                    new(io, new AssetFiles(assets), GameServer.Cn, new()), new(io), new(), gridInput: io);
                try
                {
                    var result = await session.CreateMapCombatMovement(camera, config).MoveAsync(new(3, 2), new(TimeSpan.Zero, TimeSpan.FromSeconds(2)), token);
                    Check(request.Id == "success" && result.Outcome == MapMoveOutcome.Committed && state.Fleet1Location == new Cell(3, 2) &&
                        state.BattleCount == 0 && result.Arrival.RetryTaps == 1 && !state.MovementInvalidated,
                        "Session factory did not retry and commit the confirmed move");
                }
                finally { if (request.Id == "timeout") Invalidated = state.MovementInvalidated; }
            }
            return new(request.Id, Kind, TaskOutcome.Succeeded, "next_task_probe");
        }
    }

    private sealed class TimeoutIo(EngineSession session, bool fail) : IMapViewSource, IMapSwipeInput, IMapGridInput, IMapSwipeEvidence, IImagePatchVision
    {
        private int _views;
        private int _taps;
        public async ValueTask<ScreenFrame> CaptureImageAsync(CancellationToken token)
        { await session.Driver.ScreenshotAsync(token); return session.Driver.Frame!; }
        public async ValueTask<MapViewFrame> CaptureAsync(CancellationToken token)
        {
            if (++_views > 1 && fail) throw new IOException("Synthetic walk timeout geometry capture failure");
            return new(await CaptureImageAsync(token), MapViewChecks.Regular(new(262, 227.5), new(Left: true, Upper: true)));
        }
        public async ValueTask TapAsync(PixelArea area, CancellationToken token)
        {
            await session.Driver.ClickAreaAsync(new(area.X, area.Y, area.X + area.Width, area.Y + area.Height), token);
            _taps++;
        }
        public ValueTask SwipeAsync(MapSwipeGesture gesture, CancellationToken token) => throw new InvalidOperationException("Recovery swiped without fresh geometry");
        public ValueTask<FleetMarker> FleetAsync(MapViewFrame view, VisibleGrid grid, CancellationToken token) => ValueTask.FromResult(new FleetMarker(false, false));
        public ValueTask<double?> SimilarityAsync(MapViewFrame before, VisibleGrid oldGrid, MapViewFrame after, VisibleGrid newGrid, CancellationToken token) => throw new InvalidOperationException();
        public ValueTask<ImagePatchObservation> MeasurePatchAsync(ScreenFrame frame, ImagePatchRequest request, CancellationToken token = default)
            => ValueTask.FromResult(new ImagePatchObservation(frame.Sequence,
                _views > 1 && _taps > 1 && request.Measure == PatchMeasure.Template && request.Color == new PixelColor(255, 255, 255) ? 1 : 0));
    }
}
