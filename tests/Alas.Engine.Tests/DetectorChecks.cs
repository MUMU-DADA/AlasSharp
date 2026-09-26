using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Imaging;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static class DetectorChecks
{
    private static int I(JsonNode n) => n.GetValue<int>();
    private static double D(JsonNode n) => n.GetValue<double>();
    private static bool B(JsonNode n) => n.GetValue<bool>();
    private static ScreenPoint Point(JsonNode n) => new(D(n[0]!), D(n[1]!));
    private static PixelPoint Pixel(JsonNode n) => new(I(n[0]!), I(n[1]!));
    private static GridCorners Corners(JsonNode n) => new(Point(n[0]!), Point(n[1]!), Point(n[2]!), Point(n[3]!));
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Near(double actual, double expected, string label, double epsilon = 0.002)
        => Check(Math.Abs(actual - expected) <= epsilon, $"{label}: {actual:R} != {expected:R}");
    private static void Near(ScreenPoint actual, JsonNode expected, string label, double epsilon = 0.002)
    { Near(actual.X, D(expected[0]!), label + ".x", epsilon); Near(actual.Y, D(expected[1]!), label + ".y", epsilon); }
    private static PolarLine[] Lines(JsonNode n) => n.AsArray().Select(l => new PolarLine(D(l![0]!), D(l[1]!))).ToArray();
    private static PixelArea Rect(JsonNode n) => new(I(n[0]!), I(n[1]!), I(n[2]!) - I(n[0]!), I(n[3]!) - I(n[1]!));
    private static MapDetectionRules Rules(bool chapter, string backend = "homography")
        => new MapDetectionRules { Backend = backend == "homography" ? GridDetectionBackend.Homography : GridDetectionBackend.Perspective }
            .WithChapter(chapter ? RuleCatalog.Create("campaign_main/campaign_1_1").Configure(new()).Vision : null);
    private static void Edges(MapEdges actual, JsonNode expected)
        => Check(new[] { actual.Left, actual.Right, actual.Lower, actual.Upper }.SequenceEqual(expected.AsArray().Select(b => B(b!))), "Map edges differ");
    private static void Grids(IReadOnlyList<VisibleGrid> actual, JsonNode expected, string label)
    {
        Check(actual.Count == expected.AsArray().Count, $"{label} grid count: {actual.Count} != {expected.AsArray().Count}");
        for (int i = 0; i < actual.Count; i++)
        {
            var want = expected[i]!; var grid = actual[i];
            Check(grid.LocalCell == new ViewCell(I(want["cell"]![0]!), I(want["cell"]![1]!)), label + ": grid index");
            var (a, b, c, d) = grid.Corners; ScreenPoint[] points = [a, b, c, d];
            for (int j = 0; j < 4; j++) Near(points[j], want["corners"]![j]!, label + ": polygon");
        }
    }

    public static async Task RunAsync(string python, string upstream, string artifacts, string[] frames)
    {
        foreach (var source in new[] { GridDetector.Source, PerspectiveDetection.Source, GridGeometry.AreaSource,
                     MapDetectionRules.Source, MapDetectionAssets.Source, MapSwipeInput.Source, MapSwipeInput.ControlSource })
            Check(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(upstream, source.Path)))) == source.Sha256,
                "Detector source drifted: " + source.Path);
        string output = Path.Combine(artifacts, "reference.json");
        var process = await new ProcessRunner().RunAsync(python, [Path.Combine(AppContext.BaseDirectory, "native_detector_reference.py"),
            upstream, output, .. frames.Select(Path.GetFullPath)], TimeSpan.FromMinutes(3));
        Check(process.ExitCode == 0, "Native detector failed: " + process.Error);
        var reference = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
        foreach (var item in reference["calibrations"]!.AsArray())
        {
            var sample = item!["sample"]!; var expected = item["expected"]!; var rules = new MapDetectionRules();
            var layout = GridDetector.Calibrate(new(Pixel(sample["size"]!), Corners(sample["corners"]!)), rules, B(sample["overflow"]!));
            Check(layout.Size == Pixel(expected["size"]!), "Homography canvas size differs");
            for (int i = 0; i < 9; i++) Near(layout.Transform.Matrix[i], D(expected["matrix"]![i / 3]![i % 3]!), "Homography matrix", 1e-6);
            var detected = GridDetector.Complete(layout, rules, Point(sample["location"]!), Point(sample["interior"]!), Lines(sample["lines"]!));
            Edges(detected.Edges, expected["edges"]!);
            Check(detected.VerticalEdges == I(expected["counts"]![0]!) && detected.HorizontalEdges == I(expected["counts"]![1]!), "Native edge line counts differ");
            Grids(detected.Grids, expected["grids"]!, "Calibrated");
        }
        foreach (var item in reference["swipes"]!.AsArray())
        {
            var random = new Draws(item!["draws"]!);
            var gesture = new MapSwipeGesture(Point(item["vector"]!), new(123, 159, 1052, 469),
                item["white"]?.AsArray().Select(a => Rect(a!)).ToArray(), item["black"]?.AsArray().Select(a => Rect(a!)).ToArray());
            var path = MapSwipeInput.Place(gesture, random);
            Check(path.Start == Pixel(item["start"]!) && path.End == Pixel(item["end"]!), "Swipe placement differs");
            Check(random.Count == item["draws"]!.AsArray().Count, "Swipe changed native random calls");
        }
        var files = new AssetFiles(Path.Combine(upstream, "assets"));
        var synthetic = new ScreenFrame(1, DateTimeOffset.UnixEpoch, await File.ReadAllBytesAsync(Path.Combine(artifacts, "frame-0.png")));
        foreach (var item in reference["fallbacks"]!.AsArray())
        {
            var sample = item!; var storage = sample["storage"]!;
            var fake = new ScriptedFeatures(sample);
            var detector = new GridDetector(fake, files, new() { Storage = new(Pixel(storage[0]!), Corners(storage[1]!)), DetectEdges = false });
            try
            {
                var actual = await detector.DetectAsync(synthetic);
                Check(sample["expected"]!["error"] is null, "Fallback accepted a native rejection: " + sample["name"]);
                Grids(actual.Geometry.Grids, sample["expected"]!["grids"]!, "Fallback " + sample["name"]);
                Near(actual.Geometry.CenterOffset, sample["expected"]!["offset"]!, "Fallback center offset");
            }
            catch (MapGeometryException error)
            { Check(error.Message == sample["expected"]!["error"]?.GetValue<string>(), "Fallback error differs: " + sample["name"]); }
            Check(fake.Calls.SequenceEqual(sample["calls"]!.AsArray().Select(c => c!.GetValue<string>())), "Fallback evaluation order differs");
        }
        await using var vision = new PythonTemplateVision(python, Path.Combine(AppContext.BaseDirectory, "Imaging/Worker/vision_worker.py"));
        var mask = await files.ReadAsync(MapDetectionAssets.Mask);
        long sequence = 0;
        foreach (var entry in reference["warps"]!.AsArray())
        {
            var item = entry!; var storage = item["storage"]!; var rules = new MapDetectionRules();
            var frame = new ScreenFrame(++sequence, DateTimeOffset.UnixEpoch, await File.ReadAllBytesAsync(Path.Combine(artifacts, item["image"]!.GetValue<string>())));
            var masked = await vision.MaskAsync(frame, mask, MapDetectionAssets.MaskOrigin, default);
            var layout = GridDetector.Calibrate(new(Pixel(storage[0]!), Corners(storage[1]!)), rules);
            var warped = await vision.WarpAsync(masked, mask, layout.Transform, layout.Size, rules, default);
            var expectedPixels = await File.ReadAllBytesAsync(Path.Combine(artifacts, item["warped"]!.GetValue<string>()));
            Check(warped.Edges.Png.Span.SequenceEqual(expectedPixels), "Warp/Canny/morphology pixels differ");
            Check(warped.Lines.SequenceEqual(Lines(item["lines"]!)), "Warped Hough measurements differ");
            int?[] flips = [null, -1, 0, 1, null];
            for (int i = 0; i < 5; i++)
            {
                var template = await files.ReadAsync(i == 0 ? MapDetectionAssets.Center : MapDetectionAssets.Corner);
                var actual = await vision.CorrelateAsync(warped.Edges, template, .8, flips[i], default);
                var expected = item["correlations"]![i]!;
                Near(actual.Maximum, D(expected["maximum"]!), "Correlation maximum", 1e-7);
                Check(actual.Location == Pixel(expected["location"]!) && actual.AboveThreshold.SequenceEqual(expected["points"]!.AsArray().Select(p => Pixel(p!))),
                    "Correlation maximum or threshold coordinates differ");
            }
            var boxes = await vision.RectanglesAsync(warped.Edges, default);
            for (int i = 0; i < 5; i++)
                Check(boxes[i].SequenceEqual(item["rectangles"]![i]!.AsArray().Select(r => new PixelArea(I(r![0]!), I(r[1]!), I(r[2]!), I(r[3]!)))),
                    "Convex hull/contour boxes differ");
        }
        int perspectives = 0, detections = 0, negative = 0;
        var timings = new List<double>();
        foreach (var item in reference["features"]!.AsArray())
        {
            string name = item!["image"]!.GetValue<string>(); var rules = Rules(B(item["chapter"]!));
            var frame = new ScreenFrame(++sequence, DateTimeOffset.UnixEpoch, await File.ReadAllBytesAsync(Path.Combine(artifacts, name)));
            var masked = await vision.MaskAsync(frame, mask, MapDetectionAssets.MaskOrigin, default);
            // Both images are encoded by the same installed CV library, so this also verifies every pixel.
            var expectedMask = await File.ReadAllBytesAsync(Path.Combine(artifacts, name.Replace("frame-", "masked-", StringComparison.Ordinal)));
            Check(masked.Png.Span.SequenceEqual(expectedMask), "Map UI mask differs pixel-for-pixel");
            var measured = await vision.LinesAsync(masked, mask, rules, default);
            IReadOnlyList<PolarLine>[] sets = [measured.InnerHorizontal, measured.InnerVertical, measured.EdgeHorizontal, measured.EdgeVertical];
            for (int i = 0; i < 4; i++) Check(sets[i].SequenceEqual(Lines(item["lines"]![i]!)), "Pure CV peak/Hough measurements differ");
            try
            {
                var result = PerspectiveDetection.Detect(measured, rules);
                var expected = item["expected"]!;
                Check(expected["error"] is null, "C# perspective accepted a native failure");
                Near(result.VanishPoint, expected["vanish"]!, name + ": vanish"); Near(result.DistantPoint, expected["distant"]!, name + ": distant");
                Grids(result.Grids, expected["grids"]!, name); Edges(result.Edges, expected["edges"]!); perspectives++;
            }
            catch (MapGeometryException error) { Check(error.Message == item["expected"]!["error"]?.GetValue<string>(), "Perspective error differs: " + error.Message); }
        }
        foreach (var item in reference["detections"]!.AsArray())
        {
            var sample = item!; string name = sample["image"]!.GetValue<string>();
            var rules = Rules(B(sample["chapter"]!), sample["backend"]!.GetValue<string>()) with { OperationSiren = B(sample["os"]!) };
            var detector = new GridDetector(vision, files, rules);
            var frame = new ScreenFrame(++sequence, DateTimeOffset.UnixEpoch, await File.ReadAllBytesAsync(Path.Combine(artifacts, name)));
            var watch = Stopwatch.StartNew();
            try
            {
                var actual = await detector.DetectAsync(frame); var expected = sample["expected"]!;
                Check(expected["error"] is null, "C# detector accepted native failure");
                Grids(actual.Geometry.Grids, expected["grids"]!, name + ": view"); Edges(actual.Geometry.Edges, expected["edges"]!);
                Check(actual.Geometry.Center == new ViewCell(I(expected["center"]![0]!), I(expected["center"]![1]!)), "Detected center differs");
                Near(actual.Geometry.CenterOffset, expected["offset"]!, "Detected offset"); Near(actual.Geometry.SwipeBase, expected["swipe"]!, "Detected swipe scale");
                detections++;
                if (sample["backend"]!.GetValue<string>() == "homography")
                {
                    var cached = detector.Calibration;
                    var repeated = await detector.DetectAsync(frame with { Sequence = ++sequence });
                    Check(ReferenceEquals(cached, detector.Calibration), "Homography was not reused");
                    Grids(repeated.Geometry.Grids, expected["grids"]!, "Repeated frame");
                }
            }
            catch (MapGeometryException error)
            { Check(error.Message == sample["expected"]!["error"]?.GetValue<string>(), $"{name}: Detector error differs: {error.Message}"); negative++; }
            catch (CameraOutsideViewException error)
            { Check(sample["expected"]!["error"]?.GetValue<string>() == $"Camera outside map: offset=({error.Offset.X}, {error.Offset.Y})", "Outside view failure differs"); negative++; }
            timings.Add(watch.Elapsed.TotalMilliseconds);
        }
        Check(detections > 0 && perspectives > 0 && negative > 0, "Detector lacks positive or negative coverage");
        var positive = reference["detections"]!.AsArray().First(d => d!["backend"]!.GetValue<string>() == "homography" && !B(d["os"]!) && d["expected"]!["error"] is null)!;
        await SessionAsync(python, upstream, artifacts, positive);
        await QueueAsync(python, upstream, artifacts, positive);
        await InputChecksAsync();
        await File.WriteAllTextAsync(Path.Combine(artifacts, "csharp-summary.json"), JsonSerializer.Serialize(new
        { calibrations = 90, swipes = 180, fallbacks = reference["fallbacks"]!.AsArray().Count, warps = reference["warps"]!.AsArray().Count,
            perspectives, detections, negative, elapsed_ms = timings, saved_frames = frames.Length,
            evidence = "Offline image replay only; no real-device actions or sortie settlement" }));
        Console.WriteLine($"Grid detector: 90 calibrations/edges; 180 native swipe placements; {reference["fallbacks"]!.AsArray().Count} fallback traces; {perspectives} perspective fits; {detections} complete views and {negative} expected failures; {frames.Length} saved input frames. Session screenshot/CV/ADB replay passed. No live device or settlement evidence.");
    }

    private sealed class ScriptedFeatures(JsonNode sample) : IGridFeatureVision
    {
        public List<string> Calls { get; } = [];
        private int _correlation;
        public ValueTask<ScreenFrame> MaskAsync(ScreenFrame frame, ReadOnlyMemory<byte> mask, PixelPoint origin, CancellationToken token) => ValueTask.FromResult(frame);
        public ValueTask<LineFeatures> LinesAsync(ScreenFrame frame, ReadOnlyMemory<byte> mask, MapDetectionRules rules, CancellationToken token)
            => throw new InvalidOperationException("Stored calibration unexpectedly invoked perspective detection");
        public ValueTask<WarpedFeatures> WarpAsync(ScreenFrame frame, ReadOnlyMemory<byte> mask, ProjectiveTransform transform,
            PixelPoint size, MapDetectionRules rules, CancellationToken token) => ValueTask.FromResult(new WarpedFeatures(frame, []));
        public ValueTask<CorrelationFeatures> CorrelateAsync(ScreenFrame frame, ReadOnlyMemory<byte> template, double threshold, int? flip, CancellationToken token)
        {
            int?[] flips = [null, -1, 0, 1, null];
            Check(flip == flips[_correlation] && threshold == 0.8, "Native corner flip or threshold differs");
            Calls.Add(_correlation == 0 ? "center" : "corner");
            var value = sample["correlations"]![_correlation++]!;
            return ValueTask.FromResult(new CorrelationFeatures(D(value["maximum"]!), Pixel(value["location"]!),
                value["points"]!.AsArray().Select(p => Pixel(p!)).ToArray()));
        }
        public ValueTask<IReadOnlyList<IReadOnlyList<PixelArea>>> RectanglesAsync(ScreenFrame frame, CancellationToken token)
        {
            Calls.AddRange(Enumerable.Repeat("rectangles", 5));
            return ValueTask.FromResult<IReadOnlyList<IReadOnlyList<PixelArea>>>(sample["rectangles"]!.AsArray().Select(g =>
                (IReadOnlyList<PixelArea>)g!.AsArray().Select(r => new PixelArea(I(r![0]!), I(r[1]!), I(r[2]!), I(r[3]!))).ToArray()).ToArray());
        }
    }

    private sealed class Draws(JsonNode values) : Random
    {
        public int Count;
        public override long NextInt64(long minValue, long maxValue)
        {
            var expected = values[Count++]!;
            Check(minValue == I(expected[0]!) && maxValue == (long)I(expected[1]!) + 1, "Native swipe random range differs");
            return I(expected[2]!);
        }
    }
    public static async Task<int> FakeAdbAsync(string[] args)
    {
        string fixture = Environment.GetEnvironmentVariable("ALAS_TEST_MAP_FIXTURE") ?? throw new InvalidOperationException("Missing map fixture");
        if (args.SequenceEqual(["exec-out", "screencap", "-p"]))
        {
            if (int.TryParse(Environment.GetEnvironmentVariable("ALAS_TEST_MAP_DELAY"), out int delay)) await Task.Delay(delay);
            await Console.OpenStandardOutput().WriteAsync(await File.ReadAllBytesAsync(fixture)); return 0;
        }
        if (args is ["shell", "input", "swipe", ..])
        { await File.AppendAllTextAsync(fixture + ".actions", JsonSerializer.Serialize(args) + "\n"); return 0; }
        Console.Error.Write("Unexpected map replay command"); return 2;
    }
    private static async Task SessionAsync(string python, string upstream, string artifacts, JsonNode sample)
    {
        string? previous = Environment.GetEnvironmentVariable("ALAS_TEST_MAP_FIXTURE");
        string fixture = Path.Combine(artifacts, sample["image"]!.GetValue<string>());
        Environment.SetEnvironmentVariable("ALAS_TEST_MAP_FIXTURE", fixture);
        try
        {
            var map = new CampaignState(new MapDefinition("T20", string.Join('\n', Enumerable.Repeat(string.Join(' ', Enumerable.Repeat("--", 20)), 20)), [], [], []));
            string executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Alas.Engine.Tests.exe" : "Alas.Engine.Tests");
            await using var session = new EngineSession(new(executable, "offline-map", GameServer.Cn, Path.Combine(upstream, "assets"), python,
                "org.example.game", AllowActions: true));
            var camera = await session.CreateMapCameraAsync(map, new(10, 10), Rules(B(sample["chapter"]!)), new(), new() { Predict = false }, TimeSpan.FromSeconds(20));
            var observation = await camera.ObserveAsync(MapScanMode.Normal, default);
            Check(observation.Cells.Count > 0, "Session detector did not produce observations");
            // Stationary replay cannot prove a swipe changed the game; exercise the terminal independently.
            var device = new AdbDevice(executable, "offline-map", true);
            await new MapSwipeInput(device, new Random(82)).SwipeAsync(new(new(-200, 80), new(123, 159, 1052, 469), [], []), default);
            Check(File.Exists(fixture + ".actions"), "Concrete swipe input never reached ADB transport");
            var evidence = await session.SaveEvidenceAsync(Path.Combine(artifacts, "session"), false);
            Check(evidence.Image is not null && evidence.FrameSequence > 0, "Map session lost raw screenshot evidence");
        }
        finally { Environment.SetEnvironmentVariable("ALAS_TEST_MAP_FIXTURE", previous); }
    }
    private static async Task InputChecksAsync()
    {
        var fake = new FakeProcess { Response = new(0, [], "") };
        var readOnly = new MapSwipeInput(new AdbDevice("adb", "fixture", false, fake), new Random(4));
        bool rejected = false;
        try { await readOnly.SwipeAsync(new(new(100, 0), new(0, 0, 1280, 720), null, null), default); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected && fake.Calls.Count == 0, "Read-only map gesture reached ADB");
        var input = new MapSwipeInput(new AdbDevice("adb", "fixture", true, fake), new Random(4));
        await input.SwipeAsync(new(new(1, 0), new(0, 0, 1280, 720), null, null), default);
        Check(fake.Calls.Count == 0, "Sub-ten-pixel swipe was not dropped");
    }

    private static async Task QueueAsync(string python, string upstream, string artifacts, JsonNode sample)
    {
        string? previous = Environment.GetEnvironmentVariable("ALAS_TEST_MAP_FIXTURE");
        string? previousDelay = Environment.GetEnvironmentVariable("ALAS_TEST_MAP_DELAY");
        string fixture = Path.Combine(artifacts, "queue-map.png");
        await File.WriteAllBytesAsync(fixture, await File.ReadAllBytesAsync(Path.Combine(artifacts, sample["image"]!.GetValue<string>())));
        if (File.Exists(fixture + ".actions")) File.Delete(fixture + ".actions");
        Environment.SetEnvironmentVariable("ALAS_TEST_MAP_FIXTURE", fixture);
        Environment.SetEnvironmentVariable("ALAS_TEST_MAP_DELAY", null);
        string executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Alas.Engine.Tests.exe" : "Alas.Engine.Tests");
        var options = new EngineSessionOptions(executable, "offline-map", GameServer.Cn, Path.Combine(upstream, "assets"), python);
        TaskRequest Request(string id, double seconds = 30) => new(id, "map_observe",
            new JsonObject { ["campaign"] = "campaign_main/campaign_1_1" }, TimeoutSeconds: seconds);
        var queue = new TaskQueue();
        try
        {
            var success = await queue.RunAsync([Request("first"), Request("second")], options, new(artifacts));
            Check(!success.Failed && success.Tasks.All(t => t.Outcome == TaskOutcome.Succeeded), "Read-only map queue replay failed");
            foreach (var task in success.Tasks)
            {
                var evidence = task.Evidence!;
                Check(!B(evidence["settlement_verified"]!) && !B(evidence["global_position_verified"]!), "Local observation claimed global position or settlement");
                Check(evidence["cells"]!.AsArray().Count > 0 && evidence["geometry"]!.AsArray().Count == evidence["cells"]!.AsArray().Count,
                    "Map task omitted detected cells or geometry");
                Check(I(evidence["boundary"]!["actionAttempts"]!) == 0 && evidence["frame"]!.GetValue<long>() == evidence["boundary"]!["frameSequence"]!.GetValue<long>(),
                    "Map task acted or recorded a stale screenshot");
            }
            Check(success.Tasks[1].Evidence!["frame"]!.GetValue<long>() > success.Tasks[0].Evidence!["frame"]!.GetValue<long>(), "Map tasks did not share one session");
            Check(!File.Exists(fixture + ".actions"), "Read-only observation attempted a gesture");
            var invalid = await queue.RunAsync([new("missing", "map_observe", new JsonObject { ["campaign"] = "campaign_main/campaign_2_1" })],
                options with { Python = "missing", Adb = "missing" }, new(artifacts));
            Check(invalid.Failed && invalid.Tasks[0] is { Outcome: TaskOutcome.Refused, Reason: "NotSupportedException" }, "Unported campaign was executed");
            Environment.SetEnvironmentVariable("ALAS_TEST_MAP_FIXTURE", Path.Combine(artifacts, "frame-1.png"));
            var failed = await queue.RunAsync([Request("blank"), Request("stopped")], options, new(artifacts));
            Check(failed.Failed && failed.Tasks[0].Outcome == TaskOutcome.Failed && failed.Tasks[0].FailureFrames is ["failure.png"] &&
                failed.Tasks[0].Error?.Contains("GridDetector.DetectAsync", StringComparison.Ordinal) == true && failed.Tasks[1].Reason == "previous_failure",
                "Failed detection lost its stack, failure frame or stop behavior");
            Environment.SetEnvironmentVariable("ALAS_TEST_MAP_FIXTURE", fixture);
            Environment.SetEnvironmentVariable("ALAS_TEST_MAP_DELAY", "10000");
            var timed = await queue.RunAsync([Request("timeout", 0.3)], options, new(artifacts));
            Check(timed.Failed && timed.Tasks[0].Reason == "TimeoutException", "Map task ignored its deadline");
            using (var cancellation = new CancellationTokenSource(300))
            {
                var cancelled = await queue.RunAsync([Request("cancel"), Request("next")], options, new(artifacts), cancellation.Token);
                Check(cancelled.Failed && cancelled.Tasks[0].Reason == "cancelled_during_task" && cancelled.Tasks[1].Reason == "cancelled_before_task",
                    "Map queue conflated cancellation with timeout");
            }
            await using var session = new EngineSession(options);
            var map = new CampaignState(RuleCatalog.Create("campaign_main/campaign_1_1").Map);
            bool timeout = false;
            try { await session.CreateMapCameraAsync(map, new(1, 1), new(), new(), new(), TimeSpan.FromMilliseconds(300)); }
            catch (TimeoutException) { timeout = true; }
            Check(timeout, "Camera initialization ignored its deadline");
            bool osRejected = false;
            try { await session.CreateMapCameraAsync(map, new(1, 1), new() { OperationSiren = true }, new(), new(), TimeSpan.FromSeconds(5)); }
            catch (NotSupportedException) { osRejected = true; }
            Check(osRejected, "OS geometry silently used the main campaign predictor");
            Environment.SetEnvironmentVariable("ALAS_TEST_MAP_DELAY", null);
            string root = AppContext.BaseDirectory;
            while (!File.Exists(Path.Combine(root, "Alas.Engine.slnx")))
                root = Directory.GetParent(root)?.FullName ?? throw new DirectoryNotFoundException("Engine solution not found");
            string cli = Path.Combine(root, "src/Alas.Engine.Cli/bin/Release/net10.0/Alas.Engine.Cli.dll");
            string queueFile = Path.Combine(artifacts, "map-observe-queue.json");
            await File.WriteAllTextAsync(queueFile, JsonSerializer.Serialize(new[] { Request("cli") }, TaskQueue.Json));
            var cliResult = await new ProcessRunner().RunAsync("dotnet", [cli, "run", "--queue", queueFile,
                "--adb", executable, "--serial", "offline-map", "--server", "cn", "--assets", options.Assets,
                "--python", python, "--artifacts", Path.Combine(artifacts, "cli")], TimeSpan.FromSeconds(45));
            Check(cliResult.ExitCode == 0, "CLI map task failed: " + cliResult.Error);
            var cliEvidence = JsonNode.Parse(cliResult.Output)!;
            Check(cliEvidence["tasks"]![0]!["outcome"]!.GetValue<string>() == "succeeded" && !File.Exists(fixture + ".actions"),
                "CLI read-only map entry did not preserve outcome/action boundary");
            Console.WriteLine("Read-only map queue: shared session, local-only evidence, unported-rule refusal, failure frames, cancellation and initialization deadline passed.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ALAS_TEST_MAP_FIXTURE", previous);
            Environment.SetEnvironmentVariable("ALAS_TEST_MAP_DELAY", previousDelay);
        }
    }
}
