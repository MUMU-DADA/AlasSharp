using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed class CampaignDockFullException : Exception
{
    public CampaignDockFullException() : base("Dock is full; fleet preparation cannot continue") { }
}

/// <summary>Advances a verified stage selection to fleet preparation without starting a sortie.</summary>
public sealed class CampaignPreparation(IUiDriver ui, ICampaignInterruptions? interruptions = null)
{
    public static readonly SourceFile Source = MapUiRecovery.PreparationSource;
    private static ButtonOffset MapOffset => ButtonOffset.Expand(20, 20);
    private static ButtonOffset FleetOffset => ButtonOffset.Expand(20, 50);

    public async ValueTask OpenFleetAsync(string preparation, CancellationToken token = default)
    {
        AssetRule button = preparation switch
        {
            "normal" => UiAssets.Map.MAP_PREPARATION,
            "hard" => UiAssets.Map.MAP_PREPARATION_HARD,
            _ => throw new ArgumentException("Unknown map preparation mode", nameof(preparation))
        };
        for (int click = 0; click < 6; click++)
        {
            await ui.ScreenshotAsync(token);
            if (!await ui.AppearsAsync(button, MapOffset, token: token))
                throw new InvalidDataException("Verified map preparation is no longer visible");
            await ui.ClickAsync(button, token);
            bool interruptionHandled = false;
            for (int frame = 0; frame < 20; frame++)
            {
                await ui.DelayAsync(TimeSpan.FromMilliseconds(250), token);
                await ui.ScreenshotAsync(token);
                if (await ui.AppearsAsync(UiAssets.Map.FLEET_PREPARATION, FleetOffset, token: token)) return;
                if (interruptions is not null)
                {
                    if (await interruptions.RetirementAsync(token)) { interruptionHandled = true; continue; }
                    if (await interruptions.LowEmotionAsync(token)) { interruptionHandled = true; continue; }
                    if (interruptionHandled && await ui.AppearsAsync(button, MapOffset, token: token)) break;
                }
                else if (await ui.AppearsAsync(UiAssets.Retire.RETIRE_APPEAR_1, token: token) &&
                    await ui.AppearsAsync(UiAssets.Retire.RETIRE_APPEAR_3, token: token))
                    throw new CampaignDockFullException();
                if (await ui.AppearsAsync(UiAssets.Handler.IN_MAP, token: token))
                    throw new InvalidDataException("Map was entered without fleet preparation");
            }
        }
        throw new TimeoutException("Map preparation did not reach fleet preparation");
    }
}
