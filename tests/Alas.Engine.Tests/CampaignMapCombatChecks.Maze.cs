using System.Collections.Immutable;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static partial class CampaignMapCombatChecks
{
    public static async Task MazeExecutionChecksAsync()
    {
        var map = new MapDefinition("H1", "SP -- -- -- -- -- ME MB", ["B1"], ["B1"],
            [new(0, Enemy: 1), new(1, Boss: 1)], mechanisms: new(mazes: [[new(4, 1)], [new(1, 1)], [new(8, 1)]]));
        var host = new Host
        {
            ObservationFactory = (scan, position, mode) => new(scan == 1
                ? [new(new(1 - position.Column, 0), new(IsFleet: true, IsCurrentFleet: true)),
                   new(new(7 - position.Column, 0), new(IsEnemy: true, EnemyScale: 1))]
                : [new(new(8 - position.Column, 0), new(IsBoss: true))], position, new(0, 0), mode)
        };
        var rule = new MazeRule(map);
        var execution = new CampaignExecution(rule, new() { HasMaze = true, HasAmbush = false,
            ClearAllThisTime = true, EmotionMode = CampaignEmotionMode.Ignore },
            (state, config) => new InMapCampaignOperations(host, state, config, default));
        var exit = await execution.RunAsync(); var state = execution.Context.State;
        Check(exit == CampaignLoopExit.Ended && state.BattleCount == 1 && state.Rounds.Round == 5 && state.MazeWaits.Count == 3 &&
            host.Camera!.Taps == 6, $"Maze campaign differs: {exit}, battle={state.BattleCount}, round={state.Rounds.Round}, waits={state.MazeWaits.Count}, taps={host.Camera!.Taps}");
        var operations = (InMapCampaignOperations)execution.Context.Operations;
        var described = CampaignResumeTask.Describe("test", "campaign_run", rule,
            new(exit, state.BattleCount, operations.StageReturn, MazeWaits: state.MazeWaits), true);
        Check(described.Evidence?["mazeWaits"]?.AsArray().Count == 3, "Campaign result lost confirmed maze wait evidence");

        foreach (bool movable in new[] { false, true })
        foreach (bool stage in new[] { false, true })
        {
            var occupied = new CampaignState(new("G1", "-- ME ME -- ME ME --", ["D1"], [], [new(0, Enemy: 4)],
                mechanisms: new(mazes: [[new(4, 1)], [new(1, 1)], [new(7, 1)]])));
            occupied.InitializeMapData(new(Maze: true)); occupied.Fleet1Location = new(1, 1); occupied[new(1, 1)].IsFleet = true;
            foreach (var cell in occupied[new(4, 1)].MazeNearby!) cell.IsEnemy = true;
            var c = new CampaignConfiguration { HasMaze = true, HasMovableEnemy = movable, HasAmbush = false, EmotionMode = CampaignEmotionMode.Ignore };
            occupied.Rounds.Initialize(c); occupied.RefreshFleetPaths(c);
            var camera = new Camera(occupied) { ReturnStage = stage };
            var move = new MapMovement(occupied, c, camera, () => new(camera, occupied, camera.InMapAsync, camera.Clock,
                new Probe(camera), new Handler(camera)), movableScan: new(occupied, c, new(occupied, camera, camera.Clock)));
            bool returned = false;
            try { returned = (await move.WaitForMazeAsync(new(4, 1)))?.Outcome == MapMoveOutcome.StageReturned; }
            catch (MapEnemyMovedException) when (!stage) { }
            Check(returned == stage && occupied.BattleCount == (stage ? 0 : 2) && occupied.SirenCount == (!stage && movable ? 2 : 0) &&
                occupied.MazeWaits.Count == (stage ? 0 : 3), "Occupied maze detour lost native unplanned combat counters or stage return");
            if (!stage) Check(occupied[new(2, 1)].IsCleared == !movable, "Unplanned siren attribution also marked normal enemy cleared");
        }
        {
            var mystery = new CampaignState(new("G1", "-- MM -- -- -- -- --", ["D1"], [], [new(0, Mystery: 1)],
                mechanisms: new(mazes: [[new(4, 1)], [new(1, 1)], [new(7, 1)]])));
            mystery.InitializeMapData(new(Maze: true)); mystery.Fleet1Location = new(1, 1); mystery[new(1, 1)].IsFleet = true;
            mystery[new(2, 1)].IsMystery = true;
            var c = new CampaignConfiguration { HasMaze = true, HasAmbush = false };
            mystery.Rounds.Initialize(c); mystery.RefreshFleetPaths(c);
            var camera = new Camera(mystery);
            var move = new MapMovement(mystery, c, camera, () => new(camera, mystery, camera.InMapAsync, camera.Clock,
                new Probe(camera), new Handler(camera)));
            try { await move.WaitForMazeAsync(new(4, 1)); throw new InvalidOperationException("Mystery detour did not redispatch"); }
            catch (MapEnemyMovedException) { }
            Check(mystery.MysteryCount == 1 && mystery.BattleCount == 0 && mystery.MazeWaits.Count == 3 && camera.Taps == 3,
                "Maze mystery detour lost its item confirmation or became a battle");
        }
    }
    private sealed class MazeRule(MapDefinition map) : CampaignRule
    {
        public override string Id => "test/maze";
        public override MapDefinition Map => map;
        public override ImmutableArray<SourceFile> Sources => [];
        protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; } = new Dictionary<int, BattleHook>();
    }
}
