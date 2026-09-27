using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static partial class MapMechanismChecks
{
    private static async Task FailureChecksAsync()
    {
        foreach (string failure in new[] { "click", "stale", "page", "timeout", "cancel" })
        {
            var state = State(); var target = new Cell(2, 3); var old = state.Fleet1Location;
            using var cancel = new CancellationTokenSource();
            var camera = new Camera(state)
            {
                Failure = failure,
                OnFrame = () => { if (failure == "cancel") cancel.Cancel(); }
            };
            try
            {
                var moved = await Movement(state, new() { HasLandBased = true }, camera).MoveAsync(target,
                    new(TimeSpan.FromSeconds(.5), TimeSpan.FromSeconds(4)), cancel.Token);
                Check(moved.Outcome is MapMoveOutcome.Interrupted or MapMoveOutcome.Unconfirmed, "Failed mechanism confirmed a landing");
            }
            catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException) { }
            Check(state.Fleet1Location == old && state[target].IsMechanismTrigger && state[new(4, 3)].IsMechanismBlock &&
                state.MechanismReleases.Count == 0 && camera.Invalidated && state.BattleCount == 0,
                "Mechanism failure partially released topology: " + failure);
        }
        foreach (string failure in new[] { "triggers", "blocks", "foreign", "self", "negative", "infinite", "timeout", "options" })
        {
            var state = State(); var target = state[new(2, 3)]; var camera = new Camera(state);
            if (failure == "triggers") target.MechanismTrigger = null;
            if (failure == "blocks") target.MechanismBlock = null;
            if (failure == "foreign") target.MechanismBlock = [State()[new(4, 3)]];
            if (failure == "self") target.MechanismTrigger = [state[new(3, 4)]];
            if (failure == "negative") target.MechanismWait = -1;
            if (failure == "infinite") target.MechanismWait = double.PositiveInfinity;
            if (failure == "timeout") target.MechanismWait = 20;
            bool rejected = false;
            try { await Movement(state, new() { HasLandBased = true }, camera).MoveAsync(target.Location,
                failure == "options" ? new(TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(20)) : null); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected && camera.Taps.Count == 0 && target.IsMechanismTrigger && state[new(4, 3)].IsMechanismBlock,
                "Malformed mechanism performed input or state changes: " + failure);
        }
        {
            var state = State(); var camera = new Camera(state); var target = new Cell(2, 3);
            var movement = Movement(state, new() { HasLandBased = true }, camera);
            var result = await movement.MoveAsync(target);
            Check(result.MechanismRelease is { ConfirmSeconds: 2.5, ArrivalSequence: > 1 } && camera.Frames == 12 &&
                !state[new(4, 3)].IsMechanismBlock && state[new(4, 3)].IsAccessible && state.MechanismReleases.Count == 1,
                "Ordinary route landing did not wait before releasing the mechanism");
            await movement.MoveAsync(new(4, 3));
            Check(state.Fleet1Location == new Cell(4, 3) && state.MechanismReleases.Count == 1,
                "Next movement used stale blocked costs or repeated release");
        }
        {
            var state = State(); var camera = new Camera(state); var target = new Cell(2, 3);
            state[target].MayAmmo = state[target].IsAmmo = true;
            state.CommitBattle(false);
            var moved = await Movement(state, new() { HasLandBased = true }, camera).MoveAsync(target);
            Check(moved.Arrival.SupplyClickCompleted && camera.Taps.Count == 2 &&
                state.AmmoCount == 3 && state.FleetAmmo == 4 && moved.AmmoPickup is null && moved.MechanismRelease is not null,
                "Mechanism landing silently performed pick_up_ammo accounting");
        }
    }
}
