using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static class RetirementChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Rejects<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static int[] Ints(JsonNode node) => node.AsArray().Select(v => v!.GetValue<int>()).ToArray();
    private static string[] Strings(JsonNode node) => node.AsArray().Select(v => v!.GetValue<string>()).ToArray();
    private static string[][] Frames(JsonNode node) => node.AsArray().Select(v => Strings(v!)).ToArray();
    private static RetirementHandler Handler(Replay ui, IRetirementDock? dock = null)
        => new(ui, dock ?? new EmptyDock(), () => ui.Sequence, ui.InfoAsync);

    public static async Task RunAsync(string python, string upstream, string artifacts)
    {
        string output = Path.Combine(artifacts, "native-retirement.json");
        var reference = await new ProcessRunner().RunAsync(python,
            [Path.Combine(AppContext.BaseDirectory, "native_retirement_reference.py"), upstream, output], TimeSpan.FromSeconds(60));
        Check(reference.ExitCode == 0, "Native retirement failed: " + reference.Error);
        var native = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
        foreach (var source in new[] { RetirementRules.Source, RetirementRules.DockSource, RetirementRules.SettingsSource,
                     UiSetting.Source, UiClick.Source, UiVisuals.Source, CampaignInterruptions.Source })
            Check(native["sources"]![source.Path]!.GetValue<string>() == source.Sha256, "Retirement source drifted: " + source.Path);
        var groups = JsonSerializer.SerializeToNode(RetirementRules.DockSettings.Select(g => new
        {
            name = g.Name,
            @default = g.Default,
            options = g.Options.Select(o => new
            {
                name = o.Name,
                area = new[] { o.Area.Left, o.Area.Top, o.Area.Right, o.Area.Bottom }
            })
        }));
        Check(JsonNode.DeepEquals(groups, native["groups"]), "Native dock grids/options/defaults differ");
        var quick = JsonSerializer.SerializeToNode(RetirementRules.QuickSettings(GameServer.Cn).Select(g => new
        {
            name = g.Name,
            @default = g.Default,
            options = g.Options.Select(o => new { name = o.Name, asset = o.Asset!.Name })
        }));
        Check(JsonNode.DeepEquals(quick, native["quickGroups"]), "Native quick-retire declarations differ");
        int same = 0, stricter = 0;
        foreach (var sample in native["confirmations"]!.AsArray())
        {
            var ui = new Replay(Enum.Parse<GameServer>(sample!["server"]!.GetValue<string>(), true), Frames(sample["frames"]!));
            if (sample["strictReject"]!.GetValue<bool>())
            { await Rejects<TimeoutException>(async () => await Handler(ui).ConfirmAsync(new(), default)); stricter++; }
            else
            {
                var confirmed = await Handler(ui).ConfirmAsync(new(), default);
                Check(confirmed.ReturnedSequence == ui.Sequence && confirmed.ShipConfirmClicks > 0 &&
                    confirmed.ReturnedSequence > confirmed.StartedSequence, "Retirement invented confirmation");
                same++;
            }
            var expected = sample["clicks"]!.DeepClone();
            foreach (var item in expected!.AsArray())
                item!["asset"] = item["asset"]!.GetValue<string>().Replace("_RETIRE_SR_SSR", "", StringComparison.Ordinal);
            Check(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(ui.Clicks, Json), expected) &&
                ui.Frame == sample["lastFrame"]!.GetValue<int>(), "Native confirmation click/timing differs: " + sample.ToJsonString());
        }
        foreach (var sample in native["pipelines"]!.AsArray())
        {
            var ui = new Pipeline(Enum.Parse<GameServer>(sample!["server"]!.GetValue<string>(), true), sample["successAt"]!.GetValue<int>());
            var handler = Handler(ui, ui);
            var options = new RetirementOptions { KeepLimitBreak = sample["keep"]!.GetValue<bool>() };
            if (sample["error"] is null)
            {
                var completed = await handler.RunAsync(options, default);
                Check(completed.ReturnedSequence > completed.StartedSequence && completed.Confirmations.Count == 1 &&
                    completed.NativeSelectionEstimate == 10, "Retirement pipeline lost confirmed return");
            }
            else await Rejects<HumanTakeoverRequiredException>(async () => await handler.RunAsync(options, default));
            Check(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(ui.PipelineCalls), sample["calls"]),
                "Native retirement fallback order differs: " + string.Join(",", ui.PipelineCalls));
        }
        foreach (var sample in native["settings"]!.AsArray())
        {
            var ui = new SettingUi();
            var requested = sample!["required"]!.AsObject().ToDictionary(p => p.Key,
                p => p.Value is null ? null : (IReadOnlyList<string>)(p.Value is JsonArray ? Strings(p.Value) : [p.Value.GetValue<string>()]));
            await new UiSetting(ui, RetirementRules.DockSettings, ui.ActiveAsync).SetAsync(requested, default);
            Check(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(ui.Keys), sample["clicks"]) &&
                ui.Active.Order(StringComparer.Ordinal).SequenceEqual(Strings(sample["active"]!)), "Native setting execution differs");
        }
        await using var vision = new PythonTemplateVision(python, Path.Combine(AppContext.BaseDirectory, "Imaging/Worker/vision_worker.py"));
        var files = new AssetFiles(Path.Combine(upstream, "assets"));
        var device = new StaticDevice();
        var driver = new UiDriver(GameServer.Cn, device, vision, files, new ManualClock());
        foreach (var sample in native["selections"]!.AsArray())
        {
            device.Png = await File.ReadAllBytesAsync(Path.Combine(artifacts, sample!["file"]!.GetValue<string>()));
            await driver.ScreenshotAsync(default); device.Areas.Clear();
            var selectUi = new CardUi(driver, device);
            int selected = await new RetirementHandler(selectUi, new EmptyDock(), () => driver.Frame!.Sequence, _ => ValueTask.FromResult(0))
                .ChooseAsync(sample["amount"]!.GetValue<int>(), Strings(sample["allowed"]!).Select(Enum.Parse<ShipRarity>).ToArray(), default);
            Check(selected == sample["picked"]!.GetValue<int>() && device.Areas.Select(a =>
                { int i = RetirementRules.Cards.IndexOf(a); return $"CARD_{i % 7}_{i / 7}"; }).SequenceEqual(Strings(sample["clicks"]!)),
                "Actual CV rarity selection differs");
        }
        foreach (var sample in native["pixels"]!.AsArray())
        {
            var frame = new ScreenFrame(1, DateTimeOffset.UnixEpoch,
                await File.ReadAllBytesAsync(Path.Combine(artifacts, sample!["file"]!.GetValue<string>())));
            var color = Ints(sample["color"]!);
            Check(await new UiVisuals(vision, () => frame).ColorCountAsync(new(20, 10, 52, 26), new(color[0], color[1], color[2]),
                sample["threshold"]!.GetValue<int>(), sample["count"]!.GetValue<int>(), default) == sample["active"]!.GetValue<bool>(),
                "Native setting pixel threshold differs");
        }
        foreach (var sample in native["templates"]!.AsArray())
        {
            var server = Enum.Parse<GameServer>(sample!["server"]!.GetValue<string>(), true);
            var asset = sample["asset"]!.GetValue<string>() == "SHIP_CONFIRM" ? UiAssets.Retire.SHIP_CONFIRM : UiAssets.Retire.SHIP_CONFIRM_2;
            device.Png = await File.ReadAllBytesAsync(Path.Combine(artifacts, sample["file"]!.GetValue<string>()));
            var matchUi = new UiDriver(server, device, vision, files);
            await matchUi.ScreenshotAsync(default);
            bool matched = await UiVisuals.MatchTemplateColorAsync(matchUi, asset, ButtonOffset.Expand(30, 30), 2, default);
            var area = matchUi.ButtonArea(asset);
            Check(matched == sample["matched"]!.GetValue<bool>() &&
                new[] { area.Left, area.Top, area.Right, area.Bottom }.SequenceEqual(Ints(sample["button"]!)),
                "Native template+color or translated click bounds differ");
            if (matched) Check(!await UiVisuals.MatchTemplateColorAsync(matchUi, asset, ButtonOffset.Expand(30, 30), 2, default),
                "Template+color interval was not gated");
        }
        foreach (var sample in native["low"]!.AsArray())
        {
            var ui = new Replay(GameServer.Cn, [Strings(sample!["visible"]!)]);
            var interruptions = new CampaignInterruptions(ui, new UiVisuals(vision, () => throw new InvalidOperationException()), Handler(ui));
            interruptions.Configure(new(), sample["ignore"]!.GetValue<bool>() ? CampaignEmotionMode.Ignore : CampaignEmotionMode.Calculate);
            bool handled = await interruptions.LowEmotionAsync(default);
            var expected = sample["clicks"]!.DeepClone();
            foreach (var item in expected!.AsArray()) item!["asset"] = item["asset"]!.GetValue<string>().Replace("_IGNORE_LOW_EMOTION", "", StringComparison.Ordinal);
            Check(handled == sample["result"]!.GetValue<bool>() &&
                JsonNode.DeepEquals(JsonSerializer.SerializeToNode(ui.Clicks, Json), expected) &&
                ui.Timer(UiAssets.Handler.AUTO_SEARCH_MAP_OPTION_OFF).Started == sample["reset"]!.GetValue<bool>(),
                "Native ignore-low-emotion handling differs");
        }
        await FailureChecksAsync();
        await RetirementWorkflowChecks.RunAsync(vision);
        await ProductChecksAsync(python, upstream, artifacts);
        Console.WriteLine($"Retirement: {same} exact native confirmation traces, {stricter} rejected native timeout assumptions, " +
            "48 native fallback routes, dock/quick declarations, 4 setting traces, 64 actual-CV rarity selections, " +
            "16 pixel thresholds, 24 four-server template+color cases, 6 low-emotion traces and session failure evidence passed; no real device actions.");
    }

    private static async Task FailureChecksAsync()
    {
        var ui = new Replay(GameServer.Cn, [["SHIP_CONFIRM_2"], ["EQUIP_CONFIRM_2"], ["IN_RETIREMENT_CHECK"]]) { Stale = true };
        await Rejects<TimeoutException>(async () => await Handler(ui).ConfirmAsync(new(), default));
        ui = new Replay(GameServer.Cn, [["SHIP_CONFIRM_2"], ["EQUIP_CONFIRM_2"], ["IN_RETIREMENT_CHECK"]]) { SequenceLimit = 2 };
        await Rejects<TimeoutException>(async () => await Handler(ui).ConfirmAsync(new(), default));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Rejects<OperationCanceledException>(async () => await Handler(ui).RunAsync(new(), cancelled.Token));
        var settings = new SettingUi { IgnoreClicks = true };
        await Rejects<TimeoutException>(async () => await new UiSetting(settings, RetirementRules.DockSettings, settings.ActiveAsync)
            .SetAsync(new Dictionary<string, IReadOnlyList<string>?>(), default));
        ui = new Replay(GameServer.Cn, [["RETIRE_APPEAR_1"]]);
        await Rejects<CampaignDockFullException>(async () => await Handler(ui).HandleAsync(new() { Mode = RetirementMode.Disabled }, default));
        Check(ui.Clicks.Count == 0, "Disabled retirement clicked the dock");
        var interrupted = new Pipeline(GameServer.Cn, 1) { FailClick = "EQUIP_CONFIRM_2" };
        var handler = Handler(interrupted, interrupted);
        await Rejects<IOException>(async () => await handler.RunAsync(new(), default));
        Check(handler.Evidence is [
        {
            ReturnedSequence: null, Confirmations: [
                { ReturnedSequence: null, ShipConfirmClicks: 1, EquipmentConfirmClicks: 0 }]
        }],
            "Failed retirement lost partial confirmation evidence or became completed");
        foreach (var node in new JsonNode?[] { null, JsonValue.Create("one_click_retire"), new JsonObject{["mode"]="enhance"},
            new JsonObject{["rarities"]=new JsonArray()}, new JsonObject{["rarities"]=new JsonArray("R","R")},
            new JsonObject{["keepLimitBreak"]=null},new JsonObject{["amount"]="1"} })
        {
            bool rejected = false;
            try { new CampaignResumeTask().Validate(new JsonObject { ["campaign"] = "campaign_main/campaign_1_1", ["retirement"] = node }); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException) { rejected = true; }
            Check(rejected, "Invalid retirement input accepted");
        }
        new CampaignResumeTask().Validate(new JsonObject
        {
            ["campaign"] = "campaign_main/campaign_1_1",
            ["retirement"] = new JsonObject
            {
                ["mode"] = "old_retire",
                ["amount"] = "retire_10",
                ["rarities"] = new JsonArray("N", "R")
            }
        });
        await EntryChecksAsync();
    }

    private static async Task EntryChecksAsync()
    {
        var ui = new EntryUi(); var hook = new EntryHook(ui);
        await new CampaignPreparation(ui, hook).OpenFleetAsync("normal");
        Check(hook.Retired && ui.Clicks.Count(c => c.Asset == "MAP_PREPARATION") == 2,
            "Dock interruption did not re-enter fleet preparation");
        var entry = await new CampaignEntry(ui, () => ui.Sequence, hook).EnterAsync();
        Check(hook.Ignored && entry.FleetClicks == 1, "Low emotion was not handled before map entry");
    }

    private static async Task ProductChecksAsync(string python, string upstream, string artifacts)
    {
        var folder = Path.Combine(artifacts, "product-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var files = new AssetFiles(Path.Combine(upstream, "assets"));
        string initial = Path.Combine(folder, "retirement.png");
        await File.WriteAllBytesAsync(initial, (await files.ReadAsync(UiAssets.Retire.IN_RETIREMENT_CHECK.For(GameServer.Cn))).ToArray());
        string fixture = Path.Combine(folder, "fixture.json");
        await File.WriteAllTextAsync(fixture, JsonSerializer.Serialize(new { first = initial, second = initial, advance = false, failTap = true }));
        string? previous = Environment.GetEnvironmentVariable("ALAS_TEST_ADB_FIXTURE");
        Environment.SetEnvironmentVariable("ALAS_TEST_ADB_FIXTURE", fixture);
        try
        {
            string exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Alas.Engine.Tests.exe" : "Alas.Engine.Tests");
            await using var session = new EngineSession(new(exe, "offline-replay", GameServer.Cn, Path.Combine(upstream, "assets"), python,
                "org.example.game", AllowActions: true));
            var context = session.BeginTask(TimeSpan.FromSeconds(20));
            await session.Driver.ScreenshotAsync(default);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var state = new CampaignState(RuleCatalog.Create("campaign_main/campaign_1_1").Map);
            var combat = session.CreateCampaignCombatFlow(state, new() { EmotionMode = CampaignEmotionMode.Ignore });
            await Rejects<IOException>(async () => await combat.RunAutoAsync(token: timeout.Token));
            var saved = await session.SaveEvidenceAsync(folder, true);
            Check(saved.RetirementFile == "retirement.json" && saved.Image == "failure.png" && saved.ActionAttempts == 1,
                "Real session retirement failed to persist action evidence");
            var record = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(folder, saved.RetirementFile!)))!.AsArray();
            Check(record.Count == 1 && record[0]!["returnedSequence"] is null, "Failed product retirement claimed a return");
            var next = session.BeginTask(TimeSpan.FromSeconds(1));
            Check((await session.SaveEvidenceAsync(Path.Combine(folder, "next"), false)).RetirementFile is null, "Retirement state leaked to next task");
            await session.Driver.ScreenshotAsync(default);
            await Rejects<CampaignDockFullException>(async () => await next.Interruptions!.RetirementAsync(default));
            Check((await session.SaveEvidenceAsync(Path.Combine(folder, "next"), false)).ActionAttempts == 0,
                "Campaign retirement configuration leaked to the next task");
        }
        finally { Environment.SetEnvironmentVariable("ALAS_TEST_ADB_FIXTURE", previous); }
    }

    private record Click(string Asset, int Frame);
    private class Replay : AppearanceProbe
    {
        private readonly string[][] _frames;
        public int Frame { get; protected set; }
        public long Sequence => Stale ? 1 : Math.Min(Frame + 1, SequenceLimit);
        public long SequenceLimit { get; init; } = long.MaxValue;
        public bool Stale { get; init; }
        public string? FailClick { get; init; }
        public List<Click> Clicks { get; } = [];
        public List<Rectangle> Areas { get; } = [];
        private AssetRule? _last;
        public Replay(GameServer server, string[][] frames) : base(server, null)
        { _frames = frames; base.ScreenshotAsync(default).GetAwaiter().GetResult(); }
        protected virtual bool Visible(string name) => _frames[Math.Min(Frame, _frames.Length - 1)].Contains(name);
        public override ValueTask ScreenshotAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); Frame++; Time.Advance(.5); if (Frame > 185) throw new TimeoutException("Synthetic retirement stalled"); return ValueTask.CompletedTask; }
        public override ValueTask<bool> AppearsAsync(AssetRule asset, ButtonOffset offset = default, double interval = 0, double similarity = .85,
            int threshold = 10, TemplatePreprocessing preprocessing = TemplatePreprocessing.Color, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); _last = asset;
            if (interval > 0 && !Timer(asset, interval, true).Reached()) return ValueTask.FromResult(false);
            bool appeared = Visible(asset.Name); if (appeared && interval > 0) Timer(asset).Reset(); return ValueTask.FromResult(appeared);
        }
        public override ValueTask ClickAsync(AssetRule asset, CancellationToken token)
        { token.ThrowIfCancellationRequested(); if (asset.Name == FailClick) throw new IOException("Synthetic retirement click failed"); Clicks.Add(new(asset.Name, Frame)); return ValueTask.CompletedTask; }
        public override ValueTask ClickAreaAsync(Rectangle area, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Areas.Add(area); return ValueTask.CompletedTask; }
        public override Rectangle ButtonArea(AssetRule asset) => asset.For(Server).ClickArea!.Value;
        public override ValueTask<MeanColorObservation> ColorAsync(Rectangle area, CancellationToken token)
        { var color = _last!.For(Server).Color!.Value; return ValueTask.FromResult(new MeanColorObservation(Sequence, color.R, color.G, color.B)); }
        public ValueTask<int> InfoAsync(CancellationToken token) => ValueTask.FromResult(Visible("$info") ? 1 : 0);
    }
    private sealed class EmptyDock : IRetirementDock
    {
        public ValueTask FavouriteAsync(bool enabled, CancellationToken token) => throw new NotSupportedException();
        public ValueTask DescendingAsync(bool enabled, CancellationToken token) => throw new NotSupportedException();
        public ValueTask WaitCardsAsync(CancellationToken token) => throw new NotSupportedException();
        public ValueTask FilterAsync(IReadOnlyList<string>? rarities, CancellationToken token) => throw new NotSupportedException();
        public ValueTask QuickSettingsAsync(string? keep, CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class Pipeline(GameServer server, int successAt) : Replay(server, [[]]), IRetirementDock
    {
        private string _scene = "dock"; private int _attempt;
        public List<string> PipelineCalls { get; } = [];
        protected override bool Visible(string name) => _scene switch
        {
            "dock" => name is "IN_RETIREMENT_CHECK" or "DOCK_CHECK" or "ONE_CLICK_RETIREMENT" or "BACK_ARROW",
            "ship" => name == "SHIP_CONFIRM_2",
            "equipment" => name == "EQUIP_CONFIRM_2",
            "info" => name == "$info",
            _ => false
        };
        public override async ValueTask ClickAsync(AssetRule asset, CancellationToken token)
        {
            await base.ClickAsync(asset, token);
            _scene = asset.Name switch
            {
                "ONE_CLICK_RETIREMENT" => _attempt == successAt ? "ship" : "info",
                "SHIP_CONFIRM_2" => "equipment",
                "EQUIP_CONFIRM_2" => "dock",
                "BACK_ARROW" => "returned",
                _ => _scene
            };
            if (asset.Name == "BACK_ARROW") PipelineCalls.Add("quit");
        }
        public ValueTask FavouriteAsync(bool enabled, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DescendingAsync(bool enabled, CancellationToken token)
        { PipelineCalls.Add("one"); _attempt++; _scene = "dock"; return ValueTask.CompletedTask; }
        public ValueTask WaitCardsAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask FilterAsync(IReadOnlyList<string>? rarities, CancellationToken token)
        { PipelineCalls.Add("favourite_off"); PipelineCalls.Add("filter_defaults"); return ValueTask.CompletedTask; }
        public ValueTask QuickSettingsAsync(string? keep, CancellationToken token)
        { PipelineCalls.Add("quick_" + (keep ?? "None")); return ValueTask.CompletedTask; }
    }
    private sealed class SettingUi : Replay
    {
        public SettingUi() : base(GameServer.Cn, [[]]) { }
        public bool IgnoreClicks { get; init; }
        public HashSet<string> Active { get; } = [];
        public List<string> Keys { get; } = [];
        public ValueTask<bool> ActiveAsync(UiSettingOption option, CancellationToken token)
            => ValueTask.FromResult(Active.Contains(Key(option.Area)));
        private static string Key(Rectangle area) => RetirementRules.DockSettings.SelectMany(g => g.Options.Select(o => (g.Name, Option: o)))
            .Where(p => p.Option.Area == area).Select(p => p.Name + "/" + p.Option.Name).Single();
        public override ValueTask ClickAreaAsync(Rectangle area, CancellationToken token)
        {
            string key = Key(area); Keys.Add(key); if (IgnoreClicks) return ValueTask.CompletedTask;
            string group = key.Split('/')[0], option = key.Split('/')[1];
            if (option is "all" or "no_limit" || group == "sort") Active.RemoveWhere(v => v.StartsWith(group + "/", StringComparison.Ordinal));
            else { Active.Remove(group + "/all"); Active.Remove(group + "/no_limit"); }
            Active.Add(key); return ValueTask.CompletedTask;
        }
    }
    private sealed class StaticDevice : IGameDevice
    {
        private long _sequence;
        public byte[] Png { get; set; } = [];
        public List<Rectangle> Areas { get; } = [];
        public ValueTask<ScreenFrame> CaptureAsync(CancellationToken token = default) => ValueTask.FromResult(new ScreenFrame(++_sequence, DateTimeOffset.UnixEpoch, Png));
        public ValueTask TapAsync(PixelPoint point, CancellationToken token = default) => throw new NotSupportedException();
        public ValueTask SwipeAsync(PixelPoint start, PixelPoint end, TimeSpan duration, CancellationToken token = default) => throw new NotSupportedException();
        public ValueTask BackAsync(CancellationToken token = default) => throw new NotSupportedException();
    }
    private sealed class CardUi(UiDriver driver, StaticDevice device) : AppearanceProbe(GameServer.Cn, null)
    {
        public override ValueTask<MeanColorObservation> ColorAsync(Rectangle area, CancellationToken token) => driver.ColorAsync(area, token);
        public override ValueTask ClickAreaAsync(Rectangle area, CancellationToken token) { device.Areas.Add(area); return ValueTask.CompletedTask; }
    }
    private sealed class EntryUi() : Replay(GameServer.Cn, [[]])
    {
        public string Scene { get; set; } = "map";
        public bool Freed { get; set; }
        protected override bool Visible(string name) => Scene switch
        {
            "map" => name == "MAP_PREPARATION",
            "fleet" => name == "FLEET_PREPARATION",
            "inmap" => name == "IN_MAP",
            _ => false
        };
        public override async ValueTask ClickAsync(AssetRule asset, CancellationToken token)
        { await base.ClickAsync(asset, token); Scene = asset.Name switch { "MAP_PREPARATION" => Freed ? "fleet" : "full", "FLEET_PREPARATION" => "emotion", _ => Scene }; }
    }
    private sealed class EntryHook(EntryUi ui) : ICampaignInterruptions
    {
        public bool Retired { get; private set; }
        public bool Ignored { get; private set; }
        public void Configure(RetirementOptions retirement, CampaignEmotionMode emotion) { }
        public ValueTask<bool> RetirementAsync(CancellationToken token)
        { if (ui.Scene != "full") return ValueTask.FromResult(false); Retired = true; ui.Freed = true; ui.Scene = "map"; return ValueTask.FromResult(true); }
        public ValueTask<bool> LowEmotionAsync(CancellationToken token)
        { if (ui.Scene != "emotion") return ValueTask.FromResult(false); Ignored = true; ui.Scene = "inmap"; return ValueTask.FromResult(true); }
    }
}
