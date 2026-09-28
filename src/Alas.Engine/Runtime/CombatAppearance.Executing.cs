using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed partial class CombatAppearance
{
    private static readonly (AssetRule Asset, TemplatePreprocessing Processing)[] PauseVariants =
    [
        (UiAssets.CombatUi.PAUSE_New, TemplatePreprocessing.Color),
        (UiAssets.CombatUi.PAUSE_Iridescent_Fantasy, TemplatePreprocessing.Luma),
        (UiAssets.CombatUi.PAUSE_Christmas, TemplatePreprocessing.Luma),
        (UiAssets.CombatUi.PAUSE_Neon, TemplatePreprocessing.Color),
        (UiAssets.CombatUi.PAUSE_Cyber, TemplatePreprocessing.Color),
        (UiAssets.CombatUi.PAUSE_HolyLight, TemplatePreprocessing.Color),
        (UiAssets.CombatUi.PAUSE_Pharaoh, TemplatePreprocessing.Luma),
        (UiAssets.CombatUi.PAUSE_Star, TemplatePreprocessing.Luma),
        (UiAssets.CombatUi.PAUSE_Nurse, TemplatePreprocessing.Luma),
        (UiAssets.CombatUi.PAUSE_Devil, TemplatePreprocessing.Color),
        (UiAssets.CombatUi.PAUSE_Seaside, TemplatePreprocessing.Color),
        (UiAssets.CombatUi.PAUSE_Ninja, TemplatePreprocessing.Color),
        (UiAssets.CombatUi.PAUSE_ShadowPuppetry, TemplatePreprocessing.Luma),
        (UiAssets.CombatUi.PAUSE_MaidCafe, TemplatePreprocessing.Luma),
        (UiAssets.CombatUi.PAUSE_Ancient, TemplatePreprocessing.Luma),
        (UiAssets.CombatUi.PAUSE_SpringInn, TemplatePreprocessing.Luma),
        (UiAssets.CombatUi.PAUSE_ElvenVine, TemplatePreprocessing.Luma),
        (UiAssets.CombatUi.PAUSE_GildedReverie, TemplatePreprocessing.Luma),
        (UiAssets.CombatUi.PAUSE_AzureCore, TemplatePreprocessing.Luma),
        (UiAssets.CombatUi.PAUSE_OldeRoyal, TemplatePreprocessing.Luma),
        (UiAssets.CombatUi.PAUSE_YoRHa, TemplatePreprocessing.Luma),
        (UiAssets.CombatUi.PAUSE_Ritual, TemplatePreprocessing.Luma)
    ];
    public static async ValueTask<bool> IsExecutingAsync(IUiDriver ui, CancellationToken token)
    {
        var pause = UiAssets.CombatUi.PAUSE;
        if (ui.Server is GameServer.Cn or GameServer.En)
        {
            if (await ui.AppearsAsync(pause, ButtonOffset.Expand(10, 10),
                    preprocessing: TemplatePreprocessing.Luma, token: token)) return true;
        }
        else if (await ui.AppearsAsync(pause, ButtonOffset.Expand(10, 10),
                     preprocessing: TemplatePreprocessing.Luma, token: token) &&
                 await ui.AppearsAsync(pause, token: token)) return true;
        foreach (var (asset, processing) in PauseVariants)
            if (await ui.AppearsAsync(asset, ButtonOffset.Expand(10, 10), preprocessing: processing, token: token))
                return true;
        return false;
    }

}
