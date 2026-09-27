using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static class CarrierChecks
{
    internal static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public static async Task RunAsync(string python, string upstream, string artifacts)
    {
        var assets = new AssetFiles(Path.Combine(upstream, "assets"));
        await using var vision = new PureVisionWorker(python, Path.Combine(AppContext.BaseDirectory, "Imaging", "Worker", "vision_worker.py"));
        int cases = 0, fixtures = 0;
        foreach (var server in Enum.GetValues<GameServer>())
        {
            string output = Path.Combine(artifacts, server.ToString() + ".json");
            var oracle = await new ProcessRunner().RunAsync(python,
                [Path.Combine(AppContext.BaseDirectory, "native_carrier_reference.py"), upstream, server.ToString().ToLowerInvariant(), output], TimeSpan.FromMinutes(1));
            Check(oracle.ExitCode == 0, "Native carrier oracle failed: " + oracle.Error);
            var native = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
            foreach (var source in new[] { MapCarrierHandler.Source, MapEnemySearching.Source, CampaignState.InitializationSource, CombatLoadingProbe.Source })
                Check(native["sources"]![source.Path]!.GetValue<string>() == source.Sha256, "Carrier source drift: " + source.Path);
            if (server == GameServer.Cn)
            {
                foreach (var entry in native["cases"]!.AsArray())
                {
                    var ui = new Replay(entry!["sample"]!);
                    var state = new CampaignState(new("B1", "SP MM", [], [], []));
                    MapEncounterHandling? result = null;
                    string? error = null;
                    try { result = await ui.Handler(state).HandleAsync(MapEncounterKind.CarrierSpawn, default); }
                    catch (CampaignEndedException) { error = "stage"; }
                    Check(ui.Frames == entry["frames"]!.GetValue<int>() && ui.Trace.SequenceEqual(entry["trace"]!.AsArray().Select(x => x!.GetValue<string>())) &&
                        state.CarrierCount == entry["count"]!.GetValue<int>() && (result?.Continuation == MapEncounterContinuation.InMap) == entry["handled"]!.GetValue<bool>() &&
                        error == entry["error"]?.GetValue<string>() && (result?.Carrier is null || result.Carrier.IsConsistent),
                        $"Carrier handler/wait differs: {entry["sample"]}; frames={ui.Frames}; trace={string.Join(',', ui.Trace)}; error={error}");
                    cases++;
                }
                await CampaignMapCombatChecks.CarrierMovementChecksAsync(native["walks"]!.AsArray());
            }
            foreach (var fixture in native["fixtures"]!.AsArray())
            {
                var frame = new ScreenFrame(1, DateTimeOffset.UtcNow, await File.ReadAllBytesAsync(Path.Combine(artifacts, fixture!["image"]!.GetValue<string>())));
                var ui = new UiDriver(server, new FrameDevice(frame), vision, assets);
                await ui.ScreenshotAsync(default);
                Check(await MapEnemySearching.AppearsAsync(ui, default) == fixture["search"]!.GetValue<bool>() &&
                    await new CombatLoadingProbe(vision, assets, server, () => frame).ObserveAsync(default) == fixture["loading"]!.GetValue<bool>(),
                    "Carrier/search/loading original-template CV differs: " + fixture["image"]);
                fixtures++;
            }
        }
        await FailuresAsync();
        Console.WriteLine($"Carrier: {cases} native mystery/search traces, five native arrival/scan orderings, {fixtures} original-asset fixtures across four servers, movement/scan/battle and failure boundaries passed offline; no live acceptance.");
    }
    private static JsonObject Sample() => new() { ["seconds"] = .25, ["until"] = 7, ["interruption"] = null,
        ["enabled"] = true, ["inMap"] = true, ["search"] = true };
    private static async Task FailuresAsync()
    {
        foreach (string failure in new[] { "stale", "cancel", "capture", "endless" })
        {
            var sample = Sample();
            if (failure == "endless") sample["interruption"] = "event";
            var ui = new Replay(sample) { Failure = failure };
            var state = new CampaignState(new("B1", "SP MM", [], [], []));
            using var cancellation = new CancellationTokenSource();
            if (failure == "cancel") cancellation.Cancel();
            bool failed = false;
            try { await ui.Handler(state).HandleAsync(MapEncounterKind.CarrierSpawn, cancellation.Token); }
            catch (Exception error) when (error is InvalidDataException or OperationCanceledException or IOException or TimeoutException) { failed = true; }
            Check(failed && state.CarrierCount == (failure == "cancel" ? 0 : 1) && state.BattleCount == 0 && state.MysteryCount == 0,
                "Carrier wait failure erased observed spawn or invented arrival/combat: " + failure);
        }
        var replay = new Replay(Sample());
        Check((await replay.Handler(new(new("B1", "SP MM", [], [], []))).HandleAsync(MapEncounterKind.Combat, default)).Continuation == MapEncounterContinuation.Unhandled,
            "Carrier handler claimed unrelated combat");
        foreach (bool enabled in new[] { false, true })
        foreach (bool inMap in new[] { false, true })
        {
            var sample = Sample(); sample["inMap"] = inMap;
            var ui = new Replay(sample);
            var probe = new MapEncounterProbe(ui, false, mysteryHasCarrier: enabled);
            Check(await probe.InspectAsync(ui.Sequence, default) == (enabled && inMap ? MapEncounterKind.CarrierSpawn : MapEncounterKind.None),
                "Carrier observation lost its configuration/map gate");
            ui.Extra = UiAssets.Combat.GET_ITEMS_1;
            Check(await probe.InspectAsync(ui.Sequence, default) == MapEncounterKind.ItemPopup, "Carrier preempted item reward");
            ui.Extra = UiAssets.Combat.BATTLE_PREPARATION;
            Check(await probe.InspectAsync(ui.Sequence, default) == MapEncounterKind.Combat, "Carrier preempted a real combat");
        }
    }

    private sealed class Replay(JsonNode sample) : IUiDriver, IStoryHandler
    {
        private readonly TestClock _clock = new();
        public GameServer Server => GameServer.Cn;
        public bool HasFrame => true;
        public TimeProvider Clock => _clock;
        public long Sequence { get; private set; } = 1;
        public int Frames { get; private set; }
        public string? Failure { get; init; }
        public AssetRule? Extra { get; set; }
        public List<string> Trace { get; } = [];
        private string? Interruption => sample["interruption"]?.GetValue<string>();
        private bool InMap => sample["inMap"]!.GetValue<bool>() && !(Interruption == "off-map" && Frames is >= 3 and <= 5);
        private bool Interrupt(string kind)
        {
            bool visible = Interruption == kind && Frames == 3;
            if (visible) Trace.Add(kind);
            return visible;
        }
        public MapCarrierHandler Handler(CampaignState state) => new(this, state, sample["enabled"]!.GetValue<bool>(), () => Sequence,
            new(this, this, _ => Interruption == "stage" && Frames == 3 ? throw new CampaignEndedException("Synthetic stage") : ValueTask.FromResult(false),
                _ => ValueTask.FromResult(Interrupt("guild")), _ => ValueTask.FromResult(Interrupt("urgent")),
                _ => ValueTask.FromResult(Interruption == "loading" && Frames == 3), () => Sequence,
                _ => ValueTask.FromResult(Failure == "endless" || Interruption == "event" && Frames <= 4)));
        public ValueTask ScreenshotAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Failure == "capture") throw new IOException("Synthetic capture failure");
            Frames++; if (Failure != "stale") Sequence++;
            _clock.Advance(sample["seconds"]!.GetValue<double>());
            Check(Frames <= 500, "Carrier replay failed to stop");
            return ValueTask.CompletedTask;
        }
        public ValueTask<bool> AppearsAsync(AssetRule asset, ButtonOffset offset = default, double interval = 0,
            double similarity = .85, int threshold = 10, TemplatePreprocessing preprocessing = TemplatePreprocessing.Color, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (asset == UiAssets.Handler.MAP_ENEMY_SEARCHING)
                Check(offset == ButtonOffset.Expand(5, 5) && preprocessing == TemplatePreprocessing.Luma, "Enemy search lost native luma/offset semantics");
            return ValueTask.FromResult(asset == Extra || (asset == UiAssets.Handler.IN_MAP ? InMap :
                asset == UiAssets.Handler.MAP_ENEMY_SEARCHING ? InMap && sample["search"]!.GetValue<bool>() && Frames < sample["until"]!.GetValue<int>() :
                asset == UiAssets.Handler.AUTO_SEARCH_MENU_EXIT && Interruption == "auto" && Frames == 3));
        }
        public ValueTask ClickAsync(AssetRule asset, CancellationToken token)
        {
            Check(asset == UiAssets.Handler.AUTO_SEARCH_MENU_EXIT && Interrupt("auto"), "Unexpected carrier click");
            return ValueTask.CompletedTask;
        }
        public ValueTask<bool> StorySkipAsync(CancellationToken token = default) => ValueTask.FromResult(Interrupt("story"));
        public ValueTask EnsureNoStoryAsync(bool skipFirstScreenshot, CancellationToken token)
        { Check(skipFirstScreenshot, "Enemy search changed native story screenshot order"); Trace.Add("story-clear"); return ValueTask.CompletedTask; }
        public ValueTask DelayAsync(TimeSpan time, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Trace.Add("sleep:" + time.TotalSeconds.ToString("g", System.Globalization.CultureInfo.InvariantCulture)); _clock.Advance(time.TotalSeconds); return ValueTask.CompletedTask; }
        public void ResetInterval(AssetRule asset, double seconds = 3) { Check(asset == UiAssets.Handler.AUTO_SEARCH_MENU_EXIT, "Unexpected interval reset"); }
        public ValueTask ClickAreaAsync(Rectangle area, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<MeanColorObservation> ColorAsync(Rectangle area, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<ColorBandObservation> ColorBandsAsync(ColorBandRequest request, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<OcrObservation> ReadTextAsync(OcrRequest request, CancellationToken token) => throw new NotSupportedException();
        public void ClearOffset(AssetRule asset) => throw new NotSupportedException();
        public void ClearInterval(AssetRule asset) => throw new NotSupportedException();
        public IntervalTimer Timer(AssetRule asset, double seconds = 5, bool renew = false) => throw new NotSupportedException();
    }
    private sealed class TestClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(double seconds) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }
    private sealed class FrameDevice(ScreenFrame frame) : IGameDevice
    {
        public ValueTask<ScreenFrame> CaptureAsync(CancellationToken token = default) => ValueTask.FromResult(frame);
        public ValueTask TapAsync(PixelPoint point, CancellationToken token = default) => throw new NotSupportedException();
        public ValueTask SwipeAsync(PixelPoint start, PixelPoint end, TimeSpan duration, CancellationToken token = default) => throw new NotSupportedException();
        public ValueTask BackAsync(CancellationToken token = default) => throw new NotSupportedException();
    }
}
