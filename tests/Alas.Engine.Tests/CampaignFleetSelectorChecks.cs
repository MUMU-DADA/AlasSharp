using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static class CampaignFleetSelectorChecks
{
    private sealed record Case(string[][] Frames, int Fleet2 = 2, string Order = "fleet1_mob_fleet2_boss",
        int Target = 1, bool Initialize = false, double Step = .5, bool NativeAssumesSuccess = false);
    private sealed record Call(string Asset, int[] Offset, int Frame);
    private sealed record Click(string Asset, int Frame);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task RunAsync(string python, string upstream, string artifacts)
    {
        var samples = new List<Case>();
        foreach (var order in Enum.GetValues<FleetOrder>())
        foreach (int fleet2 in new[] { 0, 2 })
        foreach (int target in fleet2 == 0 ? new[] { 1 } : new[] { 1, 2 })
        foreach (int initial in fleet2 == 0 ? new[] { 1 } : new[] { 1, 2 })
        foreach (double step in new[] { .125, .5, 2.0 })
        {
            var config = new CampaignConfiguration { Fleet2 = fleet2, FleetOrder = order };
            int desired = FleetRoles.LogicalIndex(target, config);
            samples.Add(new([["FLEET_NUM_" + initial, "SWITCH_OVER"], ["FLEET_NUM_" + desired]],
                fleet2, FleetRoles.Name(order), target, Step: step));
        }
        foreach (var order in Enum.GetValues<FleetOrder>())
        foreach (int initial in new[] { 1, 2 })
            samples.Add(new([["FLEET_NUM_" + initial, "SWITCH_OVER"], ["FLEET_NUM_" + (3 - initial), "SWITCH_OVER"]],
                Order: FleetRoles.Name(order), Initialize: true));
        samples.Add(new([["$story"], ["FLEET_NUM_2", "SWITCH_OVER"], ["FLEET_NUM_1"]]));
        samples.Add(new([["MAP_PREPARATION"], ["FLEET_NUM_1"]], NativeAssumesSuccess: true));
        samples.Add(new([["MAP_PREPARATION", "FLEET_NUM_2"], ["FLEET_NUM_2", "SWITCH_OVER"], ["FLEET_NUM_1"]]));
        samples.Add(new([["FLEET_NUM_2"], ["CAMPAIGN_CHECK", "$entrance", "FLEET_NUM_2"],
            ["CAMPAIGN_CHECK", "$entrance", "FLEET_NUM_2"], ["CAMPAIGN_CHECK", "$entrance", "$info"],
            ["CAMPAIGN_CHECK", "$entrance"]]));
        samples.Add(new([["EVENT_CHECK", "$entrance"]]));
        samples.Add(new([[], [], ["FLEET_NUM_1"]], NativeAssumesSuccess: true));
        samples.Add(new([[]], NativeAssumesSuccess: true));
        samples.Add(new([["FLEET_NUM_2"]], NativeAssumesSuccess: true));
        string input = Path.Combine(artifacts, "fleet-inputs.json"), output = Path.Combine(artifacts, "fleet-native.json");
        await File.WriteAllTextAsync(input, JsonSerializer.Serialize(samples, Json));
        var response = await new ProcessRunner().RunAsync(python,
            [Path.Combine(AppContext.BaseDirectory, "native_fleet_selection_reference.py"), upstream, input, output],
            TimeSpan.FromSeconds(60));
        Check(response.ExitCode == 0, "Native fleet selection failed: " + response.Error);
        var native = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
        foreach (var source in new[] { CampaignFleetSelector.Source, MapUiRecovery.StageSource, UiRecovery.InfoSource })
            Check(native["sources"]![source.Path]!.GetValue<string>() == source.Sha256, "Fleet source drifted");
        int equal = 0, stricter = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];
            var ui = new Replay(sample);
            var selector = Selector(ui);
            var configuration = new CampaignConfiguration { Fleet2 = sample.Fleet2, FleetOrder = FleetRoles.Parse(sample.Order) };
            FleetSelection? selected = null;
            string? error = null;
            try
            {
                selected = sample.Initialize ? await selector.InitializeAsync(configuration, TimeSpan.FromSeconds(90)) :
                    await selector.SelectAsync(sample.Target, configuration, TimeSpan.FromSeconds(90));
            }
            catch (CampaignEndedException) { error = "CampaignEnd"; }
            catch (TimeoutException) { error = "TimeoutException"; }
            var expected = native["results"]![i]!;
            if (sample.NativeAssumesSuccess)
            {
                Check(expected["error"] is null, "Native assumption fixture did not return");
                Check(ui.Clicks.All(click => click.Asset == "MAP_PREPARATION_CANCEL") &&
                    (selected is { FrameSequence: > 1 } || error == "TimeoutException"),
                    $"Unknown number or unconfirmed switch was accepted as a fleet identity in case {i}");
                stricter++;
                continue;
            }
            Check(error == expected["error"]?.GetValue<string>() &&
                ui.Frame == expected["frames"]!.GetValue<int>() &&
                JsonNode.DeepEquals(JsonSerializer.SerializeToNode(ui.Trace, Json), expected["calls"]) &&
                JsonNode.DeepEquals(JsonSerializer.SerializeToNode(ui.Clicks, Json), expected["clicks"]) &&
                JsonNode.DeepEquals(JsonSerializer.SerializeToNode(ui.Delays, Json), expected["delays"]),
                $"Native fleet control trace differs in case {i}: {JsonSerializer.Serialize(ui.Trace, Json)}");
            if (selected is not null)
                Check(selected.LogicalIndex == expected["logical"]!.GetValue<int>() &&
                    selected.DisplayedIndex == expected["displayed"]!.GetValue<int>() &&
                    (selected.Clicks > 0) == expected["changed"]!.GetValue<bool>() && selected.FrameSequence == ui.Frame + 1,
                    "Selected fleet identity differs from upstream");
            equal++;
        }

        var stale = new Replay(new([["FLEET_NUM_2", "SWITCH_OVER"], ["FLEET_NUM_1"]])) { Stale = true };
        await Rejects<InvalidDataException>(() => Selector(stale).SelectAsync(1, new() { Fleet2 = 2 }, TimeSpan.FromSeconds(30)).AsTask());
        var cancelled = new Replay(new([["FLEET_NUM_2", "SWITCH_OVER"]]));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Rejects<OperationCanceledException>(() => Selector(cancelled).SelectAsync(1, new() { Fleet2 = 2 },
            TimeSpan.FromSeconds(30), cancellation.Token).AsTask());
        Check(cancelled.Trace.Count == 0 && cancelled.Clicks.Count == 0, "Cancelled fleet selection touched the device");
        await Rejects<ArgumentOutOfRangeException>(() => Selector(cancelled).SelectAsync(2, new(), TimeSpan.FromSeconds(30)).AsTask());
        var repeated = new Replay(new([["FLEET_NUM_2", "SWITCH_OVER"]]));
        await Rejects<TimeoutException>(() => Selector(repeated).SelectAsync(1, new() { Fleet2 = 2 }, TimeSpan.FromSeconds(8)).AsTask());
        Check(repeated.Clicks.Count > 1, "Selection timeout was not tested across repeated clicks");
        await CampaignMapInitializerChecks.RunAsync();
        await CampaignMapCombatChecks.RunAsync(python, upstream);
        await SessionReplayAsync(python, upstream, artifacts);
        Console.WriteLine($"Initial fleet selection: {equal} exact native traces, {stricter} explicit unknown/timeout corrections, identity/path integration and 2 real-CV session replays passed; synthetic ADB, no game actions.");
    }

    private static async Task SessionReplayAsync(string python, string upstream, string artifacts)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Alas.Engine.Tests.exe" : "Alas.Engine.Tests");
        string? previous = Environment.GetEnvironmentVariable("ALAS_TEST_ADB_FIXTURE");
        try
        {
            foreach (bool reversed in new[] { false, true })
            {
                string folder = Path.Combine(artifacts, "fleet-session-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                string fixture = Path.Combine(folder, "fixture.json");
                await File.WriteAllTextAsync(fixture, JsonSerializer.Serialize(new
                {
                    first = Path.Combine(artifacts, reversed ? "fleet-1.png" : "fleet-2.png"),
                    second = Path.Combine(artifacts, reversed ? "fleet-2.png" : "fleet-1.png"),
                    failTap = false, advance = true
                }));
                Environment.SetEnvironmentVariable("ALAS_TEST_ADB_FIXTURE", fixture);
                await using var session = new EngineSession(new(executable, "offline-replay", GameServer.Cn,
                    Path.Combine(upstream, "assets"), python, "org.example.game", AllowActions: true));
                var host = (ICampaignInMapHost)session;
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                Check(await host.VerifyInMapAsync(limit.Token), "Synthetic fleet fixture was not in map");
                var selected = await host.PrepareInitialFleetAsync(new()
                {
                    Fleet2 = 2, Fleet1Formation = FleetFormation.Diamond, Fleet2Formation = FleetFormation.DoubleLine,
                    FleetOrder = reversed ? FleetOrder.Fleet1BossFleet2Mob : FleetOrder.Fleet1MobFleet2Boss,
                    WaitForFleetSwitchInfoBar = true
                }, limit.Token);
                Check(selected is { LogicalIndex: 1, Clicks: 1, FrameSequence: > 1 } &&
                    selected.DisplayedIndex == (reversed ? 2 : 1), "Session did not preserve observed fleet roles");
                var boundary = await session.SaveEvidenceAsync(folder, false);
                Check(boundary.ActionAttempts == 1, "Session used the wrong fleet's formation or repeated the switch");
                var actions = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "actions.json")))!.AsArray();
                int x = actions[0]!["parameters"]!["x"]!.GetValue<int>(), y = actions[0]!["parameters"]!["y"]!.GetValue<int>();
                var area = UiAssets.Map.SWITCH_OVER.For(GameServer.Cn).ClickArea!.Value;
                Check(x >= area.Left && x <= area.Right && y >= area.Top && y <= area.Bottom &&
                    actions[0]!["completed"]!.GetValue<bool>(), "Session clicked outside the upstream fleet switch");
            }
        }
        finally { Environment.SetEnvironmentVariable("ALAS_TEST_ADB_FIXTURE", previous); }
    }

    private static CampaignFleetSelector Selector(Replay ui)
    {
        var guard = new MapUiRecovery(ui, ui, new NoApplication(), ui, new NoPopup());
        return new(ui, ui, guard.HandleInStageAsync, () => ui.Stale ? 1 : ui.Frame + 1, new MidpointRandom());
    }
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static async Task Rejects<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private sealed class MidpointRandom : Random { public override double NextDouble() => .5; }
    private sealed class NoPopup : IPopupHandler { public ValueTask<bool> ConfirmAsync(CancellationToken token) => throw new InvalidOperationException(); }
    private sealed class NoApplication : IApplicationHealth
    {
        public ValueTask<bool> IsRunningAsync(CancellationToken token) => throw new InvalidOperationException();
        public ValueTask RefreshOrientationAsync(CancellationToken token) => throw new InvalidOperationException();
        public ValueTask StopAsync(CancellationToken token) => throw new InvalidOperationException();
    }
    private sealed class Replay : AppearanceProbe, IStoryHandler, IMapUiObservations
    {
        private readonly Case _case;
        public int Frame { get; private set; }
        public bool Stale { get; init; }
        public List<Call> Trace { get; } = [];
        public List<Click> Clicks { get; } = [];
        public List<double> Delays { get; } = [];
        public Replay(Case sample) : base(GameServer.Cn, null)
        { _case = sample; base.ScreenshotAsync(default).GetAwaiter().GetResult(); }
        private bool Positive(string name) => _case.Frames[Math.Min(Frame, _case.Frames.Length - 1)].Contains(name);
        public override ValueTask ScreenshotAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Frame++; Time.Advance(_case.Step);
            if (Frame > 250) throw new InvalidOperationException("Fleet replay did not terminate");
            return ValueTask.CompletedTask;
        }
        public override ValueTask<bool> AppearsAsync(AssetRule asset, ButtonOffset offset = default, double interval = 0,
            double similarity = .85, int threshold = 10, TemplatePreprocessing preprocessing = TemplatePreprocessing.Color,
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Trace.Add(new(asset.Name, [offset.Left, offset.Top, offset.Right, offset.Bottom], Frame));
            return ValueTask.FromResult(Positive(asset.Name));
        }
        public override ValueTask ClickAsync(AssetRule asset, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Clicks.Add(new(asset.Name, Frame)); return ValueTask.CompletedTask; }
        public override ValueTask DelayAsync(TimeSpan time, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Delays.Add(time.TotalSeconds); Time.Advance(time.TotalSeconds); return ValueTask.CompletedTask; }
        public ValueTask<bool> StorySkipAsync(CancellationToken token = default)
        { Trace.Add(new("$story", [], Frame)); return ValueTask.FromResult(Positive("$story")); }
        public ValueTask EnsureNoStoryAsync(bool skipFirstScreenshot, CancellationToken token) => throw new InvalidOperationException();
        public ValueTask<int> InfoBarCountAsync(CancellationToken token)
        { Trace.Add(new("$info", [], Frame)); return ValueTask.FromResult(Positive("$info") ? 1 : 0); }
        public ValueTask<bool> HasStageEntranceAsync(CancellationToken token)
        { Trace.Add(new("$entrance", [], Frame)); return ValueTask.FromResult(Positive("$entrance")); }
    }
}
