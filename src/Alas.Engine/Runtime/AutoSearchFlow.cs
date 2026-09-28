using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public interface IAutoSearchHandlers
{
    ValueTask WatchAsync(bool firstObservation, CancellationToken token);
    ValueTask<bool> LoadingAsync(CancellationToken token);
    ValueTask<bool> InStageAsync(CancellationToken token);
    ValueTask<bool> RetirementAsync(CancellationToken token);
    ValueTask<bool> LowEmotionAsync(CancellationToken token);
    ValueTask<bool> StoryAsync(CancellationToken token);
    ValueTask<bool> CatAttackAsync(CancellationToken token);
    ValueTask<bool> ConfirmAsync(CancellationToken token);
    ValueTask<bool> UrgentCommissionAsync(CancellationToken token);
    ValueTask<bool> GuildPopupAsync(CancellationToken token);
    ValueTask<bool> MissionPopupAsync(CancellationToken token);
}

public sealed record AutoSearchBattleEvidence(int Fleet, long StartedFrame, long LastFrame,
    CombatRankEvidence? Rank, bool NewShip, bool ReturnedToSearch, string Phase);
public sealed record AutoSearchEvidence(string Phase, bool StatusConfirmation, string? EndReason,
    long? EndFrame, long? StageFrame, IReadOnlyList<AutoSearchBattleEvidence> Battles);
public sealed record AutoSearchArtifact(AutoSearchEvidence Flow, IReadOnlyList<AutoSearchResourceReading> Resources);

/// <summary>AutoSearchCombat's movement, loading, execution and status loops.
/// An automatic result menu terminates the loop; it does not prove a cleared sortie.</summary>
public sealed class AutoSearchFlow(IUiDriver ui, IAutoSearchHandlers handlers, Func<long> frameSequence)
{
    public static readonly SourceFile Source = new("module/combat/auto_search_combat.py",
        "97d3928ac000da3b4b00a6eb476dbaf6e438ccd08a148f4ae0f28bbec6174877");
    private readonly IntervalTimer _stage = new(ui.Clock, 3, 6);
    private readonly IntervalTimer _autoClick = new(ui.Clock, 1);
    private readonly List<AutoSearchBattleEvidence> _battles = [];
    private string _phase = "not_started";
    private bool _statusConfirmation;
    private string? _endReason;
    private long? _endFrame, _stageFrame;
    private CombatRankProbe? _rank;
    private bool _newShip;
    public AutoSearchEvidence Evidence => new(_phase, _statusConfirmation, _endReason, _endFrame, _stageFrame, _battles.ToArray());

    public async ValueTask MoveAsync(TimeSpan timeout, CancellationToken token = default)
    {
        if (_phase is not ("not_started" or "moving_ready"))
            throw new InvalidOperationException("Auto-search movement is out of sequence");
        ui.ResetProgress();
        bool watched = false, offensive = false;
        await LoopAsync("moving", timeout, async ct =>
        {
            if (offensive)
            {
                if (await EnableMapOptionAsync(ct)) { ui.ResetInterval(UiAssets.Handler.AUTO_SEARCH_MAP_OPTION_ON); return false; }
                if (await ui.AppearsAsync(UiAssets.Handler.AUTO_SEARCH_MAP_OPTION_ON, ButtonOffset.Expand(5, 5), interval: 3, token: ct) &&
                    await ClickIfAsync(UiAssets.Handler.AUTO_SEARCH_MAP_OPTION_ON, default, 0, ct)) return false;
                if (await handlers.LowEmotionAsync(ct) || await handlers.RetirementAsync(ct)) return false;
                return await handlers.LoadingAsync(ct);
            }
            if (await RunningAsync(ct)) { await handlers.WatchAsync(!watched, ct); watched = true; }
            if (await handlers.RetirementAsync(ct))
            {
                // Native map_offensive_auto_search repairs the client stall after retirement.
                offensive = true; ui.ResetInterval(UiAssets.Handler.AUTO_SEARCH_MAP_OPTION_ON); return false;
            }
            if (await EnableMapOptionAsync(ct)) return false;
            if (await handlers.LowEmotionAsync(ct)) { _statusConfirmation = true; return false; }
            if (await handlers.StoryAsync(ct) || await handlers.CatAttackAsync(ct)) return false;
            // Native handle_vote_popup is a no-op since 2023.
            if (await handlers.LoadingAsync(ct) || await CombatAppearance.IsExecutingAsync(ui, ct)) return true;
            await ThrowIfEndedAsync(ct);
            return false;
        }, token);
        _phase = "combat_ready";
    }

