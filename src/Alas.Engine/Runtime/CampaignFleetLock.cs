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
        var control = new UiSwitch(ui,
            [new("on", UiAssets.Handler.FLEET_LOCKED), new("off", UiAssets.Handler.FLEET_UNLOCKED)], Offset);
        if (await control.ReadAsync(token) is null) return new(false, false);
        bool changed = await control.SetAsync(enabled ? "on" : "off", TimeSpan.FromSeconds(30), token: token);
        return new(true, changed);
    }
}
