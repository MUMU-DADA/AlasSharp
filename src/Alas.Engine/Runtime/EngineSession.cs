using System.Security.Cryptography;
using System.Text.Json;
using Alas.Engine.Devices;
using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;
using Alas.Engine.Tasks;

namespace Alas.Engine.Runtime;

public sealed record EngineSessionOptions(string Adb, string Serial, GameServer Server, string Assets, string VisionRuntime,
    string? ApplicationPackage = null, string? ModelDirectory = null, bool AllowActions = false,
    string? ProfileRoot = null, string? ProfileInstance = null)
{
    public bool HasProfileStore => !string.IsNullOrWhiteSpace(ProfileRoot) && !string.IsNullOrWhiteSpace(ProfileInstance);
    public void ValidateProfileBinding()
    {
        if ((ProfileRoot is not null || ProfileInstance is not null) && !HasProfileStore)
            throw new ArgumentException("Engine profile root and instance must be supplied together");
    }
}

/// <summary>One device and one pure-vision process for the entire new execution graph.</summary>
public sealed partial class EngineSession : IAsyncDisposable, IMapObservationService, ICampaignInMapHost,
    ICampaignExecutionService, ICampaignStageObservationService, ICampaignFleetPreparationService,
    ICampaignEntryService, ICampaignMapPreparationService, ICampaignEmotionService
{
    private readonly PureVisionWorker _vision;
    private readonly JournalDevice _device;
    private readonly IApplicationHealth _application;
    private readonly AssetFiles _assets;
    private readonly MapAmmoProbe _ammoProbe;
    private readonly ImageStability _imageStability;
    private readonly IntervalTimer _automationSet;
    private readonly IntervalTimer _submarineClick;
    private readonly IntervalTimer _mapCatAttack;
    private readonly List<CombatSubmarineCall> _submarineCalls = [];
    private readonly List<SubmarineMovement> _submarineMovements = [];
    private readonly List<MapMovement> _mapMovements = [];
    private readonly List<MapArrivalCheck> _mapArrivals = [];
    private readonly List<CombatHealthPreparation> _combatHealth = [];
    private readonly RetirementHandler _retirement;
    private readonly CampaignInterruptions _interruptions;
    private CampaignMapPreparation? _mapPreparation;
    private CampaignState? _campaignState;
    public UiDriver Driver { get; }
    public PageGraph Pages { get; } = UpstreamPages.Create();
    public TaskCapabilities Capabilities { get; }
    public EngineSession(EngineSessionOptions options)
    {
        options.ValidateProfileBinding();
        _options = options;
        if (options.AllowActions && string.IsNullOrWhiteSpace(options.ApplicationPackage))
            throw new ArgumentException("Device actions require an explicit game application package");
        Capabilities = new(options.AllowActions, options.ModelDirectory is not null, options.HasProfileStore);
        _device = new JournalDevice(new AdbDevice(options.Adb, options.Serial, options.AllowActions));
        _application = options.ApplicationPackage is null ? new UnconfiguredApplication() :
            new JournalApplication(new AdbApplication(options.Adb, options.Serial, options.ApplicationPackage, options.AllowActions), _device);
        string visionRuntime = options.VisionRuntime.Contains(Path.DirectorySeparatorChar) || options.VisionRuntime.Contains(Path.AltDirectorySeparatorChar)
            ? Path.GetFullPath(options.VisionRuntime) : options.VisionRuntime;
        _vision = new PureVisionWorker(visionRuntime, Path.Combine(AppContext.BaseDirectory, "Imaging/Worker/vision_worker.py"), modelDirectory: options.ModelDirectory);
        _assets = new AssetFiles(options.Assets);
        Driver = new UiDriver(options.Server, _device, _vision, _assets);
        _imageStability = new(Driver, _vision, () => Driver.Frame ?? throw new InvalidOperationException("No stability screenshot"));
        _automationSet = new(Driver.Clock, 1);
        _submarineClick = new(Driver.Clock, 1);
        _mapCatAttack = new(Driver.Clock, 2);
        var visuals = new UiVisuals(_vision, () => Driver.Frame ?? throw new InvalidOperationException("No dock screenshot"));
        var info = new MapUiObservations(() => Driver.Frame ?? throw new InvalidOperationException("No dock screenshot"),
            _vision, _assets, Driver.Server);
        _retirement = new(Driver, new RetirementDock(Driver, visuals), () => Driver.Frame?.Sequence ?? 0, info.InfoBarCountAsync);
        _interruptions = new(Driver, visuals, _retirement);
        _ammoProbe = new(Driver, new MapUiObservations(
            () => Driver.Frame ?? throw new InvalidOperationException("No ammo screenshot"), _vision, _assets, Driver.Server),
            () => Driver.Frame?.Sequence ?? 0);
    }
    public async ValueTask<MapCamera> CreateMapCameraAsync(CampaignState state, Cell initialPosition,
        MapDetectionRules detection, GridRecognitionRules recognition, MapCameraRules cameraRules,
        TimeSpan timeout, CancellationToken token = default, StageEntranceKind entrances = StageEntranceKind.Normal,
        UiRecoveryOptions? recoveryOptions = null)
    {
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (detection.OperationSiren) throw new NotSupportedException("Operation Siren geometry is available, but its grid predictor and camera workflow are not ported");
        detection.Validate(); recognition.Validate(); cameraRules.Validate();
        using var deadline = new CancellationTokenSource(timeout);
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            var detector = new GridDetector(_vision, _assets, detection);
            async ValueTask<ScreenFrame> Capture(CancellationToken ct)
            { await Driver.ScreenshotAsync(ct); return Driver.Frame!; }
            var recovery = new UiRecovery(Driver, _application, Pages, recoveryOptions ?? new UiRecoveryOptions());
            var observations = new MapUiObservations(() => Driver.Frame ?? throw new InvalidOperationException("No map screenshot"),
                _vision, _assets, Driver.Server, entrances);
            var guard = new MapUiRecovery(Driver, observations, _application, recovery, recovery);
            var source = new MapViewSource(Capture, detector, guard);
            var predictor = new GridRecognition(_vision, _assets, Driver.Server, recognition);
            var mask = await _assets.ReadAsync(detection.OperationSiren ? MapDetectionAssets.OsMask : MapDetectionAssets.Mask, combined.Token);
            var evidence = new MapSwipeEvidence(predictor, _vision, _vision, new(1, DateTimeOffset.UnixEpoch, mask), MapDetectionAssets.MaskOrigin);
            return await MapCamera.CreateAsync(state, initialPosition, source, new MapSwipeInput(_device), predictor,
                new(evidence), cameraRules, timeout, combined.Token, gridInput: new MapGridInput(_device));
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException("Map camera initialization exceeded its time limit", error); }
    }
    public MapArrivalCheck CreateMapArrivalCheck(MapCamera camera, CampaignConfiguration configuration,
        IMapEncounterHandler? handler = null, Func<CancellationToken, ValueTask>? recoverAfterCombat = null,
        Func<MapEncounterProbe, IMapEncounterHandler>? createHandler = null)
    {
        var popups = new MapWalkPopups(Driver, new UiVisuals(_vision,
            () => Driver.Frame ?? throw new InvalidOperationException("No map popup screenshot")),
            () => Driver.Frame?.Sequence ?? 0, configuration.IsClearMode, _mapCatAttack);
        var probe = new MapEncounterProbe(Driver, configuration.HasAmbush, _ammoProbe, configuration.MysteryHasCarrier,
            camera.State.Rule?.Overlays, configuration.HasFleetStep ? CreateWalkStep() : null, popups);
        handler = createHandler?.Invoke(probe) ?? handler;
        var arrival = new MapArrivalCheck(camera, camera.State, token => Driver.AppearsAsync(UiAssets.Handler.IN_MAP, token: token), Driver.Clock,
            probe, new MapAirRaidHandler(Driver, probe,
                () => Driver.Frame?.Sequence ?? throw new InvalidOperationException("No air raid screenshot"), handler), recoverAfterCombat, popups,
            camera.RecoverWalkTimeoutAsync);
        _mapArrivals.Add(arrival);
        return arrival;
    }
    public MapMovement CreateMapMovement(MapCamera camera, CampaignConfiguration configuration)
        => TrackMovement(new(camera.State, configuration, camera, () => CreateMapArrivalCheck(camera, configuration), EnsureNoMapInfoBarAsync,
            token => WithdrawCampaignAsync("low_hp", token), CreateMovableScan(camera, configuration), new MapScanner(camera.State, camera, Driver.Clock),
            token => RecoverWalkAsync(camera, token)));
    public MapMovement CreateMapCombatMovement(MapCamera camera, CampaignConfiguration configuration,
        StageEntranceKind entrances = StageEntranceKind.Normal, Func<CancellationToken, ValueTask>? refocusBoss = null)
    {
        // Keep story and urgent-commission timers across grid visits in this sortie.
        var recovery = new UiRecovery(Driver, _application, Pages, new());
        return TrackMovement(new(camera.State, configuration, camera, () => CreateMapArrivalCheck(camera, configuration,
            recoverAfterCombat: new MapCombatRecovery(camera.State, camera,
                refocusBoss ?? (token => camera.RefocusBossAsync(configuration.BossAppearRefocusSwipe, token)),
                token => ReadFleetHealthAsync(camera.State, camera.State.FleetIndex, configuration, token)).RecoverAsync,
            createHandler: probe => CreateMapEncounterHandler(camera.State, configuration, entrances, probe, recovery)), EnsureNoMapInfoBarAsync,
            token => WithdrawCampaignAsync("low_hp", token), CreateMovableScan(camera, configuration), new MapScanner(camera.State, camera, Driver.Clock),
            token => RecoverWalkAsync(camera, token)));
    }
    private MapMovement TrackMovement(MapMovement movement) { _mapMovements.Add(movement); return movement; }
    private MapWalkStep CreateWalkStep() => new(Driver,
        new MapUiObservations(() => Driver.Frame ?? throw new InvalidOperationException("No walk screenshot"), _vision, _assets, Driver.Server),
        _vision, _assets, () => Driver.Frame ?? throw new InvalidOperationException("No walk screenshot"));
    private async ValueTask RecoverWalkAsync(MapCamera camera, CancellationToken token)
    {
        await CreateWalkStep().ClearAsync(token);
        await camera.RecoverWalkAsync(token);
    }
    private MapMovableScan CreateMovableScan(MapCamera camera, CampaignConfiguration configuration)
        => new(camera.State, configuration, new MapScanner(camera.State, camera, Driver.Clock), camera.HasEnemyTemplates);
    private async ValueTask ReadFleetStatusAfterCombatAsync(CampaignState state, int fleet,
        CampaignConfiguration configuration, CancellationToken token)
    {
        await ReadFleetHealthAsync(state, fleet, configuration, token);
        _ = await ReadFleetLevelsAsync(state, fleet, true, configuration, token);
    }
    private ValueTask<FleetLevelReading?> ReadFleetLevelsAsync(CampaignState state, int fleet, bool afterBattle,
        CampaignConfiguration configuration, CancellationToken token)
        => new FleetLevelReader(Driver, () => Driver.Frame?.Sequence ?? 0)
            .ReadAsync(state.Levels, fleet, afterBattle, configuration.Levels, token);
    async ValueTask ICampaignInMapHost.InitializeLevelsAsync(CampaignState state, int fleet,
        CampaignConfiguration configuration, CancellationToken token)
    {
        state.Levels.Reset();
        _ = await ReadFleetLevelsAsync(state, fleet, false, configuration, token);
    }
    private async ValueTask ReadFleetHealthAsync(CampaignState state, int fleet,
        CampaignConfiguration configuration, CancellationToken token)
        => _ = await new FleetHealthReader(Driver, _vision,
            () => Driver.Frame ?? throw new InvalidOperationException("No fleet health screenshot"))
            .ReadAsync(state.Health, fleet, configuration.Health, token);
    async ValueTask ICampaignInMapHost.InitializeHealthAsync(CampaignState state, int fleet,
        CampaignConfiguration configuration, CancellationToken token)
    {
        state.Health.Reset();
        await ReadFleetHealthAsync(state, fleet, configuration, token);
    }
    ValueTask<CampaignWithdrawalEvidence> ICampaignInMapHost.WithdrawAsync(string reason, CancellationToken token)
        => WithdrawCampaignAsync(reason, token);
    private ValueTask<CampaignWithdrawalEvidence> WithdrawCampaignAsync(string reason, CancellationToken token)
    {
        var recovery = new UiRecovery(Driver, _application, Pages, new UiRecoveryOptions());
        var observations = new MapUiObservations(() => Driver.Frame ?? throw new InvalidOperationException("No withdrawal screenshot"),
            _vision, _assets, Driver.Server);
        var guard = new MapUiRecovery(Driver, observations, _application, recovery, recovery);
        return new CampaignWithdrawal(Driver, recovery, guard,
            () => Driver.Frame?.Sequence ?? throw new InvalidOperationException("No withdrawal screenshot"))
            .RunAsync(reason, TimeSpan.FromMinutes(1), token);
    }
    private ValueTask EnsureNoMapInfoBarAsync(CancellationToken token)
    {
        var recovery = new UiRecovery(Driver, _application, Pages, new UiRecoveryOptions());
        var observations = new MapUiObservations(() => Driver.Frame ?? throw new InvalidOperationException("No supply screenshot"),
            _vision, _assets, Driver.Server);
        return new MapUiRecovery(Driver, observations, _application, recovery, recovery)
            .EnsureNoInfoBarAsync(TimeSpan.FromSeconds(.6), token);
    }
    public CampaignMapCombat CreateCampaignMapCombat(MapCamera camera, CampaignConfiguration configuration,
        StageEntranceKind entrances = StageEntranceKind.Normal, Func<CancellationToken, ValueTask>? refocusBoss = null)
    {
        var mobMovement = new CampaignMobMovement(Driver, camera);
        var submarineMovement = new SubmarineMovement(Driver, camera,
            new UiRecovery(Driver, _application, Pages, new UiRecoveryOptions()), () => Driver.Frame?.Sequence ?? 0);
        _submarineMovements.Add(submarineMovement);
        return new(camera.State, configuration, CreateMapCombatMovement(camera, configuration, entrances, refocusBoss),
            new MapScanner(camera.State, camera, Driver.Clock),
            waitEmotion: (fleet, token) => RequireEmotion(configuration).WaitAsync(fleet, token, configuration.IsDoubleBook),
            switchFleet: CreateFleetSwitcher(camera, configuration).SwitchAsync,
            ensureEdges: token => camera.EnsureEdgesAsync(skipFirstUpdate: true, token), waitForInfoBar: EnsureNoMapInfoBarAsync,
            moveMob: mobMovement.MoveAsync, moveSubmarine: submarineMovement.MoveNearBossAsync);
    }
    public CampaignExecution CreateInMapCampaignExecution(CampaignRule rule,
        CampaignConfiguration configuration, CancellationToken token = default)
        => new(rule, configuration, (state, effective) =>
        {
            _campaignState = state;
            return new InMapCampaignOperations(this, state, effective, token, rule);
        });
    public async ValueTask<CampaignResumeResult> ResumeInMapAsync(CampaignRule rule,
        CampaignConfiguration configuration, CancellationToken token)
    {
        var execution = CreateInMapCampaignExecution(rule, configuration, token);
        var exit = await execution.RunAsync();
        var operations = (InMapCampaignOperations)execution.Context.Operations;
        return new(exit, execution.Context.State.BattleCount, operations.StageReturn, operations.InitialFleet, operations.AmmoPickups,
            execution.Context.State.Health.Observations, execution.Context.State.Withdrawal,
            execution.Context.State.Levels.Evidence(execution.Context.Config.Levels), execution.Context.State.MechanismReleases,
            execution.Context.State.MovableScans, execution.Context.State.MazeWaits, execution.Context.State.DecoyArrivals,
            execution.Context.State.AmbushEncounters, execution.Context.State.CarrierEncounters, execution.Context.State.CarrierScans,
            execution.Context.State.SubmarineEvidence);
    }
    async ValueTask<bool> ICampaignInMapHost.VerifyInMapAsync(CancellationToken token)
    {
        await Driver.ScreenshotAsync(token);
        return await Driver.AppearsAsync(UiAssets.Handler.IN_MAP, token: token);
    }
    async ValueTask ICampaignInMapHost.EnsureFleetLockAsync(bool enabled, CancellationToken token)
        => _ = await new CampaignFleetLock(Driver).EnsureAsync(enabled, token);
    async ValueTask<FleetSelection> ICampaignInMapHost.PrepareInitialFleetAsync(CampaignConfiguration configuration, CancellationToken token)
    {
        var recovery = new UiRecovery(Driver, _application, Pages, new UiRecoveryOptions());
        var observations = new MapUiObservations(() => Driver.Frame ?? throw new InvalidOperationException("No fleet screenshot"),
            _vision, _assets, Driver.Server, StageEntranceKind.Normal);
        var guard = new MapUiRecovery(Driver, observations, _application, recovery, recovery);
        var selector = new CampaignFleetSelector(Driver, recovery, guard.HandleInStageAsync,
            () => Driver.Frame?.Sequence ?? throw new InvalidOperationException("No fleet screenshot"));
        var selected = await selector.InitializeAsync(configuration, TimeSpan.FromSeconds(45), token);
        if (selected.Clicks > 0 && configuration.WaitForFleetSwitchInfoBar)
            await guard.EnsureNoInfoBarAsync(TimeSpan.FromSeconds(.6), token);
        var buff = new MapFormationProbe(_vision, _assets, Driver.Server,
            () => Driver.Frame ?? throw new InvalidOperationException("No strategy screenshot"));
        // Formation settings refer to the displayed fleet, while path costs use
        // logical mob/boss roles. Both derive from the same observed selection.
        _ = await new CampaignStrategy(Driver, buff.ObserveAsync)
            .EnsureAsync(selected.DisplayedIndex, configuration, TimeSpan.FromSeconds(45), token);
        return selected;
    }
    async ValueTask<IMapScanCamera> ICampaignInMapHost.CreateCameraAsync(CampaignState state,
        CampaignConfiguration configuration, CancellationToken token)
    {
        if (state.Map.Cameras.IsEmpty) throw new InvalidDataException("Campaign map has no initial camera declaration");
        return await CreateMapCameraAsync(state, state.Map.Cameras[0],
            new MapDetectionRules().WithChapter(configuration.Vision),
            new GridRecognitionRules { HasSiren = configuration.HasSiren, HasMystery = configuration.HasMystery },
            new MapCameraRules().WithChapter(configuration.SwipeMultipliers, configuration.MapEdgeCorner, configuration.SwipePredictWithSeaGrids),
            TimeSpan.FromSeconds(30), token);
    }
    CampaignMapCombat ICampaignInMapHost.CreateCombat(IMapScanCamera camera,
        CampaignConfiguration configuration, Func<CancellationToken, ValueTask> refocusBoss)
        => camera is MapCamera mapCamera ? CreateCampaignMapCombat(mapCamera, configuration, refocusBoss: refocusBoss) :
            throw new ArgumentException("Campaign camera does not belong to this session", nameof(camera));
    ValueTask ICampaignInMapHost.RefocusBossAsync(IMapScanCamera camera, (int X, int Y)? preset, CancellationToken token)
        => camera is MapCamera mapCamera ? mapCamera.RefocusBossAsync(preset, token) :
            throw new ArgumentException("Campaign camera does not belong to this session", nameof(camera));
    public CombatRankProbe CreateCombatRankProbe() => new(Driver);
    internal CombatFlow CreateCampaignCombatFlow(CampaignState state, CampaignConfiguration configuration,
        StageEntranceKind entrances = StageEntranceKind.Normal)
    {
        SubmarineRules.RequireSupported(configuration);
        _interruptions.Configure(configuration.Retirement, configuration.EmotionMode);
        var preparation = new CombatHealthPreparation(Driver,
            () => Driver.Frame ?? throw new InvalidOperationException("No combat preparation screenshot"),
            _imageStability, () => state.Health.Get(state.FleetIndex), configuration.Health, configuration.UseFleetLock,
            new AdbFleetDrag(_device, Driver).DragAsync);
        _combatHealth.Add(preparation);
        var emotion = configuration.EmotionMode.Calculates()
            ? RequireEmotion(configuration).ForBattle(
                FleetRoles.Reversed(configuration) ? 3 - state.FleetIndex : state.FleetIndex,
                () => Driver.Frame?.Sequence ?? throw new InvalidOperationException("No battle loading frame"),
                configuration.IsDoubleBook) : null;
        var submarine = new CombatSubmarineCall(Driver, () => Driver.Frame?.Sequence ?? 0,
            configuration.CombatMode(state.EncounterExpectedBoss), _submarineClick);
        _submarineCalls.Add(submarine);
        return CreateCombatFlow(entrances, preparation, emotion,
            state.Rule is { } rule ? token => rule.AllowExperienceAsync(Driver, token) : null, submarine);
    }
    public CombatFlow CreateCombatFlow(StageEntranceKind entrances = StageEntranceKind.Normal,
        CombatHealthPreparation? healthPreparation = null, ICombatEmotion? emotion = null,
        Func<CancellationToken, ValueTask<bool>>? allowExperience = null, CombatSubmarineCall? submarine = null)
    {
        var recovery = new UiRecovery(Driver, _application, Pages, new UiRecoveryOptions());
        var observations = new MapUiObservations(() => Driver.Frame ?? throw new InvalidOperationException("No combat screenshot"),
            _vision, _assets, Driver.Server, entrances);
        return new(Driver, recovery, recovery, observations, healthPreparation, _automationSet, _interruptions, emotion, allowExperience, submarine);
    }
    public async ValueTask<MapVisualObservation> ObserveMapAsync(CampaignRule rule, CancellationToken token)
    {
        var configuration = rule.Configure(new());
        var detector = new GridDetector(_vision, _assets, new MapDetectionRules().WithChapter(configuration.Vision));
        await Driver.ScreenshotAsync(token);
        var view = await detector.DetectAsync(Driver.Frame!, token);
        var recognition = new GridRecognition(_vision, _assets, Driver.Server,
            new() { HasSiren = configuration.HasSiren, HasMystery = configuration.HasMystery });
        // This task reports local geometry only; a screenshot alone does not establish global map position.
        var cells = await recognition.ObserveAsync(view, new(1, 1), token: token);
        return new(view, cells.Cells);
    }
    public async ValueTask<CampaignStages> ObserveStagesAsync(StageEntranceKind kinds, CancellationToken token)
    {
        await Driver.ScreenshotAsync(token);
        var frame = Driver.Frame ?? throw new InvalidOperationException("No campaign page screenshot");
        var detector = new StageEntranceDetector(_vision, _assets, Driver.Server);
        var reader = new CampaignStageReader((image, requested, ct) => detector.FindAsync(image, requested, ct),
            _vision, Driver.Server);
        return await reader.ObserveAsync(frame, kinds, token);
    }
    public ValueTask<FleetSetupResult> ConfigureFleetAsync(FleetPlan plan, IPopupHandler popups, CancellationToken token)
        => new CampaignFleetSetup(Driver, _vision,
            () => Driver.Frame ?? throw new InvalidOperationException("No fleet preparation screenshot"), popups)
            .ApplyAsync(plan, token);
    public ValueTask<CampaignEntryObservation> EnterFromFleetAsync(CancellationToken token)
        => new CampaignEntry(Driver,
            () => Driver.Frame?.Sequence ?? throw new InvalidOperationException("No map entry screenshot"), _interruptions,
            new UiRecovery(Driver, _application, Pages, new UiRecoveryOptions()))
            .EnterAsync(token);
    public ValueTask<CampaignMapPreparationResult> PrepareMapAsync(CampaignConfiguration configuration,
        TimeSpan timeout, CancellationToken token)
    {
        var observations = new MapUiObservations(
            () => Driver.Frame ?? throw new InvalidOperationException("No map preparation screenshot"), _vision, _assets, Driver.Server);
        _mapPreparation = new(Driver, _vision, _vision,
            () => Driver.Frame ?? throw new InvalidOperationException("No map preparation screenshot"), observations.InfoBarCountAsync);
        return _mapPreparation.PrepareMapAsync(configuration, timeout, token);
    }
    public async ValueTask<DoubleBookObservation> PrepareDoubleBookAsync(CampaignConfiguration configuration,
        TimeSpan timeout, CancellationToken token)
    {
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(timeout));
        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            var observed = await (_mapPreparation ?? throw new InvalidOperationException("Map preparation state is missing"))
                .PrepareDoubleBookAsync(configuration, timeout, linked.Token);
            if (configuration.IsClearMode) await EnsureNoMapInfoBarAsync(linked.Token);
            return observed;
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException("Double-book preparation or information bar did not finish", error); }
    }
    public TaskContext BeginTask(TimeSpan timeout)
    {
        _device.Actions.Clear();
        _combatHealth.Clear();
        _submarineCalls.Clear();
        _submarineMovements.Clear();
        _mapMovements.Clear();
        _mapArrivals.Clear();
        _submarineClick.Clear();
        _mapCatAttack.Clear();
        _fleetSwitchers.Clear();
        _emotion = null;
        _emotionConfiguration = null;
        _mapPreparation = null;
        _campaignState = null;
        _achievement = null;
        _retirement.ResetEvidence();
        _interruptions.Configure(new() { Mode = RetirementMode.Disabled }, CampaignEmotionMode.Calculate);
        Driver.ResetTask();
        var recovery = new UiRecovery(Driver, _application, Pages, new UiRecoveryOptions());
        return new(Driver, new UiNavigator(Driver, Pages, recovery), recovery, timeout, this, this, this, this, this, this, _interruptions, this, this);
    }
    public async Task<JsonObjectEvidence> SaveEvidenceAsync(string directory, bool failed)
    {
        Directory.CreateDirectory(directory);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        await File.WriteAllTextAsync(Path.Combine(directory, "actions.json"), JsonSerializer.Serialize(_device.Actions, json));
        string? healthFile = null;
        if (_combatHealth.Count > 0)
        {
            healthFile = "combat-health.json";
            await File.WriteAllTextAsync(Path.Combine(directory, healthFile),
                JsonSerializer.Serialize(_combatHealth.Select(preparation => preparation.Evidence), json));
        }
        string? retirementFile = null;
        if (_retirement.Evidence.Count > 0)
        {
            retirementFile = "retirement.json";
            await File.WriteAllTextAsync(Path.Combine(directory, retirementFile), JsonSerializer.Serialize(_retirement.Evidence, json));
        }
        string? emotionFile = null;
        if (_emotion?.Evidence.Count > 0)
        {
            emotionFile = "emotion.json";
            await File.WriteAllTextAsync(Path.Combine(directory, emotionFile), JsonSerializer.Serialize(_emotion.Evidence, json));
        }
        string? preparationFile = null;
        if (_mapPreparation is not null)
        {
            preparationFile = "map-preparation.json";
            await File.WriteAllTextAsync(Path.Combine(directory, preparationFile), JsonSerializer.Serialize(_mapPreparation.Evidence, json));
        }
        string? image = null, hash = null;
        string? mapStopFile = null;
        if (_achievement?.Evidence is { Info: not null } stop)
        {
            mapStopFile = "map-stop.json";
            await File.WriteAllTextAsync(Path.Combine(directory, mapStopFile), JsonSerializer.Serialize(stop, json));
        }
        string? fleetSwitchFile = null;
        var switches = _fleetSwitchers.SelectMany(switcher => switcher.Evidence).ToArray();
        if (switches.Length > 0)
        {
            fleetSwitchFile = "fleet-switch.json";
            await File.WriteAllTextAsync(Path.Combine(directory, fleetSwitchFile), JsonSerializer.Serialize(switches, json));
        }
        string? submarineFile = null;
        if (_campaignState?.SubmarineEvidence is { } submarine)
        {
            submarineFile = "submarine-location.json";
            await File.WriteAllTextAsync(Path.Combine(directory, submarineFile), JsonSerializer.Serialize(submarine, json));
        }
        string? submarineCallsFile = null;
        if (_submarineCalls.Count > 0)
        {
            submarineCallsFile = "submarine-calls.json";
            await File.WriteAllTextAsync(Path.Combine(directory, submarineCallsFile),
                JsonSerializer.Serialize(_submarineCalls.Select(call => call.Evidence), TaskQueue.Json));
        }
        string? submarineMovesFile = null;
        var moves = _submarineMovements.SelectMany(movement => movement.Evidence).ToArray();
        if (moves.Length > 0)
        {
            submarineMovesFile = "submarine-moves.json";
            await File.WriteAllTextAsync(Path.Combine(directory, submarineMovesFile), JsonSerializer.Serialize(moves, TaskQueue.Json));
        }
        long? sequence = null;
        string? walkRecoveriesFile = null;
        var walks = _mapMovements.SelectMany(movement => movement.WalkRecoveries).ToArray();
        if (walks.Length > 0)
        {
            walkRecoveriesFile = "walk-recoveries.json";
            await File.WriteAllTextAsync(Path.Combine(directory, walkRecoveriesFile), JsonSerializer.Serialize(walks, TaskQueue.Json));
        }
        string? walkTimeoutsFile = null;
        var timeouts = _mapArrivals.SelectMany(arrival => arrival.WalkTimeouts).ToArray();
        if (timeouts.Length > 0)
        {
            walkTimeoutsFile = "walk-timeouts.json";
            await File.WriteAllTextAsync(Path.Combine(directory, walkTimeoutsFile), JsonSerializer.Serialize(timeouts, TaskQueue.Json));
        }
        if (Driver.Frame is { } frame)
        {
            image = failed ? "failure.png" : "frame.png";
            await File.WriteAllBytesAsync(Path.Combine(directory, image), frame.Png.ToArray());
            hash = Convert.ToHexStringLower(SHA256.HashData(frame.Png.Span));
            sequence = frame.Sequence;
        }
        return new(image, hash, sequence, _device.Actions.Count, healthFile, retirementFile, emotionFile, preparationFile, fleetSwitchFile, mapStopFile, submarineFile, submarineCallsFile, submarineMovesFile, walkRecoveriesFile, walkTimeoutsFile);
    }
    public ValueTask DisposeAsync() => _vision.DisposeAsync();
    public sealed record JsonObjectEvidence(string? Image, string? Sha256, long? FrameSequence, int ActionAttempts,
        string? CombatHealthFile = null, string? RetirementFile = null, string? EmotionFile = null, string? MapPreparationFile = null,
        string? FleetSwitchFile = null, string? MapStopFile = null, string? SubmarineFile = null, string? SubmarineCallsFile = null,
        string? SubmarineMovesFile = null, string? WalkRecoveriesFile = null, string? WalkTimeoutsFile = null);
    private sealed record DeviceAction(string Kind, DateTimeOffset StartedAt, object Parameters)
    {
        public bool Completed { get; set; }
        public string? Error { get; set; }
    }
    private sealed class JournalDevice(IGameDevice device) : IGameDevice
    {
        public List<DeviceAction> Actions { get; } = [];
        public ValueTask<ScreenFrame> CaptureAsync(CancellationToken token = default) => device.CaptureAsync(token);
        public ValueTask TapAsync(PixelPoint point, CancellationToken token = default) => Act("tap", point, () => device.TapAsync(point, token));
        public ValueTask SwipeAsync(PixelPoint start, PixelPoint end, TimeSpan duration, CancellationToken token = default)
            => Act("swipe", new { start, end, duration }, () => device.SwipeAsync(start, end, duration, token));
        public ValueTask BackAsync(CancellationToken token = default) => Act("back", new { }, () => device.BackAsync(token));
        public async ValueTask Act(string kind, object parameters, Func<ValueTask> action)
        {
            var record = new DeviceAction(kind, DateTimeOffset.UtcNow, parameters);
            Actions.Add(record);
            try { await action(); record.Completed = true; }
            catch (Exception error) { record.Error = error.GetType().Name; throw; }
        }
    }
    private sealed class JournalApplication(IApplicationHealth application, JournalDevice device) : IApplicationHealth
    {
        public ValueTask<bool> IsRunningAsync(CancellationToken token) => application.IsRunningAsync(token);
        public ValueTask RefreshOrientationAsync(CancellationToken token) => application.RefreshOrientationAsync(token);
        public ValueTask StopAsync(CancellationToken token) => device.Act("app_stop", new { }, () => application.StopAsync(token));
    }
    private sealed class UnconfiguredApplication : IApplicationHealth
    {
        public ValueTask<bool> IsRunningAsync(CancellationToken token) => throw new InvalidOperationException("Application package is not configured");
        public ValueTask RefreshOrientationAsync(CancellationToken token) => throw new InvalidOperationException("Application package is not configured");
        public ValueTask StopAsync(CancellationToken token) => throw new InvalidOperationException("Application package is not configured");
    }
}
