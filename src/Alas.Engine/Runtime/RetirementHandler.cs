using Alas.Engine.Navigation;
using Alas.Engine.Rules;
using Alas.Engine.Devices;
using static Alas.Engine.Rules.UiAssets.Retire;

namespace Alas.Engine.Runtime;

public sealed record RetirementConfirmation(long StartedSequence, long? ReturnedSequence, int ShipConfirmClicks,
    int EquipmentConfirmClicks, int RewardObservations, bool ReturnedAfterNativeTimeout);
public sealed record RetirementEvidence(RetirementMode Mode, long StartedSequence, long? ReturnedSequence,
    int NativeSelectionEstimate, IReadOnlyList<RetirementConfirmation> Confirmations);

/// <summary>Independent retirement workflow; estimates are never treated as observed ship counts.</summary>
public sealed class RetirementHandler(IUiDriver ui, IRetirementDock dock, Func<long> sequence,
    Func<CancellationToken, ValueTask<int>> infoBars)
{
    public static readonly SourceFile Source = RetirementRules.Source;
    private readonly List<RetirementEvidence> _evidence = [];
    public IReadOnlyList<RetirementEvidence> Evidence => _evidence.ToArray();
    public void ResetEvidence() => _evidence.Clear();
    private static ButtonOffset Wide => ButtonOffset.Expand(30, 30);

    public async ValueTask<bool> HandleAsync(RetirementOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); options.Validate();
        if (await ui.AppearsAsync(RETIRE_APPEAR_1, ButtonOffset.Expand(20, 20), interval: 3, threshold: 30, token: token))
        {
            if (options.Mode == RetirementMode.Disabled) throw new CampaignDockFullException();
            await ui.ClickAsync(RETIRE_APPEAR_1, token);
            ui.ClearInterval(IN_RETIREMENT_CHECK);
            ui.ResetInterval(UiAssets.Handler.AUTO_SEARCH_MAP_OPTION_OFF);
            ui.ResetInterval(UiAssets.Handler.AUTO_SEARCH_MAP_OPTION_ON);
            // Native handle_retirement returns false after this click.
            return false;
        }
        if (!await ui.AppearsAsync(IN_RETIREMENT_CHECK, ButtonOffset.Expand(20, 20), interval: 10, token: token)) return false;
        if (options.Mode == RetirementMode.Disabled) throw new CampaignDockFullException();
        await RunAsync(options, token);
        ui.ResetInterval(IN_RETIREMENT_CHECK);
        return true;
    }

    public async ValueTask<RetirementEvidence> RunAsync(RetirementOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); options.Validate();
        if (options.Mode == RetirementMode.Disabled) throw new CampaignDockFullException();
        if (!await ui.AppearsAsync(IN_RETIREMENT_CHECK, ButtonOffset.Expand(20, 20), token: token))
            throw new InvalidDataException("Retirement requires its observed dock page");
        int index = _evidence.Count;
        var confirmations = new List<RetirementConfirmation>();
        _evidence.Add(new(options.Mode, sequence(), null, 0, []));
        async ValueTask Confirm(CancellationToken ct)
        {
            int attempt = confirmations.Count;
            await ConfirmAsync(options, ct, progress =>
            {
                if (confirmations.Count == attempt) confirmations.Add(progress);
                else confirmations[attempt] = progress;
                _evidence[index] = _evidence[index] with { Confirmations = confirmations.ToArray() };
            });
        }
        int total;
        if (options.Mode == RetirementMode.OneClick)
        {
            total = await OneClickAsync(Confirm, token);
            if (total == 0)
            {
                await dock.FavouriteAsync(false, token);
                await dock.FilterAsync(null, token);
                total = await OneClickAsync(Confirm, token);
            }
            if (ui.Server is GameServer.Cn or GameServer.En or GameServer.Jp)
            {
                if (total == 0) { await dock.QuickSettingsAsync(null, token); total = await OneClickAsync(Confirm, token); }
                if (total == 0) { await dock.QuickSettingsAsync("keep_limit_break", token); total = await OneClickAsync(Confirm, token); }
                if (total == 0 && !options.KeepLimitBreak)
                { await dock.QuickSettingsAsync("all", token); total = await OneClickAsync(Confirm, token); }
            }
        }
        else
        {
            await dock.WaitCardsAsync(token);
            total = await OldAsync(options, Confirm, token);
        }
        _evidence[index] = _evidence[index] with { NativeSelectionEstimate = total };
        if (total == 0 || !confirmations.Any(c => c.ReturnedSequence.HasValue))
            throw new HumanTakeoverRequiredException("No ships were confirmed retired; check retirement settings");
        await UiClick.UntilAsync(ui, UiAssets.Ui.BACK_ARROW, async ct =>
            !await ui.AppearsAsync(IN_RETIREMENT_CHECK, ButtonOffset.Expand(20, 20), token: ct) &&
            !await ui.AppearsAsync(DOCK_CHECK, ButtonOffset.Expand(20, 20), token: ct), Wide, 10, token);
        long returned = sequence();
        if (returned <= confirmations[^1].ReturnedSequence)
            throw new InvalidDataException("Retirement exit reused a stale frame");
        _evidence[index] = _evidence[index] with { ReturnedSequence = returned };
        return _evidence[index];
    }

    private async ValueTask<int> OneClickAsync(Func<CancellationToken, ValueTask> confirm, CancellationToken token)
    {
        await dock.FavouriteAsync(false, token);
        await dock.DescendingAsync(true, token);
        if (await infoBars(token) > 0)
            do { await ui.ScreenshotAsync(token); } while (await infoBars(token) > 0);
        int clicks = 0;
        bool first = true;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!first) await ui.ScreenshotAsync(token);
            first = false;
            if (await ui.AppearsAsync(SHIP_CONFIRM_2, Wide, token: token)) break;
            if (await infoBars(token) > 0) return 0;
            if (clicks >= 5)
            {
                // Native slow-dock wait reuses the current image, then waits for the ship confirmation.
                ui.ResetProgress();
                while (!await ui.AppearsAsync(SHIP_CONFIRM_2, Wide, token: token)) await ui.ScreenshotAsync(token);
            }
            if (await RetirementUi.ClickIfAsync(ui, ONE_CLICK_RETIREMENT, ButtonOffset.Expand(20, 20), 2, token)) clicks++;
        }
        await confirm(token);
        return 10; // Native selection estimate, not a measured ship count.
    }

    private async ValueTask<int> OldAsync(RetirementOptions options, Func<CancellationToken, ValueTask> confirm, CancellationToken token)
    {
        await dock.DescendingAsync(false, token);
        await dock.FavouriteAsync(false, token);
        await dock.FilterAsync(options.Rarities.Select(RetirementRules.FilterRarity).ToArray(), token);
        int remaining = options.Amount, total = 0;
        while (remaining > 0)
        {
            token.ThrowIfCancellationRequested();
            int selected = await ChooseAsync(Math.Min(10, remaining), options.Rarities, token);
            total += selected;
            if (selected == 0) break;
            await ui.ScreenshotAsync(token);
            if (!await UiVisuals.MatchTemplateColorAsync(ui, SHIP_CONFIRM, Wide, 0, token)) continue;
            await confirm(token);
            remaining -= selected;
            if (remaining > 0) await dock.WaitCardsAsync(token);
        }
        await dock.DescendingAsync(true, token);
        await dock.FilterAsync(null, token);
        return total;
    }

    internal async ValueTask<int> ChooseAsync(int amount, IReadOnlyList<ShipRarity> rarities, CancellationToken token)
    {
        var candidates = new List<(Rectangle Area, ShipRarity Rarity)>();
        foreach (var card in RetirementRules.Cards)
        {
            var color = await ui.ColorAsync(new(card.Left, card.Top, card.Right, card.Top + 5), token);
            foreach (var (rarity, expected) in RetirementRules.RarityColors)
                if (AssetMatcher.ColorSimilar(color, expected, 15)) candidates.Add((card, rarity));
        }
        int selected = 0;
        foreach (var (area, rarity) in candidates)
        {
            if (rarities.Contains(rarity))
            {
                await ui.ClickAreaAsync(area, token);
                await ui.DelayAsync(TimeSpan.FromSeconds(.1 + Random.Shared.NextDouble() * .05), token);
                selected++;
            }
            if (selected >= amount) break;
        }
        return selected;
    }

    internal async ValueTask<RetirementConfirmation> ConfirmAsync(RetirementOptions options, CancellationToken token,
        Action<RetirementConfirmation>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        foreach (var asset in new[] { SHIP_CONFIRM, SHIP_CONFIRM_2, EQUIP_CONFIRM, EQUIP_CONFIRM_2,
                     UiAssets.Combat.GET_ITEMS_1, SR_SSR_CONFIRM, UiAssets.Handler.POPUP_CANCEL,
                     UiAssets.Handler.POPUP_CONFIRM, UiAssets.UiWhite.POPUP_CANCEL_WHITE, UiAssets.UiWhite.POPUP_CONFIRM_WHITE })
            ui.ClearInterval(asset);
        var timeout = new IntervalTimer(ui.Clock, 10, 10); timeout.Reset();
        long start = sequence();
        long lastAction = start;
        int ships = 0, equipment = 0, rewards = 0;
        void Record() => progress?.Invoke(new(start, null, ships, equipment, rewards, false));
        Record();
        bool first = true;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!first) await ui.ScreenshotAsync(token);
            first = false;
            bool expired = timeout.Reached();
            bool returned = await ui.AppearsAsync(IN_RETIREMENT_CHECK, ButtonOffset.Expand(20, 20), token: token) &&
                !await ui.AppearsAsync(EQUIP_CONFIRM, Wide, token: token);
            if (returned && ships > 0 && sequence() > lastAction && (equipment > 0 || expired && rewards > 0))
            {
                var completed = new RetirementConfirmation(start, sequence(), ships, equipment, rewards, expired);
                progress?.Invoke(completed);
                return completed;
            }
            if (expired) throw new TimeoutException("Retirement lacks confirmation/reward and fresh return evidence");
            if (!returned) timeout.Reset();
            if (options.Mode == RetirementMode.OneClick || options.Rarities.Any(r => r is ShipRarity.SR or ShipRarity.SSR))
            {
                bool popup = await RetirementUi.ConfirmAsync(ui, ButtonOffset.Expand(20, 50), token);
                if (!popup && ui.Server is GameServer.Cn or GameServer.Jp or GameServer.Tw)
                    popup = await RetirementUi.ClickIfAsync(ui, SR_SSR_CONFIRM, ButtonOffset.Expand(20, 50), 2, token);
                if (popup)
                {
                    lastAction = sequence();
                    foreach (var asset in new[] { SHIP_CONFIRM, SHIP_CONFIRM_2, EQUIP_CONFIRM, EQUIP_CONFIRM_2 }) ui.ResetInterval(asset);
                    continue;
                }
            }
            if (await UiVisuals.MatchTemplateColorAsync(ui, SHIP_CONFIRM_2, Wide, 2, token))
            {
                await ui.ClickAsync(SHIP_CONFIRM_2, token); lastAction = sequence(); ships++; Record();
                ui.ClearInterval(UiAssets.Combat.GET_ITEMS_1); ui.ResetInterval(SHIP_CONFIRM); ui.ResetInterval(SHIP_CONFIRM_2);
                continue;
            }
            if (await UiVisuals.MatchTemplateColorAsync(ui, SHIP_CONFIRM, Wide, 2, token))
            { await ui.ClickAsync(SHIP_CONFIRM, token); lastAction = sequence(); ships++; Record(); continue; }
            if (await RetirementUi.ClickIfAsync(ui, EQUIP_CONFIRM, Wide, 2, token))
            { lastAction = sequence(); continue; }
            if (await RetirementUi.ClickIfAsync(ui, EQUIP_CONFIRM_2, Wide, 2, token))
            { lastAction = sequence(); ui.ClearInterval(UiAssets.Combat.GET_ITEMS_1); equipment++; Record(); continue; }
            if (await ui.AppearsAsync(UiAssets.Combat.GET_ITEMS_1, Wide, interval: 2, token: token))
            {
                await ui.ClickAsync(GET_ITEMS_1_RETIREMENT_SAVE, token); lastAction = sequence(); rewards++; Record();
                ui.ResetInterval(SHIP_CONFIRM); ui.ClearInterval(EQUIP_CONFIRM); ui.ClearInterval(EQUIP_CONFIRM_2);
            }
        }
    }
}
