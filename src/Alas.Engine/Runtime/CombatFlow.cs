using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public enum CombatReturn { InMap, InStage }
public sealed record CombatFlowResult(CombatReturn Return, CombatRankEvidence? Rank,
    bool NewShipObserved, bool EnemySearchingObserved, int CapturedFrames);
public sealed record CombatFlowOptions(TimeSpan PreparationTimeout, TimeSpan ExecutionTimeout, TimeSpan StatusTimeout)
{
    public static CombatFlowOptions Default { get; } = new(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(6), TimeSpan.FromMinutes(2));
}

/// <summary>Independent C# automatic combat phases. The caller still owns map state and sortie adjudication.</summary>
public sealed class CombatFlow(IUiDriver ui, IStoryHandler story, IPopupHandler popups, IMapUiObservations mapUi)
{
    public static readonly SourceFile Source = MapEncounterProbe.CombatSource;
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
    private int _started;
    private int _frames;

    public async ValueTask<CombatFlowResult> RunAutoAsync(CombatFlowOptions? options = null, CancellationToken token = default)
    {
        options ??= CombatFlowOptions.Default;
        foreach (var timeout in new[] { options.PreparationTimeout, options.ExecutionTimeout, options.StatusTimeout })
            if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(options));
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("A combat flow belongs to one battle");
        var rank = new CombatRankProbe(ui);
        await PhaseAsync("preparation", options.PreparationTimeout, PrepareAsync, token);
        await PhaseAsync("execution", options.ExecutionTimeout, (time, ct) => ExecuteAsync(rank, time, ct), token);
        var (returned, newShip, searching) = await PhaseAsync("status", options.StatusTimeout,
            (time, ct) => StatusAsync(rank, time, ct), token);
        return new(returned, rank.Evidence, newShip, searching, _frames);
    }

    private async ValueTask<T> PhaseAsync<T>(string phase, TimeSpan timeout,
        Func<TimeSpan, CancellationToken, ValueTask<T>> action, CancellationToken token)
    {
        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try { return await action(timeout, linked.Token); }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException("Combat " + phase + " exceeded its time limit", error); }
    }

    private async ValueTask CaptureAsync(CancellationToken token)
    { await ui.ScreenshotAsync(token); _frames++; }

    private async ValueTask<bool> AutomationConfirmAsync(CancellationToken token)
    {
        if (!await ui.AppearsAsync(UiAssets.Combat.AUTOMATION_CONFIRM_CHECK, interval: 1, threshold: 30, token: token))
            return false;
        if (await ui.AppearsAsync(UiAssets.Combat.AUTOMATION_CONFIRM, ButtonOffset.Expand(20, 20),
                threshold: 30, token: token))
            await ui.ClickAsync(UiAssets.Combat.AUTOMATION_CONFIRM, token);
        return true;
    }

    private async ValueTask<bool> IsExecutingAsync(CancellationToken token)
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

    private async ValueTask<bool> PrepareAsync(TimeSpan timeout, CancellationToken token)
    {
        var limit = new IntervalTimer(ui.Clock, timeout.TotalSeconds);
        limit.Reset();
        bool first = ui.HasFrame;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (limit.Reached()) throw new TimeoutException("Combat preparation did not reach a running battle");
            if (!first) await CaptureAsync(token);
            first = false;
            if (await ui.AppearsAsync(UiAssets.Combat.BATTLE_PREPARATION, ButtonOffset.Expand(20, 20), token: token))
            {
                if (await ui.AppearsAsync(UiAssets.Combat.AUTOMATION_OFF, token: token))
                {
                    await ui.ClickAsync(UiAssets.Combat.AUTOMATION_SWITCH, token);
                    await ui.DelayAsync(TimeSpan.FromSeconds(1), token);
                    continue;
                }
                if (await AutomationConfirmAsync(token)) continue;
            }
            if (await story.StorySkipAsync(token)) continue;
            if (await ui.AppearsAsync(UiAssets.Combat.BATTLE_PREPARATION, ButtonOffset.Expand(20, 20),
                    interval: 2, threshold: 30, token: token))
            { await ui.ClickAsync(UiAssets.Combat.BATTLE_PREPARATION, token); continue; }
            if (await AutomationConfirmAsync(token)) continue;
            if (await IsExecutingAsync(token)) return true;
        }
    }

    private async ValueTask<bool> ExecuteAsync(CombatRankProbe rank, TimeSpan timeout, CancellationToken token)
    {
        var limit = new IntervalTimer(ui.Clock, timeout.TotalSeconds);
        var autoSkip = new IntervalTimer(ui.Clock, 1);
        var autoClick = new IntervalTimer(ui.Clock, 1);
        var autoCheck = new IntervalTimer(ui.Clock, 5);
        limit.Reset(); autoSkip.Reset(); autoClick.Reset(); autoCheck.Reset();
        bool first = true;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (limit.Reached()) throw new TimeoutException("Combat execution did not reach a result screen");
            if (!first) await CaptureAsync(token);
            first = false;
            if (await AutomationConfirmAsync(token) || await story.StorySkipAsync(token)) continue;
            if (autoSkip.Reached() && autoClick.Reached() && !autoCheck.Reached() &&
                (await ui.AppearsAsync(UiAssets.Combat.COMBAT_AUTO, ButtonOffset.Expand(20, 20), token: token) ||
                 await ui.AppearsAsync(UiAssets.Combat.COMBAT_AUTO_133, ButtonOffset.Expand(20, 20), token: token) ||
                 await ui.AppearsAsync(UiAssets.Combat.COMBAT_AUTO_150, ButtonOffset.Expand(20, 20), token: token)))
            { await ui.ClickAsync(UiAssets.Combat.COMBAT_AUTO_SWITCH, token); autoClick.Reset(); continue; }
            if (await popups.ConfirmAsync(token)) continue;
            if (await IsExecutingAsync(token)) continue;
            if (await rank.ObserveBattleStatusAsync(token) is { } result)
            { await ui.ClickAsync(CombatRankProbe.AssetFor(result), token); return true; }
            if (await ClickItemsAsync(token)) return true;
        }
    }

    private async ValueTask<bool> ClickItemsAsync(CancellationToken token)
    {
        foreach (var asset in new[] { UiAssets.Combat.GET_ITEMS_1, UiAssets.Combat.GET_ITEMS_2, UiAssets.Combat.GET_ITEMS_3 })
            if (await ui.AppearsAsync(asset, ButtonOffset.Vertical(5), token: token))
            { await ui.ClickAsync(UiAssets.Combat.GET_ITEMS_1, token); return true; }
        return false;
    }

    private async ValueTask<(CombatReturn Return, bool NewShip, bool Searching)> StatusAsync(
        CombatRankProbe rank, TimeSpan timeout, CancellationToken token)
    {
        var limit = new IntervalTimer(ui.Clock, timeout.TotalSeconds);
        var stage = new IntervalTimer(ui.Clock, .5, count: 2);
        var map = new IntervalTimer(ui.Clock, 5);
        limit.Reset(); stage.Clear(); map.Clear();
        bool newShip = false, searching = false;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (limit.Reached()) throw new TimeoutException("Combat status did not return to the map or stage page");
            await CaptureAsync(token);
            bool stagePage = false;
            foreach (var asset in new[] { UiAssets.Ui.CAMPAIGN_CHECK, UiAssets.Ui.EVENT_CHECK, UiAssets.Ui.SP_CHECK })
                if (await ui.AppearsAsync(asset, ButtonOffset.Expand(20, 20), token: token))
                { stagePage = await mapUi.HasStageEntranceAsync(token); break; }
            if (stagePage)
            {
                if (!stage.Started) stage.Reset();
                else if (stage.Reached()) return (CombatReturn.InStage, newShip, searching);
            }
            else stage.Clear();

            bool inMap = await ui.AppearsAsync(UiAssets.Handler.IN_MAP, token: token);
            if (inMap)
            {
                bool overlay = await ui.AppearsAsync(UiAssets.Handler.MAP_ENEMY_SEARCHING,
                    ButtonOffset.Expand(5, 5), preprocessing: TemplatePreprocessing.Luma, token: token);
                if (overlay) { searching = true; map.Clear(); }
                else if (!map.Started) map.Reset();
                else if (map.Reached()) return (CombatReturn.InMap, newShip, searching);
            }
            else map.Clear();
            if (await story.StorySkipAsync(token)) continue;
            if (await ui.AppearsAsync(UiAssets.Combat.GET_SHIP, interval: 1, token: token))
            {
                newShip |= await ui.AppearsAsync(UiAssets.Combat.NEW_SHIP, token: token);
                await ui.ClickAsync(UiAssets.Combat.GET_SHIP, token); continue;
            }
            if (await ClickItemsAsync(token)) continue;
            if (await popups.ConfirmAsync(token)) continue;
            if (!await IsExecutingAsync(token))
            {
                if (await rank.ObserveBattleStatusAsync(token) is { } battle)
                { await ui.ClickAsync(CombatRankProbe.AssetFor(battle), token); continue; }
                if (await rank.ObserveExperienceAsync(token) is { } experience)
                { await ui.ClickAsync(CombatRankProbe.AssetFor(experience), token); continue; }
            }
        }
    }
}
