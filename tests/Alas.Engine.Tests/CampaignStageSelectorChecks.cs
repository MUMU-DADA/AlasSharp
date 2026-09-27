using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static partial class CampaignStageSelectorChecks
{
    public static async Task RunAsync(string upstream)
    {
        var source = CampaignStageSelector.Source;
        Check(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(upstream, source.Path)))) == source.Sha256,
            "Campaign stage selector source drifted");
        source = CampaignEntry.Source;
        Check(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(upstream, source.Path)))) == source.Sha256,
            "Native map entry source drifted");
        source = CampaignFleetLock.Source;
        Check(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(upstream, source.Path)))) == source.Sha256,
            "Native fleet lock and auto-search source drifted");
        var expectedStages = Enumerable.Range(1, 15)
            .SelectMany(chapter => Enumerable.Range(1, 4).Select(stage => $"{chapter}-{stage}"))
            .Append("15-4-121")
            .Order(StringComparer.Ordinal);
        Check(RuleCatalog.Ids.Select(id => RuleCatalog.Create(id).StageName).Order(StringComparer.Ordinal)
            .SequenceEqual(expectedStages), "Compiled main-stage identities changed");

        var switchDriver = new Driver { AutoSearchAvailable = false };
        var auto = new CampaignAutoSearch(switchDriver, new SwitchVision(switchDriver),
            () => new ScreenFrame(switchDriver.FrameSequence, DateTimeOffset.UtcNow, ReadOnlyMemory<byte>.Empty));
        Check(await auto.EnsureManualAsync() == new AutoSearchObservation(false, false, false) &&
            switchDriver.ClickedAssets.Count == 0, "Absent auto-search option caused a click");
        switchDriver = new Driver { AutoSearchAvailable = true };
        auto = new CampaignAutoSearch(switchDriver, new SwitchVision(switchDriver),
            () => new ScreenFrame(switchDriver.FrameSequence, DateTimeOffset.UtcNow, ReadOnlyMemory<byte>.Empty));
        Check(await auto.EnsureManualAsync() == new AutoSearchObservation(true, false, false) &&
            switchDriver.ClickedAssets.Count == 0, "Already disabled auto-search caused a click");
        switchDriver = new Driver { AutoSearchAvailable = true, AutoSearchEnabled = true };
        auto = new CampaignAutoSearch(switchDriver, new SwitchVision(switchDriver),
            () => new ScreenFrame(switchDriver.FrameSequence, DateTimeOffset.UtcNow, ReadOnlyMemory<byte>.Empty));
        Check(await auto.EnsureManualAsync() == new AutoSearchObservation(true, true, false) &&
            switchDriver.ClickedAssets.SequenceEqual(["AUTO_SEARCH_CHECK"]),
            "Auto-search enabled on the previous sortie was not disabled before map preparation");

        switchDriver = new Driver();
        Check(await new CampaignFleetLock(switchDriver).EnsureAsync(true) == new FleetLockObservation(false, false) &&
            switchDriver.ClickedAssets.Count == 0, "Absent fleet lock caused a blind click");
        switchDriver = new Driver { FleetLockAvailable = true, FleetLockEnabled = false };
        Check(await new CampaignFleetLock(switchDriver).EnsureAsync(true) == new FleetLockObservation(true, true) &&
            switchDriver.ClickedAssets.SequenceEqual(["FLEET_UNLOCKED"]),
            "Fleet lock failed to wait through a transient unknown state");

        var driver = new Driver { Chapter = 3 };
        var stages = new Stages(driver);
        var selected = await new CampaignStageSelector(driver, stages).SelectAsync("1-1");
        Check(selected is { Stage: "1-1", Chapter: "1", Preparation: "normal" } &&
            driver.ClickedAssets.SequenceEqual(["CHAPTER_PREV", "CHAPTER_PREV"]) &&
            driver.ClickedAreas.Count == 1 && selected.OcrFrameSequence > 0 &&
            stages.Calls > 2, "C# selector did not confirm chapter changes and preparation");

        driver = new Driver { Chapter = 2 };
        stages = new Stages(driver) { UnknownChapterOneCalls = 10 };
        selected = await new CampaignStageSelector(driver, stages).SelectAsync("1-1");
        Check(selected.Stage == "1-1" && driver.ClickedAssets.SequenceEqual(["CHAPTER_PREV"]) &&
            driver.ClickedAreas.Count == 1, "Transient unknown chapter exhausted the outer observation retry");

        driver = new Driver { Chapter = 1, ModeNormal = false, HasSecondMode = true, SecondModeHard = true };
        await new CampaignStageSelector(driver, new Stages(driver)).SelectAsync("1-1");
        Check(driver.ClickedAssets.SequenceEqual(["SWITCH_2_HARD", "SWITCH_1_NORMAL"]) && driver.ModeNormal &&
            !driver.SecondModeHard, "Upstream normal-mode switch order changed");

        driver = new Driver { Chapter = 1 };
        await Throws<InvalidDataException>(() => new CampaignStageSelector(driver, new Stages(driver) { Duplicate = true })
            .SelectAsync("1-1").AsTask(), "Duplicate OCR stage was clicked");
        Check(driver.ClickedAreas.Count == 0, "Ambiguous stage name caused a device click");

        driver = new Driver { Chapter = 3, StallChapter = true };
        await Throws<TimeoutException>(() => new CampaignStageSelector(driver, new Stages(driver))
            .SelectAsync("1-1").AsTask(), "Chapter switch without observed progress was retried indefinitely");
        Check(driver.ClickedAssets.Count == 1 && driver.ClickedAreas.Count == 0,
            "Stalled chapter repeated clicks or selected an unverified stage");

        driver = new Driver { Chapter = 1, ConfirmPreparation = false };
        await Throws<TimeoutException>(() => new CampaignStageSelector(driver, new Stages(driver))
            .SelectAsync("1-1").AsTask(), "Stage click without preparation became success");
        driver = new Driver { Chapter = 1, EnterMap = true };
        await Throws<InvalidDataException>(() => new CampaignStageSelector(driver, new Stages(driver))
            .SelectAsync("1-1").AsTask(), "Unexpected map entry became verified preparation");

        driver = new Driver { Chapter = 1 };
        var navigator = new Navigator();
        var task = new CampaignSelectTask();
        var request = new TaskRequest("select", task.Kind,
            new JsonObject { ["campaign"] = "campaign_main/campaign_1_1" });
        task.Validate(request.Input);
        Check(task.Preconditions(request, new(true, false)).SequenceEqual(["ocr_models"]),
            "Campaign selection ran without OCR models");
        var result = await task.RunAsync(request,
            new TaskContext(driver, navigator, null!, TimeSpan.FromSeconds(60), Stages: new Stages(driver)), default);
        Check(navigator.Called && result.Outcome == TaskOutcome.Succeeded &&
            result.Evidence?["stageSelectionVerified"]?.GetValue<bool>() == true &&
            result.Evidence["mapEntered"]?.GetValue<bool>() == false &&
            result.Evidence["cleared"]?.GetValue<bool>() == false,
            "Selection task claimed map entry or a cleared sortie");

        driver = new Driver { Chapter = 1 };
        navigator = new Navigator();
        var fleetTask = new CampaignFleetPreparationTask();
        result = await fleetTask.RunAsync(request with { Kind = fleetTask.Kind },
            new TaskContext(driver, navigator, null!, TimeSpan.FromSeconds(60), Stages: new Stages(driver)), default);
        Check(result.Outcome == TaskOutcome.Succeeded && navigator.Called &&
            driver.ClickedAssets.SequenceEqual(["MAP_PREPARATION"]) && driver.FleetPreparationOpen &&
            result.Evidence?["fleetPreparationObserved"]?.GetValue<bool>() == true &&
            result.Evidence["mapEntered"]?.GetValue<bool>() == false &&
            result.Evidence["cleared"]?.GetValue<bool>() == false,
            "Fleet preparation task entered the map or claimed a cleared sortie");

        var fleetService = new FleetService();
        var configured = request with { Kind = fleetTask.Kind, Input = new JsonObject
        { ["campaign"] = "campaign_main/campaign_1_1", ["fleet1"] = 1, ["fleet2"] = 2, ["submarine"] = 0 } };
        fleetTask.Validate(configured.Input);
        driver = new Driver { Chapter = 1 };
        result = await fleetTask.RunAsync(configured,
            new TaskContext(driver, new Navigator(), null!, TimeSpan.FromSeconds(60),
                Stages: new Stages(driver), Fleets: fleetService), default);
        Check(fleetService.Plan == new FleetPlan(1, 2, 0) && result.Evidence?["fleetSelectionChecked"]?.GetValue<bool>() == true &&
            result.Evidence["fleetSelectionChanged"]?.GetValue<bool>() == true &&
            result.Evidence["mapEntered"]?.GetValue<bool>() == false,
            "Typed fleet plan was not delegated to the C# session");
        await Throws<ArgumentException>(() =>
        {
            fleetTask.Validate(new JsonObject { ["campaign"] = "campaign_main/campaign_1_1", ["fleet1"] = 1 });
            return Task.CompletedTask;
        },
            "Partial fleet plan was accepted");
        await Throws<ArgumentException>(() =>
        {
            fleetTask.Validate(new JsonObject { ["campaign"] = "campaign_main/campaign_1_1", ["fleet1"] = null });
            return Task.CompletedTask;
        },
            "Null fleet plan was silently treated as observation only");

        driver = new Driver { Chapter = 1, MapPreparationVisible = false };
        await driver.ClickAreaAsync(new(200, 250, 230, 280), default);
        await Throws<InvalidDataException>(() => new CampaignPreparation(driver).OpenFleetAsync("normal").AsTask(),
            "Missing map preparation caused a blind click");
        Check(driver.ClickedAssets.Count == 0, "Missing preparation caused a device click");

        driver = new Driver { Chapter = 1, OpenFleetOnClick = false };
        await driver.ClickAreaAsync(new(200, 250, 230, 280), default);
        await Throws<TimeoutException>(() => new CampaignPreparation(driver).OpenFleetAsync("normal").AsTask(),
            "Map preparation clicks without fleet observation became success");
        Check(driver.ClickedAssets.Count == 6, "Unconfirmed preparation exceeded its click limit");

        driver = new Driver { Chapter = 1, DockFullOnPreparationClick = true };
        await driver.ClickAreaAsync(new(200, 250, 230, 280), default);
        await Throws<CampaignDockFullException>(() => new CampaignPreparation(driver).OpenFleetAsync("normal").AsTask(),
            "Dock-full interruption was retried as an ordinary preparation click");
        Check(driver.ClickedAssets.SequenceEqual(["MAP_PREPARATION"]),
            "Dock-full interruption caused another device action");

        driver = new Driver { Chapter = 1, EnterMapOnPreparationClick = true };
        await driver.ClickAreaAsync(new(200, 250, 230, 280), default);
        await Throws<InvalidDataException>(() => new CampaignPreparation(driver).OpenFleetAsync("normal").AsTask(),
            "Unexpected map entry became verified fleet preparation");
        driver = new Driver { Chapter = 1 };
        await driver.ClickAreaAsync(new(200, 250, 230, 280), default);
        await driver.ClickAsync(UiAssets.Map.MAP_PREPARATION, default);
        var entry = await new CampaignEntry(driver, () => driver.FrameSequence).EnterAsync();
        Check(entry is { FleetClicks: 1, FrameSequence: > 0 } && driver.EnterMap &&
            driver.ClickedAssets.SequenceEqual(["MAP_PREPARATION", "FLEET_PREPARATION"]),
            "Fleet preparation did not reach a fresh observed map frame");

        driver = new Driver { Chapter = 1, EnterMapOnFleetClick = false };
        await driver.ClickAreaAsync(new(200, 250, 230, 280), default);
        await driver.ClickAsync(UiAssets.Map.MAP_PREPARATION, default);
        await Throws<TimeoutException>(() => new CampaignEntry(driver, () => driver.FrameSequence).EnterAsync().AsTask(),
            "Stalled fleet preparation became map entry");
        Check(driver.ClickedAssets.Count(asset => asset == "FLEET_PREPARATION") == 6,
            "Stalled fleet preparation exceeded the upstream click bound");

        driver = new Driver { Chapter = 1, EnterMapOnFleetClick = false, ReturnToMapOnFleetClick = true };
        await driver.ClickAreaAsync(new(200, 250, 230, 280), default);
        await driver.ClickAsync(UiAssets.Map.MAP_PREPARATION, default);
        await Throws<InvalidDataException>(() => new CampaignEntry(driver, () => driver.FrameSequence).EnterAsync().AsTask(),
            "Fleet preparation returning to map preparation became map entry");

        driver = new Driver { Chapter = 1, OpenFleetOnClick = false };
        await driver.ClickAreaAsync(new(200, 250, 230, 280), default);
        await Throws<InvalidDataException>(() => new CampaignEntry(driver, () => driver.FrameSequence).EnterAsync().AsTask(),
            "Missing fleet preparation caused a blind map-entry click");
        Check(!driver.ClickedAssets.Contains("FLEET_PREPARATION"), "Missing fleet preparation caused a click");

        var runTask = new CampaignRunTask();
        await Throws<ArgumentException>(() =>
        {
            runTask.Validate(request.Input);
            return Task.CompletedTask;
        }, "Campaign run accepted missing fleet plan");
        var runInput = (JsonObject)configured.Input!.DeepClone();
        runInput["emotionMode"] = "ignore";
        runInput["fleet1Formation"] = "diamond";
        runInput["fleet2Formation"] = "line_ahead";
        runInput["fleetOrder"] = "fleet1_boss_fleet2_mob";
        foreach (var value in new JsonNode?[] { null, JsonValue.Create("unknown"), JsonValue.Create(1) })
        foreach (var field in new[] { "fleet1Formation", "fleetOrder" })
        {
            var malformed = runInput.DeepClone().AsObject();
            malformed[field] = value?.DeepClone();
            bool rejected = false;
            try { runTask.Validate(malformed); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException) { rejected = true; }
            Check(rejected, "Campaign accepted invalid formation/order before device entry");
        }
        runTask.Validate(configured.Input);
        Check(runTask.Preconditions(configured, new(true, true)).SequenceEqual(["emotion_config"]),
            "Default emotion calculation bypassed persistent configuration precondition");
        driver = new Driver { Chapter = 1 };
        var runFleet = new FleetService();
        var interruptions = new Interruptions();
        result = await runTask.RunAsync(configured with { Kind = runTask.Kind, Input = runInput },
            new TaskContext(driver, new Navigator(), null!, TimeSpan.FromSeconds(60),
                Campaign: new CampaignService(), Stages: new Stages(driver), Fleets: runFleet,
                Entry: new EntryService(driver), MapPreparation: new PreparationService(), Interruptions: interruptions), default);
        Check(interruptions.Options is { Mode: RetirementMode.OneClick, KeepLimitBreak: true },
            "Campaign run did not configure native retirement defaults before entry");
        Check(result.Outcome == TaskOutcome.Failed && runFleet.Plan == new FleetPlan(1, 0, 0) &&
            result.Evidence?["campaignIdentityVerified"]?.GetValue<bool>() == true &&
            result.Evidence["entry"]?["fleetClicks"]?.GetValue<int>() == 1 &&
            result.Evidence["sortie"]?["outcome"]?.GetValue<string>() == "ended_unknown" &&
            result.Evidence["emotionMode"]?.GetValue<string>() == "ignore" &&
            result.Evidence["autoSearch"]?["enabled"]?.GetValue<bool>() == false &&
            result.Evidence["cleared"]?.GetValue<bool>() == false,
            "Integrated C# campaign graph reported unverified settlement as a clear");
        driver = new Driver { Chapter = 1 };
        var terminal = new MapArrivalResult(MapArrivalOutcome.StageReturned, 11, 2, MapEncounterKind.Combat)
        {
            HandledEncounters = [MapEncounterKind.Combat],
            Combats = [new CombatFlowResult(CombatReturn.InStage,
                new CombatRankEvidence(CombatRank.S, CombatRankSource.BattleStatus,
                    UiAssets.Combat.BATTLE_STATUS_S.Id), false, false, 3)]
        };
        runInput["retirement"] = new JsonObject { ["mode"] = "disabled" };
        runInput["ambushEvade"] = false;
        interruptions = new Interruptions();
        result = await runTask.RunAsync(configured with { Kind = runTask.Kind, Input = runInput },
            new TaskContext(driver, new Navigator(), null!, TimeSpan.FromSeconds(60),
                Campaign: new CampaignService { StageReturn = terminal, Retirement = RetirementMode.Disabled, AmbushEvade = false }, Stages: new Stages(driver),
                Fleets: new FleetService(), Entry: new EntryService(driver),
                MapPreparation: new PreparationService(), Interruptions: interruptions), default);
        Check(interruptions.Options?.Mode == RetirementMode.Disabled, "Campaign run enabled disabled retirement during entry");
        Check(result.Evidence?["ambushEvade"]?.GetValue<bool>() == false, "Campaign run lost explicit ambush attack choice");
        Check(result.Outcome == TaskOutcome.Succeeded &&
            result.Evidence?["cleared"]?.GetValue<bool>() == true &&
            result.Evidence["sortie"]?["outcome"]?.GetValue<string>() == "cleared" &&
            result.Evidence["entry"] is not null,
            "Integrated campaign evidence merge discarded a verified settlement");
        driver = new Driver { Chapter = 1 };
        runInput.Remove("retirement");
        runInput.Remove("ambushEvade");
        try
        {
            await runTask.RunAsync(configured with { Kind = runTask.Kind, Input = runInput },
                new TaskContext(driver, new Navigator(), null!, TimeSpan.FromSeconds(60),
                    Campaign: new CampaignService { Fail = true }, Stages: new Stages(driver),
                    Fleets: new FleetService(), Entry: new EntryService(driver),
                    MapPreparation: new PreparationService()), default);
            throw new InvalidOperationException("Map execution failure was swallowed");
        }
        catch (TaskEvidenceException error)
        {
            Check(error.Phase == "map_execution" && error.InnerException is IOException &&
                error.Evidence["selection"] is not null && error.Evidence["fleetSetup"] is not null &&
                error.Evidence["entry"] is not null && error.Evidence["cleared"]?.GetValue<bool>() == false,
                "Completed campaign phases were lost on execution failure");
        }
        foreach (bool clear in new[] { false, true })
        foreach (bool book in new[] { false, true })
        {
            driver = new Driver { Chapter = 1 };
            var input = runInput.DeepClone().AsObject();
            input["clearMode"] = clear; input["doubleBook"] = book;
            var preparation = new PreparationService { ClearMode = clear, DoubleBook = clear && book };
            result = await runTask.RunAsync(configured with { Kind = runTask.Kind, Input = input },
                new(driver, new Navigator(), null!, TimeSpan.FromSeconds(60),
                    Campaign: new CampaignService { ClearMode = clear, DoubleBook = clear && book },
                    Stages: new Stages(driver), Fleets: new FleetService(), Entry: new EntryService(driver),
                    MapPreparation: preparation), default);
            Check(preparation.BookConfiguration is { } observed && observed.IsClearMode == clear &&
                observed.UseDoubleBook == book && result.Evidence!["doubleBook"]!["enabled"]!.GetValue<bool>() == (clear && book),
                "Task lost requested versus observed preparation/book state");
        }
        driver = new Driver { Chapter = 1 };
        try
        {
            await runTask.RunAsync(configured with { Kind = runTask.Kind, Input = runInput },
                new(driver, new Navigator(), null!, TimeSpan.FromSeconds(60),
                    Campaign: new CampaignService(), Stages: new Stages(driver), Fleets: new FleetService(),
                    Entry: new EntryService(driver), MapPreparation: new PreparationService { FailBook = true }), default);
            throw new InvalidOperationException("Unknown book state entered the map");
        }
        catch (TaskEvidenceException error)
        {
            Check(error.Phase == "double_book" && error.Evidence["mapPreparation"] is not null &&
                !driver.ClickedAssets.Contains("FLEET_PREPARATION"), "Book failure lost preparation evidence or started sortie");
        }
        driver = new Driver { Chapter = 1, RequireBookPopup = true };
        await driver.ClickAreaAsync(new(200, 250, 230, 280), default);
        await new CampaignPreparation(driver).OpenFleetAsync("normal");
        var popup = new BookPopup(driver);
        var bookEntry = await new CampaignEntry(driver, () => driver.FrameSequence, popups: popup).EnterAsync();
        Check(popup.Confirmed && bookEntry.FleetClicks == 1 && driver.EnterMap,
            "Book confirmation did not return to the existing entry observation loop");
        Console.WriteLine("Campaign entry: mode, chapter, fleet preparation and map-entry transitions passed offline; no real sortie or settlement verified.");
    }

    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static async Task Throws<T>(Func<Task> action, string message) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException(message); }

    private sealed class Stages(Driver driver) : ICampaignStageObservationService
    {
        public bool Duplicate { get; init; }
        public int UnknownChapterOneCalls { get; set; }
        public int Calls { get; private set; }
        public ValueTask<CampaignStages> ObserveStagesAsync(StageEntranceKind kinds, CancellationToken token)
        {
            Check(kinds == StageEntranceKind.Normal, "Main selection used non-main entrance rules");
            token.ThrowIfCancellationRequested();
            Calls++;
            if (driver.Chapter == 1 && UnknownChapterOneCalls > 0)
            {
                UnknownChapterOneCalls--;
                throw new CampaignStageUnknownException("Transient chapter animation");
            }
            string chapter = driver.Chapter.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string name = chapter + "-1";
            var reading = new CampaignStageReading(new(new(200, 250, 230, 280), new(250, 250, 300, 280)),
                name, name, chapter, "1");
            return ValueTask.FromResult(new CampaignStages(Calls, chapter,
                Duplicate ? [reading, reading] : [reading]));
        }
    }
    private sealed class Navigator : IPageNavigator
    {
        public bool Called { get; private set; }
        public ValueTask<NavigationObservation> EnsureAsync(string destination, TimeSpan timeout,
            bool skipFirstScreenshot = true, CancellationToken token = default)
        {
            Check(destination == "page_campaign" && timeout > TimeSpan.Zero, "Selection did not navigate to campaign page");
            Called = true;
            return ValueTask.FromResult(new NavigationObservation(destination, false));
        }
    }
    private sealed class FleetService : ICampaignFleetPreparationService
    {
        public FleetPlan? Plan { get; private set; }
        public ValueTask<FleetSetupResult> ConfigureFleetAsync(FleetPlan plan, IPopupHandler popups, CancellationToken token)
        { Plan = plan; return ValueTask.FromResult(new FleetSetupResult(false, true, true, plan.Submarine)); }
    }
    private sealed class EntryService(Driver driver) : ICampaignEntryService
    {
        public ValueTask<CampaignEntryObservation> EnterFromFleetAsync(CancellationToken token)
            => new CampaignEntry(driver, () => driver.FrameSequence).EnterAsync(token);
    }
    private sealed class BookPopup(Driver driver) : IPopupHandler
    {
        public bool Confirmed { get; private set; }
        public ValueTask<bool> ConfirmAsync(CancellationToken token)
        {
            Check(driver.BookPopupVisible, "Confirmation was not gated by the native book popup");
            Confirmed = true; driver.BookPopupVisible = false; driver.EnterMap = true;
            return ValueTask.FromResult(true);
        }
    }
    private sealed class PreparationService : ICampaignMapPreparationService
    {
        public CampaignMapInfo Info { get; init; } = new(1, .99, true, false, false, false, true);
        public bool ClearMode { get; init; }
        public bool DoubleBook { get; init; }
        public bool FailBook { get; init; }
        public CampaignConfiguration? BookConfiguration { get; private set; }
        public ValueTask<CampaignMapPreparationResult> PrepareMapAsync(CampaignConfiguration configuration,
            TimeSpan timeout, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new CampaignMapPreparationResult(
            Info, ClearMode, false, new(false, false, false))); }
        public ValueTask<DoubleBookObservation> PrepareDoubleBookAsync(CampaignConfiguration configuration,
            TimeSpan timeout, CancellationToken token)
        {
            BookConfiguration = configuration;
            if (FailBook) throw new TimeoutException("Synthetic book failure");
            return ValueTask.FromResult(new DoubleBookObservation(ClearMode, DoubleBook, 0, 2));
        }
    }
    private sealed class SwitchVision(Driver driver) : IImagePatchVision
    {
        public ValueTask<ImagePatchObservation> MeasurePatchAsync(ScreenFrame frame, ImagePatchRequest request,
            CancellationToken token = default)
        {
            Check(request.Measure == PatchMeasure.SimilarityCount &&
                request.Processing == PatchProcessing.ColorSimilarity &&
                request.Color == new PixelColor(158, 234, 94) && request.MinimumSimilarity == 225,
                "Auto-search color rule drifted from the upstream green check");
            return ValueTask.FromResult(new ImagePatchObservation(frame.Sequence, driver.AutoSearchEnabled ? 51 : 0));
        }
    }
    private sealed class CampaignService : ICampaignExecutionService
    {
        public bool AmbushEvade { get; init; } = true;
        public RetirementMode Retirement { get; init; } = RetirementMode.OneClick;
        public bool ClearMode { get; init; }
        public bool DoubleBook { get; init; }
        public bool Fail { get; init; }
        public MapArrivalResult? StageReturn { get; init; }
        public ValueTask<CampaignResumeResult> ResumeInMapAsync(CampaignRule rule,
            CampaignConfiguration configuration, CancellationToken token)
        {
            if (Fail) throw new IOException("Injected map execution failure");
            Check(configuration.IsClearMode == ClearMode && configuration.IsDoubleBook == DoubleBook,
                "Campaign execution lost confirmed map/book state");
            Check(configuration.Retirement.Mode == Retirement, "Campaign execution lost the requested retirement mode");
            Check(configuration.AmbushEvade == AmbushEvade, "Campaign execution lost requested ambush mode");
            Check(configuration is { EmotionMode: CampaignEmotionMode.Ignore, UseFleetLock: true,
                Fleet1Formation: FleetFormation.Diamond, Fleet2Formation: FleetFormation.LineAhead,
                FleetOrder: FleetOrder.Fleet1BossFleet2Mob, Vision: not null },
                "Integrated campaign did not preserve explicit emotion, fleet-lock and formation settings");
            return ValueTask.FromResult(new CampaignResumeResult(CampaignLoopExit.Ended, 1, StageReturn));
        }
    }
    private sealed class Interruptions : ICampaignInterruptions
    {
        public RetirementOptions? Options { get; private set; }
        public void Configure(RetirementOptions retirement, CampaignEmotionMode emotion)
        { Check(emotion == CampaignEmotionMode.Ignore, "Campaign ignored its emotion choice"); Options = retirement; }
        public ValueTask<bool> RetirementAsync(CancellationToken token)
        { Check(Options is not null, "Campaign entry used unconfigured interruptions"); return ValueTask.FromResult(false); }
        public ValueTask<bool> LowEmotionAsync(CancellationToken token) => ValueTask.FromResult(false);
    }
    private sealed class Driver : IUiDriver
    {
        public GameServer Server => GameServer.Cn;
        public bool HasFrame { get; private set; }
        public TimeProvider Clock => TimeProvider.System;
        public int Chapter { get; set; }
        public bool ModeNormal { get; set; } = true;
        public bool HasSecondMode { get; set; }
        public bool SecondModeHard { get; set; }
        public bool StallChapter { get; set; }
        public bool ConfirmPreparation { get; set; } = true;
        public bool EnterMap { get; set; }
        public bool MapPreparationVisible { get; set; } = true;
        public bool OpenFleetOnClick { get; set; } = true;
        public bool EnterMapOnPreparationClick { get; set; }
        public bool EnterMapOnFleetClick { get; set; } = true;
        public bool ReturnToMapOnFleetClick { get; set; }
        public bool DockFullOnPreparationClick { get; set; }
        public bool DockFullVisible { get; private set; }
        public bool AutoSearchAvailable { get; set; }
        public bool AutoSearchEnabled { get; set; }
        public bool RequireBookPopup { get; init; }
        public bool BookPopupVisible { get; set; }
        public bool FleetLockAvailable { get; set; }
        public bool FleetLockEnabled { get; set; }
        public int FleetLockTransitionFrames { get; private set; }
        public bool FleetPreparationOpen { get; private set; }
        public bool Selected { get; private set; }
        public long FrameSequence { get; private set; }
        public List<string> ClickedAssets { get; } = [];
        public List<Rectangle> ClickedAreas { get; } = [];
        public ValueTask ScreenshotAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); HasFrame = true; FrameSequence++; if (FleetLockTransitionFrames > 0) FleetLockTransitionFrames--; return ValueTask.CompletedTask; }
        public ValueTask<bool> AppearsAsync(AssetRule asset, ButtonOffset offset = default, double interval = 0,
            double similarity = .85, int threshold = 10, TemplatePreprocessing preprocessing = TemplatePreprocessing.Color,
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            bool present = asset == UiAssets.Campaign.SWITCH_2_HARD ? HasSecondMode && SecondModeHard :
                asset == UiAssets.Campaign.SWITCH_2_EX ? HasSecondMode && !SecondModeHard :
                asset == UiAssets.Campaign.SWITCH_1_HARD ? ModeNormal :
                asset == UiAssets.Campaign.SWITCH_1_NORMAL ? !ModeNormal :
                asset == UiAssets.Campaign.CHAPTER_NEXT || asset == UiAssets.Campaign.CHAPTER_PREV ? false :
                asset == UiAssets.Map.MAP_PREPARATION ? Selected && ConfirmPreparation && MapPreparationVisible &&
                    !FleetPreparationOpen && !EnterMap :
                asset == UiAssets.Map.FLEET_PREPARATION ? FleetPreparationOpen :
                asset == UiAssets.Retire.RETIRE_APPEAR_1 || asset == UiAssets.Retire.RETIRE_APPEAR_3 ? DockFullVisible :
                asset == UiAssets.Handler.AUTO_SEARCH_TITLE ? AutoSearchAvailable :
                asset == UiAssets.Handler.BOOK_POPUP_CHECK ? BookPopupVisible :
                asset == UiAssets.Handler.FLEET_LOCKED ? FleetLockAvailable && FleetLockTransitionFrames == 0 && FleetLockEnabled :
                asset == UiAssets.Handler.FLEET_UNLOCKED ? FleetLockAvailable && FleetLockTransitionFrames == 0 && !FleetLockEnabled :
                asset == UiAssets.Handler.IN_MAP ? Selected && EnterMap : false;
            return ValueTask.FromResult(present);
        }
        public ValueTask ClickAsync(AssetRule asset, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ClickedAssets.Add(asset.Name);
            if (asset == UiAssets.Campaign.SWITCH_2_HARD) SecondModeHard = false;
            else if (asset == UiAssets.Campaign.SWITCH_1_NORMAL) ModeNormal = true;
            else if (!StallChapter && asset == UiAssets.Campaign.CHAPTER_PREV) Chapter--;
            else if (!StallChapter && asset == UiAssets.Campaign.CHAPTER_NEXT) Chapter++;
            else if (asset == UiAssets.Map.MAP_PREPARATION && EnterMapOnPreparationClick) EnterMap = true;
            else if (asset == UiAssets.Map.MAP_PREPARATION && DockFullOnPreparationClick) DockFullVisible = true;
            else if (asset == UiAssets.Map.MAP_PREPARATION && OpenFleetOnClick) FleetPreparationOpen = true;
            else if (asset == UiAssets.Map.FLEET_PREPARATION && RequireBookPopup)
            { FleetPreparationOpen = false; BookPopupVisible = true; }
            else if (asset == UiAssets.Map.FLEET_PREPARATION && EnterMapOnFleetClick)
            { FleetPreparationOpen = false; EnterMap = true; }
            else if (asset == UiAssets.Map.FLEET_PREPARATION && ReturnToMapOnFleetClick)
                FleetPreparationOpen = false;
            else if (asset == UiAssets.Handler.AUTO_SEARCH_CHECK) AutoSearchEnabled = !AutoSearchEnabled;
            else if (asset == UiAssets.Handler.FLEET_UNLOCKED || asset == UiAssets.Handler.FLEET_LOCKED)
            { FleetLockEnabled = !FleetLockEnabled; FleetLockTransitionFrames = 2; }
            return ValueTask.CompletedTask;
        }
        public ValueTask ClickAreaAsync(Rectangle area, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Selected = true; ClickedAreas.Add(area); return ValueTask.CompletedTask; }
        public ValueTask DelayAsync(TimeSpan time, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
        public ValueTask<MeanColorObservation> ColorAsync(Rectangle area, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<ColorBandObservation> ColorBandsAsync(ColorBandRequest request, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<OcrObservation> ReadTextAsync(OcrRequest request, CancellationToken token) => throw new NotSupportedException();
        public Rectangle ButtonArea(AssetRule asset) => asset.For(Server).Area ?? throw new InvalidDataException();
        public void LoadOffset(AssetRule target, AssetRule reference) { }
        public void ClearOffset(AssetRule asset) { }
        public IntervalTimer Timer(AssetRule asset, double seconds = 5, bool renew = false) => throw new NotSupportedException();
        public void ResetInterval(AssetRule asset, double seconds = 3) { }
        public void ClearInterval(AssetRule asset) { }
    }
}
