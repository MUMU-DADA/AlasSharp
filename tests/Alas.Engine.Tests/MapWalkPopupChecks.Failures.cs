using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static partial class MapWalkPopupChecks
{
    private static async Task FailureChecksAsync()
    {
        foreach (bool guild in new[] { false, true })
        foreach (string failure in new[] { "stale", "cancel", "click", "wrong_cv" })
        {
            var ui = new Replay(GameServer.Cn, new(false, true,
                [guild ? new(Confirm: true, Cancel: true) : new(Cat: 101)])) { Failure = failure };
            var popups = new MapWalkPopups(ui, new(ui, () => ui.Frame), () => ui.FrameSequence, false);
            var probe = new MapEncounterProbe(ui, false, walkPopups: popups);
            var state = State();
            var move = new MapMovement(state, new(), ui,
                () => new(ui, state, _ => ValueTask.FromResult(true), ui.Clock, probe, walkPopups: popups));
            bool rejected = false;
            try { await move.MoveAsync(new(2, 1)); }
            catch (Exception error) when (error is InvalidDataException or IOException or OperationCanceledException) { rejected = true; }
            Check(rejected && state.MovementInvalidated && ui.Invalidated && state.Fleet1Location == new Cell(1, 1) &&
                state.BattleCount == 0 && state.MysteryCount == 0 && ui.Clicks.Count == 1,
                "Popup failure committed or continued movement: " + failure);
        }
        foreach (bool guild in new[] { false, true })
        {
            var ui = new Replay(GameServer.Cn, new(false, false,
                [guild ? new(Confirm: true, Cancel: true) : new(Cat: 101)]));
            var timer = new IntervalTimer(ui.Clock, 2);
            var popups = new MapWalkPopups(ui, new(ui, () => ui.Frame), () => ui.FrameSequence, false, timer);
            var observed = await popups.ObserveAsync(ui.FrameSequence, default);
            await popups.HandleAsync(observed, ui.FrameSequence, default);
            bool duplicateRejected = false;
            try { await popups.HandleAsync(observed, ui.FrameSequence, default); }
            catch (InvalidOperationException) { duplicateRejected = true; }
            Check(duplicateRejected && ui.Clicks.Count == 1, "A popup observation was reused for two clicks");
            popups = new(ui, new(ui, () => ui.Frame), () => ui.FrameSequence, false, timer);
            Check(await popups.ObserveAsync(ui.FrameSequence, default) == MapEncounterKind.None, "New grid tap reset popup throttle");
            await ui.DelayAsync(TimeSpan.FromSeconds(2), default);
            Check(await popups.ObserveAsync(ui.FrameSequence, default) == MapEncounterKind.None, "Exact two-second boundary changed");
            await ui.DelayAsync(TimeSpan.FromMilliseconds(1), default);
            observed = await popups.ObserveAsync(ui.FrameSequence, default);
            Check(observed == (guild ? MapEncounterKind.GuildPopup : MapEncounterKind.CatAttack), "Throttle never expired");
            await ui.ScreenshotAsync(default);
            bool staleRejected = false;
            try { await popups.HandleAsync(observed, ui.FrameSequence, default); }
            catch (InvalidOperationException) { staleRejected = true; }
            Check(staleRejected && ui.Clicks.Count == 1, "Popup observation survived a changed frame");
        }
        foreach (bool neverReturn in new[] { false, true })
        {
            // A late popup must extend the walk budget, but a dismissed popup cannot establish arrival.
            var ui = new Replay(GameServer.Cn, new(false, true,
                [new(Marker: false), new(Marker: false), new(Cat: 101, Marker: false),
                 new(Marker: false), new(Marker: !neverReturn)]));
            var popups = new MapWalkPopups(ui, new(ui, () => ui.Frame), () => ui.FrameSequence, false);
            var state = State();
            var probe = new MapEncounterProbe(ui, false, walkPopups: popups);
            var move = new MapMovement(state, new(), ui, () => new(ui, state,
                _ => ValueTask.FromResult(ui.Frames < 3 || ui.Frames >= 5 && !neverReturn), ui.Clock, probe, walkPopups: popups));
            var result = await move.MoveAsync(new(2, 1), new(TimeSpan.FromSeconds(.1), TimeSpan.FromSeconds(1)));
            Check(result.Outcome == (neverReturn ? MapMoveOutcome.Unconfirmed : MapMoveOutcome.Committed) &&
                ui.Frames > 4 && ui.Relocalizations == 0 && ui.Clicks.Count == 2 &&
                result.Arrival.HandledEncounters.SequenceEqual([MapEncounterKind.CatAttack]) &&
                state.Fleet1Location == new Cell(neverReturn ? 1 : 2, 1), "Popup deadline or return gate changed");
        }
        // Ammo observations are nonblocking: inspect later handlers on the same frame before arrival.
        var ammoUi = new Replay(GameServer.Cn, new(false, true, [new(Cat: 101), new()]));
        var ammoPopup = new MapWalkPopups(ammoUi, new(ammoUi, () => ammoUi.Frame), () => ammoUi.FrameSequence, false);
        var ammoState = State();
        var arrival = await new MapArrivalCheck(ammoUi, ammoState, _ => ValueTask.FromResult(true), ammoUi.Clock,
            new AmmoThenPopup(ammoPopup), walkPopups: ammoPopup).TapAndCheckAsync(new(2, 1));
        Check(arrival.AmmoNotificationFrames.SequenceEqual([2L]) && arrival.HandledEncounters.SequenceEqual([MapEncounterKind.CatAttack]) &&
            ammoUi.Clicks.Count == 2, "Ammo suppressed the cat handler on the same frame");
    }

    private sealed class AmmoThenPopup(MapWalkPopups popups) : IMapEncounterProbe
    {
        public ValueTask InitializeAsync(long frameSequence, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<MapEncounterKind> InspectAsync(long frameSequence, CancellationToken token)
            => frameSequence == 2 ? ValueTask.FromResult(MapEncounterKind.AmmoNotification) : InspectAfterMysteryAsync(frameSequence, token);
        public ValueTask<MapEncounterKind> InspectAfterMysteryAsync(long frameSequence, CancellationToken token)
            => popups.ObserveAsync(frameSequence, token);
    }
}
