using Alas.Engine.Devices;
using Alas.Engine.Imaging;
using Alas.Engine.Rules;
using static Alas.Engine.Rules.UiAssets.Ui;
using static Alas.Engine.Rules.UiAssets.UiWhite;
using static Alas.Engine.Rules.UiAssets.Handler;
using static Alas.Engine.Rules.UiAssets.Map;
using static Alas.Engine.Rules.UiAssets.Combat;
using static Alas.Engine.Rules.UiAssets.OsHandler;
using static Alas.Engine.Rules.UiAssets.Raid;

namespace Alas.Engine.Navigation;

public sealed record UiRecoveryOptions(int ButtonOffset = 30, int StoryOption = 0, bool StoryAllowSkip = true,
    bool MapIsThreatSafe = false, string CampaignEvent = "");
public interface IPopupHandler
{
    ValueTask<bool> ConfirmAsync(CancellationToken token);
}
public interface IStoryHandler
{
    ValueTask<bool> StorySkipAsync(CancellationToken token = default);
    ValueTask EnsureNoStoryAsync(bool skipFirstScreenshot, CancellationToken token);
}

/// <summary>Direct port of UI recovery and InfoHandler story/popups. State is isolated to a session.</summary>
public sealed class UiRecovery : IUiRecovery, IPopupHandler, IStoryHandler
{
    public static readonly SourceFile UiSource = new("module/ui/ui.py", "9f99734b71680492f78348cee7ea1c9a6e0bea2801aa3a7688bc5c32372d5f1d");
    public static readonly SourceFile InfoSource = new("module/handler/info_handler.py", "799551a37c8f771f4bd9023aa3477041f6c31b99fd60f25185dd0e829b9ccbf2");
    private readonly IUiDriver _driver;
    private readonly IApplicationHealth _application;
    private readonly UiRecoveryOptions _options;
    private readonly PageGraph _graph;
    private readonly IntervalTimer _hotFix, _storyConfirm, _optionTimer, _optionConfirm;
    private IntervalTimer _storyPopup;
    private int _fleetResetClicks, _optionRecord;
    private static ButtonOffset Wide => ButtonOffset.Expand(30, 30);
    private static ButtonOffset Popup => ButtonOffset.Expand(3, 30);

    public UiRecovery(IUiDriver driver, IApplicationHealth application, PageGraph graph, UiRecoveryOptions options)
    {
        (_driver, _application, _graph, _options) = (driver, application, graph, options);
        _hotFix = new(driver.Clock, 6);
        _storyPopup = new(driver.Clock, 10, 20);
        _storyConfirm = new(driver.Clock, 0.5, 1);
        _optionTimer = new(driver.Clock, 2);
        _optionConfirm = new(driver.Clock, 0.3);
    }

    private ValueTask<bool> Appear(AssetRule asset, ButtonOffset offset, double interval, CancellationToken token,
        double similarity = 0.85, int threshold = 10, TemplatePreprocessing preprocessing = TemplatePreprocessing.Color)
        => _driver.AppearsAsync(asset, offset, interval, similarity, threshold, preprocessing, token);
    private async ValueTask<bool> ClickIf(AssetRule asset, ButtonOffset offset, double interval, CancellationToken token)
    {
        // ModuleBase.appear_then_click has threshold=30; ModuleBase.appear has threshold=10.
        if (!await Appear(asset, offset, interval, token, threshold: 30)) return false;
        await _driver.ClickAsync(asset, token);
        return true;
    }
    private async ValueTask<bool> PopupConfirm(CancellationToken token)
    {
        if (await Appear(POPUP_CANCEL, Popup, 0, token) && await Appear(POPUP_CONFIRM, Popup, 2, token))
        {
            await _driver.ClickAsync(POPUP_CONFIRM, token);
            return true;
        }
        if (!await Appear(POPUP_CONFIRM_WHITE, Popup, 2, token)) return false;
        await _driver.ClickAsync(POPUP_CONFIRM_WHITE, token);
        return true;
    }
    public ValueTask<bool> ConfirmAsync(CancellationToken token) => PopupConfirm(token);
    private async ValueTask<bool> UrgentCommission(CancellationToken token)
    {
        bool appeared = await Appear(GET_MISSION, ButtonOffset.Vertical(_options.ButtonOffset), 2, token);
        if (appeared) { await _driver.ClickAsync(GET_MISSION, token); _hotFix.Reset(); }
        if (_hotFix.Reached()) _hotFix.Clear();
        if (_hotFix.Started && _hotFix.Elapsed is >= 3 and <= 6)
        {
            await EnsureApplication(token);
            if (await Appear(LOGIN_CHECK, Wide, 0, token))
            {
                await _application.StopAsync(token);
                throw new GameNotRunningException("Account logged out during the client hot-fix window");
            }
            _hotFix.Clear();
        }
        return appeared;
    }
    private async ValueTask EnsureApplication(CancellationToken token)
    {
        if (!await _application.IsRunningAsync(token)) throw new GameNotRunningException("Game application is not running");
    }
    public async ValueTask CheckUnknownPageAsync(bool checkApplication, bool checkOrientation, CancellationToken token)
    {
        if (checkApplication) await EnsureApplication(token);
        // The new device uses ADB; the upstream uiautomator2-only minicap removal does not apply.
        if (checkOrientation) await _application.RefreshOrientationAsync(token);
    }

