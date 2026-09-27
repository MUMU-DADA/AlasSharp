using Alas.Engine.Navigation;
using Alas.Engine.Rules;
using static Alas.Engine.Rules.UiAssets.Retire;

namespace Alas.Engine.Runtime;

public interface IRetirementDock
{
    ValueTask FavouriteAsync(bool enabled, CancellationToken token);
    ValueTask DescendingAsync(bool enabled, CancellationToken token);
    ValueTask WaitCardsAsync(CancellationToken token);
    ValueTask FilterAsync(IReadOnlyList<string>? rarities, CancellationToken token);
    ValueTask QuickSettingsAsync(string? keep, CancellationToken token);
}

/// <summary>Dock/quick-retire settings from upstream Dock and QuickRetireSettingHandler.</summary>
public sealed class RetirementDock(IUiDriver ui, UiVisuals visuals) : IRetirementDock
{
    public async ValueTask FavouriteAsync(bool enabled, CancellationToken token)
        => _ = await new UiSwitch(ui, [new("on", COMMON_SHIP_FILTER_ENABLE), new("off", COMMON_SHIP_FILTER_DISABLE)],
            ButtonOffset.Color).SetAsync(enabled ? "on" : "off", TimeSpan.FromSeconds(30), token: token);
    public async ValueTask DescendingAsync(bool enabled, CancellationToken token)
        => _ = await new UiSwitch(ui, [new("Ascending", SORT_ASC, SORTING_CLICK), new("Descending", SORT_DESC, SORTING_CLICK)],
            ButtonOffset.Color).SetAsync(enabled ? "Descending" : "Ascending", TimeSpan.FromSeconds(30), token: token);
    public async ValueTask WaitCardsAsync(CancellationToken token)
    {
        var timeout = new IntervalTimer(ui.Clock, 1.2, 1); timeout.Reset();
        bool first = true;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!first) await ui.ScreenshotAsync(token);
            first = false;
            if (await ui.AppearsAsync(DOCK_EMPTY, token: token) || timeout.Reached()) return;
        }
    }
    public async ValueTask FilterAsync(IReadOnlyList<string>? rarities, CancellationToken token)
    {
        ui.ClearInterval(DOCK_CHECK);
        bool first = true;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!first) await ui.ScreenshotAsync(token);
            first = false;
            if (await ui.AppearsAsync(DOCK_FILTER_CONFIRM, ButtonOffset.Expand(20, 60), token: token)) break;
            if (await ui.AppearsAsync(DOCK_CHECK, ButtonOffset.Expand(20, 20), interval: 5, token: token))
            { await ui.ClickAsync(DOCK_FILTER, token); continue; }
            if (await RetirementUi.ClickIfAsync(ui, EQUIP_CONFIRM, ButtonOffset.Expand(30, 30), 2, token)) continue;
            if (await RetirementUi.ClickIfAsync(ui, EQUIP_CONFIRM_2, ButtonOffset.Expand(30, 30), 2, token))
            { ui.ClearInterval(UiAssets.Combat.GET_ITEMS_1); continue; }
            if (await ui.AppearsAsync(UiAssets.Combat.GET_ITEMS_1, ButtonOffset.Expand(30, 30), interval: 2, token: token))
                await ui.ClickAsync(GET_ITEMS_1_RETIREMENT_SAVE, token);
        }
        var setting = new UiSetting(ui, RetirementRules.DockSettings, async (option, ct) =>
            await visuals.ColorCountAsync(option.Area, new(181, 142, 90), 20, 250, ct) ||
            await visuals.ColorCountAsync(option.Area, new(74, 117, 189), 20, 250, ct));
        var desired = new Dictionary<string, IReadOnlyList<string>?>();
        if (rarities is not null) desired["rarity"] = rarities;
        await setting.SetAsync(desired, token);
        first = true;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!first) await ui.ScreenshotAsync(token);
            first = false;
            if (!await ui.AppearsAsync(DOCK_FILTER_CONFIRM, ButtonOffset.Expand(20, 60), token: token) &&
                await ui.AppearsAsync(DOCK_CHECK, ButtonOffset.Expand(20, 20), token: token)) break;
            await RetirementUi.ClickIfAsync(ui, DOCK_FILTER_CONFIRM, ButtonOffset.Expand(20, 60), 3, token);
        }
        await WaitCardsAsync(token);
    }
    public async ValueTask QuickSettingsAsync(string? keep, CancellationToken token)
    {
        await UiClick.UntilAsync(ui, RETIRE_SETTING_ENTER,
            ct => ui.AppearsAsync(RETIRE_SETTING_QUIT, ButtonOffset.Expand(30, 100), token: ct),
            ButtonOffset.Expand(30, 100), 3, token);
        var settings = new UiSetting(ui, RetirementRules.QuickSettings(ui.Server),
            (option, ct) => visuals.ColorCountAsync(option.Area, new(255, 255, 255), 30, 50, ct), resetFirst: false);
        await settings.SetAsync(new Dictionary<string, IReadOnlyList<string>?> { ["filter_5"] = keep is null ? null : [keep] }, token);
        await UiClick.UntilAsync(ui, RETIRE_SETTING_QUIT,
            ct => ui.AppearsAsync(RETIRE_SETTING_ENTER, ButtonOffset.Expand(30, 100), token: ct),
            ButtonOffset.Expand(30, 100), 3, token);
    }
}

internal static class RetirementUi
{
    internal static async ValueTask<bool> ClickIfAsync(IUiDriver ui, AssetRule asset, ButtonOffset offset,
        double interval, CancellationToken token)
    {
        if (!await ui.AppearsAsync(asset, offset, interval, threshold: 30, token: token)) return false;
        await ui.ClickAsync(asset, token); return true;
    }
    internal static async ValueTask<bool> ConfirmAsync(IUiDriver ui, ButtonOffset offset, CancellationToken token)
    {
        if (await ui.AppearsAsync(UiAssets.Handler.POPUP_CANCEL, offset, token: token) &&
            await ui.AppearsAsync(UiAssets.Handler.POPUP_CONFIRM, offset, interval: 2, token: token))
        { await ui.ClickAsync(UiAssets.Handler.POPUP_CONFIRM, token); return true; }
        if (!await ui.AppearsAsync(UiAssets.UiWhite.POPUP_CONFIRM_WHITE, offset, interval: 2, token: token)) return false;
        await ui.ClickAsync(UiAssets.UiWhite.POPUP_CONFIRM_WHITE, token); return true;
    }
}
