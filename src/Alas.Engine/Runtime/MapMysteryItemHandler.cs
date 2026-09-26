using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

/// <summary>Handles the upstream item-popup branch of a mystery; other mystery rewards remain unported.</summary>
public sealed class MapMysteryItemHandler(IUiDriver ui) : IMapEncounterHandler
{
    public static readonly SourceFile Source = new("module/handler/mystery.py",
        "d7bf7e3f8e761851a21a61df567f9af3b82bdbe50fab5e06842e9b060a886e16");

    public async ValueTask<MapEncounterHandling> HandleAsync(MapEncounterKind encounter, CancellationToken token)
    {
        if (encounter != MapEncounterKind.ItemPopup)
            return new(MapEncounterContinuation.Unhandled);
        if (!await ui.AppearsAsync(UiAssets.Combat.GET_ITEMS_1, ButtonOffset.Vertical(5), token: token))
            return new(MapEncounterContinuation.Unhandled);
        // Upstream permits the MYSTERY_ITEM button when map-grid clicking is disabled or overlaps the popup.
        await ui.ClickAsync(UiAssets.Handler.MYSTERY_ITEM, token);
        await ui.DelayAsync(TimeSpan.FromSeconds(.5), token);
        await ui.ScreenshotAsync(token);
        for (int attempt = 0; attempt < 10; attempt++)
        {
            if (!await ui.AppearsAsync(UiAssets.Handler.STRATEGY_OPENED,
                    ButtonOffset.Expand(200, 200), token: token))
                return new(MapEncounterContinuation.InMap);
            await ui.ClickAsync(UiAssets.Handler.STRATEGY_OPENED, token);
            await ui.ScreenshotAsync(token);
        }
        throw new TimeoutException("Mystery strategy overlay did not close");
    }
}