    private async ValueTask<bool> OsPopups(CancellationToken token)
    {
        if (_fleetResetClicks >= 5) throw new HumanTakeoverRequiredException("Operation Siren fleet confirmation failed five times");
        if (await ClickIf(RESET_TICKET_POPUP, Wide, 3, token)) return true;
        if (await ClickIf(RESET_FLEET_PREPARATION, Wide, 3, token))
        {
            _fleetResetClicks++;
            _driver.ResetInterval(FLEET_PREPARATION);
            _driver.ResetInterval(RESET_TICKET_POPUP);
            return true;
        }
        if (!await Appear(EXCHANGE_CHECK, Wide, 3, token)) return false;
        _driver.ClearOffset(GOTO_MAIN);
        await _driver.ClickAsync(GOTO_MAIN, token);
        return true;
    }
    private async ValueTask<bool> MainPopups(bool getShip, CancellationToken token)
    {
        if (await Appear(GUILD_POPUP_CONFIRM, Popup, 0, token) && await Appear(GUILD_POPUP_CANCEL, Popup, 2, token))
        {
            await _driver.ClickAsync(GUILD_POPUP_CANCEL, token);
            return true;
        }
        if (await ClickIf(LOGIN_ANNOUNCE, Wide, 3, token) || await ClickIf(LOGIN_ANNOUNCE_2, Wide, 3, token) ||
            await ClickIf(GET_ITEMS_1, ButtonOffset.Vertical(_options.ButtonOffset), 3, token) ||
            await ClickIf(GET_ITEMS_2, ButtonOffset.Vertical(_options.ButtonOffset), 3, token)) return true;
        if (getShip && await ClickIf(GET_SHIP, ButtonOffset.Color, 5, token)) return true;
        if (await ClickIf(LOGIN_RETURN_SIGN, Wide, 3, token)) return true;
        if (await Appear(EVENT_LIST_CHECK, Wide, 5, token) && await ClickIf(GOTO_MAIN, Wide, 0, token)) return true;
        if (await ClickIf(MONTHLY_PASS_NOTICE, Wide, 3, token) || await ClickIf(BATTLE_PASS_NOTICE, Wide, 3, token)) return true;
        if (await Appear(BATTLE_PASS_NEW_SEASON, Wide, 3, token))
        {
            await _driver.ClickAsync(BACK_ARROW, token);
            return true;
        }
        if (await Appear(GET_MISSION, ButtonOffset.Bounds(-6, 48, 54, 88), 2, token))
        {
            await _driver.ClickAsync(GET_MISSION, token);
            return true;
        }
        if (await ClickIf(POPUP_SINGLE_WHITE, ButtonOffset.Expand(20, 20), 2, token)) return true;
        if (await Appear(SHIPYARD_CHECK, Wide, 5, token) && await ClickIf(GOTO_MAIN, Wide, 0, token)) return true;
        if (await Appear(META_CHECK, Wide, 5, token) && await ClickIf(GOTO_MAIN, Wide, 0, token)) return true;
        if (await Appear(PLAYER_CHECK, Wide, 3, token) &&
            (await ClickIf(GOTO_MAIN, Wide, 0, token) || await ClickIf(BACK_ARROW, Wide, 0, token))) return true;
        return false;
    }

