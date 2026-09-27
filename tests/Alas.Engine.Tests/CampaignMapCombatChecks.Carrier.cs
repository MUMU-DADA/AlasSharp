using System.Text.Json.Nodes;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static partial class CampaignMapCombatChecks
{
    public static async Task CarrierMovementChecksAsync(JsonArray native)
    {
        foreach (var entry in native)
        {
            var kinds = entry!["kinds"]!.AsArray().Select(x => x!.GetValue<string>() switch
            { "get_carrier" => MapEncounterKind.CarrierSpawn, "get_ammo" => MapEncounterKind.AmmoNotification,
                "get_item" => MapEncounterKind.ItemPopup, _ => throw new InvalidDataException() }).ToArray();
            var config = new CampaignConfiguration { HasAmbush = false, MysteryHasCarrier = true, EmotionMode = CampaignEmotionMode.Ignore, HasDecoyEnemy = true };
            var state = Prepare(new("C1", "SP MM --", ["A1"], [], []));
            state[new(2, 1)].IsMystery = true;
            state.RefreshFleetPaths(config);
            var trace = new List<string>();
            var camera = new Camera(state) { ObservationFactory = (_, position, mode) =>
            {
                Check(mode == MapScanMode.Carrier && state.Fleet1Location == new Cell(2, 1), "Carrier scan ran before relocation or used decoy/normal mode");
                trace.Add("scan:" + state.Fleet1Location);
                return new([new(new(3 - position.Column, 1 - position.Row), new(IsEnemy: true, EnemyScale: 1))], position, new(0, 0), mode);
            } };
            var scanner = new MapScanner(state, camera, camera.Clock);
            var move = new MapMovement(state, config, camera, () => new(camera, state, camera.InMapAsync, camera.Clock,
                new AmbushSequenceProbe(kinds), new CarrierSequenceHandler(camera)), carrierScanner: scanner);
            var result = await move.CollectMysteryAsync(new(2, 1));
            trace.Add("round");
            Check(result.Outcome == MapMoveOutcome.Committed && result.Arrival.CarriersConfirmed &&
                trace.SequenceEqual(entry["trace"]!.AsArray().Select(x => x!.GetValue<string>())) &&
                state.CarrierCount == entry["carrier"]!.GetValue<int>() && state.MysteryCount == entry["mystery"]!.GetValue<int>() &&
                state.BattleCount == entry["battle"]!.GetValue<int>() && state.FleetAmmo == entry["ammo"]!.GetValue<int>() &&
                state.Fleet1Location?.ToString() == entry["fleet"]!.GetValue<string>() &&
                state.CarrierScans.Count == (kinds[^1] == MapEncounterKind.CarrierSpawn ? 1 : 0),
                "Carrier arrival/last-mystery scan differs from actual native goto");
            if (state.CarrierScans.Count > 0)
                Check(state[new(3, 1)] is { IsEnemy: true, IsCarrier: true, EnemyScale: 1 } &&
                    state.CarrierScans[0].NewEnemies.SequenceEqual([new Cell(3, 1)]), "Carrier scan lost newly observed sea-tile enemy");
        }
        await CarrierCombatAndFailuresAsync();
        await CarrierCampaignCompositionAsync();
    }
    private static async Task CarrierCampaignCompositionAsync()
    {
        var map = new MapDefinition("D1", "SP MM -- MB", ["A1"], ["A1"],
            [new(0, Mystery: 1), new(1, Boss: 1)]);
        Host? host = null;
        host = new Host
        {
            ObservationFactory = (_, position, mode) =>
            {
                var state = host!.Camera!.State;
                var fleet = state.Fleet1Location ?? new Cell(1, 1);
                var target = mode == MapScanMode.Carrier ? new Cell(3, 1) : state.BattleCount > 0 ? new Cell(4, 1) : new Cell(2, 1);
                var observed = mode == MapScanMode.Carrier ? new CellObservation(IsEnemy: true, EnemyScale: 1) :
                    state.BattleCount > 0 ? new CellObservation(IsBoss: true) : new CellObservation(IsMystery: true);
                return new([new(new(fleet.Column - position.Column, 1 - position.Row), new(IsFleet: true, IsCurrentFleet: true)),
                    new(new(target.Column - position.Column, 1 - position.Row), observed)], position, new(0, 0), mode);
            },
            CombatFactory = (camera, config, refocus) =>
            {
                var scanner = new MapScanner(camera.State, camera, camera.Clock);
                var movement = new MapMovement(camera.State, config, camera, () => new(camera, camera.State, camera.InMapAsync, camera.Clock,
                    new CarrierCampaignProbe(camera), new CarrierSequenceHandler(camera),
                    new MapCombatRecovery(camera.State, camera, refocus).RecoverAsync), carrierScanner: scanner);
                return new(camera.State, config, movement, scanner);
            }
        };
        var rule = new MysteryTwoBattleRule(map);
        var execution = new CampaignExecution(rule, new() { MysteryHasCarrier = true, HasAmbush = false, EmotionMode = CampaignEmotionMode.Ignore },
            (state, config) => new InMapCampaignOperations(host, state, config, default, rule));
        CampaignLoopExit exit;
        try { exit = await execution.RunAsync(); }
        catch (Exception error) { throw new InvalidOperationException($"Carrier campaign: battle={execution.Context.State.BattleCount}, mystery={execution.Context.State.MysteryCount}, carrier={execution.Context.State.CarrierCount}, scans={host.Camera?.Scans}, cells={string.Join(';', execution.Context.State.Cells.Select(cell => cell.Location + ":" + cell.Encode()))}", error); }
        Check(exit == CampaignLoopExit.Ended && execution.Context.State is
            { BattleCount: 1, MysteryCount: 1, CarrierCount: 1, FleetAmmo: 4 } && execution.Context.State.CarrierScans.Count == 1 &&
            ((InMapCampaignOperations)execution.Context.Operations).StageReturn?.Combats is [{ Rank.IsWinningRank: true, Return: CombatReturn.InStage }],
            "Compiled campaign did not collect carrier mystery, scan, defeat the spawned enemy and retain separate boss settlement");
    }
    private sealed class CarrierCampaignProbe(Camera camera) : IMapEncounterProbe
    {
        private readonly Probe _combat = new(camera);
        private bool _carrier;
        public ValueTask InitializeAsync(long frameSequence, CancellationToken token) => _combat.InitializeAsync(frameSequence, token);
        public ValueTask<MapEncounterKind> InspectAsync(long frameSequence, CancellationToken token)
        {
            if (camera.Destination is { } destination && camera.State[destination].IsMystery)
            {
                var encounter = _carrier ? MapEncounterKind.None : MapEncounterKind.CarrierSpawn;
                _carrier = true;
                return ValueTask.FromResult(encounter);
            }
            return _combat.InspectAsync(frameSequence, token);
        }
    }
    private static async Task CarrierCombatAndFailuresAsync()
    {
        foreach (string failure in new[] { "none", "scan", "wait", "stale-evidence", "stage-evidence", "timeout" })
        {
            var config = new CampaignConfiguration { HasAmbush = false, MysteryHasCarrier = true, HasMaze = true,
                EmotionMode = CampaignEmotionMode.Ignore };
            var state = Prepare(new("D1", "SP MM -- MB", ["A1"], [], []));
            state[new(2, 1)].IsMystery = true; state.Rounds.Initialize(config); state.RefreshFleetPaths(config);
            var camera = new Camera(state) { Failure = failure is "scan" or "timeout" ? failure : null, StageForBoss = true,
                ObservationFactory = (_, position, mode) =>
                {
                    if (mode == MapScanMode.Carrier)
                    {
                        Check(state.Rounds.Round == 0 && state.MysteryCount == 1 && state.CarrierCount == 1,
                            "Carrier scan lost mystery accounting or ran after round advancement");
                        return new([new(new(3 - position.Column, 1 - position.Row), new(IsEnemy: true, EnemyScale: 1))], position, new(0, 0), mode);
                    }
                    return new([new(new(4 - position.Column, 1 - position.Row), new(IsBoss: true))], position, new(0, 0), mode);
                } };
            var scanner = new MapScanner(state, camera, camera.Clock);
            var move = new MapMovement(state, config, camera, () => new(camera, state, camera.InMapAsync, camera.Clock,
                new AmbushSequenceProbe([MapEncounterKind.CarrierSpawn]), new CarrierSequenceHandler(camera) { Failure = failure }), carrierScanner: scanner);
            MapMoveResult? result = null;
            Exception? error = null;
            try { result = await move.CollectMysteryAsync(new(2, 1)); }
            catch (Exception caught) { error = caught; }
            if (failure != "none")
            {
                bool committed = failure == "scan";
                Check((failure == "timeout" ? result?.Outcome == MapMoveOutcome.Unconfirmed : error is IOException or InvalidDataException) &&
                    state.CarrierCount == 1 && state.MysteryCount == (committed ? 1 : 0) &&
                    state.Fleet1Location == new Cell(committed ? 2 : 1, 1) && state.Rounds.Round == 0 &&
                    state.BattleCount == 0 && state.FleetAmmo == 5 && state.CarrierScans.Count == 0 && state.MovementInvalidated,
                    "Carrier failure lost confirmed state or invented movement/combat: " + failure);
                continue;
            }
            Check(error is null && result?.Outcome == MapMoveOutcome.Committed && state.Rounds.Round == 1, "Carrier did not commit and advance the round");
            var combatMovement = new MapMovement(state, config, camera, () => new(camera, state, camera.InMapAsync, camera.Clock,
                new Probe(camera), new Handler(camera)), carrierScanner: scanner);
            var combat = new CampaignMapCombat(state, config, combatMovement, scanner);
            Check(await combat.ClearEnemyAsync() && state.BattleCount == 1 && state.CarrierCount == 1 && state.FleetAmmo == 4 &&
                !state[new(3, 1)].IsCarrier && !state[new(3, 1)].IsCleared, "Spawned carrier could not be fought or was counted as a declared cleared enemy");
            bool ended = false;
            try { await combat.ClearBossAsync(); }
            catch (CampaignEndedException) { ended = true; }
            var described = CampaignResumeTask.Describe("carrier", "campaign_run", RuleCatalog.Create("campaign_main/campaign_1_1"),
                new(CampaignLoopExit.Ended, state.BattleCount, combat.StageReturn, CarrierEncounters: state.CarrierEncounters, CarrierScans: state.CarrierScans), true);
            Check(ended && described.Outcome == TaskOutcome.Succeeded && described.Evidence?["carrierEncounters"]?.AsArray().Count == 1 &&
                described.Evidence?["carrierScans"]?.AsArray().Count == 1, "Carrier scan/combat chain lost its independent observations or genuine synthetic boss result");
            var carrierOnly = result!.Arrival with { Outcome = MapArrivalOutcome.StageReturned };
            Check(CampaignResumeTask.Describe("carrier", "campaign_run", RuleCatalog.Create("campaign_main/campaign_1_1"),
                new(CampaignLoopExit.Ended, 0, carrierOnly), true).Outcome == TaskOutcome.Failed,
                "Carrier animation alone was promoted to sortie settlement");
            var combined = combat.StageReturn! with { HandledEncounters = [MapEncounterKind.CarrierSpawn, MapEncounterKind.Combat],
                Carriers = result.Arrival.Carriers };
            Check(CampaignResumeTask.Describe("carrier", "campaign_run", RuleCatalog.Create("campaign_main/campaign_1_1"),
                new(CampaignLoopExit.Ended, 0, combined), true).Outcome == TaskOutcome.Succeeded &&
                CampaignResumeTask.Describe("carrier", "campaign_run", RuleCatalog.Create("campaign_main/campaign_1_1"),
                new(CampaignLoopExit.Ended, 0, combined with { Carriers = [] }), true).Outcome == TaskOutcome.Failed,
                "Carrier-before-boss result accepted incomplete carrier observations or lost independent winning battle evidence");
        }
    }
    private sealed class CarrierSequenceHandler(Camera camera) : IMapEncounterHandler
    {
        public string? Failure { get; init; }
        public async ValueTask<MapEncounterHandling> HandleAsync(MapEncounterKind encounter, CancellationToken token)
        {
            if (encounter == MapEncounterKind.ItemPopup) { await camera.RefreshImageAsync(token); return new(MapEncounterContinuation.InMap); }
            if (encounter != MapEncounterKind.CarrierSpawn) return await new Handler(camera).HandleAsync(encounter, token);
            long observed = camera.FrameSequence;
            camera.State.CarrierCount++;
            if (Failure == "wait") throw new IOException("Synthetic carrier wait failure");
            await camera.RefreshImageAsync(token);
            return new(Failure == "stage-evidence" ? MapEncounterContinuation.InStage : MapEncounterContinuation.InMap,
                Carrier: new(observed, new(true, false, false, 1, Failure == "stale-evidence" ? observed : camera.FrameSequence)));
        }
    }
}
