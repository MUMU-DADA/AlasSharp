using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Imaging;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static class ProfileChecks
{
    public static async Task RunAsync(string python, string upstream, string artifacts)
    {
        int stages = 0, profiles = 0, entrances = 0;
        ScreenFrame? last = null;
        var assets = new AssetFiles(Path.Combine(upstream, "assets"));
        await using var vision = new PythonTemplateVision(python, Path.Combine(AppContext.BaseDirectory, "Imaging/Worker/vision_worker.py"));
        foreach (var server in Enum.GetValues<GameServer>())
        {
            string output = Path.Combine(artifacts, $"profiles-{server}.json");
            var run = await new ProcessRunner().RunAsync(python, [Path.Combine(AppContext.BaseDirectory, "native_profile_reference.py"),
                upstream, server.ToString().ToLowerInvariant(), output], TimeSpan.FromSeconds(90));
            Check(run.ExitCode == 0, "Native profile oracle: " + run.Error);
            var data = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
            foreach (var sample in data["stages"]!.AsArray())
            {
                var frame = new ScreenFrame(stages + 1, DateTimeOffset.UnixEpoch, await File.ReadAllBytesAsync(Path.Combine(artifacts, sample!["image"]!.GetValue<string>())));
                var value = await new StageEntranceDetector(vision, assets, server).FindAsync(frame, (StageEntranceKind)sample["kinds"]!.GetValue<int>(), default);
                static int[] Rect(Rectangle r) => [r.Left, r.Top, r.Right, r.Bottom];
                var actual = JsonSerializer.SerializeToNode(value.Select(v => new { icon = Rect(v.Icon), name = Rect(v.Name) }));
                if (!JsonNode.DeepEquals(actual, sample["expected"]))
                {
                    await File.WriteAllTextAsync(Path.Combine(artifacts, "profile-mismatch.json"), new JsonObject { ["sample"] = sample.DeepClone(), ["actual"] = actual }.ToJsonString());
                    throw new InvalidOperationException("Native stage extraction differs: " + sample["image"]);
                }
                stages++; entrances += value.Count; last = frame;
            }
            foreach (var sample in data["profiles"]!.AsArray())
            {
                var frame = new ScreenFrame(++profiles, DateTimeOffset.UnixEpoch, await File.ReadAllBytesAsync(Path.Combine(artifacts, sample!["image"]!.GetValue<string>())));
                var count = await new MapUiObservations(() => frame, vision, assets, server).InfoBarCountAsync(default);
                Check(count == sample["count"]!.GetValue<int>(), "Native info bar count differs: " + sample["image"]);
            }
        }
        Check(entrances >= 100, "Profile oracle did not exercise positive entrance extraction");
        await NegativeAsync(python, artifacts, last!, await assets.ReadAsync(UiAssets.Template.TEMPLATE_STAGE_CLEAR.For(GameServer.Cn)));
        Console.WriteLine($"Native profiles: {stages} stage images / {entrances} entrance rectangles / {profiles} info-bar images / 9 malformed protocol cases passed.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task NegativeAsync(string python, string artifacts, ScreenFrame frame, ReadOnlyMemory<byte> template)
    {
        string path = Path.Combine(artifacts, "bad_profile_worker.py");
        var cases = new[] { ("peaks", "[-1]"), ("peaks", "[2,1]"), ("peaks", "[1,1]"),
            ("means", "[255]"), ("means", "[256]*10"), ("means", "[-1]*10"),
            ("points", "[[-1,0]]"), ("points", "[[20,0]]"), ("points", "[[0]]") };
        foreach (var (kind, value) in cases)
        {
            await File.WriteAllTextAsync(path, "import json,sys\nfor line in sys.stdin:\n r=json.loads(line)\n print(json.dumps(dict(protocol='alas-cv/1',id=r['id'],frame=r['frame'],size=[2,2]," + kind + "=" + value + ")),flush=True)\n");
            await using var broken = new PythonTemplateVision(python, path);
            bool failed = false;
            try
            {
                if (kind == "peaks") await broken.ColorRowPeaksAsync(frame, new(0, 0, 10, 10), new(1, 2, 3), 235, 50, 50, default);
                else if (kind == "means") await broken.LetterColumnMeansAsync(frame, new(0, 0, 10, 10), new(255, 255, 255), 128, default);
                else await broken.TemplatePointsAsync(frame, new(new(0, 0, 10, 10), template), default);
            }
            catch (InvalidDataException) { failed = true; }
            Check(failed, "Malformed profile accepted: " + kind + value);
            failed = false;
            try { await broken.MeanColorAsync(frame, new(0, 0, 1, 1)); }
            catch (IOException) { failed = true; }
            Check(failed, "Malformed profile left protocol session reusable");
        }
    }
}
