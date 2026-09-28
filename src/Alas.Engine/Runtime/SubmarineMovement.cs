using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public interface ISubmarineMoveCamera
{
    long FrameSequence { get; }
    ValueTask PrepareSubmarineTapAsync(Cell target, CancellationToken token = default);
    ValueTask TapCellAsync(Cell target, CancellationToken token = default);
    ValueTask RefreshImageAsync(CancellationToken token = default);
    ValueTask<bool> PredictSubmarineAsync(Cell target, CancellationToken token = default);
    ValueTask<bool> PredictSubmarineMoveAsync(Cell target, CancellationToken token = default);
    ValueTask<FleetMarker> ReadFleetMarkerAsync(Cell target, CancellationToken token = default);
    ValueTask EnsureEdgesAsync(bool skipFirstUpdate, CancellationToken token);
    void Invalidate();
}

public sealed record SubmarineMoveResult(bool Moved, Cell Target, int Attempts, long ConfirmedFrame);
public sealed record SubmarineMoveEvidence(Cell Boss, Cell Origin, Cell Target, string Phase,
    int Attempts = 0, long? SelectionFrame = null, long? ReturnedFrame = null, bool? Moved = null);

/// <summary>Native strategy submarine relocation used by boss-call modes.</summary>
public sealed class SubmarineMovement(IUiDriver ui, ISubmarineMoveCamera camera, IPopupHandler popups, Func<long> frameSequence)
{
    public static readonly SourceFile FleetSource = CampaignState.InitializationSource;
    private readonly List<SubmarineMoveEvidence> _evidence = [];
    public IReadOnlyList<SubmarineMoveEvidence> Evidence => _evidence.AsReadOnly();

    internal static Cell? SelectTarget(CampaignState state, Cell boss, CampaignConfiguration configuration)
    {
        if (!configuration.RequiresBossRelocation() || !state.Cells.Any(cell => cell.IsSubmarineSpawnPoint)) return null;
        if (!state.IsMapInitialized || state.MovementInvalidated || !state.Contains(boss))
            throw new InvalidOperationException("Submarine movement requires a usable localized map");
        if (configuration.BossDistance() < 0) return null;
        var origin = state.SubmarineLocation ?? throw new InvalidDataException("Submarine location is unknown");
        int distance = configuration.BossDistance();
        try
        {
            state.Paths.ComputeCosts(origin, hasAmbush: false, hasEnemy: false);
            if (Manhattan(origin, boss) <= distance) return null;
            // Native's shrinking-distance fallback ultimately returns the boss if all candidates are land.
            return state.Cells.Where(cell => !cell.IsLand && Manhattan(cell.Location, boss) <= distance)
                .OrderBy(cell => cell.Cost).FirstOrDefault()?.Location ?? boss;
        }
        finally { state.RefreshFleetPaths(configuration); }
    }

