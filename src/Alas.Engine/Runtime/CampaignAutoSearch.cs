using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed record AutoSearchObservation(bool Available, bool Changed, bool Enabled);

/// <summary>Native three-title reader; switching uses the shared native Switch state machine.</summary>
public sealed class CampaignAutoSearch(IUiDriver ui, IImagePatchVision vision, Func<ScreenFrame> currentFrame)
{
    public static readonly SourceFile Source = CampaignFleetLock.Source;
    private readonly UiVisuals _visuals = new(vision, currentFrame);
    private static ButtonOffset Offset => ButtonOffset.Expand(20, 20);

    public async ValueTask<AutoSearchObservation> EnsureManualAsync(CancellationToken token = default)
    {
        await ui.ScreenshotAsync(token);
        return await EnsureManualOnFrameAsync(TimeSpan.FromSeconds(45), token);
    }

    public async ValueTask<AutoSearchObservation> EnsureManualOnFrameAsync(TimeSpan timeout, CancellationToken token)
    {
        if (await ReadAsync(token) is null) return new(false, false, false);
        bool changed = await new UiSwitch(ui,
            [new("on", UiAssets.Handler.AUTO_SEARCH_TITLE, UiAssets.Handler.AUTO_SEARCH_CHECK),
             new("off", UiAssets.Handler.AUTO_SEARCH_TITLE, UiAssets.Handler.AUTO_SEARCH_CHECK)],
            Offset, read: ReadAsync, frameSequence: () => currentFrame().Sequence).SetAsync("off", timeout, token: token);
        return new(true, changed, false);
    }

    public async ValueTask<string?> ReadAsync(CancellationToken token)
    {
        foreach (var title in new[] { UiAssets.Handler.AUTO_SEARCH_TITLE,
                     UiAssets.Handler.AUTO_SEARCH_TITLE2, UiAssets.Handler.AUTO_SEARCH_TITLE3 })
        {
            if (!await ui.AppearsAsync(title, Offset, token: token)) continue;
            ui.LoadOffset(UiAssets.Handler.AUTO_SEARCH_CHECK, title);
            return await _visuals.ColorCountAsync(ui.ButtonArea(UiAssets.Handler.AUTO_SEARCH_CHECK),
                new(158, 234, 94), 30, 50, token) ? "on" : "off";
        }
        return null;
    }
}
