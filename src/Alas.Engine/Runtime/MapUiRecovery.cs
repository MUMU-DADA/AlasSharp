using Alas.Engine.Devices;
using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public interface IMapUiObservations
{
    ValueTask<int> InfoBarCountAsync(CancellationToken token);
    ValueTask<bool> HasStageEntranceAsync(CancellationToken token);
}

public interface IMapViewGuard
{
    ValueTask<bool> CanDetectAsync(CancellationToken token);
    /// <summary>True asks for another screenshot; false leaves a geometry error for retry/correction.</summary>
    ValueTask<bool> RecoverAsync(MapGeometryException error, CancellationToken token);
}

/// <summary>Camera._update_view interruption priority and its common map/UI helpers, all in C#.</summary>
public sealed class MapUiRecovery(IUiDriver ui, IMapUiObservations observations, IApplicationHealth application,
    IStoryHandler story, IPopupHandler popups, bool operationSiren = false,
    Func<CancellationToken, ValueTask<bool>>? isInMap = null,
    Func<CancellationToken, ValueTask>? osRewardExit = null, Func<CancellationToken, ValueTask>? osMissionExit = null) : IMapViewGuard
{
    public static readonly SourceFile Source = MapScanner.Source;
    public static readonly SourceFile StageSource = new("module/handler/enemy_searching.py", "e438427c742a5ae15dbe2504e3452189d816cfb354d4da8bd920678644172540");
    public static readonly SourceFile StrategySource = new("module/handler/strategy.py", "0541ce24c093dfd0e74cd2ebb53ec4f007b1b8e0628dfb086f4d4a935733564a");
    public static readonly SourceFile PreparationSource = new("module/map/map_operation.py", "58c3368ef5b18d3c2307915893735d489f5f38ba7c88ef0bdffc7b154b67bff0");
    public static readonly SourceFile AutoSearchSource = new("module/handler/auto_search.py", "f61796a116840175a539a9154252afa3587da9824791604fe574540f27e94d56");
    private static ButtonOffset Wide => ButtonOffset.Expand(20, 20);
    private static ButtonOffset AutoMenu => ButtonOffset.Expand(250, 30);
    private ValueTask<bool> Appear(AssetRule asset, ButtonOffset offset = default, double interval = 0,
        CancellationToken token = default, TemplatePreprocessing preprocessing = TemplatePreprocessing.Color)
        => ui.AppearsAsync(asset, offset, interval, preprocessing: preprocessing, token: token);
    public ValueTask<bool> IsInMapAsync(CancellationToken token)
        => isInMap?.Invoke(token) ?? Appear(UiAssets.Handler.IN_MAP, token: token);
    public async ValueTask<bool> CanDetectAsync(CancellationToken token)
        => await IsInMapAsync(token) || await Appear(UiAssets.Handler.SUBMARINE_MOVE_CONFIRM, Wide, token: token) ||
            await Appear(UiAssets.Handler.MOB_MOVE_CANCEL, Wide, token: token) || await Appear(UiAssets.Handler.AIR_STRIKE_CONFIRM, Wide, token: token);
    public async ValueTask<bool> IsInStageAsync(CancellationToken token)
    {
        foreach (var asset in new[] { UiAssets.Ui.CAMPAIGN_CHECK, UiAssets.Ui.EVENT_CHECK, UiAssets.Ui.SP_CHECK })
            if (await Appear(asset, Wide, token: token)) return await observations.HasStageEntranceAsync(token);
        return false;
    }
    public async ValueTask<bool> RecoverAsync(MapGeometryException error, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (await observations.InfoBarCountAsync(token) > 0)
        {
            if (await observations.InfoBarCountAsync(token) > 0)
                do { await ui.ScreenshotAsync(token); } while (await observations.InfoBarCountAsync(token) > 0);
            return true;
        }
        foreach (var (asset, offset) in new[] {
            (UiAssets.Combat.GET_ITEMS_1, ButtonOffset.Vertical(5)),
            (UiAssets.Combat.GET_ITEMS_1_RYZA, ButtonOffset.Bounds(-20, -100, 20, 20)),
            (UiAssets.OsHandler.GET_ADAPTABILITY, Wide) })
            if (await Appear(asset, offset, token: token)) { await ui.ClickAsync(asset, token); return true; }
        if (await story.StorySkipAsync(token)) { await story.EnsureNoStoryAsync(false, token); return true; }
        if (await Appear(UiAssets.Handler.GET_MISSION, Wide, token: token))
        { await ui.ClickAsync(UiAssets.Handler.GET_MISSION, token); return true; }
        if (await IsInStageAsync(token)) throw new CampaignEndedException("Image is in stage");
        if (await Appear(UiAssets.Map.MAP_PREPARATION, Wide, token: token) || await Appear(UiAssets.Map.MAP_PREPARATION_HARD, Wide, token: token))
        { await CancelPreparationAsync(token); throw new CampaignEndedException("Image is in MAP_PREPARATION"); }
        if (await Appear(UiAssets.Handler.AUTO_SEARCH_MENU_CONTINUE, AutoMenu, token: token))
        { await ExitAutoSearchAsync(token); throw new CampaignEndedException("Image is in auto search menu"); }
        if (await Appear(UiAssets.Os.GLOBE_GOTO_MAP, Wide, token: token))
        { await ClickToMapAsync(UiAssets.Os.GLOBE_GOTO_MAP, Wide, 3, token); return true; }
        if (await Appear(UiAssets.OsHandler.AUTO_SEARCH_REWARD, ButtonOffset.Expand(50, 50), token: token))
        {
            if (osRewardExit is not null) await osRewardExit(token);
            else await ClickToMapAsync(UiAssets.OsHandler.AUTO_SEARCH_REWARD, ButtonOffset.Expand(50, 50), 3, token);
            return true;
        }
        if (await Appear(UiAssets.OsHandler.MISSION_CHECK, Wide, token: token))
        {
            if (osMissionExit is not null) await osMissionExit(token);
            else await ClickToMapAsync(UiAssets.OsHandler.MISSION_CHECK, ButtonOffset.Expand(200, 5), 10, token);
            return true;
        }
        if (operationSiren && await popups.ConfirmAsync(token)) return true;
        if (await Appear(UiAssets.OsShop.PORT_SUPPLY_CHECK, Wide, token: token))
        { await ui.ClickAsync(UiAssets.Ui.BACK_ARROW, token); return true; }
        if (await Appear(UiAssets.Handler.GAME_TIPS, Wide, token: token))
        { await ui.ClickAsync(UiAssets.Handler.GAME_TIPS, token); return true; }
        if (!await application.IsRunningAsync(token)) throw new GameNotRunningException("Trying to update camera but game died");
        return false;
    }
    public async ValueTask CancelPreparationAsync(CancellationToken token)
    {
        bool skip = true;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!skip) await ui.ScreenshotAsync(token);
            skip = false;
            if (await IsInStageAsync(token)) return;
            if (await Appear(UiAssets.Map.MAP_PREPARATION, Wide, 2, token) || await Appear(UiAssets.Map.MAP_PREPARATION_HARD, Wide, 2, token))
            { await ui.ClickAsync(UiAssets.Map.MAP_PREPARATION_CANCEL, token); continue; }
            if (await Appear(UiAssets.Map.FLEET_PREPARATION, ButtonOffset.Expand(20, 50), 2, token))
            { await ui.ClickAsync(UiAssets.Map.MAP_PREPARATION_CANCEL, token); continue; }
        }
    }
    public async ValueTask<bool> ExitAutoSearchAsync(CancellationToken token)
    {
        if (!await Appear(UiAssets.Handler.AUTO_SEARCH_MENU_CONTINUE, AutoMenu, token: token, preprocessing: TemplatePreprocessing.Luma)) return false;
        bool skip = true;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!skip) await ui.ScreenshotAsync(token);
            skip = false;
            if (await Appear(UiAssets.Handler.AUTO_SEARCH_MENU_EXIT, AutoMenu, 2, token))
            { await ui.ClickAsync(UiAssets.Handler.AUTO_SEARCH_MENU_EXIT, token); ui.ResetInterval(UiAssets.Handler.AUTO_SEARCH_MENU_EXIT); continue; }
            if (await IsInStageAsync(token)) return true;
        }
    }
    public async ValueTask ClickToMapAsync(AssetRule button, ButtonOffset offset, double retryWait, CancellationToken token)
    {
        var click = new IntervalTimer(ui.Clock, retryWait, (int)(retryWait / .5));
        var confirm = new IntervalTimer(ui.Clock, 0); confirm.Reset();
        bool skip = true;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!skip) await ui.ScreenshotAsync(token);
            skip = false;
            if (await IsInMapAsync(token)) { if (confirm.Reached()) return; }
            else confirm.Reset();
            if (click.Reached() && await Appear(button, offset, token: token))
            { await ui.ClickAsync(button, token); click.Reset(); }
        }
    }
}
