using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Imaging;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static partial class MapViewChecks
{
    public static async Task BossRefocusChecksAsync(string python, string upstream, string artifacts)
    {
        string output = Path.Combine(artifacts, "boss-refocus-native.json");
        var result = await new ProcessRunner().RunAsync(python,
            [Path.Combine(AppContext.BaseDirectory, "native_boss_refocus_reference.py"), upstream, output], TimeSpan.FromMinutes(2));
        Check(result.ExitCode == 0, "Native boss refocus replay failed: " + result.Error);
        var native = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
        foreach (var source in new[] { MapCombatRecovery.Source, MapCameraState.Source, MapCameraRules.Source })
            Check(native["sources"]![source.Path]!.GetValue<string>() == source.Sha256, "Boss refocus source drifted");
        var recognition = new GridRecognition(new EmptyPatches(), new AssetFiles(Path.Combine(upstream, "assets")), GameServer.Cn, new());
        // Patch measurements and view polygons are supplied directly; no CV claim is made here.
        var frame = new ScreenFrame(1, DateTimeOffset.UnixEpoch, Array.Empty<byte>());
        var initial = new MapViewFrame(frame, Regular(new(262, 227.5)));
        foreach (var entry in native["results"]!.AsArray())
        {
            var sample = entry!["sample"]!; var start = Cell(sample["start"]!);
            var clock = new FakeClock();
            int successes = 0;
            string failure = sample["failure"]!.GetValue<string>();
            var source = new Source(i =>
            {
                if (failure == "io") throw new IOException("Synthetic capture error");
                if (failure is "geometry" or "swipe" && i <= 13 || failure == "transient" && i <= 2 ||
                    failure == "edge" && successes == 1 || failure == "focus" && successes == 2)
                    throw new MapGeometryException("Synthetic geometry error");
                successes++;
                return new(frame with { Sequence = i + 1 }, Regular(new(262, 227.5),
                    new(Left: successes is 1 or 2, Lower: successes == 2)));
            }, clock);
            var input = new Input { Fail = failure == "swipe" };
            var camera = new MapCamera(Map(), new(start.X, start.Y), initial, source, input, recognition,
                new(new FixedEvidence(null)), new() { Optimize = false, Predict = false, EdgeCorner = "bottom-left" }, clock: clock);
            var configured = Cell(sample["config"]!);
            var selected = sample["preset"] is { } preset ? Cell(preset) : configured;
            camera.Suspend();
            string? error = null;
            try { await camera.RefocusBossAsync((selected.X, selected.Y)); }
            catch (MapGeometryException) { error = "geometry"; }
            catch (IOException) { error = "io"; }
            Check(error == entry["error"]?.GetValue<string>() && source.Captures == I(entry["frames"]!),
                $"Boss refocus failure/timer differs: {sample}, {error}/{source.Captures}, expected={entry}");
            Equal(Pair(camera.Position.Column, camera.Position.Row), entry["position"], "Boss camera position");
            var swipes = entry["swipes"]!.AsArray();
            Check(input.Gestures.Count == swipes.Count, "Boss refocus made a missing/extra gesture: " + sample);
            for (int i = 0; i < swipes.Count; i++) Near(input.Gestures[i].Pixels, swipes[i]!, "Boss refocus gesture");
            if (error is null)
            {
                Check(camera.Position == new Alas.Engine.Rules.Cell(start.X, start.Y), "Boss refocus did not restore saved camera");
                _ = await camera.ReadCenterMarkerAsync();
            }
            else
            {
                bool refused = false;
                try { await camera.ObserveAsync(MapScanMode.Normal, default); } catch (InvalidOperationException) { refused = true; }
                Check(refused, "Failed boss refocus left a reusable camera");
            }
        }
        await BossRefocusFailuresAsync(initial, recognition);
        await CampaignMapCombatChecks.BossRefocusExecutionChecksAsync(native["gates"]!.AsArray());
        Console.WriteLine($"Boss refocus: {native["results"]!.AsArray().Count} actual native update/edge/focus traces, 28 raw-spawn gates, loop/HP/failure checks and compiled C# campaign hook passed offline; no device acceptance.");
    }

    private static async Task BossRefocusFailuresAsync(MapViewFrame initial, GridRecognition recognition)
    {
        foreach (bool cancel in new[] { false, true })
        {
            var source = new BlockingSource(); var input = new Input();
            var camera = new MapCamera(Map(), new(5, 4), initial, source, input, recognition, new(new FixedEvidence(null)),
                new() { Optimize = false }, timeout: TimeSpan.FromMilliseconds(cancel ? 5000 : 100));
            using var cancellation = new CancellationTokenSource();
            if (cancel) cancellation.CancelAfter(100);
            bool refused = false;
            try { await camera.RefocusBossAsync((-3, 0), cancellation.Token); }
            catch (OperationCanceledException) when (cancel) { refused = true; }
            catch (TimeoutException) when (!cancel) { refused = true; }
            Check(refused && input.Gestures.Count == 0 && source.Captures == 1, "Boss cancellation/timeout attempted preset fallback");
        }
        {
            var clock = new FakeClock(); var input = new Input();
            var camera = new MapCamera(Map(), new(5, 4), initial, new Source(_ => initial, clock), input, recognition,
                new(new FixedEvidence(null)), new() { Optimize = false }, clock: clock);
            bool rejected = false;
            try { await camera.RefocusBossAsync((-3, 0)); } catch (InvalidDataException) { rejected = true; }
            Check(rejected && input.Gestures.Count == 0, "Stale frame was treated as a recoverable geometry failure");
        }
    }
}