    public async ValueTask<SubmarineMoveResult?> MoveNearBossAsync(CampaignState state, Cell boss,
        CampaignConfiguration configuration, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var target = SelectTarget(state, boss, configuration);
        if (target is null) return null;
        var entry = new SubmarineMoveEvidence(boss, state.SubmarineLocation!.Value, target.Value, "opening");
        int index = _evidence.Count; _evidence.Add(entry);
        void Save(SubmarineMoveEvidence value) { entry = value; _evidence[index] = value; }
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2), ui.Clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        var limit = new IntervalTimer(ui.Clock, 120); limit.Reset();
        void Check()
        {
            linked.Token.ThrowIfCancellationRequested();
            if (limit.Reached()) throw new TimeoutException("Submarine relocation exceeded its time limit");
        }
        async ValueTask Capture()
        {
            Check(); long previous = frameSequence();
            await ui.ScreenshotAsync(linked.Token);
            if (frameSequence() <= previous) throw new InvalidDataException("Submarine strategy reused a stale frame");
        }
        try
        {
            await Capture();
            await StrategyOpenAsync(Check, Capture, linked.Token);
            Save(entry with { Phase = "entering" });
            while (true)
            {
                Check();
                if (await ui.AppearsAsync(UiAssets.Handler.SUBMARINE_MOVE_ENTER, ButtonOffset.Vertical(200), interval: 5, token: linked.Token))
                    await ui.ClickAsync(UiAssets.Handler.SUBMARINE_MOVE_ENTER, linked.Token);
                if (await ui.AppearsAsync(UiAssets.Handler.SUBMARINE_MOVE_CONFIRM, ButtonOffset.Expand(20, 20), token: linked.Token)) break;
                await Capture();
            }
            Save(entry with { Phase = "selecting" });
            var selected = await SelectAsync(target.Value, Check, attempt => Save(entry with { Attempts = attempt }), linked.Token);
            Save(entry with { Phase = selected.Moved ? "confirming" : "cancelling", SelectionFrame = selected.ConfirmedFrame, Moved = selected.Moved });
            var button = selected.Moved ? UiAssets.Handler.SUBMARINE_MOVE_CONFIRM : UiAssets.Handler.SUBMARINE_MOVE_CANCEL;
            // Leave the selection frame before inspecting strategy buttons (including popup confirmation).
            await Capture();
            while (true)
            {
                Check();
                if (await ui.AppearsAsync(button, ButtonOffset.Expand(20, 20), interval: 5, token: linked.Token))
                    await ui.ClickAsync(button, linked.Token);
                await popups.ConfirmAsync(linked.Token);
                if (await ui.AppearsAsync(UiAssets.Handler.SUBMARINE_MOVE_ENTER, ButtonOffset.Vertical(200), token: linked.Token)) break;
                await Capture();
            }
            Save(entry with { Phase = "hiding_zone" });
            var view = new UiSwitch(ui, [new("on", UiAssets.Handler.SUBMARINE_VIEW_ON), new("off", UiAssets.Handler.SUBMARINE_VIEW_OFF)],
                ButtonOffset.Expand(100, 200), frameSequence: frameSequence);
            if (await view.ReadAsync(linked.Token) is not null) await view.SetAsync("off", TimeSpan.FromSeconds(30), token: linked.Token);
            Save(entry with { Phase = "closing" });
            while (true)
            {
                Check();
                if (await ui.AppearsAsync(UiAssets.Handler.STRATEGY_OPENED, ButtonOffset.Vertical(200), interval: 5, token: linked.Token))
                { await ui.ClickAsync(UiAssets.Handler.STRATEGY_OPENED, linked.Token); await Capture(); continue; }
                if (!await ui.AppearsAsync(UiAssets.Handler.STRATEGY_OPENED, ButtonOffset.Vertical(200), token: linked.Token)) break;
                await Capture();
            }
            await camera.RefreshImageAsync(linked.Token);
            if (camera.FrameSequence <= selected.ConfirmedFrame) throw new InvalidDataException("Submarine return reused the selection frame");
            Save(entry with { Phase = "completed", ReturnedFrame = camera.FrameSequence });
            return selected;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            state.MovementInvalidated = true; camera.Invalidate();
            // Retain the last phase and any completed selection; it is not a completed relocation.
            if (error is OperationCanceledException && !token.IsCancellationRequested && deadline.IsCancellationRequested)
                throw new TimeoutException("Submarine relocation exceeded its time limit", error);
            throw;
        }
    }

    private async ValueTask<SubmarineMoveResult> SelectAsync(Cell target, Action check, Action<int> attempt,
        CancellationToken token)
    {
        bool moved = true;
        for (int count = 1; ; count++)
        {
            check(); attempt(count);
            await camera.PrepareSubmarineTapAsync(target, token);
            await camera.TapCellAsync(target, token);
            var settle = new IntervalTimer(ui.Clock, .1);
            var timeout = new IntervalTimer(ui.Clock, 2, 6); timeout.Reset();
            while (true)
            {
                check(); long previous = camera.FrameSequence;
                await camera.RefreshImageAsync(token);
                if (camera.FrameSequence <= previous) throw new InvalidDataException("Submarine selection reused a stale frame");
                bool arrived = await camera.PredictSubmarineMoveAsync(target, token);
                if (await camera.PredictSubmarineAsync(target, token) ||
                    timeout.Reached() && (await camera.ReadFleetMarkerAsync(target, token)).Fleet)
                { arrived = true; moved = false; }
                if (arrived)
                {
                    if (!settle.Started) settle.Reset();
                    if (!settle.Reached()) continue;
                    return new(moved, target, count, camera.FrameSequence);
                }
                if (timeout.Reached()) break;
            }
            await camera.EnsureEdgesAsync(skipFirstUpdate: false, token);
        }
    }

    private async ValueTask StrategyOpenAsync(Action check, Func<ValueTask> capture, CancellationToken token)
    {
        while (true)
        {
            check();
            if (await ui.AppearsAsync(UiAssets.Handler.STRATEGY_OPENED, ButtonOffset.Vertical(200), token: token)) return;
            if (await ui.AppearsAsync(UiAssets.Handler.IN_MAP, interval: 5, token: token) &&
                !await ui.AppearsAsync(UiAssets.Handler.STRATEGY_OPENED, ButtonOffset.Vertical(200), token: token))
                await ui.ClickAsync(UiAssets.Handler.STRATEGY_OPEN, token);
            else if (await ui.AppearsAsync(UiAssets.Combat.GET_ITEMS_1, ButtonOffset.Vertical(5), token: token))
                await ui.ClickAsync(UiAssets.Combat.GET_ITEMS_1, token);
            await capture();
        }
    }
    private static int Manhattan(Cell a, Cell b) => Math.Abs(a.Column - b.Column) + Math.Abs(a.Row - b.Row);
}
