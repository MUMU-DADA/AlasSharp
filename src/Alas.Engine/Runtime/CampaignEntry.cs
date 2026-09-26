using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed record CampaignEntryObservation(int FleetClicks, long FrameSequence);

public interface ICampaignEntryService
{
    ValueTask<CampaignEntryObservation> EnterFromFleetAsync(CancellationToken token);
}

/// <summary>Advances an observed fleet preparation page into a fresh in-map observation.</summary>
public sealed class CampaignEntry(IUiDriver ui, Func<long> frameSequence)
{
    public static readonly SourceFile Source = MapUiRecovery.PreparationSource;
    private static ButtonOffset FleetOffset => ButtonOffset.Expand(20, 50);
    private static ButtonOffset MapOffset => ButtonOffset.Expand(20, 20);

    public async ValueTask<CampaignEntryObservation> EnterAsync(CancellationToken token = default)
    {
        for (int click = 0; click < 6; click++)
        {
            await ui.ScreenshotAsync(token);
            if (await ui.AppearsAsync(UiAssets.Handler.IN_MAP, token: token))
                throw new InvalidDataException("Map was entered before the fleet preparation action");
            if (!await ui.AppearsAsync(UiAssets.Map.FLEET_PREPARATION, FleetOffset, token: token))
                throw new InvalidDataException("Fleet preparation is no longer visible");
            await ui.ClickAsync(UiAssets.Map.FLEET_PREPARATION, token);
            for (int frame = 0; frame < 20; frame++)
            {
                await ui.DelayAsync(TimeSpan.FromMilliseconds(250), token);
                await ui.ScreenshotAsync(token);
                if (await ui.AppearsAsync(UiAssets.Handler.IN_MAP, token: token))
                    return new(click + 1, frameSequence());
                if (await ui.AppearsAsync(UiAssets.Map.MAP_PREPARATION, MapOffset, token: token) ||
                    await ui.AppearsAsync(UiAssets.Map.MAP_PREPARATION_HARD, MapOffset, token: token))
                    throw new InvalidDataException("Fleet preparation returned to the map preparation page");
            }
        }
        throw new TimeoutException("Fleet preparation did not enter the map");
    }
}
