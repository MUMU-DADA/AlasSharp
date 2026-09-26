using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Tests;

internal static class RecoveryChecks
{
    public static async Task RunAsync(string python, string upstream, string artifacts)
    {
        int cases = 0, steps = 0, images = 0, resets = 0;
        var graph = UpstreamPages.Create();
        foreach (var source in new[] { UiRecovery.UiSource, UiRecovery.InfoSource })
            if (Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(upstream, source.Path)))) != source.Sha256)
                throw new InvalidOperationException("Recovery source changed: " + source.Path);
        foreach (var server in Enum.GetValues<GameServer>())
        {
            string output = Path.Combine(artifacts, $"native-recovery-{server.ToString().ToLowerInvariant()}.json");
            var result = await new ProcessRunner().RunAsync(python,
                [Path.Combine(AppContext.BaseDirectory, "native_recovery_reference.py"), upstream, server.ToString().ToLowerInvariant(), output], TimeSpan.FromSeconds(90));
            if (result.ExitCode != 0) throw new InvalidOperationException("Native recovery reference failed: " + result.Error);
            var native = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
            foreach (var scenario in native["cases"]!.AsArray())
            {
                var options = scenario!["options"]!;
                var driver = new RecoveryDriver(server);
                var health = new HealthProbe(driver);
                var recovery = new UiRecovery(driver, health, graph, new UiRecoveryOptions(
                    StoryOption: options["selection"]?.GetValue<int>() ?? 0, StoryAllowSkip: options["allowSkip"]?.GetValue<bool>() ?? true,
                    MapIsThreatSafe: options["threatSafe"]?.GetValue<bool>() ?? false, CampaignEvent: options["campaign"]?.GetValue<string>() ?? ""));
                int index = 0;
                foreach (var frame in scenario["frames"]!.AsArray())
                {
                    driver.Prepare(frame!, native["options"]!.AsArray());
                    bool? value = null;
                    string? error = null;
                    try
                    {
                        value = scenario["operation"]!.GetValue<string>() == "story" ? await recovery.StorySkipAsync()
                            : await recovery.AdditionalAsync(frame!["getShip"]?.GetValue<bool>() ?? true, default);
                    }
                    catch (GameNotRunningException) { error = "GameNotRunningError"; }
                    catch (HumanTakeoverRequiredException) { error = "RequestHumanTakeover"; }
                    var actual = JsonSerializer.SerializeToNode(new { value, error, events = driver.Events });
                    var expected = scenario["outputs"]![index];
                    if (!JsonNode.DeepEquals(actual, expected))
                    {
                        await File.WriteAllTextAsync(Path.Combine(artifacts, "recovery-mismatch.json"),
                            new JsonObject { ["scenario"] = scenario.DeepClone(), ["index"] = index, ["actual"] = actual }.ToJsonString());
                        throw new InvalidOperationException($"Native recovery differs: {server}/{scenario["name"]}/{index}; inspect local recovery-mismatch.json");
                    }
                    index++; steps++;
                }
                cases++;
            }
            var assets = UiAssets.All.ToDictionary(a => a.Id);
            foreach (var sample in native["resets"]!.AsArray())
            {
                var driver = new RecoveryDriver(server);
                new UiRecovery(driver, new HealthProbe(driver), graph, new UiRecoveryOptions()).ResetIntervalsAfterClick(assets[sample!["asset"]!.GetValue<string>()]);
                if (!JsonNode.DeepEquals(JsonSerializer.SerializeToNode(driver.Events), sample["events"]) ||
                    !JsonNode.DeepEquals(JsonSerializer.SerializeToNode(driver.Timers.ToDictionary(t => t.Key, t => t.Value.Seconds)), sample["timers"]))
                    throw new InvalidOperationException("Native interval reset differs: " + sample["asset"]);
                resets++;
            }
            await using var vision = new PythonTemplateVision(python, Path.Combine(AppContext.BaseDirectory, "Imaging/Worker/vision_worker.py"));
            foreach (var sample in native["options"]!.AsArray())
            {
                var frame = new ScreenFrame(++images, DateTimeOffset.UtcNow,
                    await File.ReadAllBytesAsync(Path.Combine(artifacts, sample!["image"]!.GetValue<string>())));
                var driver = new UiDriver(server, new ImageDevice(frame), vision, new AssetFiles(Path.Combine(upstream, "assets")));
                await driver.ScreenshotAsync(default);
                var recovery = new UiRecovery(driver, new HealthProbe(new RecoveryDriver(server)), graph, new UiRecoveryOptions());
                var rectangles = await recovery.StoryOptionsAsync();
                if (!JsonNode.DeepEquals(JsonSerializer.SerializeToNode(rectangles.Select(r => new[] { r.Left, r.Top, r.Right, r.Bottom })), sample["rectangles"]))
                    throw new InvalidOperationException("Native story option geometry differs");
            }
        }
        Console.WriteLine($"Native recovery: {cases} scenarios / {steps} state transitions / {resets} button interval resets / {images} pure-CV option images passed.");
    }
}

