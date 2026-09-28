using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

/// <summary>Native combat_appear, including the fleet-lock loading shortcut and automation overlay.</summary>
public sealed class CombatAppearance(IUiDriver ui, bool useFleetLock,
    Func<CancellationToken, ValueTask<bool>> loading)
{
    public static readonly SourceFile Source = MapEncounterProbe.CombatSource;
    public async ValueTask<bool> AppearsAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (useFleetLock && !await ui.AppearsAsync(UiAssets.Handler.IN_MAP, token: token) && await loading(token)) return true;
        if (await ui.AppearsAsync(UiAssets.Combat.BATTLE_PREPARATION, ButtonOffset.Expand(30, 20), token: token)) return true;
        return await ui.AppearsAsync(UiAssets.Combat.BATTLE_PREPARATION_WITH_OVERLAY, threshold: 30, token: token) &&
            await ConfirmAutomationAsync(ui, token);
    }

    public static async ValueTask<bool> ConfirmAutomationAsync(IUiDriver ui, CancellationToken token)
    {
        if (!await ui.AppearsAsync(UiAssets.Combat.AUTOMATION_CONFIRM_CHECK, interval: 1, threshold: 30, token: token)) return false;
        if (await ui.AppearsAsync(UiAssets.Combat.AUTOMATION_CONFIRM, ButtonOffset.Expand(20, 20), token: token))
            await ui.ClickAsync(UiAssets.Combat.AUTOMATION_CONFIRM, token);
        return true;
    }
}
