using System.Text.Json.Nodes;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static partial class CampaignMapCombatChecks
{
    public static async Task AmbushNativeWalksAsync(JsonArray cases)
    {
        foreach (var expected in cases)
        {
            var sample = expected!["sample"]!;
            bool target = sample["target"]!.GetValue<bool>(), overlay = sample["overlay"]!.GetValue<bool>(),
                fought = sample["fought"]!.GetValue<bool>(), retry = sample["retry"]!.GetValue<bool>(), partial = sample["partial"]!.GetValue<bool>();
            var state = Prepare(new("B1", "SP ME", ["A1"], ["A1"], []));
            state[new(2, 1)].IsEnemy = target;
            Camera? camera = null;
            camera = new(state) { MarkerAtFrame = _ => new(!retry || camera!.Taps > 1, !partial && (!retry || camera!.Taps > 1)) };
            var sequence = target ? new[] { MapEncounterKind.Ambush, MapEncounterKind.Combat } : [MapEncounterKind.Ambush];
            var handler = new AmbushSequenceHandler(camera, overlay, fought, false);
            var movement = new MapMovement(state, new(), camera, () => new(camera, state, camera.InMapAsync, camera.Clock,
                new AmbushSequenceProbe(sequence), handler));
            var result = target ? await movement.FightAsync(new(2, 1)) : await movement.MoveAsync(new(2, 1));
            Check(result.Outcome == MapMoveOutcome.Committed && state.BattleCount == expected["battle"]!.GetValue<int>() &&
                state.SirenCount == expected["siren"]!.GetValue<int>() && state.FleetAmmo == expected["ammo"]!.GetValue<int>() &&
                state.Fleet1Location?.ToString() == expected["fleet"]!.GetValue<string>() && camera.Taps == expected["taps"]!.GetValue<int>() &&
                result.Arrival.Ambushes.Count(a => a.FleetStatusRefreshed) + result.Arrival.Combats.Length == expected["hp"]!.GetValue<int>() &&
                expected["hp"]!.GetValue<int>() == expected["lv"]!.GetValue<int>(), "Ambush native _goto state differs: " + sample);
        }
    }
    public static async Task AmbushMovementChecksAsync()
    {
        int cases = 0;
        foreach (bool overlay in new[] { false, true })
        foreach (bool retry in new[] { false, true })
        foreach (bool fought in new[] { false, true })
        foreach (int ambushCount in new[] { 1, 2 })
        foreach (string target in new[] { "empty", "combat", "stage" })
        {
            var config = new CampaignConfiguration { HasAmbush = true };
            var state = Prepare(new("B1", "SP ME", ["A1"], ["A1"], []));
            state[new(2, 1)].IsEnemy = target != "empty";
            Camera? camera = null;
            camera = new(state) { MarkerAtFrame = _ => new(!retry || camera!.Taps > 1, !retry || camera!.Taps > 1) };
            var sequence = Enumerable.Repeat(MapEncounterKind.Ambush, ambushCount).Concat(
                target == "empty" ? [] : new[] { MapEncounterKind.Combat });
            var probe = new AmbushSequenceProbe(sequence);
            var handler = new AmbushSequenceHandler(camera, overlay, fought, target == "stage");
            int recoveries = 0;
            var movement = new MapMovement(state, config, camera, () => new(camera, state, camera.InMapAsync, camera.Clock,
                probe, handler, async token => { recoveries++; await camera.RelocalizeAsync(token); }));
            var result = target == "empty" ? await movement.MoveAsync(new(2, 1)) : await movement.FightAsync(new(2, 1));
            bool canArrive = !retry || overlay;
            var expected = target == "stage" ? MapMoveOutcome.StageReturned : canArrive ? MapMoveOutcome.Committed : MapMoveOutcome.Unconfirmed;
            Check(result.Outcome == expected && result.Arrival.AmbushesConfirmed && result.Arrival.Ambushes.Length == ambushCount &&
                state.AmbushEncounters.Count == ambushCount && state.BattleCount == (target == "combat" && canArrive ? 1 : 0) &&
                state.FleetAmmo == 5 - state.BattleCount && state.SirenCount == 0 &&
                recoveries == (target == "combat" ? 1 : 0), "Ambush altered map battle accounting, recovery, or evidence");
            Check(camera.Taps == 1 + (retry && overlay && target != "stage" ? 1 : 0), "Ambush retry ignored overlay/arrival gate");
            if (target == "stage")
            {
                var task = CampaignResumeTask.Describe("test", "campaign_run", RuleCatalog.Create("campaign_main/campaign_1_1"),
                    new(CampaignLoopExit.Ended, 0, result.Arrival, AmbushEncounters: state.AmbushEncounters), true);
                Check(task.Outcome == TaskOutcome.Succeeded && task.Evidence!["ambushEncounters"]!.AsArray().Count == ambushCount,
                    "Actual boss settlement after ambush lost its independent evidence");
                var missing = result.Arrival with { Ambushes = [] };
                Check(CampaignResumeTask.Describe("test", "campaign_run", RuleCatalog.Create("campaign_main/campaign_1_1"),
                    new(CampaignLoopExit.Ended, 0, missing), true).Outcome == TaskOutcome.Failed,
                    "Unaccounted ambush allowed a cleared claim");
            }
            cases++;
        }
        foreach (string failure in new[] { "stage", "loss", "rank", "late_timeout" })
        {
            var state = Prepare(new("B1", "SP ME", ["A1"], ["A1"], []));
            state[new(2, 1)].IsEnemy = true;
            var camera = new Camera(state) { Failure = failure == "late_timeout" ? "timeout" : null };
            var handler = new AmbushSequenceHandler(camera, false, true, false) { Failure = failure };
            var movement = new MapMovement(state, new(), camera, () => new(camera, state, camera.InMapAsync, camera.Clock,
                new AmbushSequenceProbe([MapEncounterKind.Ambush]), handler));
            var result = await movement.FightAsync(new(2, 1));
            Check(result.Outcome is MapMoveOutcome.UnsupportedEncounter or MapMoveOutcome.Interrupted or MapMoveOutcome.Unconfirmed &&
                state.BattleCount == 0 && state.FleetAmmo == 5 && state.Fleet1Location == new Cell(1, 1) &&
                state.MovementInvalidated && state.AmbushEncounters.Count == 1,
                "Failed ambush changed authoritative state or lost observed evidence: " + failure);
            var task = CampaignResumeTask.Describe("test", "campaign_run", RuleCatalog.Create("campaign_main/campaign_1_1"),
                new(CampaignLoopExit.Ended, 0, result.Arrival, AmbushEncounters: state.AmbushEncounters), true);
            Check(task.Outcome == TaskOutcome.Failed && task.Evidence!["cleared"]!.GetValue<bool>() == false,
                "Ambush-only stage return became a clear");
        }
        {
            var state = Prepare(new("B1", "SP --", ["A1"], ["A1"], []));
            var camera = new Camera(state) { MarkerAtFrame = _ => new(true, false) };
            var movement = new MapMovement(state, new(), camera, () => new(camera, state, camera.InMapAsync, camera.Clock,
                new AmbushSequenceProbe([MapEncounterKind.Ambush]), new AmbushSequenceHandler(camera, true, false, false)));
            var result = await movement.MoveAsync(new(2, 1));
            Check(result.Outcome == MapMoveOutcome.Committed && camera.Taps == 1,
                "Ambush retry preempted native fleet-marker arrival confirmation");
        }
        Console.WriteLine($"Ambush movement: {cases} synthetic combinations plus loss/missing-rank/ambush-stage/late-timeout boundaries passed.");
    }
    private sealed class AmbushSequenceProbe(IEnumerable<MapEncounterKind> encounters) : IMapEncounterProbe
    {
        private readonly Queue<MapEncounterKind> _pending = new(encounters);
        public ValueTask InitializeAsync(long frameSequence, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<MapEncounterKind> InspectAsync(long frameSequence, CancellationToken token)
            => ValueTask.FromResult(_pending.TryDequeue(out var encounter) ? encounter : MapEncounterKind.None);
    }
    private sealed class AmbushSequenceHandler(Camera camera, bool overlay, bool fought, bool targetStage) : IMapEncounterHandler
    {
        public string? Failure { get; init; }
        public async ValueTask<MapEncounterHandling> HandleAsync(MapEncounterKind encounter, CancellationToken token)
        {
            long start = camera.FrameSequence;
            await camera.RefreshImageAsync(token);
            bool stage = encounter == MapEncounterKind.Combat ? targetStage : Failure == "stage";
            CombatRankEvidence? rank = Failure == "rank" ? null : Failure == "loss" ?
                new(CombatRank.C, CombatRankSource.BattleStatus, UiAssets.Combat.BATTLE_STATUS_C.Id) :
                new(CombatRank.S, CombatRankSource.BattleStatus, UiAssets.Combat.BATTLE_STATUS_S.Id);
            var combat = new CombatFlowResult(stage ? CombatReturn.InStage : CombatReturn.InMap, rank, false, false, 1);
            if (encounter == MapEncounterKind.Combat) return new(stage ? MapEncounterContinuation.InStage : MapEncounterContinuation.InMap, combat);
            return new(stage ? MapEncounterContinuation.InStage : MapEncounterContinuation.InMap, Ambush:
                new(true, fought ? AmbushMessage.Failed : AmbushMessage.Evaded, start, camera.FrameSequence, 1,
                    fought ? combat : null, overlay && !stage && (rank?.IsWinningRank ?? false)));
        }
    }
}