internal sealed class RecoveryDriver(GameServer server) : AppearanceProbe(server, null)
{
    public List<object[]> Events { get; } = [];
    public Dictionary<string, IntervalTimer> Timers { get; } = new();
    private HashSet<string> _positive = [];
    private IReadOnlyList<ColorBand> _bands = [];
    private int _gray = 128;
    private bool _disappear;
    public bool Running { get; private set; } = true;
    public void Prepare(JsonNode frame, JsonArray images)
    {
        Events.Clear();
        Time.Advance(frame["advance"]?.GetValue<double>() ?? 1);
        _positive = frame["positive"]?.AsArray().Select(n => n!.GetValue<string>()).ToHashSet() ?? [];
        _gray = frame["gray"]?.GetValue<int>() ?? 128;
        _disappear = frame["disappear"]?.GetValue<bool>() ?? false;
        Running = frame["running"]?.GetValue<bool>() ?? true;
        int count = frame["options"]?.GetValue<int>() ?? 0;
        var rectangles = images[count * 2]!["rectangles"]!.AsArray();
        _bands = rectangles.Select(r => new ColorBand(r![1]!.GetValue<int>() - 140, r[3]!.GetValue<int>() - 130)).ToArray();
    }
    public override ValueTask<bool> AppearsAsync(AssetRule asset, ButtonOffset offset = default, double interval = 0,
        double similarity = 0.85, int threshold = 10, TemplatePreprocessing preprocessing = TemplatePreprocessing.Color, CancellationToken token = default)
    {
        Events.Add(["appear", asset.Id, new[] { offset.Left, offset.Top, offset.Right, offset.Bottom }, interval, similarity, threshold, preprocessing.ToString()]);
        if (interval > 0 && !Timer(asset, interval, true).Reached()) return ValueTask.FromResult(false);
        bool value = _positive.Contains(asset.Id);
        if (value && interval > 0) Timer(asset).Reset();
        return ValueTask.FromResult(value);
    }
    public override ValueTask ClickAsync(AssetRule asset, CancellationToken token) { Events.Add(["click", asset.Id]); return ValueTask.CompletedTask; }
    public override ValueTask ClickAreaAsync(Rectangle area, CancellationToken token) { Events.Add(["click_area", new[] { area.Left, area.Top, area.Right, area.Bottom }]); return ValueTask.CompletedTask; }
    public override ValueTask<MeanColorObservation> ColorAsync(Rectangle area, CancellationToken token)
    { Events.Add(["color"]); return ValueTask.FromResult(new MeanColorObservation(1, _gray, _gray, _gray)); }
    public override ValueTask<ColorBandObservation> ColorBandsAsync(ColorBandRequest request, CancellationToken token)
    { Events.Add(["bands"]); return ValueTask.FromResult(new ColorBandObservation(1, _bands)); }
    public override ValueTask ScreenshotAsync(CancellationToken token) { Events.Add(["screenshot"]); if (_disappear) _positive.Clear(); return ValueTask.CompletedTask; }
    public override ValueTask DelayAsync(TimeSpan time, CancellationToken token) { Events.Add(["delay", time.TotalSeconds]); Time.Advance(time.TotalSeconds); return ValueTask.CompletedTask; }
    public override IntervalTimer Timer(AssetRule asset, double seconds = 5, bool renew = false)
    {
        var timer = base.Timer(asset, seconds, renew);
        Timers[asset.Name] = timer;
        return timer;
    }
    public override void ResetInterval(AssetRule asset, double seconds = 3) { Events.Add(["reset", asset.Id]); base.ResetInterval(asset, seconds); }
    public override void ClearInterval(AssetRule asset) { Events.Add(["clear", asset.Id]); base.ClearInterval(asset); }
}
internal sealed class HealthProbe(RecoveryDriver driver) : IApplicationHealth
{
    public ValueTask<bool> IsRunningAsync(CancellationToken token) { driver.Events.Add(["running"]); return ValueTask.FromResult(driver.Running); }
    public ValueTask StopAsync(CancellationToken token) { driver.Events.Add(["stop"]); return ValueTask.CompletedTask; }
    public ValueTask RefreshOrientationAsync(CancellationToken token) { driver.Events.Add(["orientation"]); return ValueTask.CompletedTask; }
}
internal sealed class ImageDevice(ScreenFrame frame) : IGameDevice
{
    public ValueTask<ScreenFrame> CaptureAsync(CancellationToken token = default) => ValueTask.FromResult(frame);
    public ValueTask TapAsync(PixelPoint point, CancellationToken token = default) => throw new InvalidOperationException("CV check cannot tap");
    public ValueTask SwipeAsync(PixelPoint start, PixelPoint end, TimeSpan duration, CancellationToken token = default) => throw new InvalidOperationException("CV check cannot swipe");
    public ValueTask BackAsync(CancellationToken token = default) => throw new InvalidOperationException("CV check cannot press back");
}
