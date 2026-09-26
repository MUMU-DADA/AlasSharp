using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed record FleetLockObservation(bool Available, bool Changed);

/// <summary>Upstream Fleet_Lock switch over the two compiled state buttons.</summary>
public sealed class CampaignFleetLock(IUiDriver ui)
{
    public static readonly SourceFile Source = new("module/handler/fast_forward.py",
        "98e57f845b86c729eaa77b8027344bd298f768a4e362c9e5e2f9ea3e872f7c76");
    private static ButtonOffset Offset => ButtonOffset.Expand(5, 20);

    public async ValueTask<FleetLockObservation> EnsureAsync(bool enabled, CancellationToken token = default)
    {
        await ui.ScreenshotAsync(token);
        bool? initial = await ReadAsync(token);
        if (initial is null) return new(false, false);
        if (initial == enabled) return new(true, false);
        bool changed = false;
        int unknownFrames = 0;
        int clickCooldown = 0;
        // Switch.set waits through the animation, then retries a click after the
        // one-second click interval. Bound the generic loop so a missing asset
        // cannot leave a campaign task running forever.
        for (int frame = 0; frame < 120; frame++)
        {
            bool? current = await ReadAsync(token);
            if (current == enabled) return new(true, changed);
            if (current is null)
            {
                unknownFrames++;
            }
            else
            {
                unknownFrames = 0;
            }

            if (clickCooldown > 0) clickCooldown--;
            bool mayClick = clickCooldown == 0 && (current is not null || unknownFrames >= 20);
            if (mayClick)
            {
                // For an unknown transition state Switch.set clicks the target
                // state; for a known state it clicks the currently visible one.
                AssetRule clickAsset = current switch
                {
                    true => UiAssets.Handler.FLEET_LOCKED,
                    false => UiAssets.Handler.FLEET_UNLOCKED,
                    null => enabled ? UiAssets.Handler.FLEET_LOCKED : UiAssets.Handler.FLEET_UNLOCKED
                };
                await ui.ClickAsync(clickAsset, token);
                changed = true;
                clickCooldown = 4;
                unknownFrames = 0;
            }

            await ui.DelayAsync(TimeSpan.FromMilliseconds(250), token);
            await ui.ScreenshotAsync(token);
        }
        throw new TimeoutException("Fleet lock did not reach the requested state or remained unrecognized");
    }

    private async ValueTask<bool?> ReadAsync(CancellationToken token)
    {
        if (await ui.AppearsAsync(UiAssets.Handler.FLEET_LOCKED, Offset, token: token)) return true;
        if (await ui.AppearsAsync(UiAssets.Handler.FLEET_UNLOCKED, Offset, token: token)) return false;
        return null;
    }
}
