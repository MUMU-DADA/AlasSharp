using System.Text.Json.Nodes;
using Alas.Engine.Imaging;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static partial class MapWalkPopupChecks
{
    private static async Task SessionChecksAsync(string python, string upstream, string artifacts)
    {
        string? previous = Environment.GetEnvironmentVariable("ALAS_TEST_MAP_FIXTURE");
        // Native mirror threshold crosses only in non-clear mode.
        Environment.SetEnvironmentVariable("ALAS_TEST_MAP_FIXTURE", Path.Combine(artifacts, "popup-cn-False-True-201.png"));
        try
        {
            string executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Alas.Engine.Tests.exe" : "Alas.Engine.Tests");
            var runner = new PopupRunner(Path.Combine(upstream, "assets"));
            var result = await new TaskQueue([runner]).RunAsync(
                [new("first", runner.Kind), new("second", runner.Kind), new("clear", runner.Kind), new("next", runner.Kind)],
                new(executable, "offline-map", GameServer.Cn, Path.Combine(upstream, "assets"), python,
                    ApplicationPackage: "org.example.game", AllowActions: true),
                new(Path.Combine(artifacts, "sessions"), ContinueOnFailure: true));
            Check(result.Tasks is [{ Outcome: TaskOutcome.Failed, Reason: "IOException" },
                { Outcome: TaskOutcome.Failed, Reason: "IOException" }, { Outcome: TaskOutcome.Failed, Reason: "IOException" }, { Outcome: TaskOutcome.Succeeded }],
                "Session did not follow popup click/failure/clear-mode boundaries: " + string.Join(';', result.Tasks.Select(r => r.Reason + ":" + r.Error)));
            for (int i = 0; i < result.Tasks.Count; i++)
            {
                var boundary = result.Tasks[i].Evidence!["boundary"]!;
                Check(boundary["actionAttempts"]!.GetValue<int>() == (i < 2 ? 1 : 0), "Popup action or timer leaked across tasks");
                if (i < 3) Check(result.Tasks[i].FailureFrames is ["failure.png"], "Popup recovery failure has no registered frame");
            }
            Check(runner.Invalidated == 3 && RunReport.Build(result.Directory).ToJson()["evidence_complete"]!.GetValue<bool>(),
                "Session map usability or popup failure artifacts are inconsistent");
        }
        finally { Environment.SetEnvironmentVariable("ALAS_TEST_MAP_FIXTURE", previous); }
    }

    private sealed class PopupRunner(string assets) : ITaskRunner
    {
        public string Kind => "popup_session_probe";
        public bool RequiresActions => true;
        public int Invalidated { get; private set; }
        public void Validate(JsonObject? input) { }
        public IReadOnlyList<string> Preconditions(TaskRequest request, TaskCapabilities capabilities) => [];
        public async ValueTask<TaskResult> RunAsync(TaskRequest request, TaskContext context, CancellationToken token)
        {
            if (request.Id != "next")
            {
                var session = (EngineSession)context.Campaign!;
                var state = new CampaignState(new MapDefinition("C3", "SP -- --\n-- -- --\n-- -- --", [], [], []));
                state.InitializeMapData(new()); state.Fleet1Location = new(1, 1);
                var config = new CampaignConfiguration { HasAmbush = false, IsClearMode = request.Id == "clear" };
                state.RefreshFleetPaths(config);
                var io = new PopupIo(session);
                var camera = new MapCamera(state, new(2, 2), await io.CaptureAsync(token), io, io,
                    new(io, new AssetFiles(assets), GameServer.Cn, new()), new(io), new(), gridInput: io);
                try
                {
                    // With fleet lock, an unhandled off-map frame waits for the next frame too.
                    // Clear mode skips the cat click, but must still surface this fixture's transport failure.
                    await session.CreateMapCombatMovement(camera, config).MoveAsync(new(3, 2), token: token);
                    throw new InvalidOperationException("Popup movement ignored the next-frame transport failure");
                }
                finally { if (state.MovementInvalidated) Invalidated++; }
            }
            return new(request.Id, Kind, TaskOutcome.Succeeded, "session_probe");
        }
    }

    private sealed class PopupIo(EngineSession session) : IMapViewSource, IMapSwipeInput, IMapGridInput, IMapSwipeEvidence, IImagePatchVision
    {
        private int _captures;
        public async ValueTask<ScreenFrame> CaptureImageAsync(CancellationToken token)
        {
            if (++_captures > 2) throw new IOException("Synthetic next-frame transport failure during popup wait");
            await session.Driver.ScreenshotAsync(token); return session.Driver.Frame!;
        }
        public async ValueTask<MapViewFrame> CaptureAsync(CancellationToken token)
            => new(await CaptureImageAsync(token), MapViewChecks.Regular(new(262, 227.5)));
        public ValueTask TapAsync(PixelArea area, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask SwipeAsync(MapSwipeGesture gesture, CancellationToken token) => throw new InvalidOperationException("Popup relocated the camera");
        public ValueTask<FleetMarker> FleetAsync(MapViewFrame view, VisibleGrid grid, CancellationToken token) => throw new InvalidOperationException();
        public ValueTask<double?> SimilarityAsync(MapViewFrame before, VisibleGrid oldGrid, MapViewFrame after, VisibleGrid newGrid, CancellationToken token) => throw new InvalidOperationException();
        public ValueTask<ImagePatchObservation> MeasurePatchAsync(ScreenFrame frame, ImagePatchRequest request, CancellationToken token = default)
            => throw new InvalidOperationException("Popup bypassed map visibility");
    }
}
