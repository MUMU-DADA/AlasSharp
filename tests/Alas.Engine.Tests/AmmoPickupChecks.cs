using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static class AmmoPickupChecks
{
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    public static async Task RunAsync(string python, string upstream, string artifacts)
    {
        var native = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(artifacts, "native-ammo.json")))!;
        foreach (var source in new[] { MapAmmoProbe.Source, UiRecovery.InfoSource })
            Check(native["sources"]![source.Path]!.GetValue<string>() == source.Sha256, "Ammo handler source drifted");
        Check(native["pickups"]!.AsArray().Count == 48, "Native supply reference omitted cases");
        int compared = 0;
        foreach (var sample in native["pickups"]!.AsArray())
        {
            var state = State(sample!["battles"]!.GetValue<int>());
            var supply = state[new(4, 1)];
            supply.IsAmmo = sample["visible"]!.GetValue<bool>();
            if (sample["current"]!.GetValue<bool>())
            {
                state[new(1, 1)].IsFleet = false;
                state.Fleet1Location = new(4, 1);
                supply.IsFleet = true;
                Costs(state);
            }
            var run = new Harness(state, sample["notification"]!.GetValue<bool>());
            foreach (var expected in sample["states"]!.AsArray())
            {
                bool result = await run.Combat.PickUpAmmoAsync();
                Check(result == expected!["returned"]!.GetValue<bool>() &&
                    state.BattleCount == expected["battle"]!.GetValue<int>() &&
                    state.SirenCount == expected["siren"]!.GetValue<int>() &&
                    state.MysteryCount == expected["mystery"]!.GetValue<int>() &&
                    state.AmmoCount == expected["stock"]!.GetValue<int>() &&
                    state.FleetAmmo == expected["fleet"]!.GetValue<int>() &&
                    state.Fleet1Location?.ToString() == expected["location"]!.GetValue<string>() &&
                    run.Trace.SequenceEqual(expected["trace"]!.AsArray().Select(value => value!.GetValue<string>())),
                    $"Native supply flow differs in sample {sample}");
                compared++;
            }
            Check(run.Combat.AmmoPickups.All(pickup => pickup.ArrivalSequence > 0 &&
                pickup.SettledSequence > pickup.ArrivalSequence && pickup.ExpectedRecovered == pickup.FleetAfter - pickup.FleetBefore &&
                pickup.StockBefore - pickup.StockAfter == pickup.ExpectedRecovered), "Supply evidence lost confirmed frames or accounting");
        }
        await FailureChecksAsync();
        await ProbeChecksAsync();
        await OperationChecksAsync();
        var partial = State(2);
        var partialRun = new Harness(partial);
        await partialRun.Combat.PickUpAmmoAsync();
        for (int i = 0; i < 3; i++) partial.CommitBattle(false);
        await partialRun.Combat.PickUpAmmoAsync();
        Check(partial.AmmoCount == native["partial"]!["stock"]!.GetValue<int>() &&
            partial.FleetAmmo == native["partial"]!["fleet"]!.GetValue<int>(), "Partial supply accounting was silently clamped");
        await VisualChecksAsync(python, upstream, artifacts, native["images"]!.AsArray());
        Console.WriteLine($"Supply pickup: {compared} native visit/state/ordered-action comparisons, partial stock, 4 real-CV observations, passive notification, failure and campaign-operation checks passed; no device action or measured reward quantity.");
    }

    private static async Task VisualChecksAsync(string python, string upstream, string artifacts, JsonArray images)
    {
        Check(images.Count == 4, "Native ammo visual fixtures are incomplete");
        await using var vision = new PureVisionWorker(python,
            Path.Combine(AppContext.BaseDirectory, "Imaging/Worker/vision_worker.py"));
        var files = new AssetFiles(Path.Combine(upstream, "assets"));
        long sequence = 0;
        foreach (var sample in images)
        {
            var frame = new ScreenFrame(++sequence, DateTimeOffset.UnixEpoch,
                await File.ReadAllBytesAsync(Path.Combine(artifacts, sample!["file"]!.GetValue<string>())));
            var ui = new UiDriver(GameServer.Cn, new ImageDevice(frame), vision, files);
            await ui.ScreenshotAsync(default);
            var observations = new MapUiObservations(() => ui.Frame!, vision, files, ui.Server);
            var probe = new MapAmmoProbe(ui, observations, () => ui.Frame!.Sequence);
            Check(await observations.InfoBarCountAsync(default) == sample["bars"]!.GetValue<int>() &&
                await probe.ObserveAsync(frame.Sequence, default) == sample["present"]!.GetValue<bool>(),
                "Real CV ammo observation differs from native image recognition");
        }
    }

    private static CampaignState State(int battles)
    {
        var state = new CampaignState(new MapDefinition("D1", "SP -- -- MA", ["A1"], ["A1"], []));
        state.InitializeMapData(new(PoorMapData: true));
        state.Fleet1Location = new(1, 1);
        state[new(1, 1)].IsFleet = state[new(1, 1)].IsCurrentFleet = true;
        for (int i = 0; i < battles; i++) state.CommitBattle(false);
        Costs(state);
        return state;
    }
    private static void Costs(CampaignState state) => state.Paths.ComputeFleetCosts(
        [new(1, state.Fleet1Location), new(2, state.Fleet2Location)],
        (state.FleetIndex == 1 ? state.Fleet1Location : state.Fleet2Location)!.Value, false);

    public static async Task VerifyDeclaredSupplyAsync(CampaignState state)
    {
        var run = new Harness(state);
        Check(!await run.Combat.PickUpAmmoAsync() && run.Combat.AmmoPickups.Count == 1,
            "Declared supply was skipped without an observed icon, or reported a battle");
    }
    public static async Task VerifyUnreachableFirstAsync(CampaignState state)
    {
        var run = new Harness(state);
        Check(!await run.Combat.PickUpAmmoAsync() && run.Trace.Count == 0,
            "Supply selection skipped an unreachable first declaration for a later tile");
    }

    private static async Task FailureChecksAsync()
    {
        foreach (string failure in new[] { "first-tap", "second-tap", "wait", "marker", "stale", "ack-stale", "settled-stale", "cancel" })
        {
            var state = State(3);
            var run = new Harness(state) { Failure = failure };
            using var cancellation = new CancellationTokenSource();
            if (failure == "cancel") cancellation.Cancel();
            bool rejected = false;
            try { await run.Combat.PickUpAmmoAsync(cancellation.Token); }
            catch (Exception error) when (error is IOException or InvalidDataException or CampaignScriptException or OperationCanceledException)
            { rejected = true; }
            Check(rejected && state is { AmmoCount: 3, FleetAmmo: 2, BattleCount: 3 } && run.Combat.AmmoPickups.Count == 0,
                "Unconfirmed supply changed stock or produced evidence: " + failure);
            Check(state.Fleet1Location == (failure is "wait" or "settled-stale" ? new Cell(4, 1) : new Cell(1, 1)),
                "Supply failure lost the distinction between confirmed arrival and inventory update: " + failure);
            Check(failure == "cancel" ? run.Trace.Count == 0 : run.Camera.Invalidated,
                "Failed supply left stale geometry usable: " + failure);
        }
        var invalid = State(3);
        invalid[new(4, 1)].IsEnemy = true;
        var rejectedRun = new Harness(invalid);
        bool enemyRejected = false;
        try { await rejectedRun.Combat.PickUpAmmoAsync(); }
        catch (ArgumentException) { enemyRejected = true; }
        Check(enemyRejected && rejectedRun.Trace.Count == 0, "Supply action silently fought an unexpected enemy");
        var absent = State(3);
        absent[new(4, 1)].MayAmmo = false;
        var absentRun = new Harness(absent);
        Check(!await absentRun.Combat.PickUpAmmoAsync() && absentRun.Trace.Count == 0, "Absent supply clicked a grid");
        var route = new CampaignState(new MapDefinition("C2", "SP -- --\n-- -- MA", [], [], [],
            walls: [new("A1", "A2"), new("B1", "B2")]));
        route.InitializeMapData(new(PoorMapData: true, Walls: true));
        route.Fleet1Location = new(1, 1);
        route[new(1, 1)].IsFleet = true;
        for (int i = 0; i < 3; i++) route.CommitBattle(false);
        Costs(route);
        var routeRun = new Harness(route, hasAmbush: true);
        await routeRun.Combat.PickUpAmmoAsync();
        Check(routeRun.Trace.SequenceEqual(["tap:C1", "tap:C2", "tap:C2", "wait:start", "wait:end"]) &&
            route is { FleetAmmo: 5, AmmoCount: 0 }, "Supply did not follow route waypoints before its final acknowledgement");
    }

    private static async Task ProbeChecksAsync()
    {
        var ui = new ProbeUi();
        var probe = new MapAmmoProbe(ui, ui, () => ui.Sequence);
        ui.Ammo = true;
        Check(!await probe.ObserveAsync(1, default) && ui.Calls.Count == 0, "Ammo template was tested without a blue information bar");
        ui.Info = true;
        Check(await probe.ObserveAsync(1, default), "Initial ammo notification was not observed");
        ui.Advance(3);
        Check(!await probe.ObserveAsync(1, default), "Ammo debounce fired at exactly three seconds");
        ui.Advance(.001);
        Check(await probe.ObserveAsync(1, default), "Ammo debounce did not fire after three seconds");
        ui.Advance(4); ui.Ammo = false;
        Check(!await probe.ObserveAsync(1, default), "Information bar alone became an ammo observation");
        ui.Ammo = true;
        Check(await probe.ObserveAsync(1, default), "Failed template match reset ammo notification timing");
        bool stale = false;
        try { await probe.ObserveAsync(2, default); } catch (InvalidDataException) { stale = true; }
        Check(stale, "Ammo observation accepted another frame");
        ui.Advance(4);
        var combined = new MapEncounterProbe(ui, false, probe);
        ui.Items = true;
        Check(await combined.InspectAsync(1, default) == MapEncounterKind.ItemPopup, "Ammo took priority over the native item popup");
        ui.Items = false;
        Check(await combined.InspectAsync(1, default) == MapEncounterKind.AmmoNotification, "Passive ammo branch was not connected to map encounters");
    }

    private static async Task OperationChecksAsync()
    {
        var map = new MapDefinition("D1", "SP -- -- MA", ["A1"], ["A1"], []);
        var state = new CampaignState(map);
        var host = new Host();
        var config = new CampaignConfiguration { PoorMapData = true, HasAmbush = false };
        var operations = new InMapCampaignOperations(host, state, config, default, new SupplyRule(map));
        await operations.EnterMapAsync();
        await operations.HandleFleetLockAsync();
        await operations.InitializeMapAsync(map);
        for (int i = 0; i < 3; i++) state.CommitBattle(false);
        Check(!await operations.PickUpAmmoAsync() && state is { FleetAmmo: 5, AmmoCount: 0, BattleCount: 3 } &&
            operations.AmmoPickups.Count == 1, "C# campaign operations did not execute and retain supply evidence");
        var result = CampaignResumeTask.Describe("supply", "campaign_resume", RuleCatalog.Create("campaign_main/campaign_1_1"),
            new(CampaignLoopExit.Ended, state.BattleCount, null, operations.InitialFleet, operations.AmmoPickups), false);
        Check(result.Evidence?["ammoPickups"]?[0]?["expectedRecovered"]?.GetValue<int>() == 3 &&
            result.Evidence["cleared"]?.GetValue<bool>() == false, "Supply accounting disappeared from task evidence or became a sortie verdict");
    }

    internal sealed class Harness
    {
        public ManualClock Clock { get; } = new();
        public List<string> Trace { get; } = [];
        public string? Failure { get; init; }
        public SupplyCamera Camera { get; }
        public CampaignMapCombat Combat { get; }
        public Harness(CampaignState state, bool notification = false, bool hasAmbush = false)
        {
            Camera = new(this);
            var ui = new ProbeUi { Source = this, Notification = notification };
            var ammo = new MapAmmoProbe(ui, ui, () => Camera.FrameSequence);
            var movement = new MapMovement(state, new() { HasAmbush = hasAmbush }, Camera,
                () => new MapArrivalCheck(Camera, state, _ => ValueTask.FromResult(true), Clock,
                    new MapEncounterProbe(ui, false, ammo)), WaitAsync);
            Combat = new(state, new() { HasAmbush = hasAmbush }, movement, new MapScanner(state, Camera, Clock));
        }
        private ValueTask WaitAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Trace.Add("wait:start");
            if (Failure == "wait") throw new IOException("Synthetic info bar failure");
            Clock.Advance(.75);
            Trace.Add("wait:end");
            return ValueTask.CompletedTask;
        }
    }
    internal sealed class SupplyCamera(Harness run) : IMapArrivalCamera, IMapScanCamera
    {
        public long FrameSequence { get; private set; } = 1;
        public bool Invalidated { get; private set; }
        public Cell Position { get; private set; } = new(1, 1);
        public ValueTask PrepareTapAsync(Cell destination, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
        public ValueTask TapCellAsync(Cell destination, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            run.Trace.Add("tap:" + destination);
            if (run.Failure == "first-tap" || run.Failure == "second-tap" && run.Trace.Count == 2)
                throw new IOException("Synthetic grid tap failure");
            return ValueTask.CompletedTask;
        }
        public ValueTask RefreshImageAsync(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            run.Clock.Advance(.25);
            bool stale = run.Failure == "stale" || run.Failure == "ack-stale" && run.Trace.Count == 2 ||
                run.Failure == "settled-stale" && run.Trace.Contains("wait:end");
            if (!stale) FrameSequence++;
            return ValueTask.CompletedTask;
        }
        public ValueTask<FleetMarker> ReadFleetMarkerAsync(Cell destination, CancellationToken token = default)
            => ValueTask.FromResult(new FleetMarker(run.Failure != "marker", true));
        public ValueTask<FleetMarker> ReadCenterMarkerAsync(CancellationToken token = default) => throw new InvalidOperationException();
        public ValueTask RelocalizeAsync(CancellationToken token = default) => throw new InvalidOperationException("Passive notification must not relocalize");
        public ValueTask AnchorAtAsync(Cell location, CancellationToken token = default) => throw new InvalidOperationException();
        public void Suspend() => throw new InvalidOperationException("Passive notification must not suspend");
        public void Invalidate() => Invalidated = true;
        public ValueTask FocusAsync(Cell destination, CancellationToken token) { Position = destination; return ValueTask.CompletedTask; }
        public ValueTask CenterAsync(double tolerance, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask EnsureEdgesAsync(bool skipFirstUpdate, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
        public ValueTask<MapObservation> ObserveAsync(MapScanMode mode, CancellationToken token) => ValueTask.FromResult(
            new MapObservation([new(new(0, 0), new(IsFleet: true, IsCurrentFleet: true))], Position, new(0, 0), mode));
    }
    private sealed class ProbeUi : AppearanceProbe, IMapUiObservations, IUiDriver
    {
        public Harness? Source { get; init; }
        public bool Notification { get; init; }
        public bool Info { get; set; }
        public bool Ammo { get; set; }
        public bool Items { get; set; }
        public long Sequence => Source?.Camera.FrameSequence ?? 1;
        public ProbeUi() : base(GameServer.Cn, null) { }
        TimeProvider IUiDriver.Clock => Source?.Clock ?? Clock;
        public void Advance(double seconds) => Time.Advance(seconds);
        public override ValueTask<bool> AppearsAsync(AssetRule asset, ButtonOffset offset = default, double interval = 0,
            double similarity = .85, int threshold = 10, TemplatePreprocessing preprocessing = TemplatePreprocessing.Color,
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Calls.Add(new(asset.Id, [], interval));
            return ValueTask.FromResult(asset == UiAssets.Handler.GET_AMMO && (Source is null ? Ammo : Notification && Sequence <= 3) ||
                asset == UiAssets.Combat.GET_ITEMS_1 && Items);
        }
        public ValueTask<int> InfoBarCountAsync(CancellationToken token) =>
            ValueTask.FromResult((Source is null ? Info : Notification && Sequence <= 3) ? 1 : 0);
        public ValueTask<bool> HasStageEntranceAsync(CancellationToken token) => throw new InvalidOperationException();
    }
    private sealed class Host : ICampaignInMapHost
    {
        public ValueTask InitializeLevelsAsync(CampaignState state, int fleet, CampaignConfiguration config, CancellationToken token)
        { state.Levels.Reset(); return ValueTask.CompletedTask; }
        public ValueTask InitializeHealthAsync(CampaignState state, int fleet, CampaignConfiguration config, CancellationToken token)
        { state.Health.Commit(fleet, 1, [.9, 0, 0, .9, 0, 0], config.Health); return ValueTask.CompletedTask; }
        public ValueTask<CampaignWithdrawalEvidence> WithdrawAsync(string reason, CancellationToken token) => throw new InvalidOperationException();
        private Harness? _run;
        public ValueTask<bool> VerifyInMapAsync(CancellationToken token) => ValueTask.FromResult(true);
        public ValueTask EnsureFleetLockAsync(bool enabled, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<FleetSelection> PrepareInitialFleetAsync(CampaignConfiguration config, CancellationToken token) =>
            ValueTask.FromResult(new FleetSelection(1, 1, 0, 1));
        public ValueTask<IMapScanCamera> CreateCameraAsync(CampaignState state, CampaignConfiguration config, CancellationToken token)
        { _run = new(state); return ValueTask.FromResult<IMapScanCamera>(_run.Camera); }
        public CampaignMapCombat CreateCombat(IMapScanCamera camera, CampaignConfiguration config,
            Func<CancellationToken, ValueTask> refocusBoss) => _run!.Combat;
        public ValueTask RefocusBossAsync(IMapScanCamera camera, (int X, int Y)? preset, CancellationToken token)
            => throw new InvalidOperationException("Supply-only fixture has no boss");
    }
    private sealed class SupplyRule(MapDefinition map) : CampaignRule
    {
        public override string Id => "test/supply";
        public override MapDefinition Map => map;
        public override System.Collections.Immutable.ImmutableArray<SourceFile> Sources => [];
        protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; } = new Dictionary<int, BattleHook>();
    }
    private sealed class ImageDevice(ScreenFrame frame) : IGameDevice
    {
        public ValueTask<ScreenFrame> CaptureAsync(CancellationToken token = default) => ValueTask.FromResult(frame);
        public ValueTask TapAsync(PixelPoint point, CancellationToken token = default) => throw new InvalidOperationException("Read-only CV fixture");
        public ValueTask SwipeAsync(PixelPoint start, PixelPoint end, TimeSpan duration, CancellationToken token = default) => throw new InvalidOperationException();
        public ValueTask BackAsync(CancellationToken token = default) => throw new InvalidOperationException();
    }
}
