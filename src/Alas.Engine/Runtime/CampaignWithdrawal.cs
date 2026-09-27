using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed record CampaignWithdrawalEvidence(string Reason, long StartedSequence, long StageSequence,
    int ExitActions, bool StageConfirmed);

/// <summary>MapOperation.withdraw: confirmation, map exit, auto-search exit, daily-page recovery, then stable stage.</summary>
public sealed class CampaignWithdrawal(IUiDriver ui, IPopupHandler popups, MapUiRecovery mapUi, Func<long> sequence)
{
    public static readonly SourceFile Source = MapUiRecovery.PreparationSource;
    public async ValueTask<CampaignWithdrawalEvidence> RunAsync(string reason, TimeSpan timeout, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Withdrawal requires a reason", nameof(reason));
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(timeout));
        using var deadline = new CancellationTokenSource(timeout, ui.Clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        var limit = new IntervalTimer(ui.Clock, timeout.TotalSeconds); limit.Reset();
        bool first = ui.HasFrame;
        long started = first ? sequence() : 0, previous = started;
        int actions = 0;
        try
        {
            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (limit.Reached()) throw new TimeoutException("Withdrawal did not return to the stage page");
                if (!first)
                {
                    await ui.ScreenshotAsync(linked.Token);
                    if (sequence() <= previous) throw new InvalidDataException("Withdrawal reused a stale screenshot");
                }
                first = false;
                previous = sequence();
                if (await popups.ConfirmAsync(linked.Token)) continue;
                if (await ui.AppearsAsync(UiAssets.Map.WITHDRAW, interval: 5, token: linked.Token))
                { await ui.ClickAsync(UiAssets.Map.WITHDRAW, linked.Token); actions++; continue; }
                if (await ui.AppearsAsync(UiAssets.Handler.AUTO_SEARCH_MENU_EXIT, ButtonOffset.Expand(250, 30), interval: 2, token: linked.Token))
                {
                    await ui.ClickAsync(UiAssets.Handler.AUTO_SEARCH_MENU_EXIT, linked.Token);
                    ui.ResetInterval(UiAssets.Handler.AUTO_SEARCH_MENU_EXIT);
                    actions++;
                    continue;
                }
                if (await ui.AppearsAsync(UiAssets.Ui.DAILY_CHECK, ButtonOffset.Expand(20, 20), interval: 3, token: linked.Token))
                { await ui.ClickAsync(UiAssets.Ui.BACK_ARROW, linked.Token); continue; }
                try { await mapUi.HandleInStageAsync(linked.Token); }
                catch (CampaignEndedException)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    // The info-bar wait may capture more images after stage detection.
                    // Confirm the final image as well before publishing its identity.
                    long stage = sequence();
                    if (!await mapUi.IsInStageAsync(linked.Token)) continue;
                    linked.Token.ThrowIfCancellationRequested();
                    if (sequence() != stage) throw new InvalidDataException("Withdrawal confirmation changed frames");
                    if (sequence() <= started || actions == 0)
                        throw new InvalidDataException("Stage return without a withdrawal action cannot confirm withdrawal");
                    return new(reason, started, sequence(), actions, true);
                }
            }
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException("Withdrawal exceeded its time limit", error); }
    }
}
