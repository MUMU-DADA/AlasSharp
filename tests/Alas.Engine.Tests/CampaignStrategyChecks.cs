using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static class CampaignStrategyChecks
{
    private sealed record Case(string Kind, string[][] Frames, double Step = .5, string Expected = "on",
        bool Selector = false, string Formation = "double_line", int Fleet = 1, int Submarine = 0,
        string Mode = "do_not_use", string? Buff = null);
    private sealed record Click(string Asset, int Frame);
    private sealed record Call(string Asset, int[] Offset, double Interval, int Frame);
    private sealed record Result(bool Changed, bool Repeated, int Frames, List<Click> Clicks, List<Call> Calls);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task RunAsync(string python, string upstream, string artifacts)
    {
        var cases = new List<Case>();
        foreach (double step in new[] { .125, .5, 2.0 })
        foreach (bool selector in new[] { false, true })
        {
            cases.Add(new("switch", [["SUBMARINE_VIEW_ON"]], step, Selector: selector));
            cases.Add(new("switch", [["SUBMARINE_VIEW_OFF"], [], [], ["SUBMARINE_VIEW_ON"]], step, Selector: selector));
            cases.Add(new("switch", [.. Enumerable.Repeat(new[] { "SUBMARINE_VIEW_OFF" }, 20), ["SUBMARINE_VIEW_ON"]],
                step, Selector: selector));
            cases.Add(new("switch", [.. Enumerable.Repeat(Array.Empty<string>(), 45), ["SUBMARINE_VIEW_ON"]],
                step, Selector: selector));
            cases.Add(new("switch", [.. Enumerable.Repeat(Array.Empty<string>(), 43), ["SUBMARINE_VIEW_OFF"], [], [], [],
                ["SUBMARINE_VIEW_ON"]], step, Selector: selector));
        }
        foreach (var mode in Enum.GetValues<SubmarineMode>())
        foreach (int submarine in new[] { 0, 1 })
        foreach (int fleet in new[] { 1, 2 })
        {
            cases.Add(new("strategy", StrategyFrames(), Formation: "diamond", Fleet: fleet,
                Submarine: submarine, Mode: JsonNamingPolicy.SnakeCaseLower.ConvertName(mode.ToString())));
        }
        cases.Add(new("strategy", [["IN_MAP"]], Buff: "double_line"));
        cases.Add(new("strategy", [["IN_MAP"]], Fleet: 2, Buff: "double_line"));
        cases.Add(new("strategy", StrategyFrames(), Formation: "diamond", Submarine: 1, Buff: "diamond"));
        cases.Add(new("strategy", [["GET_ITEMS_1"], .. StrategyFrames()], Formation: "diamond"));
        cases.Add(new("strategy", [["IN_MAP"], ["STRATEGY_OPENED", "FORMATION_2"], ["IN_MAP"]]));
        string inputs = Path.Combine(artifacts, "strategy-inputs.json");
        string output = Path.Combine(artifacts, "strategy-native.json");
        await File.WriteAllTextAsync(inputs, JsonSerializer.Serialize(cases, Json));
        var process = await new ProcessRunner().RunAsync(python,
            [Path.Combine(AppContext.BaseDirectory, "native_strategy_reference.py"), upstream, inputs, output],
            TimeSpan.FromSeconds(60));
        Check(process.ExitCode == 0, "Native strategy reference failed: " + process.Error);
        var native = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
        foreach (var source in new[] { UiSwitch.Source, CampaignStrategy.Source })
            Check(native["sources"]![source.Path]!.GetValue<string>() == source.Sha256, "Upstream strategy source drifted");

        for (int index = 0; index < cases.Count; index++)
        {
            var sample = cases[index];
            var ui = new Replay(sample);
            bool changed, repeated = false;
            if (sample.Kind == "switch") changed = await Switch(ui, sample.Selector).SetAsync(sample.Expected, Timeout);
            else
            {
                var strategy = new CampaignStrategy(ui, _ => ValueTask.FromResult(sample.Buff is { } buff
                    ? (FleetFormation?)CampaignStrategy.ParseFormation(buff) : null));
                var configuration = new CampaignConfiguration
                {
                    Fleet1Formation = CampaignStrategy.ParseFormation(sample.Formation),
                    Fleet2Formation = CampaignStrategy.ParseFormation(sample.Formation),
                    Submarine = sample.Submarine,
                    SubmarineMode = Enum.GetValues<SubmarineMode>().Single(mode =>
                        JsonNamingPolicy.SnakeCaseLower.ConvertName(mode.ToString()) == sample.Mode)
                };
                changed = await strategy.EnsureAsync(sample.Fleet, configuration, Timeout);
                repeated = await strategy.EnsureAsync(sample.Fleet, configuration, Timeout);
            }
            var actual = JsonSerializer.SerializeToNode(new Result(changed, repeated, ui.Frame, ui.Clicks, ui.Trace), Json);
            Check(JsonNode.DeepEquals(actual, native["results"]![index]),
                $"Native strategy trace differs in case {index}: {actual}");
        }

        var files = new AssetFiles(Path.Combine(upstream, "assets"));
        await using var vision = new PythonTemplateVision(python,
            Path.Combine(AppContext.BaseDirectory, "Imaging/Worker/vision_worker.py"));
        foreach (var pixel in native["pixels"]!.AsArray())
        {
            var frame = new ScreenFrame(1, DateTimeOffset.UnixEpoch,
                await File.ReadAllBytesAsync(Path.Combine(artifacts, pixel!["file"]!.GetValue<string>())));
            var result = await new MapFormationProbe(vision, files, GameServer.Cn, () => frame).ObserveAsync();
            Check((result is null ? null : CampaignStrategy.FormationName(result.Value)) == pixel["expected"]?.GetValue<string>(),
                "Native map-buff crop, template priority or threshold differs");
        }
        var stale = new StaleVision();
        await Rejects<InvalidDataException>(() => new MapFormationProbe(stale, files, GameServer.Cn,
            () => new(1, DateTimeOffset.UnixEpoch, new byte[] { 1 })).ObserveAsync().AsTask());
        var stuck = new Replay(new("switch", [[]]));
        await Rejects<TimeoutException>(() => Switch(stuck).SetAsync("on", TimeSpan.FromSeconds(3)).AsTask());
        Check(stuck.Clicks.Count == 0, "A short unknown animation caused an early click");
        await Rejects<ArgumentException>(() => Switch(stuck).SetAsync("invalid", Timeout).AsTask());

        var blocked = new Replay(new("strategy", [[]]));
        int observed = 0;
        var retryable = new CampaignStrategy(blocked, _ => { observed++; return ValueTask.FromResult<FleetFormation?>(null); });
        await Rejects<TimeoutException>(() => retryable.EnsureAsync(1, new(), TimeSpan.FromSeconds(2)).AsTask());
        await Rejects<TimeoutException>(() => retryable.EnsureAsync(1, new(), TimeSpan.FromSeconds(2)).AsTask());
        Check(observed == 2 && blocked.Clicks.Count == 0, "Failed strategy became fixed or clicked an unknown page");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Rejects<OperationCanceledException>(() => retryable.EnsureAsync(1, new(), Timeout, cancelled.Token).AsTask());
        Check(observed == 2, "Cancelled strategy continued observation");

        // Independent fleet flags and a new sortie must not reuse a previous formation decision.
        int buffs = 0;
        var flags = new CampaignStrategy(new Replay(new("strategy", [["IN_MAP"]])),
            _ => { buffs++; return ValueTask.FromResult<FleetFormation?>(FleetFormation.DoubleLine); });
        await flags.EnsureAsync(1, new(), Timeout);
        await flags.EnsureAsync(2, new(), Timeout);
        await flags.EnsureAsync(1, new(), Timeout);
        Check(buffs == 2, "Formation flags were shared across fleets or forgotten");
        var fresh = new CampaignStrategy(new Replay(new("strategy", [["IN_MAP"]])),
            _ => { buffs++; return ValueTask.FromResult<FleetFormation?>(FleetFormation.DoubleLine); });
        await fresh.EnsureAsync(1, new(), Timeout);
        Check(buffs == 3, "A new sortie reused previous formation state");
        await CampaignStageSelectorChecks.RunAsync(upstream);
        await CampaignMapCombatChecks.RunAsync(python, upstream);
        Console.WriteLine($"Campaign strategy: {cases.Count} exact native switch/strategy traces, 5 real-CV buff comparisons, failure/cancellation and C# entry integration passed; no device actions.");
    }

    private static string[][] StrategyFrames() =>
    [
        ["IN_MAP"],
        ["STRATEGY_OPENED", "FORMATION_1", "SUBMARINE_VIEW_ON", "SUBMARINE_HUNT_ON"],
        ["STRATEGY_OPENED", "FORMATION_2", "SUBMARINE_VIEW_ON", "SUBMARINE_HUNT_ON"],
        ["STRATEGY_OPENED", "FORMATION_2", "SUBMARINE_VIEW_ON", "SUBMARINE_HUNT_ON"],
        ["STRATEGY_OPENED", "FORMATION_2", "SUBMARINE_VIEW_ON", "SUBMARINE_HUNT_ON"],
        ["STRATEGY_OPENED", "FORMATION_3", "SUBMARINE_VIEW_ON", "SUBMARINE_HUNT_ON"],
        ["STRATEGY_OPENED", "FORMATION_3", "SUBMARINE_VIEW_OFF", "SUBMARINE_HUNT_ON"],
        ["STRATEGY_OPENED", "FORMATION_3", "SUBMARINE_VIEW_OFF", "SUBMARINE_HUNT_OFF"],
        ["IN_MAP"]
    ];
    private static UiSwitch Switch(IUiDriver ui, bool selector = false) => new(ui,
        [new("on", UiAssets.Handler.SUBMARINE_VIEW_ON), new("off", UiAssets.Handler.SUBMARINE_VIEW_OFF)],
        ButtonOffset.Expand(100, 200), selector);
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static async Task Rejects<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private sealed class Replay : AppearanceProbe
    {
        private readonly Case _case;
        public int Frame { get; private set; }
        public List<Click> Clicks { get; } = [];
        public List<Call> Trace { get; } = [];
        public Replay(Case sample) : base(GameServer.Cn, null)
        { _case = sample; base.ScreenshotAsync(default).GetAwaiter().GetResult(); }
        public override ValueTask ScreenshotAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Frame++; Time.Advance(_case.Step);
            if (Frame > 300) throw new InvalidOperationException("C# replay did not finish within the fixture");
            return ValueTask.CompletedTask;
        }
        public override ValueTask<bool> AppearsAsync(AssetRule asset, ButtonOffset offset = default, double interval = 0,
            double similarity = .85, int threshold = 10, TemplatePreprocessing preprocessing = TemplatePreprocessing.Color,
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Trace.Add(new(asset.Name, [offset.Left, offset.Top, offset.Right, offset.Bottom], interval, Frame));
            if (interval > 0 && !Timer(asset, interval).Reached()) return ValueTask.FromResult(false);
            bool present = _case.Frames[Math.Min(Frame, _case.Frames.Length - 1)].Contains(asset.Name);
            if (present && interval > 0) Timer(asset, interval).Reset();
            return ValueTask.FromResult(present);
        }
        public override ValueTask ClickAsync(AssetRule asset, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Clicks.Add(new(asset.Name, Frame)); return ValueTask.CompletedTask; }
    }
    private sealed class StaleVision : IVision
    {
        public ValueTask<TemplateObservation> MatchAsync(ScreenFrame frame, TemplateRequest request, CancellationToken token = default)
            => ValueTask.FromResult(new TemplateObservation(frame.Sequence - 1, true, 1, new(0, 0)));
        public ValueTask<MeanColorObservation> MeanColorAsync(ScreenFrame frame, PixelArea area, CancellationToken token = default) => throw new NotSupportedException();
        public ValueTask<ColorBandObservation> ColorBandsAsync(ScreenFrame frame, ColorBandRequest request, CancellationToken token = default) => throw new NotSupportedException();
        public ValueTask<OcrObservation> ReadTextAsync(ScreenFrame frame, OcrRequest request, CancellationToken token = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