    public async ValueTask CombatAsync(int displayedFleet, CombatSubmarineCall submarine, ICombatEmotion? emotion,
        CombatFlowOptions? options = null, CancellationToken token = default)
    {
        if (displayedFleet is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(displayedFleet));
        if (_phase != "combat_ready") throw new InvalidOperationException("Observe auto-search combat before executing it");
        options ??= CombatFlowOptions.Default;
        _rank = new(ui); _newShip = false;
        _battles.Add(new(displayedFleet, frameSequence(), frameSequence(), null, false, false, "loading"));
        bool returned = false;
        try
        {
            ui.ResetProgress();
            await LoopAsync("loading", options.PreparationTimeout, async ct =>
            {
                if (await CombatAppearance.ConfirmAutomationAsync(ui, ct) || await handlers.StoryAsync(ct)) return false;
                await ThrowIfEndedAsync(ct);
                return await CombatAppearance.IsExecutingAsync(ui, ct);
            }, token);
            submarine.Begin();
            var autoSkip = new IntervalTimer(ui.Clock, 1); autoSkip.Reset();
            var autoWindow = new IntervalTimer(ui.Clock, 5); autoWindow.Reset();
            bool autoChecked = false;
            ui.ResetProgress();
            if (emotion is not null) await emotion.ReduceAsync(CancellationToken.None);
            await LoopAsync("executing", options.ExecutionTimeout, async ct =>
            {
                if (await submarine.HandleAsync(ct)) return false;
                // The public Engine task currently supports combat_auto only. No manual mode is silently substituted.
                if (!autoChecked)
                {
                    autoChecked = autoWindow.Reached();
                    if (!autoChecked && autoSkip.Reached() && _autoClick.Reached() &&
                        (await ui.AppearsAsync(UiAssets.Combat.COMBAT_AUTO, ButtonOffset.Expand(20, 20), token: ct) ||
                         await ui.AppearsAsync(UiAssets.Combat.COMBAT_AUTO_133, ButtonOffset.Expand(20, 20), token: ct) ||
                         await ui.AppearsAsync(UiAssets.Combat.COMBAT_AUTO_150, ButtonOffset.Expand(20, 20), token: ct)))
                    { await ui.ClickAsync(UiAssets.Combat.COMBAT_AUTO_SWITCH, ct); _autoClick.Reset(); return false; }
                }
                if (await PopupsAsync(ct)) return false;
                await ThrowIfEndedAsync(ct);
                if (await CombatAppearance.IsExecutingAsync(ui, ct)) return false;
                if (await GetShipAsync(ct)) return false;
                var rank = await ObserveRankAsync(ct);
                return rank is { IsWinningRank: true } || await RunningAsync(ct);
            }, token);
            submarine.End();
            ui.ResetProgress();
            bool expInfo = false;
            await LoopAsync("status", options.StatusTimeout, async ct =>
            {
                if (await RunningAsync(ct)) { _statusConfirmation = false; return true; }
                await ThrowIfEndedAsync(ct);
                if (await GetShipAsync(ct)) return false;
                if (await EnableMapOptionAsync(ct)) { _statusConfirmation = false; return false; }
                if (await PopupsAsync(ct)) return false;
                // Observe ranks even when the game advances them itself. Observation never authorizes clicking.
                if (_statusConfirmation)
                {
                    if (!expInfo && await GetShipAsync(ct)) return false;
                    if (await ItemsAsync(ct)) return false;
                    if (!await CombatAppearance.IsExecutingAsync(ui, ct) && await _rank.ObserveBattleStatusAsync(ct) is { } battle)
                    {
                        var button = CombatRankProbe.AssetFor(battle);
                        await ui.DelayAsync(TimeSpan.FromMilliseconds(375), ct);
                        await ui.ClickAsync(button, ct); return false;
                    }
                    if (await handlers.ConfirmAsync(ct)) return false;
                    if (!await CombatAppearance.IsExecutingAsync(ui, ct) && await ExperienceAsync(ct))
                    { expInfo = true; return false; }
                }
                else _ = await ObserveRankAsync(ct);
                return false;
            }, token);
            returned = true; _phase = "moving_ready";
        }
        catch (CampaignEndedException) { submarine.End(); throw; }
        catch { submarine.Fail(); throw; }
        finally
        {
            _battles[^1] = _battles[^1] with { LastFrame = frameSequence(), Rank = _rank.Evidence,
                NewShip = _newShip, ReturnedToSearch = returned, Phase = _phase };
        }
    }

    private async ValueTask<CombatRankEvidence?> ObserveRankAsync(CancellationToken token)
        => await _rank!.ObserveBattleStatusAsync(token) ?? await _rank.ObserveExperienceAsync(token);

    private async ValueTask<bool> ExperienceAsync(CancellationToken token)
    {
        foreach (var asset in new[] { UiAssets.Combat.EXP_INFO_S, UiAssets.Combat.EXP_INFO_A, UiAssets.Combat.EXP_INFO_B })
        {
            if (!await ui.AppearsAsync(asset, threshold: 30, token: token)) continue;
            // Native actions use appear_then_click's threshold 30. Rank evidence keeps its stricter observation rule.
            _ = await _rank!.ObserveExperienceAsync(token);
            await ui.ClickAsync(asset, token);
            await ui.DelayAsync(TimeSpan.FromMilliseconds(375), token);
            return true;
        }
        return false;
    }

