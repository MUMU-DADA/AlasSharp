using System.Collections.Immutable;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static partial class CampaignMapCombatChecks
{
    public static async Task MovableExecutionChecksAsync()
    {
        // Two fleet-step waypoints trigger a moving round before the original enemy.
        // The actual campaign loop must select again from the newly scanned location.
        var map = new MapDefinition("G2", "SP -- -- -- -- -- MB\n-- -- -- -- -- MS --", ["B1"], ["B1"],
            [new(0, Siren: 1), new(1, Boss: 1)]);
        var host = new Host
        {
            ObservationFactory = (scan, position, mode) => new(scan == 1
                ? [new(new(1 - position.Column, 1 - position.Row), new(IsFleet: true, IsCurrentFleet: true)),
                   new(new(6 - position.Column, 2 - position.Row), new(IsSiren: true, EnemyGenre: "Siren_DD"))]
                : mode == MapScanMode.Movable
                ? [new(new(6 - position.Column, 1 - position.Row), new(IsSiren: true, EnemyGenre: "Siren_DD"))]
                : [new(new(7 - position.Column, 1 - position.Row), new(IsBoss: true))], position, new(0, 0), mode)
        };
        var rule = new MovableRule(map);
        var execution = new CampaignExecution(rule, new() { HasMovableEnemy = true, HasSiren = true,
            HasFleetStep = true, Fleet1Step = 2, HasAmbush = false, ClearAllThisTime = true, EmotionMode = CampaignEmotionMode.Ignore },
            (state, config) => new InMapCampaignOperations(host, state, config, default));
        var exit = await execution.RunAsync();
        Check(exit == CampaignLoopExit.Ended && execution.Context.State is { BattleCount: 1, SirenCount: 1 } state &&
            state.MovableScans.Count == 1 && state.Rounds.Round == 4 && host.Camera!.Taps == 5 && state.MovementInvalidated,
            $"Campaign movement differs: {exit}, battles={execution.Context.State.BattleCount}, sirens={execution.Context.State.SirenCount}, " +
            $"scans={execution.Context.State.MovableScans.Count}, rounds={execution.Context.State.Rounds.Round}, taps={host.Camera!.Taps}");
        var operations = (InMapCampaignOperations)execution.Context.Operations;
        var evidence = CampaignResumeTask.Describe("test", "campaign_run", rule,
            new(CampaignLoopExit.Ended, 1, operations.StageReturn, MovableScans: execution.Context.State.MovableScans), true);
        Check(evidence.Evidence?["movableScans"]?.AsArray().Count == 1, "Task omitted dynamic scan evidence");
    }
    private sealed class MovableRule(MapDefinition map) : CampaignRule
    {
        public override string Id => "test/movable";
        public override MapDefinition Map => map;
        public override ImmutableArray<SourceFile> Sources => [];
        protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; } = new Dictionary<int, BattleHook>();
    }
}
