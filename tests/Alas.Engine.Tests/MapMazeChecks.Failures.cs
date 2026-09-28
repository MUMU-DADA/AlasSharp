using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static partial class MapMazeChecks
{
    private static async Task FailureChecksAsync()
    {
        var config = new CampaignConfiguration { HasMaze = true, HasAmbush = false };
        foreach (string failure in new[] { "click", "stale", "timeout", "page", "cancel" })
        {
            var state = State(); state.Rounds.Initialize(config); state.RefreshFleetPaths(config);
            using var cancelled = new CancellationTokenSource();
            var camera = new Camera { Failure = failure, Cancellation = cancelled };
            try
            {
                var result = await Movement(state, config, camera).WaitForMazeAsync(C("D1"), cancelled.Token);
                Check(result?.Outcome is MapMoveOutcome.Unconfirmed or MapMoveOutcome.Interrupted, "Failed maze move was accepted");
            }
            catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException) { }
            Check(state.Fleet1Location == C("A1") && state.MazeWaits.Count == 0 && state.Rounds.Round == 0 &&
                state.MovementInvalidated && camera.Invalidated, "Failed detour advanced maze round or location: " + failure);
        }
        foreach (string invalid in new[] { "null", "empty", "foreign", "self", "occupied", "rounds" })
        {
            var state = State(); var maze = state[C("D1")]; var camera = new Camera();
            if (invalid != "rounds") state.Rounds.Initialize(config);
            if (invalid == "null") maze.MazeNearby = null;
            if (invalid == "empty") maze.MazeNearby = [];
            if (invalid == "foreign") maze.MazeNearby = [State()[C("C1")]];
            if (invalid == "self") maze.MazeNearby = [maze];
            if (invalid == "occupied") foreach (var cell in maze.MazeNearby!) cell.IsFleet = true;
            state.RefreshFleetPaths(config);
            bool rejected = false;
            try { await Movement(state, config, camera).WaitForMazeAsync(C("D1")); }
            catch (Exception e) when (e is InvalidDataException or InvalidOperationException or CampaignScriptException) { rejected = true; }
            Check(rejected && camera.Taps.Count == 0 && state.Rounds.Round == 0, "Invalid maze declaration produced an action: " + invalid);
        }
        foreach (bool useCurrent in new[] { false, true })
        foreach (bool boss in new[] { false, true })
        {
            var c = config with { WalkUseCurrentFleet = useCurrent }; var state = State(); state.Rounds.Initialize(c); state.RefreshFleetPaths(c);
            var camera = new Camera { CurrentOnly = true }; var move = Movement(state, c, camera);
            var options = new MapArrivalOptions(TimeSpan.FromSeconds(.5), TimeSpan.FromSeconds(3));
            state[C("C1")].MayBoss = boss;
            var result = boss ? await move.ProbeBossAsync(C("C1"), options) : await move.MoveAsync(C("C1"), options);
            Check(result.Outcome == MapMoveOutcome.Committed && (camera.Frames <= 12) == (useCurrent && !boss) &&
                state.BattleCount == 0,
                "Current-marker option/boss exclusion must defer to timeout confirmation without claiming a boss victory");
        }
        {
            var state = State(); state.Rounds.Initialize(config); state.RefreshFleetPaths(config);
            state[C("B1")].MayAmmo = state[C("B1")].IsAmmo = true;
            var camera = new Camera();
            try { await Movement(state, config, camera).WaitForMazeAsync(C("D1")); throw new InvalidOperationException("Ammo detour did not redispatch"); }
            catch (MapEnemyMovedException) { }
            Check(state.MazeWaits.Count == 3 && state.AmmoCount == 3 && state.FleetAmmo == 5 && camera.Taps.Count == 5,
                "Maze supply detour lost acknowledgement tap or invented pickup inventory");
        }
        {
            var state = new CampaignState(new("G1", "-- -- -- -- -- -- MS", ["D1"], [], [new(0, Siren: 1)],
                mechanisms: new(mazes: [[C("D1")], [C("A1")], [C("G1")]])));
            state.InitializeMapData(new(Maze: true)); state.Fleet1Location = C("A1"); state[C("A1")].IsFleet = true;
            state[C("G1")].IsSiren = true;
            var c = config with { HasMovableEnemy = true };
            state.Rounds.Initialize(c); state.RefreshFleetPaths(c);
            var camera = new Camera();
            try { await Movement(state, c, camera).WaitForMazeAsync(C("D1")); throw new InvalidOperationException("Moving enemy did not interrupt maze wait"); }
            catch (MapEnemyMovedException) { }
            Check(state.Rounds.Round == 2 && state.MazeWaits.Count == 2 && state.MovableScans.Count == 1 && !state.MovementInvalidated,
                "Moving enemy turn did not run its scanner before maze phase change");
        }
    }
}
