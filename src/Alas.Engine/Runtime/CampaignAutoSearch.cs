using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed record AutoSearchObservation(bool Available, bool Changed, bool Enabled);

public interface ICampaignAutoSearchService
{
    ValueTask<AutoSearchObservation> EnsureManualAsync(CancellationToken token);
}

/// <summary>Map-preparation auto-search switch using the native title variants and green-check rule.</summary>
public sealed class CampaignAutoSearch(IUiDriver ui, IImagePatchVision vision, Func<ScreenFrame> currentFrame)
{
    public static readonly SourceFile Source = CampaignFleetLock.Source;
    private static ButtonOffset Offset => ButtonOffset.Expand(20, 20);

    public async ValueTask<AutoSearchObservation> EnsureManualAsync(CancellationToken token = default)
    {
        await ui.ScreenshotAsync(token);
        var title = await TitleAsync(token);
        if (title is null) return new(false, false, false);
        if (!await EnabledAsync(title, token)) return new(true, false, false);

        for (int click = 0; click < 6; click++)
        {
            ui.LoadOffset(UiAssets.Handler.AUTO_SEARCH_CHECK, title);
            await ui.ClickAsync(UiAssets.Handler.AUTO_SEARCH_CHECK, token);
            for (int frame = 0; frame < 20; frame++)
            {
                await ui.DelayAsync(TimeSpan.FromMilliseconds(250), token);
                await ui.ScreenshotAsync(token);
                title = await TitleAsync(token) ??
                    throw new InvalidDataException("Auto-search option disappeared before its new state was observed");
                if (!await EnabledAsync(title, token)) return new(true, true, false);
            }
        }
        throw new TimeoutException("Auto-search could not be disabled on map preparation");
    }

    private async ValueTask<AssetRule?> TitleAsync(CancellationToken token)
    {
        foreach (var title in new[] { UiAssets.Handler.AUTO_SEARCH_TITLE,
                     UiAssets.Handler.AUTO_SEARCH_TITLE2, UiAssets.Handler.AUTO_SEARCH_TITLE3 })
            if (await ui.AppearsAsync(title, Offset, token: token)) return title;
        return null;
    }

    private async ValueTask<bool> EnabledAsync(AssetRule title, CancellationToken token)
    {
        ui.LoadOffset(UiAssets.Handler.AUTO_SEARCH_CHECK, title);
        var frame = currentFrame();
        var area = ui.ButtonArea(UiAssets.Handler.AUTO_SEARCH_CHECK);
        var green = await vision.MeasurePatchAsync(frame,
            new(area.Area, area.Width, area.Height, PatchMeasure.SimilarityCount,
                PatchProcessing.ColorSimilarity, new(158, 234, 94), MinimumSimilarity: 225), token);
        if (green.FrameSequence != frame.Sequence)
            throw new InvalidDataException("Auto-search color belongs to another frame");
        return green.Value > 50;
    }
}