    public async ValueTask<bool> AdditionalAsync(bool getShip, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (await OsPopups(token) || await PopupConfirm(token) || await UrgentCommission(token) ||
            await MainPopups(getShip, token) || await StorySkipAsync(token)) return true;
        if (await Appear(GAME_TIPS, Wide, 2, token)) { await _driver.ClickAsync(GOTO_MAIN, token); return true; }
        if (await Appear(DORM_INFO, Wide, 3, token, similarity: 0.75)) { await _driver.ClickAsync(DORM_INFO, token); return true; }
        if (await ClickIf(DORM_FEED_CANCEL, Wide, 3, token) || await ClickIf(DORM_TROPHY_CONFIRM, Wide, 3, token)) return true;
        if (await ClickIf(MEOWFFICER_INFO, Wide, 3, token)) { _driver.ResetInterval(GET_SHIP); return true; }
        if (await Appear(UiAssets.Meowfficer.MEOWFFICER_BUY, Wide, 3, token))
        {
            await _driver.ClickAsync(BACK_ARROW, token); _driver.ResetInterval(GET_SHIP); return true;
        }
        if (await Appear(MAP_PREPARATION, Wide, 3, token) || await Appear(MAP_PREPARATION_HARD, Wide, 3, token) ||
            await Appear(FLEET_PREPARATION, ButtonOffset.Expand(20, 50), 3, token) || await Appear(RAID_FLEET_PREPARATION, Wide, 3, token))
        {
            await _driver.ClickAsync(MAP_PREPARATION_CANCEL, token); return true;
        }
        if (await ClickIf(AUTO_SEARCH_MENU_EXIT, ButtonOffset.Expand(200, 30), 3, token) ||
            await ClickIf(AUTO_SEARCH_REWARD, ButtonOffset.Expand(50, 50), 3, token)) return true;
        if (await Appear(WITHDRAW, Wide, 3, token))
        {
            await _driver.DelayAsync(TimeSpan.FromSeconds(2), token);
            await _driver.ScreenshotAsync(token);
            bool clicked = await ClickIf(WITHDRAW, Wide, 0, token);
            _driver.ResetInterval(WITHDRAW);
            if (clicked) return true;
        }
        if (await ClickIf(LOGIN_CHECK, Wide, 3, token) || await ClickIf(MAINTENANCE_ANNOUNCE, Wide, 3, token)) return true;
        if (await Appear(UiAssets.Exercise.EXERCISE_PREPARATION, ButtonOffset.Color, 3, token))
        {
            await _driver.ClickAsync(GOTO_MAIN, token); return true;
        }
        if (await IdlePage(token)) return true;
        if (await Appear(MAIN_GOTO_MEMORIES_WHITE, ButtonOffset.Color, 3, token))
        {
            await _driver.ClickAsync(MAIN_TAB_SWITCH_WHITE, token); return true;
        }
        return false;
    }
    private async ValueTask<bool> IdlePage(CancellationToken token)
    {
        var timer = _driver.Timer(IDLE, 3);
        if (!timer.Reached()) return false;
        foreach (var asset in new[] { IDLE, IDLE_2, IDLE_3 })
            if (await Appear(asset, ButtonOffset.Expand(5, 5), 0, token, preprocessing: TemplatePreprocessing.Luma))
            {
                await _driver.ClickAsync(REWARD_GOTO_MAIN, token);
                timer.Reset(); return true;
            }
        return false;
    }

