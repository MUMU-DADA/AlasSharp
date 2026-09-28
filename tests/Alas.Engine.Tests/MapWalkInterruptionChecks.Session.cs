using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Imaging;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static partial class MapWalkInterruptionChecks
{
    private static async Task SessionAsync(string python, string upstream, string artifacts)
    {
        string? previous = Environment.GetEnvironmentVariable("ALAS_TEST_ADB_FIXTURE");
        try
        {
            string exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Alas.Engine.Tests.exe" : "Alas.Engine.Tests");
            var runner = new SessionRunner(artifacts, Path.Combine(upstream, "assets"));
            var queue = await new TaskQueue([runner]).RunAsync(new[] { "emotion", "dock", "disabled", "loading", "next" }.Select(id => new TaskRequest(id, runner.Kind)).ToArray(),
                new(exe, "offline-replay", GameServer.Cn, Path.Combine(upstream, "assets"), python,
                    ApplicationPackage: "org.example.game", AllowActions: true),
                new(Path.Combine(artifacts, "reports"), ContinueOnFailure: true));
            Check(queue.Tasks.Select(t => t.Outcome).SequenceEqual(new[] { TaskOutcome.Succeeded, TaskOutcome.Failed,
                TaskOutcome.Succeeded, TaskOutcome.Succeeded, TaskOutcome.Succeeded }),
                "Real session interruption wiring failed: " + string.Join(';', queue.Tasks.Select(t => t.Error)));
            Check(queue.Tasks[1].Reason == nameof(CampaignDockFullException) && queue.Tasks[1].FailureFrames is ["failure.png"] && runner.Invalidated,
                "Dock-full movement lost failure frame or left authority usable");
            int[] actions = [2, 1, 1, 1, 0];
            for (int i = 0; i < queue.Tasks.Count; i++)
                Check(queue.Tasks[i].Evidence!["boundary"]!["actionAttempts"]!.GetValue<int>() == actions[i], "Interruption actions leaked across tasks");
            var records = Directory.GetFiles(queue.Directory, "walk-interruptions.json", SearchOption.AllDirectories);
            Check(records.Length == 3 && Complete(), "Interruption evidence missing or leaked into unlocked/next task");
            string emotionPath = records.Single(path => JsonSerializer.Deserialize<WalkInterruptionEvidence[]>(File.ReadAllText(path), TaskQueue.Json)!.Any(e => e.LowEmotionHandled));
            string original = await File.ReadAllTextAsync(emotionPath);
            try
            {
                File.Delete(emotionPath); Check(!Complete(), "Missing interruption artifact accepted");
                foreach (string corrupt in new[] { "[]", "[null]", "{", "[{\"observedFrame\":1,\"offensiveCompleted\":true}]" })
                { await File.WriteAllTextAsync(emotionPath, corrupt); Check(!Complete(), "Invalid interruption artifact accepted"); }
            }
            finally { await File.WriteAllTextAsync(emotionPath, original); }
            bool Complete() => RunReport.Build(queue.Directory).ToJson()["evidence_complete"]!.GetValue<bool>();
        }
        finally { Environment.SetEnvironmentVariable("ALAS_TEST_ADB_FIXTURE", previous); }
    }

    private sealed class SessionRunner(string artifacts, string assets) : ITaskRunner
    {
        public string Kind => "walk_interruption_probe";
        public bool RequiresActions => true;
        public bool Invalidated { get; private set; }
        public void Validate(JsonObject? input) { }
        public IReadOnlyList<string> Preconditions(TaskRequest request, TaskCapabilities capabilities) => [];
        public async ValueTask<TaskResult> RunAsync(TaskRequest request, TaskContext context, CancellationToken token)
        {
            if (request.Id != "next")
            {
                var session = (EngineSession)context.Campaign!;
                string directory = Path.Combine(artifacts, "device-" + request.Id); Directory.CreateDirectory(directory);
                string fixture = Path.Combine(directory, "fixture.json");
                File.Delete(Path.Combine(directory, "state.txt"));
                await File.WriteAllTextAsync(fixture, JsonSerializer.Serialize(new { first = Path.Combine(artifacts, "map.png"),
                    second = Path.Combine(artifacts, "map.png"), advance = false, failTap = false }), token);
                Environment.SetEnvironmentVariable("ALAS_TEST_ADB_FIXTURE", fixture);
                var state = new CampaignState(new MapDefinition("C3", "SP -- --\n-- -- --\n-- -- --", [], [], []));
                var config = new CampaignConfiguration { HasAmbush = false, UseFleetLock = request.Id != "disabled",
                    Retirement = new() { Mode = RetirementMode.Disabled }, EmotionMode = CampaignEmotionMode.Ignore };
                state.InitializeMapData(new()); state.Fleet1Location = new(1, 1); state.RefreshFleetPaths(config);
                var io = new SessionIo(session, fixture, Path.Combine(artifacts, request.Id == "disabled" ? "dock.png" : request.Id + ".png"));
                var camera = new MapCamera(state, new(2, 2), await io.CaptureAsync(token), io, io,
                    new(io, new AssetFiles(assets), GameServer.Cn, new()), new(io), new(), gridInput: io);
                try
                {
                    // Both production movement factories must carry the same interruption chain.
                    var movement = request.Id == "emotion" ? session.CreateMapCombatMovement(camera, config) : session.CreateMapMovement(camera, config);
                    var result = await movement.MoveAsync(new(3, 2), token: token);
                    if (request.Id == "emotion")
                        Check(result.Outcome == MapMoveOutcome.Committed && state.Fleet1Location == new Cell(3, 2) && state.BattleCount == 0,
                            "Ignored emotion did not resume the original move without inventing combat");
                    else Check(result.Arrival.Encounter == (request.Id == "loading" ? MapEncounterKind.Combat : MapEncounterKind.UnknownPage) &&
                        result.Outcome != MapMoveOutcome.Committed && state.Fleet1Location == new Cell(1, 1), "Off-map frame was committed as an arrival");
                }
                finally { if (request.Id == "dock") Invalidated = state.MovementInvalidated; }
            }
            return new(request.Id, Kind, TaskOutcome.Succeeded, "offline_session_checked");
        }
    }

    private sealed class SessionIo(EngineSession session, string fixture, string interrupted) : IMapViewSource, IMapSwipeInput,
        IMapGridInput, IMapSwipeEvidence, IImagePatchVision
    {
        public async ValueTask<ScreenFrame> CaptureImageAsync(CancellationToken token)
        { await session.Driver.ScreenshotAsync(token); return session.Driver.Frame!; }
        public async ValueTask<MapViewFrame> CaptureAsync(CancellationToken token)
            => new(await CaptureImageAsync(token), MapViewChecks.Regular(new(262, 227.5), new(Left: true, Upper: true)));
        public async ValueTask TapAsync(PixelArea area, CancellationToken token)
        {
            await session.Driver.ClickAreaAsync(new(area.X, area.Y, area.X + area.Width, area.Y + area.Height), token);
            var data = JsonNode.Parse(await File.ReadAllTextAsync(fixture, token))!;
            data["first"] = interrupted; data["advance"] = true;
            await File.WriteAllTextAsync(fixture, data.ToJsonString(), token);
        }
        public ValueTask SwipeAsync(MapSwipeGesture gesture, CancellationToken token) => throw new InvalidOperationException("Unexpected interruption swipe");
        public ValueTask<FleetMarker> FleetAsync(MapViewFrame view, VisibleGrid grid, CancellationToken token) => ValueTask.FromResult(new FleetMarker(true, true));
        public ValueTask<double?> SimilarityAsync(MapViewFrame before, VisibleGrid oldGrid, MapViewFrame after, VisibleGrid newGrid, CancellationToken token) => throw new InvalidOperationException();
        public ValueTask<ImagePatchObservation> MeasurePatchAsync(ScreenFrame frame, ImagePatchRequest request, CancellationToken token = default)
            => ValueTask.FromResult(new ImagePatchObservation(frame.Sequence, request.Measure == PatchMeasure.Template &&
                request.Color == new PixelColor(255, 255, 255) ? 1 : 0));
    }
}
