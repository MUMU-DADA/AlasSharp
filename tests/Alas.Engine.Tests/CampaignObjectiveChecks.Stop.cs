using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static partial class CampaignObjectiveChecks
{
    private static readonly CampaignMapInfo ReachedInfo = new(1, .99, true, true, true, true, true);
    private static async Task StopChecksAsync()
    {
        var rule = RuleCatalog.Create("campaign_main/campaign_1_1");
        foreach (var page in new[] { UiAssets.Map.MAP_PREPARATION, UiAssets.Map.MAP_PREPARATION_HARD, UiAssets.Map.FLEET_PREPARATION })
        foreach (bool increase in new[] { false, true })
        {
            var ui = new StopDriver(page); var store = new StopStore(ui);
            await ui.ScreenshotAsync(default);
            var controller = new CampaignAchievement(ui, () => ui.Sequence, ui.InStageAsync, store, []);
            await controller.PrepareAsync(rule, new() { MapAchievement = MapAchievement.ThreeStars, StageIncrease = increase }, default);
            var evidence = await controller.StopAsync(ReachedInfo, TimeSpan.FromSeconds(30), default);
            Check(evidence is { Persisted: true, CancelClicks: 1, ReturnedFrame: 2 } &&
                evidence.Disabled == !increase && evidence.NextStage == (increase ? "1-2" : null) && store.Writes == 1,
                "Cancel/return/persistence did not preserve native stop decision");
            Check(ui.Calls.Any(c => c.Asset == page.Id && c.Interval == 2), "Cancel lost upstream appearance interval");
            await Rejects<InvalidOperationException>(() => controller.StopAsync(ReachedInfo, TimeSpan.FromSeconds(30), default).AsTask());
        }
        foreach (string failure in new[] { "click", "stale", "timeout", "inmap", "save", "cancel" })
        {
            var ui = new StopDriver(UiAssets.Map.MAP_PREPARATION) { Failure = failure };
            var store = new StopStore(ui) { Fail = failure == "save" };
            await ui.ScreenshotAsync(default);
            var controller = new CampaignAchievement(ui, () => ui.Sequence, ui.InStageAsync, store, []);
            await controller.PrepareAsync(rule, new() { MapAchievement = MapAchievement.FullyCleared }, default);
            using var cancel = new CancellationTokenSource(); if (failure == "cancel") cancel.Cancel();
            try { await controller.StopAsync(ReachedInfo, TimeSpan.FromSeconds(15), cancel.Token); throw new Exception("Failure was swallowed"); }
            catch (Exception error) when (error is IOException or InvalidDataException or TimeoutException or OperationCanceledException) { }
            Check(controller.Evidence is { Persisted: false, Info: not null } && store.Writes == 0 &&
                (controller.Evidence.ReturnedFrame is not null) == (failure == "save"), "Partial stop failure lost evidence or wrote configuration early");
        }
        {
            var ui = new StopDriver(UiAssets.Map.MAP_PREPARATION);
            var store = new StopStore(ui) { Snapshot = new("0", "campaign_main", "1-2", true) };
            var controller = new CampaignAchievement(ui, () => ui.Sequence, ui.InStageAsync, store, []);
            await Rejects<InvalidDataException>(() => controller.PrepareAsync(rule, new(), default).AsTask());
            Check(ui.Clicks == 0 && store.Writes == 0, "Mismatched configuration reached device or persistence");
        }
    }

    private sealed class StopStore(StopDriver ui) : ICampaignStopStore
    {
        public CampaignStopSnapshot Snapshot { get; init; } = new("0", "campaign_main", "1-1", true);
        public bool Fail { get; init; }
        public int Writes { get; private set; }
        public ValueTask<CampaignStopSnapshot> ReadAsync(CancellationToken token) => ValueTask.FromResult(Snapshot);
        public ValueTask SaveAsync(CampaignStopSnapshot expected, string? nextStage, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Check(ui.Sequence > ReachedInfo.FrameSequence && ui.Returned && expected == Snapshot, "Persistence preceded confirmed stage return");
            if (Fail) throw new IOException("Synthetic persistence failure");
            Writes++; return ValueTask.CompletedTask;
        }
    }
    private sealed class StopDriver(AssetRule page) : AppearanceProbe(GameServer.Cn, page.Id)
    {
        public string? Failure { get; init; }
        public long Sequence { get; private set; }
        public int Clicks { get; private set; }
        public bool Returned { get; private set; }
        public ValueTask<bool> InStageAsync(CancellationToken token) => ValueTask.FromResult(Returned);
        public override async ValueTask ScreenshotAsync(CancellationToken token)
        {
            await base.ScreenshotAsync(token);
            if (Failure != "stale" || Sequence == 0) Sequence++;
            Returned = Clicks > 0 && Failure != "timeout";
        }
        public override ValueTask<bool> AppearsAsync(AssetRule asset, ButtonOffset offset = default, double interval = 0,
            double similarity = .85, int threshold = 10, TemplatePreprocessing preprocessing = TemplatePreprocessing.Color,
            CancellationToken token = default) => Failure == "inmap" && asset == UiAssets.Handler.IN_MAP
                ? ValueTask.FromResult(true) : base.AppearsAsync(asset, offset, interval, similarity, threshold, preprocessing, token);
        public override ValueTask ClickAsync(AssetRule asset, CancellationToken token)
        {
            Check(asset == UiAssets.Map.MAP_PREPARATION_CANCEL, "Stop clicked a non-cancel control");
            if (Failure == "click") throw new IOException("Synthetic click failure");
            Clicks++; return ValueTask.CompletedTask;
        }
    }
}
