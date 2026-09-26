using System.Globalization;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed record CampaignStageSelection(string Stage, string Chapter, long OcrFrameSequence,
    Rectangle Entrance, string Preparation);

/// <summary>Selects a compiled main-stage rule and stops at the observed map preparation page.</summary>
public sealed class CampaignStageSelector(IUiDriver ui, ICampaignStageObservationService stages)
{
    public static readonly SourceFile Source = new("module/campaign/campaign_ui.py",
        "005dd1ab24def262dd237c33cafa540fd20cce428ce991214016c34a805abada");
    private static ButtonOffset SwitchOffset => ButtonOffset.Expand(30, 10);
    private static ButtonOffset PreparationOffset => ButtonOffset.Expand(20, 20);

    public async ValueTask<CampaignStageSelection> SelectAsync(string stage, CancellationToken token = default)
    {
        var (chapter, index) = CampaignStageReader.Separate(stage);
        if (chapter is null || index is null || !int.TryParse(chapter, NumberStyles.None,
                CultureInfo.InvariantCulture, out int target) || target < 1)
            throw new NotSupportedException("Only compiled numeric main-stage selection is available");
        await EnsureNormalModeAsync(token);
        var observed = await ObserveStableAsync(token);
        for (int clicks = 0; clicks <= 20; clicks++)
        {
            if (!int.TryParse(observed.Chapter, NumberStyles.None, CultureInfo.InvariantCulture, out int current))
                throw new InvalidDataException("Stage OCR is not on a numeric main chapter");
            if (current == target)
            {
                var first = UniqueStage(observed, stage);
                var confirmed = await ObserveStableAsync(token);
                var second = UniqueStage(confirmed, stage);
                if (confirmed.Chapter != chapter ||
                    Math.Abs(first.Entrance.Icon.Left - second.Entrance.Icon.Left) > 5 ||
                    Math.Abs(first.Entrance.Icon.Top - second.Entrance.Icon.Top) > 5)
                    throw new InvalidDataException("Stage entrance changed between OCR confirmations");
                await ui.ClickAreaAsync(second.Entrance.Icon, token);
                string preparation = await WaitForPreparationAsync(token);
                return new(stage, chapter, confirmed.FrameSequence, second.Entrance.Icon, preparation);
            }
            if (clicks == 20) break;
            var direction = current < target ? UiAssets.Campaign.CHAPTER_NEXT : UiAssets.Campaign.CHAPTER_PREV;
            await ui.ClickAsync(direction, token);
            observed = await WaitForChapterChangeAsync(observed.Chapter, token);
        }
        throw new TimeoutException("Chapter selection exceeded its click limit");
    }

    private async ValueTask EnsureNormalModeAsync(CancellationToken token)
    {
        await ui.ScreenshotAsync(token);
        if (await ui.AppearsAsync(UiAssets.Campaign.SWITCH_2_HARD, SwitchOffset, token: token))
        {
            await ui.ClickAsync(UiAssets.Campaign.SWITCH_2_HARD, token);
            await WaitForSwitchAsync(UiAssets.Campaign.SWITCH_2_EX, token);
        }
        if (await ui.AppearsAsync(UiAssets.Campaign.SWITCH_1_HARD, SwitchOffset, token: token)) return;
        if (!await ui.AppearsAsync(UiAssets.Campaign.SWITCH_1_NORMAL, SwitchOffset, token: token))
            throw new InvalidDataException("Campaign mode switch is not recognized");
        await ui.ClickAsync(UiAssets.Campaign.SWITCH_1_NORMAL, token);
        await WaitForSwitchAsync(UiAssets.Campaign.SWITCH_1_HARD, token);
    }

    private async ValueTask WaitForSwitchAsync(AssetRule expected, CancellationToken token)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            await ui.DelayAsync(TimeSpan.FromMilliseconds(250), token);
            await ui.ScreenshotAsync(token);
            if (await ui.AppearsAsync(expected, SwitchOffset, token: token)) return;
        }
        throw new TimeoutException("Campaign mode switch did not settle");
    }

    private async ValueTask<CampaignStages> ObserveStableAsync(CancellationToken token)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            CampaignStages first;
            try { first = await stages.ObserveStagesAsync(StageEntranceKind.Normal, token); }
            catch (CampaignStageUnknownException) { continue; }
            await ui.DelayAsync(TimeSpan.FromMilliseconds(200), token);
            try
            {
                var second = await stages.ObserveStagesAsync(StageEntranceKind.Normal, token);
                if (first.Chapter == second.Chapter) return second;
            }
            catch (CampaignStageUnknownException) { }
        }
        throw new CampaignStageUnknownException("Campaign chapter OCR did not stabilize");
    }

    private async ValueTask<CampaignStages> WaitForChapterChangeAsync(string previous, CancellationToken token)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            await ui.DelayAsync(TimeSpan.FromMilliseconds(250), token);
            try
            {
                var observed = await ObserveStableAsync(token);
                if (observed.Chapter != previous) return observed;
            }
            catch (CampaignStageUnknownException) { }
        }
        throw new TimeoutException("Chapter switch did not change the observed chapter");
    }

    private async ValueTask<string> WaitForPreparationAsync(CancellationToken token)
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            await ui.DelayAsync(TimeSpan.FromMilliseconds(250), token);
            await ui.ScreenshotAsync(token);
            if (await ui.AppearsAsync(UiAssets.Map.MAP_PREPARATION, PreparationOffset, token: token))
                return "normal";
            if (await ui.AppearsAsync(UiAssets.Map.MAP_PREPARATION_HARD, PreparationOffset, token: token))
                return "hard";
            if (await ui.AppearsAsync(UiAssets.Handler.IN_MAP, token: token))
                throw new InvalidDataException("Stage selection entered a map without a preparation observation");
        }
        throw new TimeoutException("Selected stage did not reach map preparation");
    }

    private static CampaignStageReading UniqueStage(CampaignStages observed, string stage)
    {
        var matches = observed.Readings.Where(reading => reading.Name == stage).ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException("Requested stage is missing or ambiguous in the observed chapter");
        return matches[0];
    }
}
