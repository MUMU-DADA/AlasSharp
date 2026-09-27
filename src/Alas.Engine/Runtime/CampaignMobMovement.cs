using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

/// <summary>Device side of the upstream strategy mob-move action.
/// Coordinates still come from the localized C# map camera; no chapter or
/// Python callback is involved in the interaction.</summary>
public sealed class CampaignMobMovement(IUiDriver ui, MapCamera camera)
{
    public static readonly SourceFile Source = new("campaign/campaign_main/campaign_15_base.py",
        "60f28ffa4c3c6a8255428d99e974f8d7ef7f1c4ea8f3921d4ae5fc48bb2e4ed6");

    public async ValueTask<bool> MoveAsync(Cell origin, Cell target, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        // The upstream helper brings the farther of the two adjacent cells into
        // view so one localized frame contains both taps. Focusing only the
        // origin can leave a boundary target outside the projection.
        static int Distance(Cell a, Cell b) => Math.Abs(a.Column - b.Column) + Math.Abs(a.Row - b.Row);
        await camera.PrepareTapAsync(Distance(origin, camera.Position) >= Distance(target, camera.Position) ? origin : target, token);
        await ui.ScreenshotAsync(token);

        var limit = new IntervalTimer(ui.Clock, 2, 4);
        limit.Reset();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (await ui.AppearsAsync(UiAssets.Handler.STRATEGY_OPENED,
                    ButtonOffset.Vertical(200), token: token)) break;
            if (limit.Reached()) throw new TimeoutException("Strategy panel did not open for movable-enemy action");
            await ui.ClickAsync(UiAssets.Handler.STRATEGY_OPEN, token);
            await ui.DelayAsync(TimeSpan.FromMilliseconds(250), token);
            await ui.ScreenshotAsync(token);
            limit.Reset();
        }

        // The action is available only while its native button is present. A
        // missing button means the upstream strategy has exhausted its trials.
        if (!await ui.AppearsAsync(UiAssets.Handler.MOB_MOVE_ENTER,
                ButtonOffset.Expand(80, 80), token: token))
        {
            await CloseStrategyAsync(token);
            return false;
        }
        await ui.ClickAsync(UiAssets.Handler.MOB_MOVE_ENTER, token);
        await ui.DelayAsync(TimeSpan.FromMilliseconds(250), token);
        await ui.ScreenshotAsync(token);
        if (!await ui.AppearsAsync(UiAssets.Handler.MOB_MOVE_CANCEL,
                ButtonOffset.Expand(80, 80), token: token))
        {
            await CloseStrategyAsync(token);
            return false;
        }

        // Origin and destination are tapped against one unchanged localized
        // frame. MapCamera refreshes only after both clicks, matching the
        // overlay's two-step selection protocol.
        await camera.TapCellsAsync([origin, target], token);
        for (int attempt = 0; attempt < 12; attempt++)
        {
            token.ThrowIfCancellationRequested();
            await ui.ScreenshotAsync(token);
            if (await ui.AppearsAsync(UiAssets.Handler.STRATEGY_OPENED,
                    ButtonOffset.Vertical(200), token: token))
            {
                await ui.ClickAsync(UiAssets.Handler.STRATEGY_OPENED, token);
                return true;
            }
            if (await ui.AppearsAsync(UiAssets.Handler.POPUP_CANCEL,
                    ButtonOffset.Bounds(3, 30, 3, 30), token: token) &&
                await ui.AppearsAsync(UiAssets.Handler.POPUP_CONFIRM,
                    ButtonOffset.Bounds(3, 30, 3, 30), token: token))
            {
                await ui.ClickAsync(UiAssets.Handler.POPUP_CONFIRM, token);
                continue;
            }
            if (await ui.AppearsAsync(UiAssets.UiWhite.POPUP_CONFIRM_WHITE,
                    ButtonOffset.Bounds(3, 30, 3, 30), token: token))
            {
                await ui.ClickAsync(UiAssets.UiWhite.POPUP_CONFIRM_WHITE, token);
                continue;
            }
            await ui.DelayAsync(TimeSpan.FromMilliseconds(250), token);
        }
        throw new TimeoutException("Movable-enemy target selection was not confirmed");
    }

    private async ValueTask CloseStrategyAsync(CancellationToken token)
    {
        for (int attempt = 0; attempt < 12; attempt++)
        {
            token.ThrowIfCancellationRequested();
            await ui.ScreenshotAsync(token);
            if (!await ui.AppearsAsync(UiAssets.Handler.STRATEGY_OPENED,
                    ButtonOffset.Vertical(200), token: token)) return;
            await ui.ClickAsync(UiAssets.Handler.STRATEGY_OPENED, token);
            await ui.DelayAsync(TimeSpan.FromMilliseconds(250), token);
        }
        throw new TimeoutException("Strategy panel did not close after movable-enemy action");
    }
}
