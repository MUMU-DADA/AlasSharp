using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public interface ICampaignInterruptions
{
    void Configure(RetirementOptions retirement, CampaignEmotionMode emotion);
    ValueTask<bool> RetirementAsync(CancellationToken token);
    ValueTask<bool> LowEmotionAsync(CancellationToken token);
}

/// <summary>Shared entry/combat interruption chain, with C# rules and no upstream business process.</summary>
public sealed class CampaignInterruptions(IUiDriver ui, UiVisuals visuals, RetirementHandler retire) : ICampaignInterruptions
{
    public static readonly SourceFile Source = UiRecovery.InfoSource;
    private RetirementOptions _retirement = new() { Mode = RetirementMode.Disabled };
    private CampaignEmotionMode _emotion;
    public void Configure(RetirementOptions retirement, CampaignEmotionMode emotion)
    { retirement.Validate(); _retirement = retirement; _emotion = emotion; }
    public async ValueTask<bool> RetirementAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        foreach (var asset in new[] { UiAssets.Handler.GAME_TIPS, UiAssets.Handler.GAME_TIPS3, UiAssets.Handler.GAME_TIPS4 })
            if (await ui.AppearsAsync(asset, ButtonOffset.Expand(20, 20), interval: 2, token: token) &&
                await visuals.ColorCountAsync(ui.ButtonArea(asset), new(40, 40, 40), 15, 50, token))
            { await ui.ClickAsync(UiAssets.Handler.GAME_TIPS, token); return true; }
        return await retire.HandleAsync(_retirement, token);
    }
    public async ValueTask<bool> LowEmotionAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_emotion.Ignores()) return false;
        bool handled = await RetirementUi.ConfirmAsync(ui, ButtonOffset.Expand(3, 30), token);
        if (handled) ui.ResetInterval(UiAssets.Handler.AUTO_SEARCH_MAP_OPTION_OFF);
        return handled;
    }
}
