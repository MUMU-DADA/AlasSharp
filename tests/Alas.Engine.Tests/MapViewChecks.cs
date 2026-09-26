using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Imaging;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static class MapViewChecks
{
    private static readonly PixelArea Area = new(0, 0, 1280, 720);
    private static readonly PixelPoint Tile = new(100, 100);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static int I(JsonNode node) => node.GetValue<int>();
    private static bool B(JsonNode node) => node.GetValue<bool>();
    private static double D(JsonNode node) => node.GetValue<double>();
    private static ViewCell Cell(JsonNode node) => new(I(node[0]!), I(node[1]!));
    private static ScreenPoint Point(JsonNode node) => new(D(node[0]!), D(node[1]!));
    private static GridCorners Corners(JsonNode node) => new(Point(node[0]!), Point(node[1]!), Point(node[2]!), Point(node[3]!));
    private static JsonArray Pair(int x, int y) => new(JsonValue.Create(x), JsonValue.Create(y));
    private static JsonArray Pair(ViewCell value) => Pair(value.X, value.Y);
    private static void Near(double actual, double expected, string label)
        => Check(Math.Abs(actual - expected) <= 2e-5 * Math.Max(1, Math.Abs(expected)), $"{label}: {actual:R} != {expected:R}");
    private static void Near(ScreenPoint actual, JsonNode expected, string label)
    { Near(actual.X, D(expected[0]!), label + ".x"); Near(actual.Y, D(expected[1]!), label + ".y"); }
    private static void Equal(JsonNode? actual, JsonNode? expected, string label)
        => Check(JsonNode.DeepEquals(actual, expected), $"{label}: {actual} != {expected}");
    private static PixelArea ReadArea(JsonNode node)
        => new(I(node[0]!), I(node[1]!), I(node[2]!) - I(node[0]!), I(node[3]!) - I(node[1]!));

    internal static MapViewGeometry Regular(ScreenPoint screen, MapEdges edges = default, int columns = 3, int rows = 3)
    {
        var grids = new List<VisibleGrid>();
        ScreenPoint P(int x, int y) => new(100 + 100 * x + 8 * y, 100 + 85 * y);
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < columns; x++)
                grids.Add(new(new(x, y), new(P(x, y), P(x + 1, y), P(x, y + 1), P(x + 1, y + 1))));
        return new(grids, Area, screen, Tile, edges);
    }

    public static async Task RunAsync(string python, string upstream, string artifacts)
    {
        foreach (var source in new[] { MapViewGeometry.Source, MapCameraState.Source, GridRecognition.Source, MapCameraRules.Source,
                     GridGeometry.Source, GridGeometry.AreaSource, MapCamera.DirectionSource, MapSwipeEvidence.MaskSource })
            Check(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(upstream, source.Path)))) == source.Sha256,
                "Native camera source drifted: " + source.Path);
        string output = Path.Combine(artifacts, "native-view.json");
        var process = await new ProcessRunner().RunAsync(python,
            [Path.Combine(AppContext.BaseDirectory, "native_view_reference.py"), upstream, output], TimeSpan.FromSeconds(90));
        Check(process.ExitCode == 0, "Native view oracle failed: " + process.Error);
        var reference = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
        foreach (var tap in reference["taps"]!.AsArray())
        {
            var area = ReadArea(tap!["area"]!);
            var draws = new TapDraws(tap["draws"]!.AsArray().Select(value => I(value!)).ToArray());
            var point = MapGridInput.Place(area, draws);
            Equal(Pair(point.X, point.Y), tap["expected"], "Native grid click placement");
            Check(draws.Count == 6, "Map grid click changed native random draw count");
        }
        var firstTap = reference["taps"]![0]!;
        var tapDevice = new TapDevice();
        await new MapGridInput(tapDevice, new TapDraws(firstTap["draws"]!.AsArray().Select(value => I(value!)).ToArray()))
            .TapAsync(ReadArea(firstTap["area"]!), default);
        Equal(Pair(tapDevice.Point!.Value.X, tapDevice.Point.Value.Y), firstTap["expected"],
            "Map grid click was not sent through the C# device");
        int projected = 0, outside = 0;
        foreach (var entry in reference["geometry"]!.AsArray())
        {
            var sample = entry!["sample"]!; var expected = entry["expected"]!;
            string label = sample["name"]!.GetValue<string>();
            try
            {
                var view = new MapViewGeometry(sample["grids"]!.AsArray().Select(g => new VisibleGrid(Cell(g!["cell"]!), Corners(g["corners"]!))),
                    ReadArea(sample["area"]!), Point(sample["screen"]!), new(I(sample["tile"]![0]!), I(sample["tile"]![1]!)));
                Check(expected["error"] is null, label + ": missing native failure");
                Equal(Pair(view.Center), expected["center"], label + ": center");
                Equal(Pair(view.Shape), expected["shape"], label + ": shape");
                Near(view.CenterOffset, expected["offset"]!, label + ": offset");
                Near(view.SwipeBase, expected["swipe"]!, label + ": swipe");
                Check(view.Grids.Length == expected["cells"]!.AsArray().Count, label + ": filtered grid count");
                int index = 0;
                foreach (var grid in view.Grids)
                {
                    var want = expected["cells"]![index++]!; var projection = view.Projections[grid.LocalCell];
                    Equal(Pair(grid.LocalCell), want["cell"], label + ": cell order");
                    Check(projection.Inner == ReadArea(want["inner"]!) && projection.Outer == ReadArea(want["outer"]!), label + ": click areas");
                    Near(projection.ScreenToGrid(new(321.25, 249.75)), want["projected"]!, label + ": projection");
                    Near(projection.GridToScreen(new(0.4, 0.6)), want["back"]!, label + ": inverse");
                    projected++;
                }
            }
            catch (CameraOutsideViewException error)
            {
                Check(expected["error"]?.GetValue<string>() == $"Camera outside map: offset=({error.Offset.X}, {error.Offset.Y})", label + ": outside offset");
                outside++;
            }
            catch (MapGeometryException error)
            { Check(expected["error"]?.GetValue<string>() == error.Message, label + ": unexpected geometry error"); outside++; }
        }

        var png = await File.ReadAllBytesAsync(Path.Combine(artifacts, "pair-0.png"));
        var blank = new ScreenFrame(1, DateTimeOffset.UnixEpoch, png);
        MapViewFrame ViewAt(ViewCell center, int columns = 3, int rows = 2)
            => new(blank, Regular(new(100 + 100 * (center.X + 0.5) + 8 * (center.Y + 0.5), 100 + 85 * (center.Y + 0.5)), columns: columns, rows: rows));
        foreach (var entry in reference["swipes"]!.AsArray())
        {
            var sample = entry!["sample"]!;
            var old = ViewAt(Cell(sample["old_center"]!));
            var next = ViewAt(Cell(sample["new_center"]!)) with { Frame = blank with { Sequence = 2 } };
            var evidence = new ScriptedEvidence(sample);
            var actual = await new MapSwipePredictor(evidence).PredictAsync(old, next, B(sample["current"]!), B(sample["sea"]!));
            Equal(actual is { } value ? Pair(value) : null, entry["expected"], "Swipe prediction");
            Check(evidence.FleetCalls == (B(sample["current"]!) ? 12 : 0), "Raw marker prediction was short-circuited");
        }
        foreach (var entry in reference["cameras"]!.AsArray())
        {
            var sample = entry!["sample"]!; var expected = entry["expected"]!;
            var old = ViewAt(new(1, 1), 3, 3);
            var e = sample["edges"]!;
            var next = new MapViewFrame(blank with { Sequence = 2 }, Regular(new(262, 227.5), new(B(e[0]!), B(e[1]!), B(e[2]!), B(e[3]!))));
            var state = new MapCameraState(new(9, 7), new(5, 4), old);
            if (sample["pending"] is { } pending) state.PrepareSwipe(Cell(pending));
            // A single marker can express predicted deltas of -1..2 using center offsets.
            var evidence = new FixedEvidence(sample["predicted"] is { } d ? Cell(d) : null);
            await state.UpdateAsync(next, new(evidence), B(sample["predict"]!));
            Equal(Pair(state.Position.Column, state.Position.Row), expected["position"], "Camera edge correction");
            if (sample["pending"] is null)
            {
                var initial = new MapCameraState(new(9, 7), new(5, 4), next);
                Equal(Pair(initial.Position.Column, initial.Position.Row), expected["position"], "Initial camera edge correction");
                Check(initial.PendingSwipe is null && initial.Previous is null, "Initial correction fabricated swipe state");
            }
            Equal(state.PendingSwipe is { } p ? Pair(p) : null, expected["pending"], "Camera pending swipe");
            Check((state.Previous is not null) == B(expected["previous"]!), "Camera pending frame drifted");
            bool predicts = sample["pending"] is { } pend && Cell(pend) != default && B(sample["predict"]!);
            Check(evidence.Calls == (predicts ? 18 : 0), "Zero/absent swipe unexpectedly predicted");
        }
        var files = new AssetFiles(Path.Combine(upstream, "assets"));
        var recognition = new GridRecognition(new EmptyPatches(), files, GameServer.Cn, new());
        await ImageRefreshAsync(blank, files);
        await GridTapAsync(blank, recognition);
        foreach (var entry in reference["controls"]!.AsArray()) await ControlAsync(entry!, blank, recognition);
        foreach (var entry in reference["optimized"]!.AsArray()) await OptimizationAsync(entry!, blank, recognition);
        foreach (var entry in reference["settling"]!.AsArray()) await SettlingAsync(entry!, blank, recognition);
        var scanMap = Map(); scanMap.InitializeMapData(new(PoorMapData: true));
        var scanClock = new FakeClock();
        var scanView = new MapViewFrame(blank, Regular(new(262, 227.5)));
        var scanInput = new Input();
        var scanCamera = new MapCamera(scanMap, new(5, 4), scanView,
            new Source(i => scanView with { Frame = blank with { Sequence = i + 1 } }, scanClock),
            scanInput, recognition, new(new FixedEvidence(null)), new() { Predict = false }, clock: scanClock);
        var scan = await new MapScanner(scanMap, scanCamera).ScanAsync(new(), TimeSpan.FromSeconds(10),
            queue: [new(6, 4)], mustScan: [new(6, 4)]);
        Check(scan.Visited.SequenceEqual([new Cell(6, 4)]) && scan.RejectedViews == 0 && scanInput.Gestures.Count == 1,
            "Concrete camera failed the scanner integration");
        await NegativeChecksAsync(blank, recognition);
        await PixelChecksAsync(reference["pixels"]!, python, artifacts, files);
        Console.WriteLine($"Native view: {reference["geometry"]!.AsArray().Count} layouts / {projected} grid projections / {outside} expected failures; " +
            $"{reference["swipes"]!.AsArray().Count} swipe votes / {reference["cameras"]!.AsArray().Count} camera state transitions / " +
            $"{reference["controls"]!.AsArray().Count} focus/edge traces / {reference["optimized"]!.AsArray().Count} optimized controls / " +
            $"{reference["settling"]!.AsArray().Count} native settling traces / {reference["pixels"]!.AsArray().Count} actual CV pairs / " +
            $"{reference["taps"]!.AsArray().Count} native grid clicks. " +
            "Synthetic polygons and pixels; no detector, real device or sortie verification.");
    }

    private sealed class ScriptedEvidence(JsonNode sample) : IMapSwipeEvidence
    {
        public int FleetCalls;
        public ValueTask<FleetMarker> FleetAsync(MapViewFrame view, VisibleGrid grid, CancellationToken token)
        {
            FleetCalls++;
            var marker = sample[view.Frame.Sequence == 1 ? "old" : "new"]!.AsArray().Single(g => Cell(g!["cell"]!) == grid.LocalCell)!["marker"]!;
            return ValueTask.FromResult(new FleetMarker(B(marker[0]!), B(marker[1]!)));
        }
        public ValueTask<double?> SimilarityAsync(MapViewFrame before, VisibleGrid oldGrid, MapViewFrame after, VisibleGrid newGrid, CancellationToken token)
            => ValueTask.FromResult<double?>(sample["matches"]!.AsArray().Any(pair => Cell(pair![0]!) == oldGrid.LocalCell && Cell(pair[1]!) == newGrid.LocalCell) ? 0.91 : 0.9);
    }
    private sealed class FixedEvidence(ViewCell? delta) : IMapSwipeEvidence
    {
        public int Calls;
        public ValueTask<FleetMarker> FleetAsync(MapViewFrame view, VisibleGrid grid, CancellationToken token)
        {
            Calls++;
            var target = delta is not { } d ? new(-9, -9) : view.Frame.Sequence == 1
                ? new ViewCell(Math.Max(d.X, 0), Math.Max(d.Y, 0)) : new(Math.Max(-d.X, 0), Math.Max(-d.Y, 0));
            bool marked = grid.LocalCell == target;
            return ValueTask.FromResult(new FleetMarker(marked, marked));
        }
        public ValueTask<double?> SimilarityAsync(MapViewFrame before, VisibleGrid oldGrid, MapViewFrame after, VisibleGrid newGrid, CancellationToken token)
            => throw new InvalidOperationException("Unexpected pair prediction");
    }
    private sealed class EmptyPatches : IImagePatchVision
    {
        public int Calls { get; private set; }
        public ValueTask<ImagePatchObservation> MeasurePatchAsync(ScreenFrame frame, ImagePatchRequest request, CancellationToken token = default)
        { Calls++; return ValueTask.FromResult(new ImagePatchObservation(frame.Sequence, 0)); }
    }
    private sealed class TapDraws(int[] values) : Random
    {
        public int Count { get; private set; }
        public override long NextInt64(long minValue, long maxValue)
        {
            int value = values[Count++];
            Check(value >= minValue && value < maxValue, "Native tap draw lies outside grid area");
            return value;
        }
    }
    private sealed class FakeClock : TimeProvider
    {
        private long _ticks;
        public void Advance(double seconds = 0.4) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
    }
    private sealed class Source(Func<int, MapViewFrame> get, FakeClock clock) : IMapViewSource
    {
        public int Captures;
        public ValueTask<MapViewFrame> CaptureAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); clock.Advance(); return ValueTask.FromResult(get(++Captures)); }
        public async ValueTask<ScreenFrame> CaptureImageAsync(CancellationToken token)
            => (await CaptureAsync(token)).Frame;
    }

    private static async Task ImageRefreshAsync(ScreenFrame frame, AssetFiles files)
    {
        var patches = new EmptyPatches();
        var recognition = new GridRecognition(patches, files, GameServer.Cn, new());
        var initial = new MapViewFrame(frame, Regular(new(262, 227.5)));
        var clock = new FakeClock();
        var source = new Source(i => new(frame with { Sequence = i + 1 }, Regular(new(340, 280))), clock);
        var input = new Input();
        var camera = new MapCamera(Map(), new(5, 4), initial, source, input, recognition,
            new(new FixedEvidence(null)), new() { Predict = false, Optimize = false }, clock: clock);
        await camera.ObserveAsync(MapScanMode.Normal, default);
        int initialCalls = patches.Calls;
        Check(initialCalls > 0, "Initial map view was not recognized");
        await camera.RefreshImageAsync();
        Check(ReferenceEquals(camera.View.Geometry, initial.Geometry) && camera.Position == new Cell(5, 4) &&
            camera.View.Frame.Sequence == 2 && source.Captures == 1 && input.Gestures.Count == 0,
            "Image-only refresh changed localized map geometry or moved the camera");
        await camera.ObserveAsync(MapScanMode.Normal, default);
        Check(patches.Calls == initialCalls * 2, "Image-only refresh reused an old grid observation");

        var staleSource = new Source(_ => initial, new FakeClock());
        var stale = new MapCamera(Map(), new(5, 4), initial, staleSource, input, recognition,
            new(new FixedEvidence(null)), new() { Predict = false, Optimize = false });
        bool rejected = false;
        try { await stale.RefreshImageAsync(); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Image-only refresh accepted a stale screenshot");
        rejected = false;
        try { await stale.ObserveAsync(MapScanMode.Normal, default); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Camera with failed image refresh remained usable");

        var pending = new MapCameraState(new(9, 7), new(5, 4), initial);
        pending.PrepareSwipe(new(1, 0));
        rejected = false;
        try { pending.UpdateImage(frame with { Sequence = 2 }); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected && pending.View.Frame.Sequence == 1, "Image-only refresh concealed a pending swipe");
    }
    private static async Task GridTapAsync(ScreenFrame frame, GridRecognition recognition)
    {
        var view = new MapViewFrame(frame, Regular(new(262, 227.5)));
        var clock = new FakeClock();
        var source = new Source(i => view with { Frame = frame with { Sequence = i + 1 } }, clock);
        var swipes = new Input(); var taps = new TapInput();
        var camera = new MapCamera(Map(), new(5, 4), view, source, swipes, recognition,
            new(new FixedEvidence(null)), new() { Predict = false, Optimize = false }, clock: clock, gridInput: taps);
        var local = view.Geometry.Center;
        await camera.TapCellAsync(new(5, 4));
        Check(taps.Areas.SequenceEqual([view.Geometry.Projections[local].Inner]) &&
            source.Captures == 0 && swipes.Gestures.Count == 0 && camera.Position == new Cell(5, 4),
            "Visible grid tap changed camera state or used the wrong click area");
        bool blocked = false;
        try { await camera.ObserveAsync(MapScanMode.Normal, default); }
        catch (MapImageRefreshRequiredException) { blocked = true; }
        Check(blocked, "Grid tap allowed stale map observation");
        await camera.RefreshImageAsync();
        Check(source.Captures == 1, "Grid tap refresh did not capture a new image");
        await camera.TapCellAsync(new(7, 4));
        Check(source.Captures == 2 && swipes.Gestures.Count == 1 && taps.Areas.Count == 2 &&
            taps.Areas[1] == camera.View.Geometry.Projections[camera.View.Geometry.Center].Inner &&
            camera.Position == new Cell(7, 4), "Offscreen grid was not localized before tap");
        blocked = false;
        try { await camera.TapCellAsync(new(7, 4)); }
        catch (MapImageRefreshRequiredException) { blocked = true; }
        Check(blocked && taps.Areas.Count == 2, "Second grid tap did not require a fresh frame");
        await camera.RefreshImageAsync();
        await camera.ObserveAsync(MapScanMode.Normal, default);
        taps.Fail = true;
        bool failed = false;
        try { await camera.TapCellAsync(new(7, 4)); } catch (IOException) { failed = true; }
        Check(failed, "Grid tap failure was hidden");
        failed = false;
        try { await camera.ObserveAsync(MapScanMode.Normal, default); } catch (InvalidOperationException) { failed = true; }
        Check(failed, "Camera remained usable after an uncertain grid tap");
    }
    private sealed class TapInput : IMapGridInput
    {
        public List<PixelArea> Areas { get; } = [];
        public bool Fail;
        public ValueTask TapAsync(PixelArea area, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Areas.Add(area);
            if (Fail) throw new IOException("synthetic tap failure");
            return ValueTask.CompletedTask;
        }
    }
    private sealed class TapDevice : IGameDevice
    {
        public PixelPoint? Point;
        public ValueTask TapAsync(PixelPoint point, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Point = point; return ValueTask.CompletedTask; }
        public ValueTask<ScreenFrame> CaptureAsync(CancellationToken token = default) => throw new InvalidOperationException("Unexpected capture");
        public ValueTask SwipeAsync(PixelPoint start, PixelPoint end, TimeSpan duration, CancellationToken token = default)
            => throw new InvalidOperationException("Unexpected swipe");
        public ValueTask BackAsync(CancellationToken token = default) => throw new InvalidOperationException("Unexpected back");
    }
    private sealed class Input : IMapSwipeInput
    {
        public readonly List<MapSwipeGesture> Gestures = [];
        public bool Fail;
        public ValueTask SwipeAsync(MapSwipeGesture gesture, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Gestures.Add(gesture); if (Fail) throw new IOException("synthetic gesture failure"); return ValueTask.CompletedTask; }
    }
    private static CampaignState Map() => new(new MapDefinition("I7", string.Join('\n', Enumerable.Repeat("-- -- -- -- -- -- -- -- --", 7)), [], [], []));
    private static async Task ControlAsync(JsonNode entry, ScreenFrame frame, GridRecognition recognition)
    {
        var s = entry["sample"]!; var expected = entry["expected"]!;
        string action = s["action"]!.GetValue<string>(); bool edge = action == "edges";
        ScreenPoint center = edge ? new(240, 230) : new(262, 227.5);
        var old = new MapViewFrame(frame, Regular(center));
        var clock = new FakeClock();
        var source = new Source(i => new(frame with { Sequence = i + 1 }, Regular(center, edge ? new(Left: i >= 1, Lower: i >= 2) : default)), clock);
        var input = new Input();
        var camera = new MapCamera(Map(), new(5, 4), old, source, input, recognition, new(new FixedEvidence(null)),
            new() { Predict = false, Optimize = false, EdgeCorner = "bottom-left" }, clock: clock);
        IReadOnlyList<ViewCell>? record = null;
        if (edge) record = await camera.EnsureEdgesAsync(B(s["skip"]!), B(s["reverse"]!), s["preset"] is { } preset ? Cell(preset) : null, new(3, 2));
        else if (action == "focus") await camera.FocusAsync(new(7, 6), default);
        else await camera.CenterAsync(0, default);
        Check(source.Captures == I(expected["captures"]!), "Control screenshot count");
        Equal(Pair(camera.Position.Column, camera.Position.Row), expected["position"], "Control final camera");
        if (edge) Equal(new JsonArray(record!.Select(v => (JsonNode)Pair(v)).ToArray()), expected["record"], "Edge swipe records");
        var trace = expected["trace"]!.AsArray();
        Check(input.Gestures.Count == trace.Count, "Control gesture count");
        for (int i = 0; i < trace.Count; i++)
        { Near(input.Gestures[i].Pixels, trace[i]!["pixels"]!, "Pixel gesture"); Check(input.Gestures[i].Box == ReadArea(trace[i]!["box"]!), "Gesture bounds"); }
        var observation = await camera.ObserveAsync(MapScanMode.Init, default);
        Check(observation.Camera == camera.Position && observation.LocalCenter == camera.View.Geometry.Center &&
            observation.Mode == MapScanMode.Init && observation.Cells.Count == 9, "Camera did not feed geometry into observation");
    }

    private static async Task PixelChecksAsync(JsonNode pixels, string python, string artifacts, AssetFiles files)
    {
        await using var vision = new PythonTemplateVision(python, Path.Combine(AppContext.BaseDirectory, "Imaging/Worker/vision_worker.py"));
        var recognition = new GridRecognition(vision, files, GameServer.Cn, new());
        async Task<ScreenFrame> Frame(string name, long sequence) => new(sequence, DateTimeOffset.UnixEpoch, await File.ReadAllBytesAsync(Path.Combine(artifacts, name)));
        var old = await Frame("pair-0.png", 1); var next = await Frame("pair-1.png", 2); var mask = await Frame("pair-mask.png", 3);
        var imageSource = new MapViewSource(_ => ValueTask.FromResult(old with { Sequence = 17 }),
            new GridDetector(vision, files, new MapDetectionRules()));
        var masked = await imageSource.CaptureImageAsync(default);
        var expectedMask = await vision.MaskAsync(old with { Sequence = 17 },
            await files.ReadAsync(MapDetectionAssets.Mask, default), MapDetectionAssets.MaskOrigin, default);
        Check(masked.Sequence == 17 && masked.Png.Span.SequenceEqual(expectedMask.Png.Span),
            "Image-only map capture did not apply the native UI mask");
        var evidence = new MapSwipeEvidence(recognition, vision, vision, mask, new(123, 55));
        foreach (var item in pixels.AsArray())
        {
            var a = new VisibleGrid(new(0, 0), Corners(item!["old"]!)); var b = new VisibleGrid(new(0, 0), Corners(item["new"]!));
            var geometry = Regular(new(262, 227.5));
            var v1 = new MapViewFrame(old, geometry); var v2 = new MapViewFrame(next, geometry);
            var marker = await evidence.FleetAsync(v1, a, default);
            Check(marker.Fleet == B(item["marker"]![0]!) && marker.Current == B(item["marker"]![1]!), "Raw pixel fleet marker differs");
            var value = await evidence.SimilarityAsync(v1, a, v2, b, default);
            Check(value.HasValue == B(item["valid"]!), "Native detection-mask gate differs");
            if (value is { } score) Near(score, D(item["score"]!), "Actual CV pair score");
            Check(value > 0.9 == B(item["match"]!), "Actual CV pair threshold differs");
        }
        bool rejected = false;
        try { await vision.CompareAsync(old, next, new(Area, new(73, 60), Area, new(72, 72))); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "Invalid pair dimensions reached worker");
        string wrongWorker = Path.Combine(artifacts, "wrong_pair_worker.py");
        await File.WriteAllTextAsync(wrongWorker, """
            import json, sys
            for line in sys.stdin:
                request = json.loads(line)
                print(json.dumps(dict(protocol='alas-cv/1', id=request['id'], frame=request['frame'],
                    second_frame=request['second_frame'] + 1, value=0.99)), flush=True)
            """);
        await using var wrong = new PythonTemplateVision(python, wrongWorker);
        rejected = false;
        try { await wrong.CompareAsync(old, next, new(new(0, 0, 5, 5), new(5, 5), new(0, 0, 7, 7), new(7, 7))); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Pair measurement accepted a wrong second frame");
    }

    private static async Task OptimizationAsync(JsonNode entry, ScreenFrame frame, GridRecognition recognition)
    {
        var sample = entry["sample"]!; var expected = entry["expected"]!;
        var map = Map(); var geometry = Regular(new(262, 227.5)); var position = new Cell(5, 4);
        var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        foreach (var p in sample["globals"]!.AsArray())
        {
            var cell = Cell(p!["cell"]!); var target = map[new(cell.X + 1, cell.Y + 1)];
            if (p["state"]!["is_land"] is { } land) target.IsLand = B(land);
            if (p["state"]!["is_current_fleet"] is { } fleet) target.IsCurrentFleet = B(fleet);
        }
        var observations = geometry.Grids.Select(grid => new MapCellObservation(grid.LocalCell,
            sample["patches"]!.AsArray().SingleOrDefault(p => Cell(p!["cell"]!) == grid.LocalCell)?["state"]?.Deserialize<CellObservation>(json) ?? new())).ToArray();
        var areas = MapCamera.SwipeAreas(map, position, geometry, new(observations, position, geometry.Center), new(1, -1));
        static string Key(PixelArea a) => $"{a.X},{a.Y},{a.Width},{a.Height}";
        Check(areas.Preferred.Select(Key).Order().SequenceEqual(expected["preferred"]!.AsArray().Select(a => Key(ReadArea(a!))).Order()),
            "Native swipe preferred endpoints differ");
        Check(areas.Forbidden.Select(Key).Order().SequenceEqual(expected["forbidden"]!.AsArray().Select(a => Key(ReadArea(a!))).Order()),
            "Native swipe forbidden regions differ");
        var clock = new FakeClock(); var input = new Input();
        var view = new MapViewFrame(frame, geometry);
        var source = new Source(i => view with { Frame = frame with { Sequence = i + 1 } }, clock);
        var method = sample["method"]!.GetValue<string>() switch { "minitouch" => MapControlMethod.Minitouch,
            "MaaTouch" => MapControlMethod.MaaTouch, _ => MapControlMethod.Adb };
        var camera = new MapCamera(map, position, view, source, input, recognition, new(new FixedEvidence(null)),
            new() { Predict = false }, method, clock: clock);
        await camera.FocusAsync(new(6, 3), default);
        Check(input.Gestures.Count == 1 && input.Gestures[0].PreferredEnds is not null && input.Gestures[0].ForbiddenAreas is not null,
            "Default camera omitted swipe avoidance");
        Near(input.Gestures[0].Pixels, expected["pixels"]!, "Native device multiplier");
    }

    private static async Task NegativeChecksAsync(ScreenFrame frame, GridRecognition recognition)
    {
        var view = new MapViewFrame(frame, Regular(new(262, 227.5)));
        var clock = new FakeClock(); var input = new Input { Fail = true };
        var source = new Source(i => view, clock);
        var camera = new MapCamera(Map(), new(5, 4), view, source, input, recognition, new(new FixedEvidence(null)),
            new() { Optimize = false }, clock: clock);
        bool failed = false;
        try { await camera.FocusAsync(new(6, 4), default); } catch (IOException) { failed = true; }
        Check(failed && input.Gestures.Count == 1, "Gesture failure was hidden");
        failed = false;
        try { await camera.ObserveAsync(MapScanMode.Normal, default); } catch (InvalidOperationException) { failed = true; }
        Check(failed, "Uncertain physical state remained usable");
        var state = new MapCameraState(new(9, 7), new(5, 4), view);
        state.PrepareSwipe(new(1, 0));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { await state.UpdateAsync(view, new(new FixedEvidence(null)), token: cancel.Token); } catch (OperationCanceledException) { }
        Check(state.Position == new Cell(5, 4) && state.PendingSwipe == new ViewCell(1, 0), "Cancelled update partially committed");
        failed = false;
        try { _ = new GridGeometry(new(new(0, 0), new(1, 0), new(1, 0), new(2, 0)), Tile); } catch (ArgumentException) { failed = true; }
        Check(failed, "Degenerate grid accepted");
        failed = false;
        try { _ = new MapViewGeometry([view.Geometry.Grids[0], view.Geometry.Grids[0]], Area, new(150, 140), Tile); } catch (ArgumentException) { failed = true; }
        Check(failed, "Duplicate grid accepted");

        async Task Throws<T>(Func<Task> action, string label) where T : Exception
        {
            var task = action();
            if (await Task.WhenAny(task, Task.Delay(3000)) != task) throw new InvalidOperationException("Test watchdog: " + label);
            try { await task; } catch (T) { return; }
            throw new InvalidOperationException(label);
        }
        var stale = new MapCamera(Map(), new(5, 4), view, source, new Input(), recognition, new(new FixedEvidence(null)),
            new() { Optimize = false, Predict = false }, clock: clock);
        await Throws<InvalidDataException>(() => stale.FocusAsync(new(6, 4), default).AsTask(), "Stale image was accepted");

        var blocked = new BlockingSource();
        var limited = new MapCamera(Map(), new(5, 4), view, blocked, new Input(), recognition, new(new FixedEvidence(null)),
            new() { Optimize = false }, timeout: TimeSpan.FromMilliseconds(200));
        await Throws<TimeoutException>(() => limited.FocusAsync(new(6, 4), default).AsTask(), "Camera deadline was ignored");
        var cancelSource = new BlockingSource();
        var cancellable = new MapCamera(Map(), new(5, 4), view, cancelSource, new Input(), recognition, new(new FixedEvidence(null)),
            new() { Optimize = false });
        using var abort = new CancellationTokenSource(200);
        await Throws<OperationCanceledException>(() => cancellable.FocusAsync(new(6, 4), abort.Token).AsTask(), "Cancellation became timeout");

        var serialSource = new BlockingSource();
        var serial = new MapCamera(Map(), new(5, 4), view, serialSource, new Input(), recognition, new(new FixedEvidence(null)),
            new() { Optimize = false });
        using var firstCancel = new CancellationTokenSource();
        var first = serial.FocusAsync(new(6, 4), firstCancel.Token).AsTask();
        await serialSource.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using var waiterCancel = new CancellationTokenSource(100);
        await Throws<OperationCanceledException>(() => serial.ObserveAsync(MapScanMode.Normal, waiterCancel.Token).AsTask(), "Queued camera read ignored cancellation");
        firstCancel.Cancel();
        await Throws<OperationCanceledException>(() => first, "Owner camera read ignored cancellation");
        Check(serialSource.Captures == 1, "Camera cancellation admitted overlapping capture");
    }

    private sealed class BlockingSource : IMapViewSource
    {
        public int Captures;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<MapViewFrame> CaptureAsync(CancellationToken token)
        {
            Captures++; Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Blocking source unexpectedly resumed");
        }
        public ValueTask<ScreenFrame> CaptureImageAsync(CancellationToken token)
            => throw new InvalidOperationException("Unexpected image-only capture");
    }
    private sealed class SettlingSource(ScreenFrame frame, JsonNode sample, FakeClock clock) : IMapViewSource
    {
        public int Captures;
        public ValueTask<MapViewFrame> CaptureAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            clock.Advance(D(sample["delays"]![Captures]!));
            var geometry = Regular(Point(sample["screens"]![Captures]!));
            return ValueTask.FromResult(new MapViewFrame(frame with { Sequence = ++Captures + 1 }, geometry));
        }
        public ValueTask<ScreenFrame> CaptureImageAsync(CancellationToken token)
            => throw new InvalidOperationException("Unexpected image-only capture");
    }
    private static async Task SettlingAsync(JsonNode entry, ScreenFrame frame, GridRecognition recognition)
    {
        var sample = entry["sample"]!; var expected = entry["expected"]!; var clock = new FakeClock();
        var source = new SettlingSource(frame, sample, clock);
        var camera = new MapCamera(Map(), new(5, 4), new(frame, Regular(new(262, 227.5))), source, new Input(), recognition,
            new(new FixedEvidence(null)), new() { Optimize = false, Predict = false }, clock: clock);
        await camera.FocusAsync(new(6, 4), default);
        Check(source.Captures == I(expected["captures"]!), "Native swipe settling screenshot count differs");
        Equal(Pair(camera.Position.Column, camera.Position.Row), expected["position"], "Native settled camera");
    }
}
