using System.Text.Json.Nodes;
using Alas.Engine.Imaging;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static class FleetOverlapSessionChecks
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    internal static async Task RunAsync(string python, string upstream, string artifacts)
    {
        string? previous = Environment.GetEnvironmentVariable("ALAS_TEST_MAP_FIXTURE");
        Environment.SetEnvironmentVariable("ALAS_TEST_MAP_FIXTURE", Path.Combine(artifacts, "in-map.png"));
        try
        {
            string exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Alas.Engine.Tests.exe" : "Alas.Engine.Tests");
            var runner = new Runner(Path.Combine(upstream, "assets"));
            var queue = await new TaskQueue([runner]).RunAsync([new("success", runner.Kind), new("failure", runner.Kind), new("next", runner.Kind)],
                new(exe, "offline-map", GameServer.Cn, Path.Combine(upstream, "assets"), python,
                    ApplicationPackage: "org.example.game", AllowActions: true),
                new(Path.Combine(artifacts, "reports"), ContinueOnFailure: true));
            Check(queue.Tasks is [{ Outcome: TaskOutcome.Succeeded }, { Outcome: TaskOutcome.Failed, Reason: "IOException" }, { Outcome: TaskOutcome.Succeeded }],
                "Actual overlap session failed: " + string.Join(';', queue.Tasks.Select(task => task.Error)));
            Check(runner.FailureUncommitted && queue.Tasks[1].FailureFrames is ["failure.png"] &&
                RunReport.Build(queue.Directory).ToJson()["evidence_complete"]!.GetValue<bool>(), "Overlap failure lost fleet state or artifacts");
            int[] actions = [2, 1, 0];
            for (int i = 0; i < queue.Tasks.Count; i++)
                Check(queue.Tasks[i].Evidence!["boundary"]!["actionAttempts"]!.GetValue<int>() == actions[i],
                    "Shared-fleet movement duplicated an input or leaked actions across tasks");
        }
        finally { Environment.SetEnvironmentVariable("ALAS_TEST_MAP_FIXTURE", previous); }
    }
    private sealed class Runner(string assets) : ITaskRunner
    {
        public string Kind => "fleet_overlap_session_probe";
        public bool RequiresActions => true;
        public bool FailureUncommitted { get; private set; }
        public void Validate(JsonObject? input) { }
        public IReadOnlyList<string> Preconditions(TaskRequest request, TaskCapabilities capabilities) => [];
        public async ValueTask<TaskResult> RunAsync(TaskRequest request, TaskContext context, CancellationToken token)
        {
            if (request.Id != "next")
            {
                var session = (EngineSession)context.Campaign!;
                var state = new CampaignState(new MapDefinition("C3", "SP -- --\n-- -- SP\n-- -- --", [], [], []));
                state.InitializeMapData(new()); state.Fleet1Location = new(1, 1); state.Fleet2Location = new(3, 2);
                var config = new CampaignConfiguration { HasAmbush = false, Fleet2 = 2 }; state.RefreshFleetPaths(config);
                var io = new Io(session, request.Id == "failure");
                var camera = new MapCamera(state, new(2, 2), await io.CaptureAsync(token), io, io,
                    new(io, new AssetFiles(assets), GameServer.Cn, new()), new(io), new(), gridInput: io);
                try
                {
                    var joined = await session.CreateMapMovement(camera, config).MoveAsync(new(3, 2), token: token);
                    Check(joined.Outcome == MapMoveOutcome.Committed && state.Fleet1Location == state.Fleet2Location && state.Cells.Count(g => g.IsFleet) == 1,
                        "Engine factory rejected or miscommitted a known shared destination");
                    var left = await session.CreateMapCombatMovement(camera, config).MoveAsync(new(1, 2), token: token);
                    Check(left.Outcome == MapMoveOutcome.Committed && state.Fleet1Location == new Cell(1, 2) && state.Fleet2Location == new Cell(3, 2) &&
                        state[new(3, 2)].IsFleet && !state[new(3, 2)].IsCurrentFleet && state[new(1, 2)].IsCurrentFleet && state.BattleCount == 0,
                        "Engine departure lost the stationary fleet or invented combat");
                }
                finally
                {
                    if (request.Id == "failure") FailureUncommitted = state.MovementInvalidated &&
                        state.Fleet1Location == new Cell(1, 1) && state.Fleet2Location == new Cell(3, 2) &&
                        state[new(1, 1)].IsFleet && state[new(3, 2)].IsFleet;
                }
            }
            return new(request.Id, Kind, TaskOutcome.Succeeded, "synthetic_overlap_checked");
        }
    }
    private sealed class Io(EngineSession session, bool fail) : IMapViewSource, IMapSwipeInput, IMapGridInput, IMapSwipeEvidence, IImagePatchVision
    {
        private int _captures;
        public async ValueTask<ScreenFrame> CaptureImageAsync(CancellationToken token)
        {
            if (++_captures > 1 && fail) throw new IOException("Synthetic shared-fleet movement capture failure");
            await session.Driver.ScreenshotAsync(token); return session.Driver.Frame!;
        }
        public async ValueTask<MapViewFrame> CaptureAsync(CancellationToken token)
            => new(await CaptureImageAsync(token), MapViewChecks.Regular(new(262, 227.5), new(Left: true, Upper: true)));
        public ValueTask TapAsync(PixelArea area, CancellationToken token)
            => session.Driver.ClickAreaAsync(new(area.X, area.Y, area.X + area.Width, area.Y + area.Height), token);
        public ValueTask SwipeAsync(MapSwipeGesture gesture, CancellationToken token) => throw new InvalidOperationException("Unexpected overlap swipe");
        public ValueTask<FleetMarker> FleetAsync(MapViewFrame view, VisibleGrid grid, CancellationToken token) => ValueTask.FromResult(new FleetMarker(true, true));
        public ValueTask<double?> SimilarityAsync(MapViewFrame before, VisibleGrid oldGrid, MapViewFrame after, VisibleGrid newGrid, CancellationToken token) => throw new InvalidOperationException();
        public ValueTask<ImagePatchObservation> MeasurePatchAsync(ScreenFrame frame, ImagePatchRequest request, CancellationToken token = default)
            => ValueTask.FromResult(new ImagePatchObservation(frame.Sequence, request.Measure == PatchMeasure.Template &&
                request.Color == new PixelColor(255, 255, 255) ? 1 : 0));
    }
}
