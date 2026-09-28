using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static partial class SubmarineCallChecks
{
    private sealed record Step(double Seconds, int Mask);
    private sealed record Case(string Mode, double? RetryAge, Step[] Steps);
    private sealed record Call(int Frame, string Asset);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly AssetRule[] Assets = [UiAssets.Combat.SUBMARINE_AVAILABLE_CHECK_1,
        UiAssets.Combat.SUBMARINE_AVAILABLE_CHECK_2, UiAssets.Combat.SUBMARINE_CALLED, UiAssets.Combat.SUBMARINE_READY];

    public static async Task RunAsync(string python, string upstream, string artifacts)
    {
        Directory.CreateDirectory(artifacts);
        var samples = new List<Case>();
        foreach (var mode in Enum.GetValues<SubmarineMode>())
        foreach (double? age in new double?[] { null, 0, .5, 1, 1.25 })
        {
            foreach (int mask in Enumerable.Range(0, 16))
            foreach (double step in new[] { .25, 1, 1.25 })
                samples.Add(new(mode.Name(), age, Enumerable.Range(0, (int)(7 / step) + 1)
                    .Select(i => new Step(i * step, mask)).ToArray()));
            foreach (Step[] trace in new Step[][] {
                [new(0, 0), new(1, 1), new(1.25, 3), new(2, 11), new(2.5, 7)],
                [new(0, 3), new(1, 3), new(1.0001, 3), new(5, 3), new(5.0001, 7)],
                [new(0, 0), new(5, 11), new(5.0001, 15)],
                [new(0, 0), new(4.9999, 15), new(5, 15)],
                [new(0, 0), new(5.0001, 15)] })
                samples.Add(new(mode.Name(), age, trace));
        }
        string input = Path.Combine(artifacts, "input.json");
        await File.WriteAllTextAsync(input, JsonSerializer.Serialize(samples, Json));
        await using var vision = new PureVisionWorker(python, Path.Combine(AppContext.BaseDirectory, "Imaging/Worker/vision_worker.py"));
        var files = new AssetFiles(Path.Combine(upstream, "assets"));
        int pixels = 0;
        foreach (var server in Enum.GetValues<GameServer>())
        {
            string output = Path.Combine(artifacts, "native-" + server + ".json");
            var response = await new ProcessRunner().RunAsync(python,
                [Path.Combine(AppContext.BaseDirectory, "native_submarine_call_reference.py"), upstream, input, output,
                    server.ToString().ToLowerInvariant()], TimeSpan.FromSeconds(60));
            Check(response.ExitCode == 0, "Native submarine handler failed: " + response.Error);
            var native = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
            foreach (var source in new[] { CombatSubmarineCall.Source, CombatFlow.Source })
                Check(native["sources"]![source.Path]!.GetValue<string>() == source.Sha256, "Submarine call/combat source drifted");
            for (int i = 0; i < samples.Count; i++)
            {
                var sample = samples[i];
                var ui = new Replay();
                var retry = new IntervalTimer(ui.Clock, 1);
                if (sample.RetryAge is { } age) { retry.Reset(); ui.Time.Advance(age); }
                double start = ui.Time.Seconds;
                var controller = new CombatSubmarineCall(ui, () => ui.Frame, SubmarineRules.Parse(sample.Mode), retry);
                controller.Begin();
                var handled = new List<bool>(); var stopped = new List<bool>();
                foreach (var step in sample.Steps)
                {
                    ui.Frame = handled.Count + 1; ui.Mask = step.Mask; ui.Time.Seconds = start + step.Seconds;
                    handled.Add(await controller.HandleAsync());
                    stopped.Add(controller.Evidence.State != "waiting");
                }
                var actual = JsonSerializer.SerializeToNode(new { calls = ui.Calls, clicks = ui.Clicks, handled, stopped }, Json);
                Check(JsonNode.DeepEquals(actual, native["results"]![i]), $"Native submarine trace differs: {server} case {i}: {actual}");
                Check(controller.Evidence.Attempts.Count == ui.Clicks.Count && controller.Evidence.Attempts.All(a => a.Completed),
                    "Submarine click attempts were lost");
            }
            var matcher = new AssetMatcher(server, vision, files);
            foreach (var pixel in native["pixels"]!.AsArray())
            {
                var frame = new ScreenFrame(++pixels, DateTimeOffset.UnixEpoch,
                    await File.ReadAllBytesAsync(Path.Combine(artifacts, pixel!["file"]!.GetValue<string>())));
                var asset = Assets.Single(a => a.Name == pixel["asset"]!.GetValue<string>());
                bool matched = await matcher.AppearsAsync(frame, asset, default);
                Check(matched == pixel["matched"]!.GetValue<bool>(), "Submarine native CV differs: " + pixel);
            }
        }
        await FailuresAsync();
        await CombatFlowChecks.SubmarineAsync();
        await CampaignMapInitializerChecks.RunAsync();
        await ConfigurationAsync(artifacts);
        await ReportsAsync(python, upstream, artifacts);
        Console.WriteLine($"Submarine call: {samples.Count} native traces per server, {pixels} CV fixtures, combat integration, failure and report checks passed; no game actions.");
    }

    private static async Task FailuresAsync()
    {
        foreach (bool failClick in new[] { false, true })
        {
            var ui = new Replay { Mask = 11, FailClick = failClick };
            var call = new CombatSubmarineCall(ui, () => ui.Frame, SubmarineMode.EveryCombat);
            call.Begin();
            await Rejects<InvalidOperationException>(() => { call.Begin(); return Task.CompletedTask; });
            if (failClick) await Rejects<IOException>(() => call.HandleAsync().AsTask());
            else
            {
                Check(await call.HandleAsync() && call.Evidence.ObservedFrame is null, "Click was treated as confirmation");
                await Rejects<InvalidDataException>(() => call.HandleAsync().AsTask());
            }
            Check(call.Evidence is { State: "failed", Attempts.Count: 1 } && call.Evidence.Attempts[0].Completed != failClick,
                "Failure discarded a click attempt or marked a failed device action complete");
        }
        var shared = new Replay { Mask = 11 };
        var timer = new IntervalTimer(shared.Clock, 1);
        var first = new CombatSubmarineCall(shared, () => shared.Frame, SubmarineMode.EveryCombat, timer);
        first.Begin(); Check(await first.HandleAsync(), "Initial retry should be ready"); first.End();
        shared.Frame++; shared.Time.Advance(.5);
        var second = new CombatSubmarineCall(shared, () => shared.Frame, SubmarineMode.EveryCombat, timer);
        second.Begin(); Check(!await second.HandleAsync(), "Starting a battle reset the shared retry timer");
        shared.Frame++; shared.Time.Advance(.5001);
        Check(await second.HandleAsync(), "Shared retry timer never became ready");
        second.End();
        Check(second.Evidence is { State: "battle_ended_unconfirmed", ObservedFrame: null }, "Unconfirmed battle fabricated a call");
        var noFrame = new Replay { HasFrame = false };
        await Rejects<InvalidDataException>(() => { new CombatSubmarineCall(noFrame, () => 0, SubmarineMode.EveryCombat).Begin(); return Task.CompletedTask; });
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var untouched = new Replay();
        var cancelCall = new CombatSubmarineCall(untouched, () => untouched.Frame, SubmarineMode.EveryCombat);
        cancelCall.Begin();
        await Rejects<OperationCanceledException>(() => cancelCall.HandleAsync(cancelled.Token).AsTask());
        Check(untouched.Calls.Count == 0 && untouched.Clicks.Count == 0, "Cancelled call touched the device");
    }

    private static async Task ConfigurationAsync(string artifacts)
    {
        foreach (var mode in Enum.GetValues<SubmarineMode>())
        foreach (int fleet in new[] { 0, 1 })
        {
            var input = new JsonObject { ["campaign"] = "campaign_main/campaign_13_1", ["fleet1"] = 1,
                ["fleet2"] = 0, ["submarine"] = fleet, ["submarineMode"] = mode.Name() };
            new CampaignRunTask().Validate(input);
        }
        foreach (JsonNode? invalid in new JsonNode?[] { null, JsonValue.Create("invalid") })
            await Rejects<ArgumentException>(() => { new CampaignRunTask().Validate(new()
                { ["campaign"] = "campaign_main/campaign_13_1", ["fleet1"] = 1, ["fleet2"] = 0,
                    ["submarine"] = 1, ["submarineMode"] = invalid }); return Task.CompletedTask; });
        // Native chapter configuration disables submarines before support validation and device preparation.
        foreach (var mode in new[] { SubmarineMode.BossOnly, SubmarineMode.HuntAndBoss })
            new CampaignRunTask().Validate(new() { ["campaign"] = "campaign_main/campaign_7_2", ["fleet1"] = 1,
                ["fleet2"] = 0, ["submarine"] = 1, ["submarineMode"] = mode.Name() });
        await CampaignCommandChecks.RunAsync(Path.Combine(artifacts, "commands"));
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Rejects<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private sealed class ClockSource : TimeProvider
    {
        public double Seconds { get; set; } = 100;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => (long)Math.Round(Seconds * TimeSpan.TicksPerSecond);
        public void Advance(double seconds) => Seconds += seconds;
    }
    private sealed class Replay : IUiDriver
    {
        public GameServer Server => GameServer.Cn;
        public bool HasFrame { get; set; } = true;
        public ClockSource Time { get; } = new();
        public TimeProvider Clock => Time;
        public int Frame { get; set; } = 1;
        public int Mask { get; set; }
        public bool FailClick { get; set; }
        public List<Call> Calls { get; } = [];
        public List<int> Clicks { get; } = [];
        public ValueTask<bool> AppearsAsync(AssetRule asset, ButtonOffset offset = default, double interval = 0,
            double similarity = .85, int threshold = 10, TemplatePreprocessing preprocessing = TemplatePreprocessing.Color, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); Check(offset == default && interval == 0 && threshold == 10, "Native call options changed");
            Calls.Add(new(Frame, asset.Name)); return ValueTask.FromResult((Mask & (1 << Array.IndexOf(Assets, asset))) != 0);
        }
        public ValueTask ClickAsync(AssetRule asset, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Check(asset == UiAssets.Combat.SUBMARINE_READY, "Wrong call asset");
            if (FailClick) throw new IOException("Synthetic call failure"); Clicks.Add(Frame); return ValueTask.CompletedTask;
        }
        public ValueTask ScreenshotAsync(CancellationToken token) => throw new NotSupportedException();
        public ValueTask ClickAreaAsync(Rectangle area, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<MeanColorObservation> ColorAsync(Rectangle area, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<ColorBandObservation> ColorBandsAsync(ColorBandRequest request, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<OcrObservation> ReadTextAsync(OcrRequest request, CancellationToken token) => throw new NotSupportedException();
        public void ClearOffset(AssetRule asset) => throw new NotSupportedException();
        public IntervalTimer Timer(AssetRule asset, double seconds = 5, bool renew = false) => throw new NotSupportedException();
        public void ResetInterval(AssetRule asset, double seconds = 3) => throw new NotSupportedException();
        public void ClearInterval(AssetRule asset) => throw new NotSupportedException();
        public ValueTask DelayAsync(TimeSpan time, CancellationToken token) => throw new NotSupportedException();
    }
}
