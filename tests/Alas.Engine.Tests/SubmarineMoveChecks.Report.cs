using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Imaging;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static partial class SubmarineMoveChecks
{
    private static async Task ReportsAsync(string python, string upstream, string artifacts)
    {
        // A nonexistent executable fails before any device I/O; the actual session still owns the factory and artifacts.
        string missingAdb = Path.GetFullPath(Path.Combine(artifacts, Guid.NewGuid().ToString("N"), "missing-adb.exe"));
        var options = new EngineSessionOptions(missingAdb, "offline", GameServer.Cn, Path.Combine(upstream, "assets"), python);
        var runner = new MoveReportProbe(Path.Combine(upstream, "assets"));
        var result = await new TaskQueue([runner]).RunAsync(
            [new("move", runner.Kind), new("next", runner.Kind)], options,
            new(Path.Combine(artifacts, "reports"), ContinueOnFailure: true));
        Check(result.Tasks is [{ Outcome: TaskOutcome.Failed }, { Outcome: TaskOutcome.Succeeded }] &&
            runner.StateInvalidated, "Relocation transport failure did not invalidate state and preserve the next task");
        var paths = Directory.GetFiles(result.Directory, "submarine-moves.json", SearchOption.AllDirectories);
        Check(paths.Length == 1 && Complete(), "Session move evidence was missing or leaked across task boundaries");
        string original = await File.ReadAllTextAsync(paths[0]);
        var saved = JsonSerializer.Deserialize<SubmarineMoveEvidence[]>(original, TaskQueue.Json)!;
        Check(saved is [{ Phase: "opening", Attempts: 0, SelectionFrame: null, ReturnedFrame: null, Moved: null }] &&
            saved[0].Boss == new Cell(5, 2) && saved[0].Target == new Cell(3, 2),
            "Session lost the failed relocation's stage or selected target");
        try
        {
            File.Delete(paths[0]); Check(!Complete(), "Missing move file passed report validation");
            foreach (string corrupt in new[] { "{", "[]", "[null]",
                JsonSerializer.Serialize(new[] { saved[0] with { Phase = "completed" } }, TaskQueue.Json) })
            {
                await File.WriteAllTextAsync(paths[0], corrupt);
                Check(!Complete(), "Corrupt move evidence passed report validation");
            }
        }
        finally { await File.WriteAllTextAsync(paths[0], original); }
        Check(Complete(), "Restored failure evidence remained incomplete");
        bool Complete() => RunReport.Build(result.Directory).ToJson()["evidence_complete"]!.GetValue<bool>();
        Console.WriteLine("Actual EngineSession/TaskQueue relocation failure: saved phase, missing/corrupt evidence and next-task isolation passed without device I/O.");
    }

    private sealed class MoveReportProbe(string assets) : ITaskRunner
    {
        public string Kind => "move_report_probe";
        public bool RequiresActions => false;
        public bool StateInvalidated { get; private set; }
        public void Validate(JsonObject? input) { }
        public IReadOnlyList<string> Preconditions(TaskRequest request, TaskCapabilities capabilities) => [];
        public async ValueTask<TaskResult> RunAsync(TaskRequest request, TaskContext context, CancellationToken token)
        {
            if (request.Id == "move")
            {
                var session = context.Campaign as EngineSession ?? throw new InvalidOperationException("Actual session required");
                var state = State(); state[new(5, 2)].IsBoss = true;
                var configuration = new CampaignConfiguration { Submarine = 1, SubmarineMode = SubmarineMode.BossOnly,
                    EmotionMode = CampaignEmotionMode.Ignore };
                state.RefreshFleetPaths(configuration);
                var unused = new UnusedMapIo();
                var view = new MapViewFrame(new(1, DateTimeOffset.UnixEpoch, ReadOnlyMemory<byte>.Empty),
                    MapViewChecks.Regular(new(262, 227.5)));
                var camera = new MapCamera(state, new(3, 2), view, unused, unused,
                    new(unused, new AssetFiles(assets), GameServer.Cn, new()), new(unused), new());
                try { await session.CreateCampaignMapCombat(camera, configuration).ClearBossAsync(token); }
                finally { StateInvalidated = state.MovementInvalidated; }
                throw new InvalidOperationException("Missing ADB unexpectedly allowed a relocation");
            }
            return new(request.Id, Kind, TaskOutcome.Succeeded, "next_task_probe");
        }
    }

    private sealed class UnusedMapIo : IMapViewSource, IMapSwipeInput, IMapSwipeEvidence, IImagePatchVision
    {
        private static Exception Unexpected() => new InvalidOperationException("Map I/O ran after strategy transport failure");
        public ValueTask<MapViewFrame> CaptureAsync(CancellationToken token) => throw Unexpected();
        public ValueTask<ScreenFrame> CaptureImageAsync(CancellationToken token) => throw Unexpected();
        public ValueTask SwipeAsync(MapSwipeGesture gesture, CancellationToken token) => throw Unexpected();
        public ValueTask<FleetMarker> FleetAsync(MapViewFrame view, VisibleGrid grid, CancellationToken token) => throw Unexpected();
        public ValueTask<double?> SimilarityAsync(MapViewFrame before, VisibleGrid oldGrid,
            MapViewFrame after, VisibleGrid newGrid, CancellationToken token) => throw Unexpected();
        public ValueTask<ImagePatchObservation> MeasurePatchAsync(ScreenFrame frame, ImagePatchRequest request,
            CancellationToken token = default) => throw Unexpected();
    }
}
