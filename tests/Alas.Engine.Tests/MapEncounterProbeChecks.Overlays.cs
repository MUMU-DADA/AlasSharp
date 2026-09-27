using System.Text.Json.Nodes;
using Alas.Engine.Imaging;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static partial class MapEncounterProbeChecks
{
    public static async Task<int> OverlayRulesAsync(string python, string upstream, string artifacts, GameServer server, JsonNode native)
    {
        await using var vision = new PureVisionWorker(python, Path.Combine(AppContext.BaseDirectory, "Imaging/Worker/vision_worker.py"));
        var colors = new Dictionary<string, (double Air, double Ambush)>();
        int cases = 0;
        foreach (var entry in native["chapters"]!.AsArray())
        {
            var rule = RuleCatalog.Create(entry!["id"]!.GetValue<string>());
            foreach (var sample in entry["cases"]!.AsArray())
            {
                string path = sample!["image"]!.GetValue<string>();
                if (!colors.TryGetValue(path, out var color))
                {
                    var image = new ScreenFrame(2, DateTimeOffset.UnixEpoch, await File.ReadAllBytesAsync(Path.Combine(artifacts, path)));
                    var air = await vision.MeanColorAsync(image, UiAssets.Handler.MAP_AIR_RAID.For(server).Area!.Value.Area);
                    var ambush = await vision.MeanColorAsync(image, UiAssets.Handler.MAP_AMBUSH.For(server).Area!.Value.Area);
                    colors[path] = color = (air.R, ambush.R);
                    Check(air.FrameSequence == 2 && ambush.FrameSequence == 2 &&
                        Math.Abs(air.R - sample["airRed"]!.GetValue<double>()) < .000001 &&
                        Math.Abs(ambush.R - sample["ambushRed"]!.GetValue<double>()) < .000001, "Native/CV mean colors differ");
                }
                var ui = new Ui { Server = server, AirRed = 99, AmbushRed = 99 };
                var probe = new MapEncounterProbe(ui, true, overlays: rule.Overlays);
                await probe.InitializeAsync(1, default);
                ui.Sequence = 2; ui.AirRed = color.Air; ui.AmbushRed = color.Ambush;
                var actual = await probe.InspectAsync(2, default);
                Check(actual.ToString() == sample["encounter"]!.GetValue<string>(), "Native chapter overlay decision differs: " + rule.Id + ":" + path);
                // The air-raid handler must use the same chapter threshold while waiting for disappearance.
                ui.ScreenshotReds.Enqueue(color.Air); ui.ScreenshotReds.Enqueue(color.Air);
                ui.ScreenshotReds.Enqueue(99); ui.ScreenshotReds.Enqueue(99); ui.ScreenshotReds.Enqueue(99);
                await new MapAirRaidHandler(ui, probe, () => ui.Sequence).HandleAsync(MapEncounterKind.AirRaid, default);
                Check(ui.Screenshots == sample["waitFrames"]!.GetValue<int>(),
                    $"Air-raid wait lost the chapter override: {rule.Id}:{path}, frames={ui.Screenshots}, native={sample["waitFrames"]}");
                cases++;
            }
        }
        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, -.01, 1.01 })
        {
            bool rejected = false;
            try { _ = new MapEncounterProbe(new Ui(), true, overlays: new(invalid)); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "Invalid overlay rule was accepted before actions");
        }
        // The obsolete enemy-search transparency field is preserved as source data, not applied as a new gate.
        foreach (double unused in new[] { 0d, .65, 1d })
        {
            var ui = new Ui();
            ui.Appearing.UnionWith([UiAssets.Handler.IN_MAP.Id, UiAssets.Handler.MAP_ENEMY_SEARCHING.Id]);
            var probe = new MapEncounterProbe(ui, false, mysteryHasCarrier: true, overlays: new(EnemySearching: unused));
            Check(await probe.InspectAsync(1, default) == MapEncounterKind.CarrierSpawn, "Unused native declaration altered luma enemy-search detection");
        }
        if (server == GameServer.Cn) await SessionOverlayRulesAsync(python, upstream, artifacts, native);
        return cases;
    }

    private static async Task SessionOverlayRulesAsync(string python, string upstream, string artifacts, JsonNode native)
    {
        string fixture = Path.Combine(artifacts, "session-overlay.png");
        string? previous = Environment.GetEnvironmentVariable("ALAS_TEST_MAP_FIXTURE");
        Environment.SetEnvironmentVariable("ALAS_TEST_MAP_FIXTURE", fixture);
        try
        {
            var baseline = await File.ReadAllBytesAsync(Path.Combine(artifacts, native["baseline"]!.GetValue<string>()));
            string executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Alas.Engine.Tests.exe" : "Alas.Engine.Tests");
            await using var session = new EngineSession(new(executable, "offline-map", GameServer.Cn, Path.Combine(upstream, "assets"), python));
            foreach (var entry in native["chapters"]!.AsArray())
            foreach (string image in new[] { "cn-air-137.png", "cn-air-158.png" })
            {
                string id = entry!["id"]!.GetValue<string>();
                var rule = RuleCatalog.Create(id);
                var sample = entry["cases"]!.AsArray().Single(value => value!["image"]!.GetValue<string>() == image)!;
                var overlay = await File.ReadAllBytesAsync(Path.Combine(artifacts, image));
                await File.WriteAllBytesAsync(fixture, baseline);
                await session.Driver.ScreenshotAsync(default);
                // Only composition is exercised here; the separate map tests own camera geometry/movement.
                var camera = new MapCamera(new(rule.Map, rule), new(3, 2), new(session.Driver.Frame!, MapViewChecks.Regular(new(262, 227.5))),
                    null!, null!, null!, null!, new(), correctInitialEdges: false);
                MapEncounterProbe? captured = null;
                _ = session.CreateMapArrivalCheck(camera, rule.Configure(new()), createHandler: probe =>
                { captured = probe; return new MapMysteryItemHandler(session.Driver); });
                await captured!.InitializeAsync(session.Driver.Frame!.Sequence, default);
                await File.WriteAllBytesAsync(fixture, overlay);
                await session.Driver.ScreenshotAsync(default);
                var result = await captured.InspectAsync(session.Driver.Frame!.Sequence, default);
                Check(result.ToString() == sample["encounter"]!.GetValue<string>(),
                    "EngineSession factory discarded the campaign's overlay rules: " + id);
            }
        }
        finally { Environment.SetEnvironmentVariable("ALAS_TEST_MAP_FIXTURE", previous); }
    }
}
