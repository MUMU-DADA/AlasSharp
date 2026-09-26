using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static class CampaignStageSelectorChecks
{
    public static async Task RunAsync(string upstream)
    {
        var source = CampaignStageSelector.Source;
        Check(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(upstream, source.Path)))) == source.Sha256,
            "Campaign stage selector source drifted");
        Check(RuleCatalog.Ids.Select(id => RuleCatalog.Create(id).StageName)
            .SequenceEqual(["1-1", "1-2", "1-3", "1-4"]), "Compiled main-stage identities changed");

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
        Console.WriteLine("Campaign stage/fleet preparation: mode, chapter, unique stage, map and fleet preparation transitions passed offline; no sortie entered.");
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
        public bool DockFullOnPreparationClick { get; set; }
        public bool DockFullVisible { get; private set; }
        public bool FleetPreparationOpen { get; private set; }
        public bool Selected { get; private set; }
        public List<string> ClickedAssets { get; } = [];
        public List<Rectangle> ClickedAreas { get; } = [];
        public ValueTask ScreenshotAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); HasFrame = true; return ValueTask.CompletedTask; }
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
            return ValueTask.CompletedTask;
        }
        public ValueTask ClickAreaAsync(Rectangle area, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Selected = true; ClickedAreas.Add(area); return ValueTask.CompletedTask; }
        public ValueTask DelayAsync(TimeSpan time, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
        public ValueTask<MeanColorObservation> ColorAsync(Rectangle area, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<ColorBandObservation> ColorBandsAsync(ColorBandRequest request, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<OcrObservation> ReadTextAsync(OcrRequest request, CancellationToken token) => throw new NotSupportedException();
        public void ClearOffset(AssetRule asset) { }
        public IntervalTimer Timer(AssetRule asset, double seconds = 5, bool renew = false) => throw new NotSupportedException();
        public void ResetInterval(AssetRule asset, double seconds = 3) { }
        public void ClearInterval(AssetRule asset) { }
    }
}
