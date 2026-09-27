using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static class CombatHealthChecks
{
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static async Task Rejects<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static double[] Doubles(JsonNode node) => node.AsArray().Select(v => v!.GetValue<double>()).ToArray();
    private static int[] Ints(JsonNode node) => node.AsArray().Select(v => v!.GetValue<int>()).ToArray();
    private static FleetHealthSnapshot? Snapshot(double[] hp)
        => hp.Length == 0 ? null : new FleetHealthState().Commit(1, 1, hp, new());
    private static readonly FleetHealthOptions Enabled = new() { UseHpBalance = true, UseEmergencyRepair = true };

    public static async Task RunAsync(string python, string upstream, string artifacts)
    {
        string output = Path.Combine(artifacts, "native-combat-health.json");
        var result = await new ProcessRunner().RunAsync(python,
            [Path.Combine(AppContext.BaseDirectory, "native_combat_health_reference.py"), upstream, output], TimeSpan.FromSeconds(60));
        Check(result.ExitCode == 0, "Native combat HP oracle failed: " + result.Error);
        var data = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
        foreach (var source in new[] { FleetBalanceRules.Source, CombatHealthPreparation.Source, ImageStability.Source, AdbFleetDrag.Source })
            Check(data["sources"]![source.Path]!.GetValue<string>() == source.Sha256, "Native source changed: " + source.Path);
        foreach (var sample in data["orders"]!.AsArray())
        {
            var options = new FleetHealthOptions { HpBalanceThreshold = sample!["threshold"]!.GetValue<double>() };
            Check(FleetBalanceRules.ExpectedOrder(Doubles(sample["hp"]!), options).SequenceEqual(Ints(sample["order"]!)),
                "Native scout order differs: " + sample.ToJsonString());
        }
        foreach (var sample in data["exchanges"]!.AsArray())
        {
            var actual = FleetBalanceRules.ExchangeSteps(Ints(sample!["target"]!), sample["minitouch"]!.GetValue<bool>());
            Check(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(actual.Select(p => new[] { p.From, p.To })), sample["steps"]),
                "Native exchange steps differ");
        }
        foreach (var sample in data["decisions"]!.AsArray())
            Check(FleetBalanceRules.NeedsEmergencyRepair(Doubles(sample!["hp"]!), Enabled with {
                RepairUseSingleThreshold = sample["single"]!.GetValue<double>(),
                RepairUseMultiThreshold = sample["multi"]!.GetValue<double>() }) == sample["use"]!.GetValue<bool>(),
                "Native repair decision differs: " + sample.ToJsonString());

        var pngs = await Task.WhenAll(Enumerable.Range(0, 3).Select(i => File.ReadAllBytesAsync(Path.Combine(artifacts, $"power-{i}.png"))));
        await using var vision = new PythonTemplateVision(python, Path.Combine(AppContext.BaseDirectory, "Imaging/Worker/vision_worker.py"));
        var area = Ints(data["area"]!);
        var stableUi = new Ui(pngs, [0]);
        await stableUi.PrimeAsync();
        var stability = new ImageStability(stableUi, vision, () => stableUi.Current);
        foreach (var sample in data["stability"]!.AsArray())
        {
            stableUi.Reset(Ints(sample!["frames"]!));
            var observed = await stability.WaitAsync(new(area[0], area[1], area[2]-area[0], area[3]-area[1]), default);
            Check(observed.CapturedFrames == sample["lastFrame"]!.GetValue<int>() && observed.Stable == sample["stable"]!.GetValue<bool>(),
                "Native color stability timing differs: " + JsonSerializer.Serialize(observed));
        }
        foreach (var sample in data["traces"]!.AsArray())
        {
            var ui = new Ui(pngs, [0], sample!["frames"]!.AsArray()); await ui.PrimeAsync();
            var hp = Snapshot(Doubles(sample["hp"]!));
            var options = Enabled with { UseEmergencyRepair = sample["enabled"]!.GetValue<bool>() };
            var prep = Make(ui, vision, hp, options);
            bool used = await prep.HandleRepairAsync(default);
            var actual = JsonSerializer.SerializeToNode(new { result = used, calls = ui.Events, clicks = ui.Clicks, lastFrame = ui.Frame });
            var expected = sample.DeepClone().AsObject(); expected.Remove("hp"); expected.Remove("frames"); expected.Remove("enabled");
            if (!JsonNode.DeepEquals(actual, expected))
            {
                await File.WriteAllTextAsync(Path.Combine(artifacts, "repair-mismatch.json"),
                    new JsonObject { ["actual"] = actual, ["expected"] = expected }.ToJsonString());
                throw new InvalidOperationException("Native repair trace differs");
            }
            Check(prep.Evidence.RepairClicks.Count == ui.Clicks.Count, "Repair evidence lost a click");
            Check(hp is null || hp.Weighted.SequenceEqual(Doubles(sample["hp"]!)), "Repair invented recovered HP");
        }
        JsonArray autoScene = new(new JsonArray());
        var autoUi = new Ui(pngs, [0], autoScene); await autoUi.PrimeAsync();
        var autoTimer = new IntervalTimer(autoUi.Clock, 1);
        foreach (var sample in data["automation"]!.AsArray())
        {
            autoScene[0] = sample!["assets"]!.DeepClone();
            await autoUi.DelayAsync(TimeSpan.FromSeconds(sample["elapsed"]!.GetValue<double>()), default);
            autoUi.Events.Clear(); autoUi.Clicks.Clear();
            // Recreate a battle flow but retain the session timer, as the product does.
            bool handled = await new CombatFlow(autoUi, autoUi, autoUi, autoUi, automationSetTimer: autoTimer).SetAutomationAsync(default);
            Check(handled == sample["result"]!.GetValue<bool>() &&
                JsonNode.DeepEquals(JsonSerializer.SerializeToNode(autoUi.Events), sample["calls"]) &&
                JsonNode.DeepEquals(JsonSerializer.SerializeToNode(autoUi.Clicks), sample["clicks"]), "Native automation-set timer differs");
        }
        var dragUi = new Ui(pngs, [0]); var device = new Device();
        await new AdbFleetDrag(device, dragUi).DragAsync(new(403, 421), new(821, 326), default);
        var actualDrag = JsonSerializer.SerializeToNode(device.Events.Concat(dragUi.Areas));
        Check(JsonNode.DeepEquals(actualDrag, data["drags"]), "Native ADB drag fallback differs");
        await FailureChecksAsync(pngs, vision);
        await CompositionChecksAsync(pngs, vision, data["preparation"]!);
        await ProductChecksAsync(python, upstream, artifacts);
        Console.WriteLine($"Combat HP: {data["orders"]!.AsArray().Count} native scout orders, 12 exchange plans, " +
            $"{data["decisions"]!.AsArray().Count} repair decisions, 7 native handler traces, 5 real-CV stability traces, " +
            "7 automation timing traces, ADB fallback/combat/session evidence/failure checks passed; no real device verification.");
    }

    private static CombatHealthPreparation Make(Ui ui, IVision vision, FleetHealthSnapshot? hp,
        FleetHealthOptions? options = null, bool fleetLock = false,
        Func<PixelPoint, PixelPoint, CancellationToken, ValueTask>? drag = null)
        => new(ui, () => ui.Current, new ImageStability(ui, vision, () => ui.Current), () => hp,
            options ?? Enabled, fleetLock, drag ?? ((start, end, token) => ValueTask.CompletedTask));

    private static async Task FailureChecksAsync(byte[][] pngs, IVision vision)
    {
        JsonArray available = JsonSerializer.SerializeToNode(new[] { new[] { "BATTLE_PREPARATION", "EMERGENCY_REPAIR_AVAILABLE" } })!.AsArray();
        var ui = new Ui(pngs, [0], available); await ui.PrimeAsync();
        var hp = Snapshot([.8, 0, 0, .2, .8, .5])!;
        int drags = 0;
        ValueTask Drag(PixelPoint from, PixelPoint to, CancellationToken token) { drags++; return ValueTask.CompletedTask; }
        var disabled = Make(ui, vision, hp, Enabled with { UseHpBalance = false }, drag: Drag);
        await disabled.BalanceAsync(default);
        Check(!await disabled.HandleRepairAsync(default) && drags == 0 && ui.Events.Count == 0,
            "Emergency repair escaped the balance gate");
        var locked = Make(ui, vision, hp, fleetLock: true, drag: Drag);
        await locked.BalanceAsync(default);
        Check(drags == 0 && await locked.HandleRepairAsync(default), "Fleet lock disabled repair or allowed exchange");
        var balance = Make(ui, vision, hp, drag: Drag);
        await balance.BalanceAsync(default);
        Check(drags == 1 && balance.Evidence.Exchanges.All(e => e.InputCompleted) &&
            hp.Weighted.SequenceEqual(new[] { .8, 0, 0, .2, .8, .5 }), "Balance changed observed HP or lost input evidence");
        var failed = Make(ui, vision, hp, drag: (_, _, _) => throw new IOException("Synthetic drag failed"));
        await Rejects<IOException>(async () => await failed.BalanceAsync(default));
        Check(failed.Evidence.Exchanges is [{ InputCompleted: false }], "Failed drag became completed exchange");
        await Rejects<InvalidOperationException>(async () => await Make(ui, vision, null).BalanceAsync(default));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Rejects<OperationCanceledException>(async () => await balance.BalanceAsync(cancelled.Token));
        await Rejects<OperationCanceledException>(async () => await balance.HandleRepairAsync(cancelled.Token));
        var unstable = new Ui(pngs, Enumerable.Range(0, 70).Select(i => i % 3).ToArray(), available);
        await unstable.PrimeAsync();
        var unstablePrep = Make(unstable, vision, hp, fleetLock: true);
        Check(await unstablePrep.HandleRepairAsync(default) && unstablePrep.Evidence.RepairClicks.Single().Stability is { Stable: false },
            "Native nonfatal stability timeout changed semantics or claimed stability");
        ui.Stale = true;
        await Rejects<InvalidDataException>(async () => await Make(ui, vision, hp).HandleRepairAsync(default));
        await Rejects<InvalidDataException>(async () => await new ImageStability(ui, vision, () => ui.Current).WaitAsync(new(0, 0, 8, 8), default));
        ui.Stale = false;
        await Rejects<InvalidDataException>(async () => await new ImageStability(ui, new WrongFrameVision(), () => ui.Current)
            .WaitAsync(new(0, 0, 8, 8), default));
        var device = new Device { Fail = true }; var inputUi = new Ui(pngs, [0]);
        await Rejects<IOException>(async () => await new AdbFleetDrag(device, inputUi).DragAsync(new(403, 421), new(821, 326), default));
        Check(inputUi.Areas.Count == 0, "Failed swipe still clicked its destination");
        foreach (string key in new[] { "balanceThreshold", "repairSingleThreshold", "repairMultiThreshold" })
            foreach (double value in new[] { -.1, 1.1 })
                await Rejects<ArgumentException>(() => { new CampaignResumeTask().Validate(new JsonObject {
                    ["campaign"] = "campaign_main/campaign_1_1", ["hpControl"] = new JsonObject { [key] = value } }); return Task.CompletedTask; });
        new CampaignResumeTask().Validate(new JsonObject { ["campaign"] = "campaign_main/campaign_1_1",
            ["hpControl"] = new JsonObject { ["balance"] = true, ["emergencyRepair"] = true,
                ["balanceThreshold"] = .2, ["repairSingleThreshold"] = .3, ["repairMultiThreshold"] = .6 } });
    }

    private static async Task CompositionChecksAsync(byte[][] pngs, IVision vision, JsonNode preparation)
    {
        Check(preparation.AsArray().Select(v => v!.GetValue<string>()).SequenceEqual(new[] {
            "balance", "automation", "retirement", "emotion", "repair", "battle", "confirm", "story", "executing", "interval" }),
            "Native preparation order changed");
        var frames = JsonSerializer.SerializeToNode(new[] {
            new[] { "BATTLE_PREPARATION", "EMERGENCY_REPAIR_AVAILABLE" },
            new[] { "BATTLE_PREPARATION", "EMERGENCY_REPAIR_AVAILABLE" },
            new[] { "BATTLE_PREPARATION", "EMERGENCY_REPAIR_AVAILABLE" },
            new[] { "BATTLE_PREPARATION", "EMERGENCY_REPAIR_AVAILABLE" },
            new[] { "BATTLE_PREPARATION", "EMERGENCY_REPAIR_AVAILABLE" },
            new[] { "EMERGENCY_REPAIR_CONFIRM" }, new[] { "BATTLE_PREPARATION" },
            new[] { "PAUSE" }, new[] { "BATTLE_STATUS_S" }, new[] { "CAMPAIGN_CHECK" } })!.AsArray();
        var ui = new Ui(pngs, [0], frames) { HandleInterruptions = true }; await ui.PrimeAsync();
        var hp = Snapshot([.8, 0, 0, .2, .8, .5])!;
        var prep = Make(ui, vision, hp);
        var completed = await new CombatFlow(ui, ui, ui, ui, prep, interruptions: ui).RunAutoAsync(new(TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)));
        Check(completed.Return == CombatReturn.InStage && completed.HealthPreparation?.RepairClicks.Count == 2 &&
            ui.Clicks.Select(v => JsonSerializer.SerializeToNode(v)!["asset"]!.GetValue<string>()).SequenceEqual(new[] {
                "EMERGENCY_REPAIR_AVAILABLE", "EMERGENCY_REPAIR_CONFIRM", "BATTLE_PREPARATION", "BATTLE_STATUS_S" }),
            "Full C# combat lost repair priority, confirm, or result evidence");
        int battle = ui.Events.FindIndex(e => JsonSerializer.SerializeToNode(e)?["asset"]?.GetValue<string>() == "BATTLE_PREPARATION" &&
            JsonSerializer.SerializeToNode(e)?["interval"]?.GetValue<double>() == 2);
        Check(battle >= 0 && ui.Events.Take(battle).All(e => JsonSerializer.SerializeToNode(e)?["asset"]?.GetValue<string>() != "$story"),
            "Story skipped ahead of native preparation handlers");
        var events = ui.Events.Select(e => JsonSerializer.SerializeToNode(e)!).ToArray();
        Check(ui.Retired && ui.Ignored && events.Where(e => e["frame"]?.GetValue<int>() < 2)
            .All(e => e["asset"]?.GetValue<string>() != "EMERGENCY_REPAIR_CONFIRM"),
            "Combat repair ran before pending retirement or low-emotion handling");
        int retirement = Array.FindIndex(events, e => e["frame"]?.GetValue<int>() == 2 && e["asset"]?.GetValue<string>() == "$retirement");
        int emotion = Array.FindIndex(events, e => e["frame"]?.GetValue<int>() == 2 && e["asset"]?.GetValue<string>() == "$emotion");
        int repair = Array.FindIndex(events, e => e["asset"]?.GetValue<string>() == "EMERGENCY_REPAIR_CONFIRM");
        Check(retirement >= 0 && emotion > retirement && repair > emotion,
            "Combat interruption handlers drifted from the native retirement, emotion, repair order");
        var stalled = new Ui(pngs, [0], JsonSerializer.SerializeToNode(new[] {
            new[] { "BATTLE_PREPARATION", "EMERGENCY_REPAIR_AVAILABLE", "MAIN_FLEET_POWER_ZERO" } })!.AsArray()) { CaptureDelay = true };
        await stalled.PrimeAsync();
        await Rejects<TimeoutException>(async () => await new CombatFlow(stalled, stalled, stalled, stalled,
            Make(stalled, vision, hp, fleetLock: true)).RunAutoAsync(new(TimeSpan.FromMilliseconds(70),
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1))));
        Check(stalled.Clicks.Count == 0, "Unfinished repair wait clicked battle start");
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Rejects<OperationCanceledException>(async () => await new CombatFlow(stalled, stalled, stalled, stalled,
            Make(stalled, vision, hp, fleetLock: true)).RunAutoAsync(token: cancelled.Token));
    }

    private static async Task ProductChecksAsync(string python, string upstream, string artifacts)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Alas.Engine.Tests.exe" : "Alas.Engine.Tests");
        string? previous = Environment.GetEnvironmentVariable("ALAS_TEST_ADB_FIXTURE");
        string folder = Path.Combine(artifacts, "product-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var assetFiles = new AssetFiles(Path.Combine(upstream, "assets"));
        string power = Path.Combine(folder, "frame.png");
        await File.WriteAllBytesAsync(power, (await assetFiles.ReadAsync(UiAssets.Combat.BATTLE_PREPARATION.For(GameServer.Cn))).ToArray());
        string fixture = Path.Combine(folder, "fixture.json");
        await File.WriteAllTextAsync(fixture, JsonSerializer.Serialize(new { first = power, second = power,
            failTap = true, advance = false, allowSwipe = true }));
        Environment.SetEnvironmentVariable("ALAS_TEST_ADB_FIXTURE", fixture);
        try
        {
            await using var session = new EngineSession(new(executable, "offline-replay", GameServer.Cn,
                Path.Combine(upstream, "assets"), python, "org.example.game", AllowActions: true));
            _ = session.BeginTask(TimeSpan.FromSeconds(15));
            await session.Driver.ScreenshotAsync(default);
            var state = new CampaignState(RuleCatalog.Create("campaign_main/campaign_1_1").Map);
            state.Health.Commit(1, 1, [.9, 0, 0, .2, .8, .5], Enabled);
            var flow = session.CreateCampaignCombatFlow(state, new() { UseFleetLock = false, Health = Enabled, EmotionMode = CampaignEmotionMode.Ignore });
            await Rejects<IOException>(async () => await flow.RunAutoAsync());
            var saved = await session.SaveEvidenceAsync(folder, true);
            var actions = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "actions.json")))!.AsArray();
            var health = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(folder, saved.CombatHealthFile!)))!.AsArray();
            Check(actions.Count == 2 && actions[0]!["kind"]!.GetValue<string>() == "swipe" &&
                actions[0]!["completed"]!.GetValue<bool>() && actions[1]!["completed"]!.GetValue<bool>() == false &&
                health[0]!["exchanges"]![0]!["inputCompleted"]!.GetValue<bool>() == false && saved.Image == "failure.png",
                "Actual session composition lost failed drag, health evidence or failure frame");
            session.BeginTask(TimeSpan.FromSeconds(1));
            Check((await session.SaveEvidenceAsync(Path.Combine(folder, "next"), false)).CombatHealthFile is null,
                "Combat HP evidence leaked to another task");
        }
        finally { Environment.SetEnvironmentVariable("ALAS_TEST_ADB_FIXTURE", previous); }
    }

    private sealed class Ui(byte[][] pngs, int[] initial, JsonArray? scenes = null) : AppearanceProbe(GameServer.Cn, null),
        IStoryHandler, IPopupHandler, IMapUiObservations, ICampaignInterruptions
    {
        private int[] _images = initial;
        private long _sequence = 1;
        public int Frame { get; private set; }
        public bool Stale { get; set; }
        public bool CaptureDelay { get; set; }
        public bool HandleInterruptions { get; init; }
        public bool Retired { get; private set; }
        public bool Ignored { get; private set; }
        public void Configure(RetirementOptions retirement, CampaignEmotionMode emotion) => throw new NotSupportedException();
        public ValueTask<bool> RetirementAsync(CancellationToken token)
        {
            Events.Add(new { asset = "$retirement", frame = Frame });
            bool handled = HandleInterruptions && !Retired;
            Retired |= handled; return ValueTask.FromResult(handled);
        }
        public ValueTask<bool> LowEmotionAsync(CancellationToken token)
        {
            Events.Add(new { asset = "$emotion", frame = Frame });
            bool handled = HandleInterruptions && !Ignored;
            Ignored |= handled; return ValueTask.FromResult(handled);
        }
        public ScreenFrame Current => new(_sequence, DateTimeOffset.UnixEpoch, pngs[_images[Math.Min(Frame, _images.Length - 1)]]);
        public List<object> Events { get; } = [];
        public List<object> Clicks { get; } = [];
        public List<object> Areas { get; } = [];
        public async Task PrimeAsync() => await base.ScreenshotAsync(default);
        public void Reset(int[] frames) { _images = frames; Frame = 0; _sequence++; }
        public override async ValueTask ScreenshotAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (CaptureDelay) await Task.Delay(10, token);
            Frame++; if (!Stale) _sequence++; Time.Advance(.125);
            if (Frame > 110) throw new InvalidOperationException("Synthetic preparation did not terminate");
        }
        public override ValueTask<bool> AppearsAsync(AssetRule asset, ButtonOffset offset = default, double interval = 0,
            double similarity = .85, int threshold = 10, TemplatePreprocessing preprocessing = TemplatePreprocessing.Color,
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Events.Add(new { asset = asset.Name, offset = new[] { offset.Left, offset.Top, offset.Right, offset.Bottom }, interval, threshold, frame = Frame });
            return ValueTask.FromResult(scenes is not null && scenes[Math.Min(Frame, scenes.Count - 1)]!.AsArray()
                .Any(v => v!.GetValue<string>() == asset.Name));
        }
        public override ValueTask ClickAsync(AssetRule asset, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Clicks.Add(new { asset = asset.Name, frame = Frame }); return ValueTask.CompletedTask; }
        public override ValueTask ClickAreaAsync(Rectangle area, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Areas.Add(new { kind = "tapArea", area = new[] { area.Left, area.Top, area.Right, area.Bottom }, check = false }); return ValueTask.CompletedTask; }
        public override void ClearInterval(AssetRule asset) { Events.Add(new { clear = asset.Name, frame = Frame }); base.ClearInterval(asset); }
        public ValueTask<bool> StorySkipAsync(CancellationToken token = default)
        { Events.Add(new { asset = "$story", frame = Frame }); return ValueTask.FromResult(false); }
        public ValueTask EnsureNoStoryAsync(bool skipFirstScreenshot, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<bool> ConfirmAsync(CancellationToken token) => ValueTask.FromResult(false);
        public ValueTask<int> InfoBarCountAsync(CancellationToken token) => ValueTask.FromResult(0);
        public ValueTask<bool> HasStageEntranceAsync(CancellationToken token) => ValueTask.FromResult(true);
    }
    private sealed class Device : IGameDevice
    {
        public bool Fail { get; init; }
        public List<object> Events { get; } = [];
        public ValueTask SwipeAsync(PixelPoint start, PixelPoint end, TimeSpan duration, CancellationToken token = default)
        {
            if (Fail) throw new IOException("Synthetic swipe failed");
            Events.Add(new { kind = "swipe", start = new[] { start.X, start.Y }, end = new[] { end.X, end.Y }, seconds = duration.TotalSeconds });
            return ValueTask.CompletedTask;
        }
        public ValueTask<ScreenFrame> CaptureAsync(CancellationToken token = default) => throw new NotSupportedException();
        public ValueTask TapAsync(PixelPoint point, CancellationToken token = default) => throw new NotSupportedException();
        public ValueTask BackAsync(CancellationToken token = default) => throw new NotSupportedException();
    }
    private sealed class WrongFrameVision : IVision
    {
        public ValueTask<TemplateObservation> MatchAsync(ScreenFrame frame, TemplateRequest request, CancellationToken token = default)
            => ValueTask.FromResult(new TemplateObservation(frame.Sequence-1, true, 1, new(0, 0)));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public ValueTask<MeanColorObservation> MeanColorAsync(ScreenFrame frame, PixelArea area, CancellationToken token = default) => throw new NotSupportedException();
        public ValueTask<ColorBandObservation> ColorBandsAsync(ScreenFrame frame, ColorBandRequest request, CancellationToken token = default) => throw new NotSupportedException();
        public ValueTask<OcrObservation> ReadTextAsync(ScreenFrame frame, OcrRequest request, CancellationToken token = default) => throw new NotSupportedException();
    }
}
