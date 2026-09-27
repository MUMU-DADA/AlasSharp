using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public enum AmbushMessage { Unknown, Evaded, Failed }
// An observed encounter does not establish arrival at Destination or consume a map enemy.
public sealed record AmbushEncounterEvidence(int Fleet, Cell Destination, MapAmbushResult Result);
public sealed record MapAmbushResult(bool EvadeRequested, AmbushMessage Message,
    long StartedSequence, long FinishedSequence, int Clicks, CombatFlowResult? Combat, bool FleetStatusRefreshed)
{
    public bool IsConsistent => StartedSequence > 0 && FinishedSequence > StartedSequence && Clicks >= 0 &&
        Enum.IsDefined(Message) && (EvadeRequested || Message == AmbushMessage.Unknown) &&
        (EvadeRequested && Message != AmbushMessage.Failed || Combat is not null) &&
        (Message != AmbushMessage.Evaded || Combat is null);
    public bool CanContinue => IsConsistent &&
        (Combat is null || Combat is { Return: CombatReturn.InMap, Rank.IsWinningRank: true });
}

/// <summary>Native info_letter_preprocess and template order. Only numeric pixel transforms reach the CV worker.</summary>
public sealed class MapAmbushInfo(IVision vision, AssetFiles assets, Func<ScreenFrame> current, GameServer server)
{
    public static readonly SourceFile Source = UiRecovery.InfoSource;
    public async ValueTask<AmbushMessage> ReadAsync(CancellationToken token)
    {
        var frame = current();
        var area = UiAssets.Handler.INFO_BAR_DETECT.For(server).Area ?? throw new InvalidDataException("Info bar has no area");
        foreach (var (asset, result) in new[] {
            (UiAssets.Template.TEMPLATE_AMBUSH_EVADE_SUCCESS, AmbushMessage.Evaded),
            (UiAssets.Template.TEMPLATE_AMBUSH_EVADE_FAILED, AmbushMessage.Failed) })
        {
            var found = await vision.MatchAsync(frame, new(await assets.ReadAsync(asset.For(server), token), area.Area, .85,
                Transform: new(64, .75)), token);
            if (current().Sequence != frame.Sequence || found.FrameSequence != frame.Sequence)
                throw new InvalidDataException("Ambush message observation changed frames");
            if (found.Matched) return result;
        }
        return AmbushMessage.Unknown;
    }
}

/// <summary>Native _handle_ambush_evade / _handle_ambush_attack. Ambush battles do not consume map spawn counts.</summary>
public sealed class MapAmbushHandler(IUiDriver ui, IMapUiObservations observations, Func<long> currentFrame,
    Func<CancellationToken, ValueTask<AmbushMessage>> readMessage,
    Func<bool, CancellationToken, ValueTask<CombatFlowResult>> combat, bool evade,
    Func<bool> refreshFleetStatus, Func<CancellationToken, ValueTask> readFleetStatus,
    ICampaignInterruptions? interruptions = null, IMapEncounterHandler? next = null,
    TimeSpan? preparationTimeout = null) : IMapEncounterHandler
{
    public static readonly SourceFile Source = MapEncounterProbe.AmbushSource;
    private static ButtonOffset Offset => ButtonOffset.Expand(30, 30);

    public async ValueTask<MapEncounterHandling> HandleAsync(MapEncounterKind encounter, CancellationToken token)
    {
        if (encounter != MapEncounterKind.Ambush)
            return next is null ? new(MapEncounterContinuation.Unhandled) : await next.HandleAsync(encounter, token);
        token.ThrowIfCancellationRequested();
        long started = Frame();
        bool refresh = refreshFleetStatus();
        int clicks = 0;
        AmbushMessage message = AmbushMessage.Unknown;
        bool? waitForSearch = null;
        TimeSpan timeout = preparationTimeout ?? TimeSpan.FromSeconds(30);
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(preparationTimeout));
        using (var deadline = new CancellationTokenSource(timeout, ui.Clock))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token))
        {
            var limit = new IntervalTimer(ui.Clock, timeout.TotalSeconds); limit.Reset();
            async ValueTask Capture()
            {
                linked.Token.ThrowIfCancellationRequested();
                if (limit.Reached()) throw new TimeoutException("Ambush preparation did not complete");
                long before = Frame();
                await ui.ScreenshotAsync(linked.Token);
                if (Frame() <= before) throw new InvalidDataException("Ambush reused a stale screenshot");
            }
            async ValueTask ClearInfo()
            {
                if (await observations.InfoBarCountAsync(linked.Token) > 0)
                    do { await Capture(); } while (await observations.InfoBarCountAsync(linked.Token) > 0);
            }
            try
            {
                var button = evade ? UiAssets.Handler.MAP_AMBUSH_EVADE : UiAssets.Handler.MAP_AMBUSH_ATTACK;
                do { await Capture(); } while (!await ui.AppearsAsync(button, Offset, token: linked.Token));
                if (evade) await ClearInfo();
                bool first = true;
                while (true)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    if (!first) await Capture();
                    first = false;
                    if (evade ? await observations.InfoBarCountAsync(linked.Token) > 0 : await CombatAppearedAsync(linked.Token)) break;
                    if (await ui.AppearsAsync(button, Offset, interval: 3, token: linked.Token))
                    { await ui.ClickAsync(button, linked.Token); clicks++; continue; }
                    if (!evade && interruptions is not null)
                    {
                        if (await interruptions.LowEmotionAsync(linked.Token)) continue;
                        if (await interruptions.RetirementAsync(linked.Token)) continue;
                    }
                }
                if (evade)
                {
                    long frame = Frame();
                    message = await readMessage(linked.Token);
                    if (!Enum.IsDefined(message) || Frame() != frame) throw new InvalidDataException("Invalid ambush message observation");
                    if (message == AmbushMessage.Failed) waitForSearch = false;
                    else if (message == AmbushMessage.Unknown)
                    {
                        var clear = new IntervalTimer(ui.Clock, .6); clear.Reset();
                        bool firstClear = true;
                        do
                        {
                            if (!firstClear) await Capture();
                            firstClear = false;
                            await ClearInfo();
                        } while (!clear.Reached());
                        if (await CombatAppearedAsync(linked.Token)) waitForSearch = true;
                    }
                }
                else waitForSearch = false;
            }
            catch (OperationCanceledException error) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
            { throw new TimeoutException("Ambush preparation exceeded its time limit", error); }
        }
        // Battle phases have their own deadlines; the short button/message wait must not cancel a running battle.
        CombatFlowResult? battle = waitForSearch is { } wait ? await combat(wait, token) : null;
        bool canResume = battle is null || battle is { Return: CombatReturn.InMap, Rank.IsWinningRank: true };
        if (canResume && refresh) await readFleetStatus(token);
        var result = new MapAmbushResult(evade, message, started, Frame(), clicks, battle, canResume && refresh);
        return new(battle?.Return == CombatReturn.InStage ? MapEncounterContinuation.InStage : MapEncounterContinuation.InMap,
            Ambush: result);
    }

    private long Frame() => currentFrame() is > 0 and var frame ? frame :
        throw new InvalidDataException("Ambush handling requires a current screenshot");
    private async ValueTask<bool> CombatAppearedAsync(CancellationToken token)
        => await ui.AppearsAsync(UiAssets.Combat.BATTLE_PREPARATION, ButtonOffset.Expand(30, 20), token: token) ||
            await ui.AppearsAsync(UiAssets.Combat.BATTLE_PREPARATION_WITH_OVERLAY, threshold: 30, token: token);
}