    private async ValueTask<bool> PopupsAsync(CancellationToken token)
        => await handlers.ConfirmAsync(token) || await handlers.UrgentCommissionAsync(token) ||
           await handlers.StoryAsync(token) || await handlers.GuildPopupAsync(token) || await handlers.MissionPopupAsync(token);

    private async ValueTask<bool> GetShipAsync(CancellationToken token)
    {
        if (!await ClickIfAsync(UiAssets.Combat.GET_SHIP, default, 1, token)) return false;
        _newShip |= await ui.AppearsAsync(UiAssets.Combat.NEW_SHIP, token: token);
        return true;
    }

    private async ValueTask<bool> ItemsAsync(CancellationToken token)
    {
        foreach (var asset in new[] { UiAssets.Combat.GET_ITEMS_1, UiAssets.Combat.GET_ITEMS_2, UiAssets.Combat.GET_ITEMS_3 })
            if (await ui.AppearsAsync(asset, ButtonOffset.Vertical(5), token: token))
            {
                await ui.ClickAsync(UiAssets.Combat.GET_ITEMS_1, token);
                foreach (var rank in new[] { UiAssets.Combat.BATTLE_STATUS_S, UiAssets.Combat.BATTLE_STATUS_A, UiAssets.Combat.BATTLE_STATUS_B })
                    ui.ResetInterval(rank);
                return true;
            }
        return false;
    }

    public async ValueTask<bool> RunningAsync(CancellationToken token)
        => await ui.AppearsAsync(UiAssets.Handler.AUTO_SEARCH_MAP_OPTION_ON, ButtonOffset.Expand(5, 5), token: token) &&
           await ui.AppearsAsync(UiAssets.Handler.AUTO_SEARCH_MAP_OPTION_ON, token: token);

    private async ValueTask<bool> EnableMapOptionAsync(CancellationToken token)
        => await ui.AppearsAsync(UiAssets.Handler.AUTO_SEARCH_MAP_OPTION_OFF, ButtonOffset.Expand(5, 5), token: token) &&
           await ClickIfAsync(UiAssets.Handler.AUTO_SEARCH_MAP_OPTION_OFF, default, 2, token);

    private async ValueTask<bool> ClickIfAsync(AssetRule asset, ButtonOffset offset, double interval, CancellationToken token)
    {
        if (!await ui.AppearsAsync(asset, offset, interval, threshold: 30, token: token)) return false;
        await ui.ClickAsync(asset, token); return true;
    }

    private async ValueTask ThrowIfEndedAsync(CancellationToken token)
    {
        bool menu = await ui.AppearsAsync(UiAssets.Handler.AUTO_SEARCH_MENU_CONTINUE, ButtonOffset.Expand(250, 30),
            preprocessing: TemplatePreprocessing.Luma, token: token);
        bool stage = false;
        if (!menu)
        {
            if (await handlers.InStageAsync(token))
            {
                if (!_stage.Started) _stage.Reset();
                else stage = _stage.Reached();
            }
            else _stage.Reset();
        }
        if (!menu && !stage) return;
        _endFrame = frameSequence(); _endReason = menu ? "result_menu" : "menu_missing";
        if (stage) _stageFrame = frameSequence();
        _phase = "ended";
        throw new CampaignEndedException("Auto-search ended: " + _endReason);
    }

    public async ValueTask ExitMenuAsync(TimeSpan timeout, CancellationToken token)
    {
        if (_endReason != "result_menu") return;
        // Native ensure_auto_search_exit; never continue into another sortie implicitly.
        await LoopAsync("exiting", timeout, async ct =>
        {
            if (await ui.AppearsAsync(UiAssets.Handler.AUTO_SEARCH_MENU_EXIT, ButtonOffset.Expand(250, 30), interval: 2, token: ct))
            { await ui.ClickAsync(UiAssets.Handler.AUTO_SEARCH_MENU_EXIT, ct); ui.ResetInterval(UiAssets.Handler.AUTO_SEARCH_MENU_EXIT); return false; }
            if (frameSequence() <= _endFrame || !await handlers.InStageAsync(ct)) return false;
            _stageFrame = frameSequence(); return true;
        }, token);
        _phase = "ended";
    }

    private async ValueTask LoopAsync(string phase, TimeSpan timeout, Func<CancellationToken, ValueTask<bool>> step, CancellationToken token)
    {
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(timeout));
        _phase = phase;
        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        var limit = new IntervalTimer(ui.Clock, timeout.TotalSeconds); limit.Reset();
        long sequence = frameSequence();
        if (!ui.HasFrame || sequence <= 0) throw new InvalidDataException("Auto search requires a current numbered frame");
        bool first = true;
        try
        {
            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (limit.Reached()) throw new TimeoutException("Auto-search " + phase + " exceeded its time limit");
                if (!first)
                {
                    await ui.ScreenshotAsync(linked.Token);
                    if (frameSequence() <= sequence) throw new InvalidDataException("Auto search reused a stale screenshot");
                }
                first = false; sequence = frameSequence();
                if (await step(linked.Token)) return;
            }
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException("Auto-search " + phase + " exceeded its time limit", error); }
    }
}