    public async ValueTask<bool> StorySkipAsync(CancellationToken token = default)
    {
        // Original InfoHandler.handle_story_skip exception, preserved in the rule port.
        if (_options.MapIsThreatSafe && _options.CampaignEvent != "event_20201012_cn") return false;
        return await StoryCoreAsync(token);
    }
    public async ValueTask EnsureNoStoryAsync(bool skipFirstScreenshot, CancellationToken token)
    {
        var quiet = new IntervalTimer(_driver.Clock, 3, 6);
        quiet.Reset();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!skipFirstScreenshot) await _driver.ScreenshotAsync(token);
            skipFirstScreenshot = false;
            // Native ensure_no_story calls story_skip directly, without the threat-safe guard.
            if (await StoryCoreAsync(token)) quiet.Reset();
            if (quiet.Reached()) return;
        }
    }
    private async ValueTask<bool> StoryCoreAsync(CancellationToken token)
    {
        if (_storyPopup.Started && !_storyPopup.Reached() && await PopupConfirm(token))
        {
            _storyPopup = new IntervalTimer(_driver.Clock, 10);
            ResetStoryIntervals(); return true;
        }
        var blackRule = STORY_LETTER_BLACK.For(_driver.Server);
        var color = await _driver.ColorAsync(blackRule.Area!.Value, token);
        if ((AssetMatcher.ColorSimilar(color, blackRule.Color!.Value, 10) || AssetMatcher.ColorSimilar(color, new Rgb(0, 0, 0), 10)) &&
            await ClickIf(STORY_LETTERS_ONLY, ButtonOffset.Expand(20, 20), 2, token))
        {
            _storyPopup.Reset(); return true;
        }
        if (_optionTimer.Reached() && await Appear(STORY_SKIP_3, ButtonOffset.Expand(20, 20), 0, token))
        {
            var options = await StoryOptionsAsync(token);
            if (options.Count == 0) { _optionRecord = 0; _optionConfirm.Reset(); }
            else if (options.Count == _optionRecord)
            {
                if (_optionConfirm.Reached())
                {
                    long index = _options.StoryOption < 0 ? (long)options.Count + _options.StoryOption : _options.StoryOption;
                    if (index < 0 || index >= options.Count) index = 0;
                    await _driver.ClickAreaAsync(options[(int)index], token);
                    _optionTimer.Reset(); _storyPopup.Reset(); ResetStoryIntervals();
                    _optionRecord = 0; _optionConfirm.Reset(); return true;
                }
            }
            else { _optionRecord = options.Count; _optionConfirm.Reset(); }
        }
        if (await Appear(STORY_SKIP_3, ButtonOffset.Expand(20, 20), 2, token))
        {
            _driver.ResetInterval(STORY_SKIP_3);
            if (_storyConfirm.Reached())
            {
                await _driver.ClickAsync(_options.StoryAllowSkip ? STORY_SKIP : CLICK_SAFE_AREA, token);
                _storyConfirm.Reset(); _storyPopup.Reset(); return true;
            }
            _driver.ClearInterval(STORY_SKIP_3);
        }
        else _storyConfirm.Reset();
        if (await ClickIf(STORY_CLOSE, ButtonOffset.Expand(10, 10), 2, token)) { _storyPopup.Reset(); return true; }
        return false;
    }
    public async ValueTask<IReadOnlyList<Rectangle>> StoryOptionsAsync(CancellationToken token = default)
    {
        // InfoHandler._story_option_buttons_2: CV returns vertical peak bases; C# owns button geometry.
        var result = await _driver.ColorBandsAsync(new ColorBandRequest(new PixelArea(330, 135, 25, 420),
            247, 247, 247, 5, 200, 200, 40, 40, 4), token);
        return result.Bands.Select(b => new Rectangle(335, 140 + b.Top, 975, 130 + b.Bottom)).ToArray();
    }
    private void ResetStoryIntervals() { _driver.ResetInterval(STORY_SKIP_3); _driver.ResetInterval(STORY_LETTERS_ONLY); }

    public void ResetIntervalsAfterClick(AssetRule button)
    {
        if (button == MEOWFFICER_GOTO_DORMMENU || button == DORMMENU_GOTO_DORM || button == DORMMENU_GOTO_MEOWFFICER)
            _driver.ResetInterval(GET_SHIP);
        foreach (var edge in _graph["page_main"].Links)
            if (button == edge.Button) _driver.ResetInterval(GET_SHIP);
        if (button == MAIN_GOTO_REWARD || button == MAIN_GOTO_REWARD_WHITE) _driver.ResetInterval(GET_SHIP);
        if (button == REWARD_GOTO_TACTICAL) _driver.ResetInterval(REWARD_GOTO_TACTICAL_WHITE);
        if (button == REWARD_GOTO_TACTICAL_WHITE) _driver.ResetInterval(REWARD_GOTO_TACTICAL);
        if (button == MAIN_GOTO_CAMPAIGN || button == MAIN_GOTO_CAMPAIGN_WHITE)
        {
            _driver.ResetInterval(GET_SHIP); _driver.ResetInterval(RAID_CHECK);
        }
        if (button == SHOP_GOTO_SUPPLY_PACK) _driver.ResetInterval(EXCHANGE_CHECK);
        if (button == RPG_GOTO_STAGE || button == RPG_GOTO_STORY || button == RPG_LEAVE_CITY)
            _driver.Timer(GET_SHIP, 5, renew: true).Reset();
    }
}
