using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static class AutoSearchChecks
{
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static async Task Rejects<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }

    public static async Task RunAsync(string python, string upstream, string artifacts)
    {
        Directory.CreateDirectory(artifacts);
        var output = Path.Combine(artifacts, "auto-search-native.json");
        var process = await new ProcessRunner().RunAsync(python,
            [Path.Combine(AppContext.BaseDirectory, "native_auto_search_reference.py"), upstream, output], TimeSpan.FromMinutes(1));
        Check(process.ExitCode == 0, "Native auto-search oracle failed: " + process.Error);
        var native = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
        foreach (var source in new[] { AutoSearchFlow.Source, CampaignAutoSearchSettings.Source, AutoSearchResources.Source })
            Check(native["sources"]![source.Path]!.GetValue<string>() == source.Sha256, "Auto-search source drifted");
        int count = 0;
        foreach (var sample in native["results"]!.AsArray())
        {
            var ui = new Replay(sample!["case"]!);
            var flow = new AutoSearchFlow(ui, ui, () => ui.Sequence);
            bool ended = false;
            try
            {
                await flow.MoveAsync(TimeSpan.FromSeconds(60));
                var sub = new CombatSubmarineCall(ui, () => ui.Sequence, SubmarineMode.DoNotUse);
                await flow.CombatAsync(1, sub, null);
            }
            catch (CampaignEndedException) { ended = true; }
            Check(ended == sample["ended"]!.GetValue<bool>() && ui.Sequence == sample["frames"]!.GetValue<long>() &&
                flow.Evidence.StatusConfirmation == sample["confirm"]!.GetValue<bool>() &&
                JsonNode.DeepEquals(JsonSerializer.SerializeToNode(ui.Clicks), sample["clicks"]),
                $"Auto-search trace differs in case {count}: expected={sample}, actual=" +
                    JsonSerializer.Serialize(new { ended, frames = ui.Sequence, clicks = ui.Clicks, flow = flow.Evidence }));
            if (!ended) Check(flow.Evidence.Battles is [{ ReturnedToSearch: true }], "Completed auto-search battle was not recorded");
            count++;
        }
        int roles = 0;
        foreach (var server in Enum.GetValues<GameServer>())
        foreach (var sample in native["settings"]!.AsArray())
        {
            int target = sample!["target"]!.GetValue<int>();
            var ui = new SettingsUi(server, sample["active"]!.AsArray().Select(v => v!.GetValue<int>()));
            var settings = new CampaignAutoSearchSettings(ui, ui, () => ui.Image);
            Task Run() => target < 4 ? settings.EnsureFleetOrderAsync((FleetOrder)target).AsTask()
                : settings.EnsureSubmarineAsync(target == 4).AsTask();
            if (sample["confirmed"]!.GetValue<bool>()) await Run();
            else await Rejects<TimeoutException>(Run);
            Check(ui.Clicks.SequenceEqual(sample["clicks"]!.AsArray().Select(v => v!.GetValue<int>())), "Role settings differ from native");
            roles++;
        }
        await FailureChecksAsync();
        await CounterChecksAsync();
        await ResourcesAsync();
        await CampaignFleetSetupChecks.RunAsync();
        await SessionAsync(python, upstream, artifacts);
        Console.WriteLine($"Auto-search passed: {count} native flow traces, {roles} four-server role cases, failure and resource checks; synthetic observations only.");
    }

    private static async Task SessionAsync(string python, string upstream, string artifacts)
    {
        string frame = Path.Combine(artifacts, "auto-search-menu.png");
        string fixture = Path.Combine(artifacts, "auto-search-adb.json");
        await File.WriteAllTextAsync(fixture, JsonSerializer.Serialize(new { first = frame, second = frame, failTap = true, advance = false }));
        string? previous = Environment.GetEnvironmentVariable("ALAS_TEST_ADB_FIXTURE");
        Environment.SetEnvironmentVariable("ALAS_TEST_ADB_FIXTURE", fixture);
        try
        {
            var executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Alas.Engine.Tests.exe" : "Alas.Engine.Tests");
            var options = new EngineSessionOptions(executable, "offline-replay", GameServer.Cn, Path.Combine(upstream, "assets"),
                python, "org.example.game", AllowActions: true);
            var queue = await new TaskQueue([new SessionProbe()]).RunAsync([new("auto", "auto_search_probe", TimeoutSeconds: 60)], options, new(artifacts));
            var result = queue.Tasks.Single();
            Check(result.Outcome == TaskOutcome.Failed && result.FailureFrames is { Length: 1 },
                "Auto-search session did not preserve failure and frame: " + JsonSerializer.Serialize(result));
            string file = Directory.GetFiles(queue.Directory, "auto-search.json", SearchOption.AllDirectories).Single();
            string original = await File.ReadAllTextAsync(file);
            var data = JsonNode.Parse(original)!;
            Check(data["flow"]!["phase"]!.GetValue<string>() == "exiting" &&
                data["flow"]!["endReason"]!.GetValue<string>() == "result_menu" &&
                RunReport.Build(queue.Directory).ToJson()["evidence_complete"]!.GetValue<bool>(),
                "Session skipped automatic flow or lost its current phase");
            File.Delete(file);
            Check(!RunReport.Build(queue.Directory).ToJson()["evidence_complete"]!.GetValue<bool>(), "Missing auto-search evidence was accepted");
            data["flow"]!["endFrame"] = 0;
            await File.WriteAllTextAsync(file, data.ToJsonString());
            Check(!RunReport.Build(queue.Directory).ToJson()["evidence_complete"]!.GetValue<bool>(), "Invalid auto-search frame was accepted");
            await File.WriteAllTextAsync(file, original);
            await using var session = new EngineSession(options);
            session.BeginTask(TimeSpan.FromSeconds(10));
            Check((await session.SaveEvidenceAsync(Path.Combine(artifacts, "next-task"), false)).AutoSearchFile is null,
                "A new task inherited the previous auto-search state");
        }
        finally { Environment.SetEnvironmentVariable("ALAS_TEST_ADB_FIXTURE", previous); }
    }

    private sealed class SessionProbe : ITaskRunner
    {
        public string Kind => "auto_search_probe";
        public bool RequiresActions => true;
        public void Validate(JsonObject? input) { }
        public IReadOnlyList<string> Preconditions(TaskRequest request, TaskCapabilities capabilities) => [];
        public async ValueTask<TaskResult> RunAsync(TaskRequest request, TaskContext context, CancellationToken token)
        {
            var rule = RuleCatalog.Create("campaign_main/campaign_1_1");
            var result = await context.Campaign!.ResumeInMapAsync(rule, new() { UseAutoSearch = true,
                IsClearMode = true, EmotionMode = CampaignEmotionMode.Ignore, Retirement = new() { Mode = RetirementMode.Disabled } }, token);
            return CampaignResumeTask.Describe(request.Id, Kind, rule, result, false);
        }
    }

    private static async Task FailureChecksAsync()
    {
        var empty = JsonNode.Parse("""{"step":1,"frames":[{}]}""")!;
        var stalled = new Replay(empty);
        await Rejects<TimeoutException>(() => new AutoSearchFlow(stalled, stalled, () => stalled.Sequence)
            .MoveAsync(TimeSpan.FromSeconds(2)).AsTask());
        Check(stalled.Clicks.Count == 0, "Unknown automatic map caused a click");
        var stale = new Replay(empty) { Stale = true };
        await Rejects<InvalidDataException>(() => new AutoSearchFlow(stale, stale, () => stale.Sequence)
            .MoveAsync(TimeSpan.FromSeconds(2)).AsTask());
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Rejects<OperationCanceledException>(() => new AutoSearchFlow(stalled, stalled, () => stalled.Sequence)
            .MoveAsync(TimeSpan.FromSeconds(2), cancelled.Token).AsTask());
        var fail = new Replay(JsonNode.Parse("""{"step":1,"frames":[{"assets":["AUTO_SEARCH_MAP_OPTION_OFF"]}]}""")!) { FailClick = true };
        await Rejects<IOException>(() => new AutoSearchFlow(fail, fail, () => fail.Sequence).MoveAsync(TimeSpan.FromSeconds(3)).AsTask());
        var absent = new SettingsUi(GameServer.En, [0]) { SidebarAbsent = true };
        await Rejects<TimeoutException>(() => new CampaignAutoSearchSettings(absent, absent, () => absent.Image)
            .EnsureFleetOrderAsync(FleetOrder.Fleet1BossFleet2Mob).AsTask());
        Check(absent.Clicks.Count == 0 && absent.SidebarClicks > 0, "Missing sidebar caused a role selection");
        var menu = new Replay(JsonNode.Parse("""{"step":1,"frames":[{"menu":true},{"assets":["AUTO_SEARCH_MENU_EXIT"]},{"stage":true}]}""")!);
        var flow = new AutoSearchFlow(menu, menu, () => menu.Sequence);
        await Rejects<CampaignEndedException>(() => flow.MoveAsync(TimeSpan.FromSeconds(10)).AsTask());
        await flow.ExitMenuAsync(TimeSpan.FromSeconds(10), default);
        Check(flow.Evidence is { EndReason: "result_menu", EndFrame: 1, StageFrame: 3, Battles.Count: 0 }, "Menu exit invented combat settlement");
        var described = CampaignResumeTask.Describe("auto", "campaign_run", RuleCatalog.Create("campaign_main/campaign_1_1"),
            new(CampaignLoopExit.Ended, 0, null, AutoSearch: flow.Evidence), true);
        Check(described.Outcome == TaskOutcome.Failed && described.Reason == "sortie_settlement_unverified", "Automatic menu was treated as a clear");
    }

    private static async Task CounterChecksAsync()
    {
        foreach (string? operation in new[] { null, "auto_search_moving", "auto_search_combat:1" })
        {
            var scenario = new Scenario("campaign_main/campaign_1_1", AutoSearch: true, HandleError: true,
                Signal: "ended", SignalOperation: operation ?? "never");
            var probe = new ProbeOperations(scenario);
            var execution = new CampaignExecution(RuleCatalog.Create(scenario.Rule), new() { UseAutoSearch = true, HandleError = true }, probe);
            probe.State = execution.Context.State;
            Check(probe.State.AutoSearch, "Confirmed automatic mode did not reach the campaign state");
            var end = await execution.RunAsync();
            Check(probe.State.BattleCount == (operation is null ? 20 : 0) &&
                end == (operation is null ? CampaignLoopExit.Exhausted : CampaignLoopExit.Ended) &&
                !probe.Calls.Contains("map_init") && !probe.Calls.Contains("handle_map_fleet_lock"),
                "Auto-search battle was double-counted, counted after early end or used the manual map loop");
        }
    }

    private static async Task ResourcesAsync()
    {
        foreach (var server in Enum.GetValues<GameServer>())
        foreach (bool overlay in new[] { false, true })
        {
            var ui = new ResourceUi(server, overlay);
            var resources = new AutoSearchResources(ui, () => ui.Sequence, 1000);
            await resources.ObserveAsync(default);
            Check(resources.OilLimitTriggered && !resources.CoinLimitTriggered && ui.OcrCalls == 2,
                "Automatic resource thresholds or task balancer scope changed");
            await resources.ObserveAsync(default);
            Check(ui.OcrCalls == 2, "Resources were reread after confirmation in one move");
            resources.BeginMove(); ui.Oil = 1200; await ui.ScreenshotAsync(default);
            await resources.ObserveAsync(default);
            Check(!resources.OilLimitTriggered, "Recovered oil did not clear the prior OCR limit");
            resources.BeginMove(); ui.Oil = 0; await ui.ScreenshotAsync(default);
            await resources.ObserveAsync(default); ui.Oil = 400; await ui.ScreenshotAsync(default);
            await resources.ObserveAsync(default);
            Check(resources.OilLimitTriggered, "Zero oil reading prevented a later retry");
            resources.BeginMove(); ui.WrongFrame = true;
            await Rejects<InvalidDataException>(() => resources.ObserveAsync(default).AsTask());
        }
    }

    private sealed class Replay : AppearanceProbe, IAutoSearchHandlers
    {
        private readonly JsonNode _case;
        private readonly HashSet<(long, string)> _handled = [];
        public long Sequence { get; private set; } = 1;
        public bool Stale { get; init; }
        public bool FailClick { get; init; }
        public List<object> Clicks { get; } = [];
        private JsonNode Data => _case["frames"]![(int)Math.Min(Sequence - 1, _case["frames"]!.AsArray().Count - 1)]!;
        public Replay(JsonNode sample) : base(GameServer.Cn, null)
        { _case = sample; base.ScreenshotAsync(default).GetAwaiter().GetResult(); }
        public override ValueTask ScreenshotAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); if (!Stale) Sequence++; Time.Advance(_case["step"]!.GetValue<double>()); return ValueTask.CompletedTask; }
        public override ValueTask<bool> AppearsAsync(AssetRule asset, ButtonOffset offset = default, double interval = 0,
            double similarity = .85, int threshold = 10, TemplatePreprocessing preprocessing = TemplatePreprocessing.Color,
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            bool visible = Data["assets"]?.AsArray().Any(v => v!.GetValue<string>() == asset.Name) ?? false;
            if (asset.Name.StartsWith("EXP_INFO_", StringComparison.Ordinal) && Data["weak"]?.GetValue<bool>() == true && threshold < 30)
                visible = false;
            if (asset == UiAssets.Handler.AUTO_SEARCH_MENU_CONTINUE)
            {
                Check(offset == ButtonOffset.Expand(250, 30) && preprocessing == TemplatePreprocessing.Luma, "Auto-search menu matching drifted");
                visible |= Data["menu"]?.GetValue<bool>() ?? false;
            }
            if (interval > 0 && !Timer(asset, interval, renew: true).Reached()) return ValueTask.FromResult(false);
            if (visible && interval > 0) Timer(asset, interval).Reset();
            return ValueTask.FromResult(visible);
        }
        public override ValueTask ClickAsync(AssetRule asset, CancellationToken token)
        { token.ThrowIfCancellationRequested(); if (FailClick) throw new IOException("Synthetic click failure"); Clicks.Add(new { asset = asset.Name, frame = Sequence }); return ValueTask.CompletedTask; }
        private ValueTask<bool> Once(string name, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(Data[name]?.GetValue<bool>() == true && _handled.Add((Sequence, name))); }
        public ValueTask WatchAsync(bool firstObservation, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<bool> LoadingAsync(CancellationToken token) => ValueTask.FromResult(Data["loading"]?.GetValue<bool>() ?? false);
        public ValueTask<bool> InStageAsync(CancellationToken token) => ValueTask.FromResult(Data["stage"]?.GetValue<bool>() ?? false);
        public ValueTask<bool> RetirementAsync(CancellationToken token) => Once("retire", token);
        public ValueTask<bool> LowEmotionAsync(CancellationToken token) => Once("low", token);
        public ValueTask<bool> StoryAsync(CancellationToken token) => Once("story", token);
        public ValueTask<bool> CatAttackAsync(CancellationToken token) => Once("cat", token);
        public ValueTask<bool> ConfirmAsync(CancellationToken token) => Once("confirm", token);
        public ValueTask<bool> UrgentCommissionAsync(CancellationToken token) => Once("urgent", token);
        public ValueTask<bool> GuildPopupAsync(CancellationToken token) => Once("guild", token);
        public ValueTask<bool> MissionPopupAsync(CancellationToken token) => Once("mission", token);
    }

    private sealed class SettingsUi : AppearanceProbe, IImagePatchVision
    {
        private readonly HashSet<int> _active;
        private long _sequence = 1;
        public ScreenFrame Image => new(_sequence, DateTimeOffset.UnixEpoch, ReadOnlyMemory<byte>.Empty);
        public List<int> Clicks { get; } = [];
        public bool SidebarAbsent { get; init; }
        public int SidebarClicks { get; private set; }
        public SettingsUi(GameServer server, IEnumerable<int> active) : base(server, null)
        { _active = [.. active]; base.ScreenshotAsync(default).GetAwaiter().GetResult(); }
        public override ValueTask ScreenshotAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); _sequence++; Time.Advance(.5); return ValueTask.CompletedTask; }
        public override Rectangle ButtonArea(AssetRule asset) => asset.For(Server).ClickArea!.Value;
        public override ValueTask<bool> AppearsAsync(AssetRule asset, ButtonOffset offset = default, double interval = 0,
            double similarity = .85, int threshold = 10, TemplatePreprocessing preprocessing = TemplatePreprocessing.Color,
            CancellationToken token = default) => ValueTask.FromResult(asset == UiAssets.Map.FLEET_PREPARATION_CHECK);
        public ValueTask<ImagePatchObservation> MeasurePatchAsync(ScreenFrame frame, ImagePatchRequest request, CancellationToken token = default)
        {
            int value = 0;
            if (request.Color == new PixelColor(99, 235, 255))
                value = !SidebarAbsent && request.Area.Y == (Server == GameServer.En ? 277 : 377) ? 51 : 0;
            else if (request.Color == new PixelColor(255, 255, 255)) value = 101;
            else if (request.Color == new PixelColor(156, 255, 82))
                value = CampaignAutoSearchSettings.Settings.Where((a, i) => _active.Contains(i)).Any(a => ButtonArea(a).Area == request.Area) ? 21 : 0;
            return ValueTask.FromResult(new ImagePatchObservation(frame.Sequence, value));
        }
        public override ValueTask ClickAsync(AssetRule asset, CancellationToken token)
        {
            int index = Array.IndexOf(CampaignAutoSearchSettings.Settings, asset);
            Check(index >= 0, "Unexpected role click"); Clicks.Add(index);
            _active.RemoveWhere(i => index < 4 ? i < 4 : i >= 4); _active.Add(index);
            return ValueTask.CompletedTask;
        }
        public override ValueTask ClickAreaAsync(Rectangle area, CancellationToken token)
        { Check(area.Top == (Server == GameServer.En ? 277 : 377), "Sidebar variant changed"); SidebarClicks++; return ValueTask.CompletedTask; }
    }

    private sealed class ResourceUi(GameServer server, bool overlay) : AppearanceProbe(server, null)
    {
        public long Sequence { get; private set; } = 1;
        public bool WrongFrame { get; set; }
        public int Oil { get; set; } = 800;
        public int OcrCalls { get; private set; }
        public override Rectangle ButtonArea(AssetRule asset) => asset.For(Server).ClickArea!.Value;
        public override ValueTask ScreenshotAsync(CancellationToken token) { Sequence++; Time.Advance(.5); return ValueTask.CompletedTask; }
        public override ValueTask<MeanColorObservation> ColorAsync(Rectangle area, CancellationToken token)
        {
            var color = overlay ? new Rgb(59, 59, 64) : UiAssets.Campaign.OCR_OIL_CHECK.For(Server).Color!.Value;
            return ValueTask.FromResult(new MeanColorObservation(Sequence, color.R, color.G, color.B));
        }
        public override ValueTask<OcrObservation> ReadTextAsync(OcrRequest request, CancellationToken token)
        {
            OcrCalls++;
            bool oil = request.Area == UiAssets.Campaign.OCR_OIL.For(Server).Area!.Value.Area;
            int expected = oil ? overlay ? 165 : Server == GameServer.Jp ? 201 : 247 : Server == GameServer.Jp ? 201 : 239;
            Check(request.LetterR == expected && request.LetterG == expected && request.LetterB == expected && request.Threshold == 128,
                "Resource OCR server or overlay parameters drifted");
            return ValueTask.FromResult(new OcrObservation(Sequence - (WrongFrame ? 1 : 0), (oil ? Oil : 8000).ToString(), 1));
        }
    }
}
