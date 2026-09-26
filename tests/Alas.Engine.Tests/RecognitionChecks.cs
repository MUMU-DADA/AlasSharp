using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Imaging;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static class RecognitionChecks
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    public static async Task RunAsync(string python, string upstream, string artifacts)
    {
        foreach (var source in new[] { GridRecognition.Source, GridRecognitionRules.Source, UiAssets.Template.Source })
            if (Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(upstream, source.Path)))) != source.Sha256)
                throw new InvalidOperationException("Grid recognition source drifted: " + source.Path);
        string output = Path.Combine(artifacts, "native-recognition.json");
        var result = await new ProcessRunner().RunAsync(python,
            [Path.Combine(AppContext.BaseDirectory, "native_recognition_reference.py"), upstream, output], TimeSpan.FromSeconds(90));
        if (result.ExitCode != 0) throw new InvalidOperationException("Native grid recognition failed: " + result.Error);
        var reference = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
        string worker = Path.Combine(AppContext.BaseDirectory, "Imaging/Worker/vision_worker.py");
        var files = new AssetFiles(Path.Combine(upstream, "assets"));
        await using var vision = new PythonTemplateVision(python, worker);
        long sequence = 0;
        int cases = 0, patches = 0, views = 0;
        var positive = new HashSet<string>();
        foreach (var item in reference["cases"]!.AsArray())
        {
            var options = item!["options"]!.Deserialize<GridRecognitionRules>(Json)!;
            options = options with { Enemies = options.Enemies.Select(e => e.Genre == "Light" ? e with { Scales = [0.8, 1, 1.2] } : e).ToImmutableArray() };
            var points = item["corners"]!.AsArray().Select(p => new ScreenPoint(p![0]!.GetValue<double>(), p[1]!.GetValue<double>())).ToArray();
            var corners = new GridCorners(points[0], points[1], points[2], points[3]);
            var frame = new ScreenFrame(++sequence, DateTimeOffset.UtcNow, await File.ReadAllBytesAsync(Path.Combine(artifacts, item["image"]!.GetValue<string>())));
            var predictor = new GridRecognition(vision, files, GameServer.Cn, options);
            var actual = await predictor.PredictAsync(frame, corners);
            var serialized = JsonSerializer.SerializeToNode(actual, Json);
            if (!JsonNode.DeepEquals(serialized, item["expected"]))
                throw new InvalidOperationException($"Grid recognition differs: {item["name"]}\nExpected: {item["expected"]}\nActual: {serialized}");
            foreach (var value in serialized!.AsObject())
                if (value.Value is JsonValue scalar && scalar.TryGetValue<bool>(out bool flag) && flag) positive.Add(value.Key);
            if (actual.EnemyScale > 0) positive.Add("enemy_scale");
            if (actual.EnemyGenre is not null and not "Enemy" and not "") positive.Add("enemy_genre");
            // Exercise the actual pixel -> observation -> global map state seam.
            var observed = await predictor.ObserveAsync(frame, [new(new(1, 1), corners)], new(2, 1), new(1, 1));
            var state = new CampaignState(new MapDefinition("C1", "-- ME --", [], [], []));
            var merged = state.ApplyObservation(observed);
            var expected = new CellState(new(2, 1), MapTile.Enemy);
            expected.Merge(actual);
            if (!merged.Accepted || state[new(2, 1)].Encode() != expected.Encode() || state[new(1, 1)].IsEnemy || state[new(3, 1)].IsEnemy)
                throw new InvalidOperationException("Pixel observations did not feed the expected global cell");
            cases++; views++;
        }
        var noise = new ScreenFrame(++sequence, DateTimeOffset.UtcNow, await File.ReadAllBytesAsync(Path.Combine(artifacts, "noise.png")));
        foreach (var item in reference["patches"]!.AsArray())
        {
            int[] area = item!["area"]!.Deserialize<int[]>()!, size = item["size"]!.Deserialize<int[]>()!, color = item["color"]!.Deserialize<int[]>()!;
            var request = new ImagePatchRequest(new(area[0], area[1], area[2], area[3]), size[0], size[1],
                PatchMeasure.SimilarityCount, PatchProcessing.ColorSimilarity, new(color[0], color[1], color[2]),
                MinimumSimilarity: item["minimum"]!.GetValue<int>());
            var count = await vision.MeasurePatchAsync(noise, request);
            if (count.Value != item["counts"]![0]!.GetValue<int>()) throw new InvalidOperationException("RGB patch count differs");
            count = await vision.MeasurePatchAsync(noise, request with { Measure = PatchMeasure.HsvCount, Hsv = new(138, 151) });
            if (count.Value != item["counts"]![1]!.GetValue<int>()) throw new InvalidOperationException("HSV patch count differs");
            var bytes = await File.ReadAllBytesAsync(Path.Combine(artifacts, item["template"]!.GetValue<string>()));
            var matched = await vision.MeasurePatchAsync(noise, request with { Measure = PatchMeasure.Template, Processing = PatchProcessing.Gray, Template = bytes });
            if (Math.Abs(matched.Value - item["score"]!.GetValue<double>()) > 1e-6) throw new InvalidOperationException("Gray patch template differs");
            patches += 3;
        }
        var templateNames = new Dictionary<string, string>();
        foreach (var asset in UiAssets.All.Where(a => a.Kind == AssetKind.Template && a.Id.StartsWith("module.template.assets.", StringComparison.Ordinal)))
        {
            var bytes = await files.ReadAsync(asset.For(GameServer.Cn));
            templateNames.TryAdd(Convert.ToHexString(SHA256.HashData(bytes.Span)), asset.Id.Split('.').Last());
        }
        int traced = 0, measurements = 0;
        foreach (var item in reference["traced"]!.AsArray())
        {
            var options = item!["options"]!.Deserialize<GridRecognitionRules>(Json)!;
            options = options with { Enemies = options.Enemies.Select(e => e.Genre == "Light" ? e with { Scales = [0.8, 1, 1.2] } : e).ToImmutableArray() };
            var script = new ScriptedPatches(item, templateNames);
            var predictor = new GridRecognition(script, files, GameServer.Cn, options);
            var actual = await predictor.PredictAsync(noise, new(new(240, 420), new(360, 420), new(240, 540), new(360, 540)));
            if (!JsonNode.DeepEquals(JsonSerializer.SerializeToNode(actual, Json), item["expected"]) || !JsonNode.DeepEquals(script.Trace, item["trace"]))
                throw new InvalidOperationException($"Native recognition control flow differs at sample {traced}:\n{script.Trace}\n{item["trace"]}");
            traced++; measurements += script.Trace.Count;
        }
        var required = new[] { "is_siren", "is_current_fleet", "is_boss", "is_enemy", "is_fleet", "is_missile_attack", "is_mystery", "is_submarine", "enemy_scale", "enemy_genre" };
        if (required.Any(field => !positive.Contains(field))) throw new InvalidOperationException("Missing positive pixel cases: " + string.Join(", ", required.Except(positive)));
        await NegativeChecks(files, noise);
        Console.WriteLine($"Native grid recognition: {cases} pixel scenarios / {views} complete view merges / {patches} primitive measurements / {traced} threshold scenarios ({measurements} ordered measurements). All active flags have positive pixels. Synthetic pixels and supplied geometry only; no device or line detection verification.");
    }

    private sealed class ScriptedPatches(JsonNode sample, Dictionary<string, string> templates) : IImagePatchVision
    {
        public JsonArray Trace { get; } = [];
        public ValueTask<ImagePatchObservation> MeasurePatchAsync(ScreenFrame frame, ImagePatchRequest request, CancellationToken token = default)
        {
            string key = request.Measure == PatchMeasure.Template ? templates[Convert.ToHexString(SHA256.HashData(request.Template.Span))] :
                request.Measure == PatchMeasure.HsvCount ? request.Hsv.HLow == 138 ? "current" : "small" :
                request.Color == new PixelColor(255, 150, 24) ? "orange" : request.Color == new PixelColor(148, 255, 247) ? "mystery" : "missile";
            double value = sample[request.Measure == PatchMeasure.Template ? "scores" : "numbers"]![key]!.GetValue<double>();
            Trace.Add(new JsonArray(JsonValue.Create(key), JsonValue.Create(value)));
            return ValueTask.FromResult(new ImagePatchObservation(frame.Sequence, value));
        }
    }

    private static async Task NegativeChecks(AssetFiles files, ScreenFrame frame)
    {
        var counter = new BadPatches();
        var predictor = new GridRecognition(counter, files, GameServer.Cn, new());
        var valid = new GridCorners(new(240, 420), new(360, 420), new(240, 540), new(360, 540));
        try { await predictor.PredictAsync(frame, valid with { TopRight = new(double.NaN, 420) }); throw new InvalidOperationException("Invalid geometry accepted"); }
        catch (ArgumentException) { }
        try { await predictor.ObserveAsync(frame, [new(new(0,0), valid), new(new(0,0), valid)], new(1,1), new(0,0)); throw new InvalidOperationException("Duplicate geometry accepted"); }
        catch (ArgumentException) { }
        if (counter.Calls != 0) throw new InvalidOperationException("Invalid view reached vision worker");
        try { await predictor.PredictAsync(frame, valid); throw new InvalidOperationException("Wrong measurement frame accepted"); }
        catch (InvalidDataException) { }
    }
    private sealed class BadPatches : IImagePatchVision
    {
        public int Calls { get; private set; }
        public ValueTask<ImagePatchObservation> MeasurePatchAsync(ScreenFrame frame, ImagePatchRequest request, CancellationToken token = default)
        {
            Calls++;
            return ValueTask.FromResult(new ImagePatchObservation(frame.Sequence + 1, 1));
        }
    }
}
