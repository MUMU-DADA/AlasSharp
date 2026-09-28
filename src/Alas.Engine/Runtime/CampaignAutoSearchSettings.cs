using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

/// <summary>Native fleet sidebar and role selection, including detected button offsets.</summary>
public sealed class CampaignAutoSearchSettings(IUiDriver ui, IImagePatchVision vision, Func<ScreenFrame> currentFrame)
{
    public static readonly SourceFile Source = new("module/handler/auto_search.py",
        "f61796a116840175a539a9154252afa3587da9824791604fe574540f27e94d56");
    private readonly UiVisuals _visuals = new(vision, currentFrame);
    internal static readonly AssetRule[] Settings = [UiAssets.Handler.AUTO_SEARCH_SET_MOB,
        UiAssets.Handler.AUTO_SEARCH_SET_BOSS, UiAssets.Handler.AUTO_SEARCH_SET_ALL,
        UiAssets.Handler.AUTO_SEARCH_SET_STANDBY, UiAssets.Handler.AUTO_SEARCH_SET_SUB_AUTO,
        UiAssets.Handler.AUTO_SEARCH_SET_SUB_STANDBY];

    public ValueTask EnsureSubmarineStandbyAsync(CancellationToken token = default)
        => EnsureAsync(UiAssets.Handler.AUTO_SEARCH_SET_SUB_STANDBY, token);

    public ValueTask EnsureFleetOrderAsync(FleetOrder order, CancellationToken token = default)
        => EnsureAsync(order switch
        {
            FleetOrder.Fleet1MobFleet2Boss => UiAssets.Handler.AUTO_SEARCH_SET_MOB,
            FleetOrder.Fleet1BossFleet2Mob => UiAssets.Handler.AUTO_SEARCH_SET_BOSS,
            FleetOrder.Fleet1AllFleet2Standby => UiAssets.Handler.AUTO_SEARCH_SET_ALL,
            FleetOrder.Fleet1StandbyFleet2All => UiAssets.Handler.AUTO_SEARCH_SET_STANDBY,
            _ => throw new ArgumentOutOfRangeException(nameof(order))
        }, token);

    public ValueTask EnsureSubmarineAsync(bool autoCall, CancellationToken token = default)
        => EnsureAsync(autoCall ? UiAssets.Handler.AUTO_SEARCH_SET_SUB_AUTO : UiAssets.Handler.AUTO_SEARCH_SET_SUB_STANDBY, token);

    private async ValueTask EnsureAsync(AssetRule target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await CaptureAsync(token);
        var interval = new IntervalTimer(ui.Clock, 1, 2);
        var limit = new IntervalTimer(ui.Clock, 3); limit.Reset();
        var sidebar = await SidebarAsync(token);
        while (true)
        {
            int current = 0;
            var areas = await SidebarAsync(token);
            for (int i = 0; i < areas.Length; i++)
            {
                if (await _visuals.ColorCountAsync(areas[i], new(99, 235, 255), 30, 50, token)) current = i + 1;
                else if (!await _visuals.ColorCountAsync(areas[i], new(255, 255, 255), 30, 100, token)) break;
            }
            if (current == 3) break;
            if (limit.Reached()) throw new TimeoutException("Fleet sidebar did not expose auto-search settings");
            if (interval.Reached()) { await ui.ClickAreaAsync(sidebar[2], token); interval.Reset(); }
            await CaptureAsync(token);
        }
        for (int attempt = 0; attempt < 6; attempt++)
        {
            var active = new List<AssetRule>();
            foreach (var setting in Settings)
                if (await _visuals.ColorCountAsync(ui.ButtonArea(setting), new(156, 255, 82), 30, 20, token)) active.Add(setting);
            if (active.Contains(target)) return;
            // Native never clicks an option when no active setting is visible.
            if (active.Count > 0) await ui.ClickAsync(target, token);
            if (attempt == 5) break;
            await ui.DelayAsync(TimeSpan.FromMilliseconds(400), token);
            await CaptureAsync(token);
        }
        throw new TimeoutException("Auto-search setting was not observed: " + target.Id);
    }

    private async ValueTask<Rectangle[]> SidebarAsync(CancellationToken token)
    {
        int offset = await ui.AppearsAsync(UiAssets.Map.FLEET_PREPARATION_CHECK, ButtonOffset.Expand(20, 80), token: token)
            ? ui.ButtonArea(UiAssets.Map.FLEET_PREPARATION_CHECK).Top - UiAssets.Map.FLEET_PREPARATION_CHECK.For(ui.Server).ClickArea!.Value.Top : 0;
        // Direct migration of AutoSearchHandler._fleet_sidebar's server variants.
        var origin = ui.Server == GameServer.En ? new Rectangle(1178, 171, 1276, 213) : new Rectangle(1185, 155, 1238, 259);
        int step = ui.Server == GameServer.En ? 53 : 111;
        return Enumerable.Range(0, 3).Select(i => origin.Offset(0, offset + i * step)).ToArray();
    }

    private async ValueTask CaptureAsync(CancellationToken token)
    {
        long before = currentFrame().Sequence;
        await ui.ScreenshotAsync(token);
        if (currentFrame().Sequence <= before) throw new InvalidDataException("Fleet settings reused a stale screenshot");
    }
}
