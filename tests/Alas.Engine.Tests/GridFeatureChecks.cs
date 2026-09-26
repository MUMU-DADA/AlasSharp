using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Imaging;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static class GridFeatureChecks
{
    public static async Task<int> RunAsync(string python, string artifacts)
    {
        int checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        var frame = new ScreenFrame(12, DateTimeOffset.UnixEpoch, VisionChecks.Png(32, 24, new byte[32 * 24 * 3]));
        var template = VisionChecks.Png(5, 4, new byte[5 * 4 * 3]);
        var rules = new MapDetectionRules();
        ScreenPoint[] corners = [new(0, 0), new(32, 0), new(0, 24), new(32, 24)];
        var transform = new ProjectiveTransform(corners, corners);
        string worker = Path.Combine(artifacts, "feature_response_worker.py");
        // An isolated worker returns the supplied malformed measurement with correct identity.
        async Task Reject(JsonObject response, Func<PythonTemplateVision, Task> invoke, string label)
        {
            await File.WriteAllTextAsync(worker, "import json,sys\npayload=json.loads(" + JsonSerializer.Serialize(response.ToJsonString()) + ")\n" +
                "for line in sys.stdin:\n r=json.loads(line); print(json.dumps(dict(protocol='alas-cv/1',id=r['id'],frame=r['frame'],**payload)),flush=True)\n");
            await using var vision = new PythonTemplateVision(python, worker);
            bool rejected = false;
            try { await invoke(vision); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Malformed feature accepted: " + label);
            rejected = false;
            try { await invoke(vision); }
            catch (IOException) { rejected = true; }
            Check(rejected, "Malformed feature left reusable session: " + label);
        }
        var wrongSize = new JsonObject { ["image"] = Convert.ToBase64String(template) };
        await Reject(wrongSize, v => v.MaskAsync(frame, template, default, default).AsTask(), "masked size");
        var wrongWarp = wrongSize.DeepClone().AsObject(); wrongWarp["lines"] = new JsonArray();
        await Reject(wrongWarp, v => v.WarpAsync(frame, template, transform, new(32, 24), rules, default).AsTask(), "warped size");
        foreach (var response in new[]
        {
            "{\"maximum\":0.9,\"location\":[32,0],\"points\":[[0,0]]}",
            "{\"maximum\":0.9,\"location\":[0,0],\"points\":[[-1,0]]}",
            "{\"maximum\":0.9,\"location\":[0,0],\"points\":[]}",
            "{\"maximum\":0.8,\"location\":[0,0],\"points\":[[0,0]]}",
            "{\"maximum\":0.9,\"location\":[0,0],\"points\":[[0,0],[0,0]]}"
        })
            await Reject(JsonNode.Parse(response)!.AsObject(), v => v.CorrelateAsync(frame, template, .8, null, default).AsTask(), "correlation points");
        await Reject(JsonNode.Parse("{\"rectangles\":[[[31,0,2,2]],[],[],[],[]]}")!.AsObject(),
            v => v.RectanglesAsync(frame, default).AsTask(), "contour bounds");
        await Reject(JsonNode.Parse("{\"inner_h\":[[1,-0.5]],\"inner_v\":[],\"edge_h\":[],\"edge_v\":[]}")!.AsObject(),
            v => v.LinesAsync(frame, template, rules, default).AsTask(), "Hough angle");
        foreach (var invalid in new[]
        {
            rules with { CornerOffsets = default }, rules with { Area = new(int.MaxValue, 0, 2, 2) },
            rules with { EdgeColor = new(-1, 256) }, rules with { CenterGoodThreshold = .7 },
            rules with { Storage = new(new(0, 1), new(corners[0], corners[1], corners[2], corners[3])) },
            rules with { InternalPeaks = new(new(100, 200), Distance: .5) }
        })
        {
            bool rejected = false;
            try { invalid.Validate(); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "Invalid detector rule accepted");
        }
        return checks;
    }
}
