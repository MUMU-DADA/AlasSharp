using System.Collections.Immutable;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static partial class CampaignMapCombatChecks
{
    public static async Task MechanismExecutionChecksAsync()
    {
        var map = new MapDefinition("E2", "SP -- -- ME MB\n-- -- ++ -- --", ["B1"], ["B1"],
            [new(0, Enemy: 1), new(1, Boss: 1)], mechanisms: new(landBased: [new(new(3, 2), MapDirection.Up)]));
        var rule = new MechanismRule(map);
        var host = new Host
        {
            ObservationFactory = (scan, position, mode) => new(scan == 1
                ? [new(new(0, 0), new(IsFleet: true, IsCurrentFleet: true)), new(new(3, 0), new(IsEnemy: true, EnemyScale: 1))]
                : [new(new(3, 0), new(IsFleet: true, IsCurrentFleet: true)), new(new(4, 0), new(IsBoss: true))],
                position, new(1, 0), mode)
        };
        var execution = new CampaignExecution(rule, new() { HasLandBased = true, HasAmbush = false, ClearAllThisTime = true, EmotionMode = CampaignEmotionMode.Ignore },
            (state, config) => new InMapCampaignOperations(host, state, config, default));
        Check(await execution.RunAsync() == CampaignLoopExit.Ended && host.Camera is { Taps: 3, Scans: 2 } &&
            execution.Context.State is { BattleCount: 1, FleetAmmo: 4 } state &&
            state.MechanismReleases.Count == 1 && !state.Cells.Any(cell => cell.IsMechanismBlock || cell.IsMechanismTrigger),
            "Full-clear did not retry after mechanism release then fight and return from boss in one campaign");
        var operations = (InMapCampaignOperations)execution.Context.Operations;
        var result = CampaignResumeTask.Describe("test", "campaign_run", rule,
            new(CampaignLoopExit.Ended, 1, operations.StageReturn, MechanismReleases: execution.Context.State.MechanismReleases), true);
        Check(result.Evidence?["mechanismReleases"]?.AsArray().Count == 1, "Task lost confirmed mechanism state transition");

        foreach (bool returnStage in new[] { false, true })
        {
            var occupied = Prepare(new("C1", "SP ME --", ["A1"], [], []));
            var trigger = occupied[new(2, 1)]; var block = occupied[new(3, 1)];
            trigger.IsMechanismTrigger = trigger.IsEnemy = true; trigger.MechanismTrigger = [trigger]; trigger.MechanismBlock = [block];
            block.IsMechanismBlock = true;
            var config = new CampaignConfiguration { HasLandBased = true, EmotionMode = CampaignEmotionMode.Ignore };
            occupied.RefreshFleetPaths(config);
            var camera = new Camera(occupied) { ReturnStage = returnStage };
            var combat = Create(occupied, config, camera);
            try { await combat.ClearMechanismAsync(); throw new InvalidOperationException("Mechanism did not trigger campaign retry"); }
            catch (MapEnemyMovedException) when (!returnStage) { }
            catch (CampaignEndedException) when (returnStage) { }
            Check(occupied.BattleCount == (returnStage ? 0 : 1) && block.IsMechanismBlock == returnStage &&
                (combat.StageReturn is not null) == returnStage, "Occupied trigger lost battle/return semantics or unlocked before landing");
        }
        {
            var occupied = Prepare(new("C1", "SP MM --", ["A1"], [], []));
            var trigger = occupied[new(2, 1)]; var block = occupied[new(3, 1)];
            trigger.IsMechanismTrigger = trigger.IsMystery = true; trigger.MechanismTrigger = [trigger]; trigger.MechanismBlock = [block];
            block.IsMechanismBlock = true;
            var config = new CampaignConfiguration { HasLandBased = true, EmotionMode = CampaignEmotionMode.Ignore };
            occupied.RefreshFleetPaths(config);
            var camera = new Camera(occupied); var combat = Create(occupied, config, camera);
            try { await combat.ClearMechanismAsync(); throw new InvalidOperationException("Mystery trigger did not retry"); }
            catch (MapEnemyMovedException) { }
            Check(occupied.BattleCount == 0 && occupied.MysteryCount == 1 && !block.IsMechanismBlock && camera.Taps == 1,
                "Mystery on mechanism lost reward accounting or became a battle");
        }
    }
    private sealed class MechanismRule(MapDefinition map) : CampaignRule
    {
        public override string Id => "test/mechanism";
        public override MapDefinition Map => map;
        public override ImmutableArray<SourceFile> Sources => [];
        protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; } = new Dictionary<int, BattleHook>();
    }
}
