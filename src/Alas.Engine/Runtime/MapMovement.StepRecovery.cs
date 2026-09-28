using System.Collections.Immutable;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed record WalkRecoveryEvidence(int Fleet, Cell Origin, Cell Target, string Phase, MapArrivalResult Interruption,
    ImmutableArray<Cell> Steps = default, int CompletedSteps = 0, long? RecoveredFrame = null);

public sealed class MapWalkException(int evidenceIndex, MapCombatExpectation expectation)
    : Exception("Map walk exceeded the available fleet steps")
{
    internal int EvidenceIndex { get; } = evidenceIndex;
    internal MapCombatExpectation Expectation { get; } = expectation;
}

public sealed partial class MapMovement
{
    private readonly List<WalkRecoveryEvidence> _walkRecoveries = [];
    public IReadOnlyList<WalkRecoveryEvidence> WalkRecoveries => _walkRecoveries.AsReadOnly();

    private int RecordWalkInterruption(Cell origin, Cell target, MapArrivalResult result)
    {
        int index = _walkRecoveries.Count;
        _walkRecoveries.Add(new(state.FleetIndex, origin, target, "observed", result, []));
        return index;
    }

    private async ValueTask<MapMoveResult> MoveWithRecoveryAsync(Cell destination, MapAction action,
        MapArrivalOptions? options, CancellationToken token, MapCombatExpectation? expectation = null)
    {
        MapWalkException interruption;
        try { return await MoveCoreAsync(destination, action, options, token, expectation: expectation); }
        catch (MapWalkException error) { interruption = error; }
        int index = interruption.EvidenceIndex;
        void Save(WalkRecoveryEvidence value) => _walkRecoveries[index] = value;
        try
        {
            if (!configuration.HasFleetStep || recoverWalk is null || state.MovementInvalidated)
                throw new NotSupportedException("Walk-step recovery requires the native step configuration and camera recovery", interruption);
            Save(_walkRecoveries[index] with { Phase = "recovering" });
            await recoverWalk(token);
            if (camera.FrameSequence <= _walkRecoveries[index].Interruption.FrameSequence)
                throw new InvalidDataException("Walk recovery reused the interrupted frame");
            var route = state.Paths.FindRoute(destination, step: 1, turningOptimize: false);
            if (!route.IsReachable || route.Waypoints.Count == 0)
                throw new CampaignScriptException("Walk-step recovery has no reachable single-step route");
            Save(_walkRecoveries[index] with { Phase = "walking", Steps = route.Waypoints.ToImmutableArray(), RecoveredFrame = camera.FrameSequence });
            var rawOptions = (options ?? MapArrivalOptions.Default) with
            {
                ExpectCombat = interruption.Expectation != MapCombatExpectation.None,
                ExpectMystery = action == MapAction.Mystery && expectation is null
            };
            MapMoveResult? result = null;
            for (int step = 0; step < route.Waypoints.Count; step++)
            {
                token.ThrowIfCancellationRequested();
                // Native recovery calls raw _goto: no recursive maze wait or repeated MapWalkError recovery.
                // Its original expectation also applies to intermediate recovery nodes.
                bool last = step == route.Waypoints.Count - 1;
                var prior = _walkRecoveries[index].Interruption;
                bool interactionAlreadyConfirmed = action == MapAction.Fight && !prior.Combats.IsEmpty ||
                    action == MapAction.Mystery && (prior.HandledEncounters.Contains(MapEncounterKind.ItemPopup) ||
                        !prior.AmmoNotificationFrames.IsEmpty || !prior.Carriers.IsEmpty);
                try
                {
                    result = await MoveCoreAsync(route.Waypoints[step], last && !interactionAlreadyConfirmed ? action : MapAction.Visit, rawOptions, token,
                        expectation: interruption.Expectation);
                }
                catch (MapEnemyMovedException)
                {
                    Save(_walkRecoveries[index] with { Phase = "redispatched", CompletedSteps = step + 1 });
                    throw;
                }
                if (result.Outcome != MapMoveOutcome.Committed) return result;
                Save(_walkRecoveries[index] with { CompletedSteps = step + 1 });
            }
            Save(_walkRecoveries[index] with { Phase = "completed" });
            return result!;
        }
        catch (MapEnemyMovedException) { throw; }
        catch { Invalidate(); throw; }
    }

    private void CommitInterruptedInteractions(CellState target, MapArrivalResult result,
        MapCombatExpectation expectation, bool permitsCombat, bool permitsMystery)
    {
        if (!result.AmbushesConfirmed || !result.CarriersConfirmed ||
            result.Combats.Length != result.HandledEncounters.Count(kind => kind == MapEncounterKind.Combat) ||
            result.Combats.Any(combat => combat is not { Return: CombatReturn.InMap, Rank.IsWinningRank: true }) ||
            !permitsCombat && !result.Combats.IsEmpty ||
            !permitsMystery && state.Rule?.CountMysteryItems != false && result.HandledEncounters.Contains(MapEncounterKind.ItemPopup))
            throw new InvalidDataException("Walk interruption contains unconfirmed interactions");
        bool siren = expectation == MapCombatExpectation.None ? configuration.HasMovableEnemy : expectation == MapCombatExpectation.Siren;
        foreach (var combat in result.Combats)
        {
            state.CommitBattle(siren);
            if (!siren && target.MayEnemy) target.IsCleared = true;
        }
        state.MysteryCount = checked(state.MysteryCount + result.AmmoNotificationFrames.Length + result.Carriers.Length +
            (state.Rule?.CountMysteryItems != false ? result.HandledEncounters.Count(kind => kind == MapEncounterKind.ItemPopup) : 0));
        // A rejected move does not advance the round, move either fleet, wipe a cell or replenish supply.
    }
}
