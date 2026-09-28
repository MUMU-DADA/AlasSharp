using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Imaging;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static partial class MapWalkRecoveryChecks
{
    private static async Task ReportsAsync(string python, string upstream, string artifacts)
    {
        string? previous = Environment.GetEnvironmentVariable("ALAS_TEST_MAP_FIXTURE");
        string fixture = Path.Combine(artifacts, "walk-cn-True-0.png");
        Environment.SetEnvironmentVariable("ALAS_TEST_MAP_FIXTURE", fixture);
        try
        {
            string executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Alas.Engine.Tests.exe" : "Alas.Engine.Tests");
            var runner = new ReportRunner(Path.Combine(upstream, "assets"));
            var result = await new TaskQueue([runner]).RunAsync(
                [new("walk", runner.Kind, TimeoutSeconds: 8), new("next", runner.Kind)],
                new(executable, "offline-map", GameServer.Cn, Path.Combine(upstream, "assets"), python),
                new(Path.Combine(artifacts, "reports"), ContinueOnFailure: true));
            Check(result.Tasks is [{ Outcome: TaskOutcome.Failed, Reason: "TimeoutException" }, { Outcome: TaskOutcome.Succeeded }],
                "Actual session did not stop at the never-clearing walk message: " + result.Tasks[0].Error);
            Check(result.Tasks[0].FailureFrames is ["failure.png"], "Walk timeout did not register its failure frame");
            var paths = Directory.GetFiles(result.Directory, "walk-recoveries.json", SearchOption.AllDirectories);
            Check(paths.Length == 1 && Complete(), "Walk artifacts were missing or leaked across tasks");
            string original = await File.ReadAllTextAsync(paths[0]);
            var records = JsonSerializer.Deserialize<WalkRecoveryEvidence[]>(original, TaskQueue.Json)!;
            Check(records is [{ Phase: "recovering", CompletedSteps: 0 }] && runner.Invalidated,
                "Actual session factory lost recovery phase or kept a failed map usable");
            try
            {
                File.Delete(paths[0]); Check(!Complete(), "Missing walk artifact accepted");
                foreach (string corrupt in new[] { "[]", "[null]", "{", JsonSerializer.Serialize(new[] { records[0] with { Phase = "completed" } }, TaskQueue.Json) })
                { await File.WriteAllTextAsync(paths[0], corrupt); Check(!Complete(), "Corrupt walk artifact accepted"); }
            }
            finally { await File.WriteAllTextAsync(paths[0], original); }
            bool Complete() => RunReport.Build(result.Directory).ToJson()["evidence_complete"]!.GetValue<bool>();
        }
        finally { Environment.SetEnvironmentVariable("ALAS_TEST_MAP_FIXTURE", previous); }
        Console.WriteLine("Actual EngineSession/TaskQueue, pure CV and simulated ADB: walk timeout artifact, failure frame, corruption and task isolation passed.");
    }

    private sealed class ReportRunner(string assets) : ITaskRunner
    {
        public string Kind => "walk_report_probe";
        public bool RequiresActions => false;
        public bool Invalidated { get; private set; }
        public void Validate(JsonObject? input) { }
        public IReadOnlyList<string> Preconditions(TaskRequest request, TaskCapabilities capabilities) => [];
        public async ValueTask<TaskResult> RunAsync(TaskRequest request, TaskContext context, CancellationToken token)
        {
            if (request.Id == "walk")
            {
                var session = (EngineSession)context.Campaign!;
                var state = new CampaignState(new MapDefinition("C3", "SP -- --\n-- -- --\n-- -- --", [], [], []));
                state.InitializeMapData(new()); state.Fleet1Location = new(1, 1);
                var config = new CampaignConfiguration { HasFleetStep = true, HasAmbush = false };
                state.RefreshFleetPaths(config);
                var io = new ReportIo(session);
                var camera = new MapCamera(state, new(2, 2), await io.CaptureAsync(token), io, io,
                    new(io, new AssetFiles(assets), GameServer.Cn, new()), new(io), new(), gridInput: io);
                try { await session.CreateMapMovement(camera, config).MoveAsync(new(3, 2), token: token); }
                finally { Invalidated = state.MovementInvalidated; }
                throw new InvalidOperationException("Non-clearing walk message unexpectedly allowed movement");
            }
            return new(request.Id, Kind, TaskOutcome.Succeeded, "next_task_probe");
        }
    }

    private sealed class ReportIo(EngineSession session) : IMapViewSource, IMapSwipeInput, IMapGridInput, IMapSwipeEvidence, IImagePatchVision
    {
        public async ValueTask<ScreenFrame> CaptureImageAsync(CancellationToken token)
        { await session.Driver.ScreenshotAsync(token); return session.Driver.Frame!; }
        public async ValueTask<MapViewFrame> CaptureAsync(CancellationToken token)
            => new(await CaptureImageAsync(token), MapViewChecks.Regular(new(262, 227.5)));
        public ValueTask TapAsync(PixelArea area, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask SwipeAsync(MapSwipeGesture gesture, CancellationToken token) => throw new InvalidOperationException("Recovery passed non-clearing message");
        public ValueTask<FleetMarker> FleetAsync(MapViewFrame view, VisibleGrid grid, CancellationToken token) => throw new InvalidOperationException();
        public ValueTask<double?> SimilarityAsync(MapViewFrame before, VisibleGrid oldGrid, MapViewFrame after, VisibleGrid newGrid, CancellationToken token) => throw new InvalidOperationException();
        public ValueTask<ImagePatchObservation> MeasurePatchAsync(ScreenFrame frame, ImagePatchRequest request, CancellationToken token = default)
            => throw new InvalidOperationException("Movement checked a fleet marker before the walk message");
    }
}
