using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static class MapRecoveryChecks
{
    public static async Task RunAsync(string python, string upstream, string artifacts)
    {
        foreach (var source in new[] { MapUiRecovery.Source, MapUiRecovery.StageSource, MapUiRecovery.StrategySource,
                     MapUiRecovery.PreparationSource, MapUiRecovery.AutoSearchSource, StageEntranceRules.Source, UiRecovery.InfoSource, UiRecovery.UiSource })
            Check(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(upstream, source.Path)))) == source.Sha256,
                "Recovery source drift: " + source.Path);
        string output = Path.Combine(artifacts, "native-map-recovery.json");
        var process = await new ProcessRunner().RunAsync(python,
            [Path.Combine(AppContext.BaseDirectory, "native_map_recovery_reference.py"), upstream, output], TimeSpan.FromSeconds(90));
        Check(process.ExitCode == 0, "Native map recovery failed: " + process.Error);
        var cases = JsonNode.Parse(await File.ReadAllTextAsync(output))!["cases"]!.AsArray();
        foreach (var item in cases)
        {
            var sample = item!["sample"]!;
            var driver = new Driver(sample);
            var guard = new MapUiRecovery(driver, driver, driver, driver, driver, B(sample, "opsi"),
                osRewardExit: B(sample, "hooks") ? ct => { driver.Events.Add(["reward_exit"]); return ValueTask.CompletedTask; } : null,
                osMissionExit: B(sample, "hooks") ? ct => { driver.Events.Add(["mission_exit"]); return ValueTask.CompletedTask; } : null);
            bool? value = null; string? error = null;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                switch (sample["operation"]!.GetValue<string>())
                {
                    case "gate": value = await guard.CanDetectAsync(deadline.Token); break;
                    case "cancel": await guard.CancelPreparationAsync(deadline.Token); value = true; break;
                    case "auto": value = await guard.ExitAutoSearchAsync(deadline.Token); break;
                    default:
                        await guard.CanDetectAsync(deadline.Token);
                        value = await guard.RecoverAsync(B(sample, "outside") ? new CameraOutsideViewException(new(1, -2), default) :
                            new MapGeometryException("fixture geometry"), deadline.Token); break;
                }
            }
            catch (CampaignEndedException) { error = "CampaignEnd"; }
            catch (GameNotRunningException) { error = "GameNotRunningError"; }
            var actual = JsonSerializer.SerializeToNode(new { value, error, events = driver.Events });
            if (!JsonNode.DeepEquals(actual, item["expected"]))
            {
                await File.WriteAllTextAsync(Path.Combine(artifacts, "map-recovery-mismatch.json"), new JsonObject {
                    ["case"] = item.DeepClone(), ["actual"] = actual }.ToJsonString());
                throw new InvalidOperationException("Map recovery trace differs: " + sample["name"]);
            }
        }
        await CameraTimerChecksAsync(python, upstream, artifacts, JsonNode.Parse(await File.ReadAllTextAsync(output))!["updates"]!.AsArray());
        await ProfileChecks.RunAsync(python, upstream, artifacts);
        Console.WriteLine($"Native map recovery: {cases.Count} complete priority/helper traces / 4 camera error-timer traces passed.");
    }
    private static bool B(JsonNode n, string key) => n[key]?.GetValue<bool>() ?? false;
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task CameraTimerChecksAsync(string python, string upstream, string artifacts, JsonArray updates)
    {
        await using var vision = new PythonTemplateVision(python, Path.Combine(AppContext.BaseDirectory, "Imaging/Worker/vision_worker.py"));
        var assets = new AssetFiles(Path.Combine(upstream, "assets"));
        var recognizer = new GridRecognition(vision, assets, GameServer.Cn, new());
        var predictor = new MapSwipePredictor(new EmptyEvidence());
        var image = await File.ReadAllBytesAsync(Path.Combine(artifacts, "cn-stage-0.png"));
        var geometry = MapViewChecks.Regular(new(262, 227.5));
        var state = new CampaignState(new MapDefinition("I7", string.Join('\n', Enumerable.Repeat("-- -- -- -- -- -- -- -- --", 7)), [], [], []));
        foreach (var scenario in updates)
        {
            var clock = new ManualClock();
            var source = new TimedSource(scenario!["steps"]!.AsArray().Select(s => s!.GetValue<string>()).ToArray(), image, geometry, clock);
            var initial = new MapViewFrame(new(1, DateTimeOffset.UnixEpoch, image), geometry);
            var camera = new MapCamera(state, new(5, 4), initial, source, new NoSwipe(), recognizer, predictor,
                new() { Predict = false, Optimize = false }, clock: clock);
            string? error = null;
            try { await camera.RefreshAsync(); }
            catch (MapGeometryException) { error = "MapDetectionError"; }
            catch (IOException) { error = "OSError"; }
            Check(error == scenario["error"]?.GetValue<string>() && source.Captures == scenario["captures"]!.GetValue<int>(),
                "Native camera error confirmation differs: " + scenario["name"]);
            if (error is not null)
            {
                bool invalid = false;
                try { await camera.ObserveAsync(MapScanMode.Normal, default); }
                catch (InvalidOperationException) { invalid = true; }
                Check(invalid, "Failed camera was reused after uncertain view");
            }
        }
        var retryClock = new ManualClock();
        var retry = new TimedSource(["error", "error", "success"], image, geometry, retryClock);
        var initialCamera = await MapCamera.CreateAsync(state, new(5, 4), retry, new NoSwipe(), recognizer, predictor,
            new() { Predict = false, Optimize = false }, TimeSpan.FromSeconds(30), clock: retryClock);
        Check(retry.Captures == 3 && initialCamera.View.Frame.Sequence == 4, "Initial camera did not retry transient geometry");
        CameraOutsideViewException outside;
        try { MapViewChecks.Regular(new(600, 227.5)); throw new InvalidOperationException("Outside geometry was accepted"); }
        catch (CameraOutsideViewException error) { outside = error; }
        var partialGeometry = (MapViewGeometry)typeof(CameraOutsideViewException).GetProperty("Geometry",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(outside)!;
        Check(outside.Offset.X != 0 && partialGeometry.SwipeBase.X > 0, "Outside fixture lacks a correction vector");
        var partial = new MapViewFrame(new(2, DateTimeOffset.UnixEpoch, image), partialGeometry);
        typeof(CameraOutsideViewException).GetProperty("PartialView")!.SetValue(outside, partial);
        var corrected = new MapViewFrame(new(3, DateTimeOffset.UnixEpoch, image), geometry);
        var input = new RecordingSwipe();
        var correctionSource = new OutsideSource(outside, corrected);
        var moving = new MapCamera(state, new(5, 4), new(new(1, DateTimeOffset.UnixEpoch, image), geometry),
            correctionSource, input, recognizer, predictor, new() { Predict = false, Optimize = false });
        await moving.RefreshAsync();
        Check(correctionSource.Captures == 2 && input.Gestures.Count == 1 &&
            moving.View.Frame.Sequence == 3 && moving.Position == new Cell(5, 4),
            "Outside correction lost the nested view or changed pending map position");
        var initSource = new OutsideSource(outside, corrected);
        var initInput = new RecordingSwipe();
        var initialized = await MapCamera.CreateAsync(state, new(5, 4), initSource, initInput, recognizer, predictor,
            new() { Predict = false, Optimize = false }, TimeSpan.FromSeconds(30));
        Check(initSource.Captures == 2 && initInput.Gestures.Count == 1 && initialized.View.Frame.Sequence == 3 &&
            initialized.Position == new Cell(5, 4), "Initial outside camera did not localize after correction");
        var pendingClock = new ManualClock();
        var pendingSource = new OutsideSource(outside, corrected, pendingClock);
        var pendingInput = new RecordingSwipe();
        var pendingCamera = new MapCamera(state, new(5, 4), new(new(1, DateTimeOffset.UnixEpoch, image), geometry),
            pendingSource, pendingInput, recognizer, predictor, new() { Predict = false, Optimize = false }, clock: pendingClock);
        await pendingCamera.FocusAsync(new(6, 4), default);
        Check(pendingSource.Captures == 2 && pendingInput.Gestures.Count == 2 && pendingCamera.Position == new Cell(6, 4),
            "Outside correction lost or double-applied the original map displacement");
    }
    private sealed class OutsideSource(CameraOutsideViewException outside, MapViewFrame corrected, ManualClock? clock = null) : IMapViewSource
    {
        public int Captures { get; private set; }
        public ValueTask<MapViewFrame> CaptureAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); clock?.Advance(.4);
            if (++Captures == 1) throw outside;
            if (Captures == 2) return ValueTask.FromResult(corrected);
            throw new InvalidOperationException("Outside correction captured too many frames");
        }
    }
    private sealed class RecordingSwipe : IMapSwipeInput
    {
        public List<MapSwipeGesture> Gestures { get; } = [];
        public ValueTask SwipeAsync(MapSwipeGesture gesture, CancellationToken token)
        { Gestures.Add(gesture); return ValueTask.CompletedTask; }
    }
    private sealed class TimedSource(string[] steps, byte[] image, MapViewGeometry geometry, ManualClock clock) : IMapViewSource
    {
        public int Captures { get; private set; }
        public ValueTask<MapViewFrame> CaptureAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); clock.Advance(.6);
            var step = steps[Captures++];
            return step switch
            {
                "error" => throw new MapGeometryException("fixture geometry"),
                "handled" => throw new MapInterruptionHandledException(),
                "io" => throw new IOException("fixture io"),
                _ => ValueTask.FromResult(new MapViewFrame(new(Captures + 1, DateTimeOffset.UnixEpoch, image), geometry))
            };
        }
    }
    private sealed class NoSwipe : IMapSwipeInput
    { public ValueTask SwipeAsync(MapSwipeGesture gesture, CancellationToken token) => throw new InvalidOperationException("Unexpected swipe"); }
    private sealed class EmptyEvidence : IMapSwipeEvidence
    {
        public ValueTask<FleetMarker> FleetAsync(MapViewFrame view, VisibleGrid grid, CancellationToken token) => ValueTask.FromResult(default(FleetMarker));
        public ValueTask<double?> SimilarityAsync(MapViewFrame before, VisibleGrid oldGrid, MapViewFrame after, VisibleGrid newGrid, CancellationToken token)
            => ValueTask.FromResult<double?>(null);
    }
    private sealed class Driver(JsonNode sample) : AppearanceProbe(GameServer.Cn, null), IMapUiObservations, IApplicationHealth, IStoryHandler, IPopupHandler
    {
        public List<object[]> Events { get; } = [];
        private HashSet<string> _positive = sample["positive"]!.AsArray().Select(s => s!.GetValue<string>()).ToHashSet();
        private int _info = sample["info"]!.GetValue<int>();
        private bool _entrance = B(sample, "entrance");
        public override ValueTask<bool> AppearsAsync(AssetRule asset, ButtonOffset offset = default, double interval = 0,
            double similarity = .85, int threshold = 10, TemplatePreprocessing preprocessing = TemplatePreprocessing.Color, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Events.Add(["appear", asset.Id, new[] { offset.Left, offset.Top, offset.Right, offset.Bottom }, interval, similarity, threshold, preprocessing.ToString()]);
            if (interval > 0 && !Timer(asset, interval, true).Reached()) return ValueTask.FromResult(false);
            bool value = _positive.Contains(asset.Id);
            if (value && interval > 0) Timer(asset).Reset();
            return ValueTask.FromResult(value);
        }
        public override ValueTask ScreenshotAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Events.Add(["screenshot"]); Time.Advance(.61);
            _positive = sample["after"]?.AsArray().Select(s => s!.GetValue<string>()).ToHashSet() ?? [UiAssets.Ui.CAMPAIGN_CHECK.Id];
            _entrance = true; _info = 0; return ValueTask.CompletedTask;
        }
        public override ValueTask ClickAsync(AssetRule asset, CancellationToken token) { Events.Add(["click", asset.Id]); return ValueTask.CompletedTask; }
        public override void ResetInterval(AssetRule asset, double seconds = 3) { Events.Add(["reset", asset.Id]); base.ResetInterval(asset, seconds); }
        public ValueTask<int> InfoBarCountAsync(CancellationToken token) { Events.Add(["info"]); return ValueTask.FromResult(_info); }
        public ValueTask<bool> HasStageEntranceAsync(CancellationToken token) { Events.Add(["entrance"]); return ValueTask.FromResult(_entrance); }
        public ValueTask<bool> StorySkipAsync(CancellationToken token = default) { Events.Add(["story"]); return ValueTask.FromResult(B(sample, "story")); }
        public ValueTask EnsureNoStoryAsync(bool skipFirstScreenshot, CancellationToken token) { Events.Add(["ensure_story", skipFirstScreenshot]); return ValueTask.CompletedTask; }
        public ValueTask<bool> ConfirmAsync(CancellationToken token) { Events.Add(["popup"]); return ValueTask.FromResult(B(sample, "popup")); }
        public ValueTask<bool> IsRunningAsync(CancellationToken token) { Events.Add(["running"]); return ValueTask.FromResult(B(sample, "running")); }
        public ValueTask StopAsync(CancellationToken token) => throw new InvalidOperationException("Unexpected application stop");
        public ValueTask RefreshOrientationAsync(CancellationToken token) => throw new InvalidOperationException("Unexpected orientation refresh");
    }
}
