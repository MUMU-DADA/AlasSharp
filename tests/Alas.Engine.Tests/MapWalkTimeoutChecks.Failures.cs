using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static partial class MapWalkTimeoutChecks
{
    private static async Task FailuresAsync()
    {
        foreach (bool combat in new[] { false, true })
        {
            var state = new CampaignState(new MapDefinition("B1", "SP " + (combat ? "ME" : "MM"), [], [], []));
            state.InitializeMapData(new()); state.Fleet1Location = new(1, 1); state.RefreshFleetPaths(new());
            state[new(2, 1)].IsEnemy = combat; state[new(2, 1)].IsMystery = !combat;
            var camera = new Replay(new(1, "fleet", false, "", 1, 1));
            var prelude = new Prelude(combat);
            var arrival = new MapArrivalCheck(camera, state, _ => ValueTask.FromResult(true), camera.Clock,
                prelude, prelude, recoverAfterCombat: camera.RecoverCombatAsync, recoverAfterWalkTimeout: camera.RecoverAsync);
            var move = new MapMovement(state, new(), camera, () => arrival);
            var result = combat ? await move.FightAsync(new(2, 1)) : await move.CollectMysteryAsync(new(2, 1));
            Check(result.Outcome == MapMoveOutcome.Committed && state.Fleet1Location == new Cell(2, 1) &&
                state.BattleCount == (combat ? 1 : 0) && state.MysteryCount == (combat ? 0 : 1) && state.FleetAmmo == (combat ? 4 : 5) &&
                result.Arrival.WalkTimeouts is [{ RetapCompleted: true }], "Retap lost or duplicated previously confirmed battle/ammo evidence");
        }
        using (var cancellation = new CancellationTokenSource())
        {
            var state = new CampaignState(new MapDefinition("B1", "SP --", [], [], []));
            state.InitializeMapData(new()); state.Fleet1Location = new(1, 1); state.RefreshFleetPaths(new());
            var camera = new Replay(new(1, "fleet", false, "", 10, 1));
            camera.OnRecovery = _ => { if (camera.RecoveryFrames.Count == 1) cancellation.Cancel(); return ValueTask.CompletedTask; };
            var arrival = new MapArrivalCheck(camera, state, _ => ValueTask.FromResult(true), camera.Clock, camera,
                recoverAfterWalkTimeout: camera.RecoverAsync);
            bool stopped = false;
            try { await new MapMovement(state, new(), camera, () => arrival).MoveAsync(new(2, 1), token: cancellation.Token); }
            catch (OperationCanceledException) { stopped = true; }
            Check(stopped && camera.Taps.Count == 2 && state.MovementInvalidated && state.Fleet1Location == new Cell(1, 1) &&
                arrival.WalkTimeouts.Count == 2 && !arrival.WalkTimeouts[^1].RetapCompleted,
                "Repeated walk retries ignored outer cancellation or fabricated arrival");
        }
        foreach (string failure in new[] { "stale", "recovery", "cancel", "retap", "capture" })
        {
            var state = new CampaignState(new MapDefinition("B1", "SP --", [], [], []));
            state.InitializeMapData(new()); state.Fleet1Location = new(1, 1); state.RefreshFleetPaths(new());
            var camera = new Replay(new(1, "fleet", false, "", 1, 1)) { Failure = failure };
            var arrival = new MapArrivalCheck(camera, state, _ => ValueTask.FromResult(true), camera.Clock,
                camera, recoverAfterWalkTimeout: camera.RecoverAsync);
            var move = new MapMovement(state, new(), camera, () => arrival);
            bool rejected = false;
            try
            {
                var result = await move.MoveAsync(new(2, 1), new(TimeSpan.Zero, TimeSpan.FromMilliseconds(200)));
                rejected = result.Outcome == MapMoveOutcome.Unconfirmed;
            }
            catch (Exception error) when (error is InvalidDataException or IOException or OperationCanceledException) { rejected = true; }
            Check(rejected && state.MovementInvalidated && camera.Invalidated && camera.Taps.Count == 1 &&
                state.Fleet1Location == new Cell(1, 1), "Timeout failure continued or committed: " + failure);
            if (failure == "capture") Check(arrival.WalkTimeouts.Count == 0 && camera.RecoveryFrames.Count == 0,
                "Blocked capture became a recoverable walking timeout");
            else Check(arrival.WalkTimeouts.Count == 1 && !arrival.WalkTimeouts[0].RetapCompleted,
                "Failure lost recovery phase or claimed a completed retap");
            foreach (var record in arrival.WalkTimeouts) RunReport.ValidateWalkTimeout(record);
        }
        var valid = new WalkTimeoutEvidence(1, new(2, 1), 10, 11, 11, true);
        foreach (var corrupt in new[] { valid with { Fleet = 3 }, valid with { Target = new(0, 1) }, valid with { ObservedFrame = 0 },
            valid with { RecoveredFrame = 10 }, valid with { RecoveredFrame = null }, valid with { RetapFrame = 10 }, valid with { RetapFrame = null } })
        {
            bool rejected = false;
            try { RunReport.ValidateWalkTimeout(corrupt); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Inconsistent timeout evidence passed report validation");
        }
        // A marker after the timeout may confirm movement, never manufacture the expected battle/mystery.
        foreach (bool mystery in new[] { false, true })
        {
            var state = new CampaignState(new MapDefinition("B1", "SP " + (mystery ? "MM" : "ME"), [], [], []));
            state.InitializeMapData(new()); state.Fleet1Location = new(1, 1); state.RefreshFleetPaths(new());
            state[new(2, 1)].IsEnemy = !mystery; state[new(2, 1)].IsMystery = mystery;
            var camera = new Replay(new(1, "current", false, mystery ? "mystery" : "combat", 0, 1));
            var arrival = new MapArrivalCheck(camera, state, _ => ValueTask.FromResult(true), camera.Clock,
                camera, recoverAfterWalkTimeout: camera.RecoverAsync);
            var move = new MapMovement(state, new(), camera, () => arrival);
            var result = mystery ? await move.CollectMysteryAsync(new(2, 1)) : await move.FightAsync(new(2, 1));
            Check(result.Outcome == MapMoveOutcome.UnsupportedEncounter && state.Fleet1Location == new Cell(1, 1) &&
                state.BattleCount == 0 && state.MysteryCount == 0 && state.FleetAmmo == 5 && state.MovementInvalidated,
                "Timeout current-marker confirmation fabricated expected interaction success");
        }
    }

    private sealed class Prelude(bool combat) : IMapEncounterProbe, IMapEncounterHandler
    {
        private bool _seen;
        public ValueTask InitializeAsync(long frameSequence, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<MapEncounterKind> InspectAsync(long frameSequence, CancellationToken token)
        {
            bool first = !_seen; _seen = true;
            return ValueTask.FromResult(!first ? MapEncounterKind.None : combat ? MapEncounterKind.Combat : MapEncounterKind.AmmoNotification);
        }
        public ValueTask<MapEncounterHandling> HandleAsync(MapEncounterKind encounter, CancellationToken token)
            => ValueTask.FromResult(new MapEncounterHandling(MapEncounterContinuation.InMap,
                new(CombatReturn.InMap, new(CombatRank.S, CombatRankSource.BattleStatus, UiAssets.Combat.BATTLE_STATUS_S.Id), false, false, 1)));
    }
}
