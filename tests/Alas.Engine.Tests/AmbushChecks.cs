using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static partial class AmbushChecks
{
    public static async Task RunAsync(string python, string upstream, string artifacts)
    {
        var assets = new AssetFiles(Path.Combine(upstream, "assets"));
        await using var vision = new PythonTemplateVision(python,
            Path.Combine(AppContext.BaseDirectory, "Imaging", "Worker", "vision_worker.py"));
        int traces = 0, fixtures = 0;
        foreach (var server in Enum.GetValues<GameServer>())
        {
            string output = Path.Combine(artifacts, server.ToString() + ".json");
            var process = await new ProcessRunner().RunAsync(python,
                [Path.Combine(AppContext.BaseDirectory, "native_ambush_reference.py"), upstream,
                    server.ToString().ToLowerInvariant(), output], TimeSpan.FromMinutes(1));
            Check(process.ExitCode == 0, "Native ambush reference failed: " + process.Error);
            var native = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
            foreach (var source in new[] { MapAmbushHandler.Source, MapAmbushInfo.Source, MapUiRecovery.StageSource, CampaignState.InitializationSource })
                Check(native["sources"]![source.Path]!.GetValue<string>() == source.Sha256, "Ambush source drift: " + source.Path);
            if (server == GameServer.Cn)
            {
                foreach (var entry in native["cases"]!.AsArray())
                {
                    var ui = new Replay(entry!["sample"]!);
                    var result = (await ui.Handler().HandleAsync(MapEncounterKind.Ambush, default)).Ambush!;
                    Check(result.IsConsistent && result.CanContinue && ui.Frames == entry["frames"]!.GetValue<int>() &&
                        ui.Clicks.SequenceEqual(entry["clicks"]!.AsArray().Select(x => x!.GetValue<int>())) &&
                        ui.Trace.SequenceEqual(entry["trace"]!.AsArray().Select(x => x!.GetValue<string>())) &&
                        result.FleetStatusRefreshed == entry["handled"]!.GetValue<bool>() &&
                        ui.StatusReads == (result.FleetStatusRefreshed ? 1 : 0),
                        $"Ambush handler differs: {entry["sample"]}; frames={ui.Frames}, trace={string.Join(',', ui.Trace)}");
                    traces++;
                }
                await CombatFlowChecks.AmbushReturnsAsync(native["returns"]!.AsArray());
                await CampaignMapCombatChecks.AmbushNativeWalksAsync(native["walks"]!.AsArray());
            }
            foreach (var fixture in native["fixtures"]!.AsArray())
            {
                var frame = new ScreenFrame(1, DateTimeOffset.UtcNow,
                    await File.ReadAllBytesAsync(Path.Combine(artifacts, fixture!["image"]!.GetValue<string>())));
                var message = await new MapAmbushInfo(vision, assets, () => frame, server).ReadAsync(default);
                Check(message.ToString().ToLowerInvariant() == fixture["message"]!.GetValue<string>(), "Ambush info decision differs: " + fixture);
                int i = 0;
                foreach (var template in new[] { UiAssets.Template.TEMPLATE_AMBUSH_EVADE_SUCCESS, UiAssets.Template.TEMPLATE_AMBUSH_EVADE_FAILED })
                {
                    var observation = await vision.MatchAsync(frame, new(await assets.ReadAsync(template.For(server)),
                        UiAssets.Handler.INFO_BAR_DETECT.For(server).Area!.Value.Area, .85, Transform: new(64, .75)));
                    Check(Math.Abs(observation.Similarity - fixture["scores"]![i++]!.GetValue<double>()) < .00001,
                        "Numeric pixel transform differs from original native template matching");
                }
                fixtures++;
            }
        }
        await FailureChecksAsync();
        await CampaignMapCombatChecks.AmbushMovementChecksAsync();
        await InputChecksAsync();
        await MapEncounterProbeChecks.RunAsync();
        Console.WriteLine($"Ambush: {traces} native handler traces, {fixtures} original-asset fixtures across four servers, 10 native return timers, 12 native walks, movement accounting/retry and input/evidence failures passed offline; no live device acceptance.");
    }

    private static async Task FailureChecksAsync()
    {
        foreach (string failure in new[] { "cancel", "stale", "timeout", "click", "message_frame", "message_value", "combat", "status" })
        {
            var ui = new Replay(Sample()) { Failure = failure };
            using var cancel = new CancellationTokenSource();
            if (failure == "cancel") cancel.Cancel();
            bool failed = false;
            try { await ui.Handler().HandleAsync(MapEncounterKind.Ambush, cancel.Token); }
            catch (Exception error) when (error is OperationCanceledException or InvalidDataException or TimeoutException or IOException) { failed = true; }
            Check(failed, "Ambush failure was swallowed: " + failure);
            if (failure is "cancel" or "stale" or "timeout") Check(ui.Clicks.Count == 0, "Failed preparation clicked");
        }
        foreach (var rank in new CombatRank?[] { null, CombatRank.C, CombatRank.D })
        {
            var ui = new Replay(Sample()) { Rank = rank };
            var result = (await ui.Handler().HandleAsync(MapEncounterKind.Ambush, default)).Ambush!;
            Check(!result.CanContinue && !result.FleetStatusRefreshed && ui.StatusReads == 0,
                "Missing/losing ambush rank permitted further movement");
        }
        var unknown = new Replay(Sample());
        Check((await unknown.Handler().HandleAsync(MapEncounterKind.UnknownPage, default)).Continuation == MapEncounterContinuation.Unhandled &&
            unknown.Frames == 0, "Unknown page was treated as an ambush");
        // A short preparation timeout has no ownership of the subsequent battle.
        var longBattle = new Replay(Sample()) { LongBattle = true };
        Check((await longBattle.Handler().HandleAsync(MapEncounterKind.Ambush, default)).Ambush!.CanContinue,
            "Preparation deadline leaked into combat");
    }

    private static async Task InputChecksAsync()
    {
        foreach (var runner in new ITaskRunner[] { new CampaignRunTask(), new CampaignResumeTask() })
        foreach (var value in new JsonNode?[] { null, JsonValue.Create("false"), JsonValue.Create(0), new JsonObject() })
        {
            var input = new JsonObject { ["campaign"] = "campaign_main/campaign_1_1", ["ambushEvade"] = value?.DeepClone() };
            if (runner is CampaignRunTask) { input["fleet1"] = 1; input["fleet2"] = 0; input["submarine"] = 0; }
            bool rejected = false;
            try { runner.Validate(input); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or FormatException) { rejected = true; }
            Check(rejected, "Task accepted malformed ambushEvade: " + value);
        }
        foreach (bool? evade in new bool?[] { null, false, true })
        {
            var input = new JsonObject { ["campaign"] = "campaign_main/campaign_1_1" };
            if (evade is not null) input["ambushEvade"] = evade.Value;
            var service = new Service();
            var runner = new CampaignResumeTask();
            await runner.RunAsync(new("test", runner.Kind, input),
                new(null!, null!, null!, TimeSpan.FromSeconds(5), Campaign: service), default);
            Check(service.Configuration!.AmbushEvade == (evade ?? true), "Resume lost ambush input/default");
        }
    }
    private sealed class Service : ICampaignExecutionService
    {
        public CampaignConfiguration? Configuration { get; private set; }
        public ValueTask<CampaignResumeResult> ResumeInMapAsync(CampaignRule rule, CampaignConfiguration configuration, CancellationToken token)
        { Configuration = configuration; return ValueTask.FromResult(new CampaignResumeResult(CampaignLoopExit.Ended, 0, null)); }
    }
    private static JsonObject Sample() => new() { ["evade"] = true, ["delay"] = 0, ["stale"] = false,
        ["overlay"] = true, ["message"] = "failed" };
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(double seconds = .25) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }
    private sealed class Replay(JsonNode sample) : IUiDriver, IMapUiObservations, ICampaignInterruptions
    {
        private readonly Clock _clock = new();
        private readonly Dictionary<string, IntervalTimer> _intervals = [];
        public TimeProvider Clock => _clock;
        public GameServer Server => GameServer.Cn;
        public bool HasFrame => true;
        public int Frames { get; private set; }
        public long Sequence { get; private set; } = 1;
        public int StatusReads { get; private set; }
        public List<int> Clicks { get; } = [];
        public List<string> Trace { get; } = [];
        public string? Failure { get; init; }
        public bool LongBattle { get; init; }
        public CombatRank? Rank { get; init; } = CombatRank.S;
        public MapAmbushHandler Handler() => new(this, this, () => Sequence, ReadMessage, Combat,
            sample["evade"]!.GetValue<bool>(), () => sample["overlay"]!.GetValue<bool>(), ReadStatus, this);
        private ValueTask<AmbushMessage> ReadMessage(CancellationToken token)
        {
            if (Failure == "message_frame") Sequence++;
            return ValueTask.FromResult(Failure == "message_value" ? (AmbushMessage)99 : sample["message"]!.GetValue<string>() switch
            { "evaded" => AmbushMessage.Evaded, "failed" => AmbushMessage.Failed, _ => AmbushMessage.Unknown });
        }
        private ValueTask<CombatFlowResult> Combat(bool searching, CancellationToken token)
        {
            if (LongBattle) { _clock.Advance(120); token.ThrowIfCancellationRequested(); }
            if (Failure == "combat") throw new IOException("Synthetic battle failure");
            Trace.Add("combat:" + (searching ? "default" : "no_searching"));
            CombatRankEvidence? rank = Rank is { } r ? new(r, CombatRankSource.BattleStatus, (r switch
            { CombatRank.S => UiAssets.Combat.BATTLE_STATUS_S, CombatRank.C => UiAssets.Combat.BATTLE_STATUS_C,
                CombatRank.D => UiAssets.Combat.BATTLE_STATUS_D, _ => throw new InvalidOperationException() }).Id) : null;
            return ValueTask.FromResult(new CombatFlowResult(CombatReturn.InMap, rank, false, false, 1));
        }
        private ValueTask ReadStatus(CancellationToken token)
        { if (Failure == "status") throw new IOException("Synthetic HP failure"); StatusReads++; return ValueTask.CompletedTask; }
        public ValueTask ScreenshotAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); Frames++; if (Failure != "stale") Sequence++; _clock.Advance(); return ValueTask.CompletedTask; }
        public ValueTask<bool> AppearsAsync(AssetRule asset, ButtonOffset offset = default, double interval = 0,
            double similarity = .85, int threshold = 10, TemplatePreprocessing preprocessing = TemplatePreprocessing.Color, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (asset == UiAssets.Combat.BATTLE_PREPARATION || asset == UiAssets.Combat.BATTLE_PREPARATION_WITH_OVERLAY)
                return ValueTask.FromResult(Clicks.Count > 0 && Frames >= Clicks[0] + 17 &&
                    (!sample["evade"]!.GetValue<bool>() || sample["message"]!.GetValue<string>() == "unknown_combat"));
            Check(offset == ButtonOffset.Expand(30, 30), "Ambush button offset changed");
            if (interval > 0 && !Timer(asset, interval).Reached()) return ValueTask.FromResult(false);
            bool visible = Failure != "timeout" && Frames >= sample["delay"]!.GetValue<int>() + 1;
            if (visible && interval > 0) Timer(asset, interval).Reset();
            return ValueTask.FromResult(visible);
        }
        public ValueTask ClickAsync(AssetRule asset, CancellationToken token)
        { if (Failure == "click") throw new IOException("Synthetic click failure"); Clicks.Add(Frames); Trace.Add("click:" + Frames); return ValueTask.CompletedTask; }
        public ValueTask<int> InfoBarCountAsync(CancellationToken token)
        {
            int delay = sample["delay"]!.GetValue<int>();
            bool visible = Clicks.Count == 0 ? sample["stale"]!.GetValue<bool>() && Frames >= delay + 1 && Frames < delay + 4 :
                Frames >= Clicks[0] + 17 && Frames < Clicks[0] + 19;
            return ValueTask.FromResult(visible ? 1 : 0);
        }
        public ValueTask<bool> HasStageEntranceAsync(CancellationToken token) => ValueTask.FromResult(false);
        public void Configure(RetirementOptions retirement, CampaignEmotionMode emotion) => throw new NotSupportedException();
        public ValueTask<bool> LowEmotionAsync(CancellationToken token)
        { Trace.Add("emotion:" + Frames); return ValueTask.FromResult(Frames % 2 == 0); }
        public ValueTask<bool> RetirementAsync(CancellationToken token)
        { Trace.Add("retirement:" + Frames); return ValueTask.FromResult(true); }
        public IntervalTimer Timer(AssetRule asset, double seconds = 5, bool renew = false)
        { if (!_intervals.TryGetValue(asset.Id, out var timer)) _intervals[asset.Id] = timer = new(_clock, seconds); return timer; }
        public void ResetInterval(AssetRule asset, double seconds = 3) => Timer(asset, seconds).Reset();
        public void ClearInterval(AssetRule asset) => Timer(asset).Clear();
        public void ClearOffset(AssetRule asset) => throw new NotSupportedException();
        public ValueTask ClickAreaAsync(Rectangle area, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<MeanColorObservation> ColorAsync(Rectangle area, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<ColorBandObservation> ColorBandsAsync(ColorBandRequest request, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<OcrObservation> ReadTextAsync(OcrRequest request, CancellationToken token) => throw new NotSupportedException();
        public ValueTask DelayAsync(TimeSpan time, CancellationToken token) { _clock.Advance(time.TotalSeconds); return ValueTask.CompletedTask; }
    }
}
