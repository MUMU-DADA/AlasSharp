using System.Security.Cryptography;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static partial class CampaignMapCombatChecks
{
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    public static async Task RunAsync(string python, string upstream)
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(upstream, CampaignMapCombat.Source.Path));
        Check(Convert.ToHexStringLower(SHA256.HashData(bytes)) == CampaignMapCombat.Source.Sha256,
            "Native map action source drifted");

        var map = new MapDefinition("C2", "SP -- --\n-- -- ME", ["B1"], [],
            [new SpawnWave(0, Enemy: 1)], walls:
            [new MapEdge(new Cell(1, 1), new Cell(1, 2)), new MapEdge(new Cell(2, 1), new Cell(2, 2))]);
        var state = Prepare(map, walls: true);
        state[new(3, 2)].IsEnemy = true;
        state.Paths.ComputeFleetCosts([new(1, state.Fleet1Location)], state.Fleet1Location!.Value, true);
        var camera = new Camera(state);
        var combat = Create(state, new() { EmotionMode = CampaignEmotionMode.Ignore }, camera);
        Check(await combat.ClearEnemyAsync() && camera.Taps == 2 && camera.Scans == 1 &&
            state.BattleCount == 1 && state.AmmoCount == 3 && state.FleetAmmo == 4 && state.Fleet1Location == new Cell(3, 2) &&
            !state[new(3, 2)].IsEnemy && combat.StageReturn is null,
            "Enemy clear did not follow route nodes, commit one battle, then scan");
        Check(!await combat.ClearEnemyAsync() && camera.Taps == 2,
            "No observed enemy caused another grid action");

        map = new MapDefinition("C1", "SP MM MM", ["B1"], [],
            [new SpawnWave(0, Mystery: 2)]);
        state = Prepare(map);
        state[new(2, 1)].IsMystery = state[new(3, 1)].IsMystery = true;
        state.Paths.ComputeFleetCosts([new(1, state.Fleet1Location)], state.Fleet1Location!.Value, true);
        camera = new Camera(state);
        combat = Create(state, new() { EmotionMode = CampaignEmotionMode.Ignore }, camera);
        Check(!await combat.ClearMysteriesAsync() && camera.Taps == 2 && state.MysteryCount == 2 &&
            state.Fleet1Location == new Cell(3, 1) && !state.Cells.Any(grid => grid.IsMystery) &&
            state.BattleCount == 0 && state.AmmoCount == 3 && state.FleetAmmo == 5 && !await combat.ClearMysteriesAsync(),
            "Accessible mysteries were not picked up in cost order without changing battle state");

        map = new MapDefinition("B2", "SP ME\nME --", ["A1"], [], [new SpawnWave(0, Enemy: 2)]);
        foreach (var (priority, target) in new[]
                 {
                     (EnemyScalePriority.StrongestFirst, new Cell(2, 1)),
                     (EnemyScalePriority.WeakestFirst, new Cell(1, 2))
                 })
        {
            state = Prepare(map);
            state[new(2, 1)].IsEnemy = state[new(1, 2)].IsEnemy = true;
            state[new(2, 1)].EnemyScale = 3;
            state[new(1, 2)].EnemyScale = 1;
            state.Paths.ComputeFleetCosts([new(1, state.Fleet1Location)], state.Fleet1Location!.Value, true);
            camera = new Camera(state);
            combat = Create(state, new() { EnemyPriority = priority, EmotionMode = CampaignEmotionMode.Ignore }, camera);
            Check(await combat.ClearEnemyAsync() && state.Fleet1Location == target && camera.Taps == 1,
                "Enemy-scale priority did not select the upstream scale group");
        }

        map = new MapDefinition("B1", "SP MB", ["A1"], [], [new SpawnWave(0, Boss: 1)]);
        foreach (var rank in new CombatRank?[] { CombatRank.S, CombatRank.C, null })
        {
            state = Prepare(map);
            state[new(2, 1)].IsBoss = true;
            state.Paths.ComputeFleetCosts([new(1, state.Fleet1Location)], state.Fleet1Location!.Value, true);
            camera = new Camera(state) { ReturnStage = true, Rank = rank };
            combat = Create(state, new() { EmotionMode = CampaignEmotionMode.Ignore }, camera);
            bool ended = false, rejected = false;
            try { await combat.ClearBossAsync(); }
            catch (CampaignEndedException) { ended = true; }
            catch (CampaignScriptException) { rejected = true; }
            Check((rank == CombatRank.S ? ended && combat.StageReturn?.Combats.Length == 1 :
                rejected && combat.StageReturn is null) && state.BattleCount == 0 &&
                state.Fleet1Location == new Cell(1, 1) && state[new(2, 1)].IsBoss,
                "Stage return was promoted without a winning rank or committed map state");
        }
        state = Prepare(map);
        state[new(2, 1)].IsBoss = true;
        state.Paths.ComputeFleetCosts([new(1, state.Fleet1Location)], state.Fleet1Location!.Value, true);
        camera = new Camera(state);
        combat = Create(state, new() { EmotionMode = CampaignEmotionMode.Ignore }, camera);
        Check(!await combat.ClearBossAsync() && combat.StageReturn is null && state.BattleCount == 1 &&
            camera.Scans == 1 && state.Fleet1Location == new Cell(2, 1),
            "Boss return to map did not continue the upstream potential-spawn search");

        state = Prepare(map);
        state.Paths.ComputeFleetCosts([new(1, state.Fleet1Location)], state.Fleet1Location!.Value, true);
        camera = new Camera(state);
        combat = Create(state, new() { EmotionMode = CampaignEmotionMode.Ignore }, camera);
        Check(!await combat.ClearBossAsync() && camera.Taps == 1 && state.BattleCount == 0 &&
            state.Fleet1Location == new Cell(2, 1),
            "Empty potential-boss spawn was misclassified as observed combat");

        map = new MapDefinition("C1", "SP MB MB", ["A1"], [], [new SpawnWave(0, Boss: 1)]);
        state = Prepare(map);
        state.Paths.ComputeFleetCosts([new(1, state.Fleet1Location)], state.Fleet1Location!.Value, true);
        camera = new Camera(state) { PotentialBossCombat = new(3, 1), ReturnStage = true };
        combat = Create(state, new() { EmotionMode = CampaignEmotionMode.Ignore }, camera);
        bool potentialEnded = false;
        try { await combat.ClearBossAsync(); }
        catch (CampaignEndedException) { potentialEnded = true; }
        Check(potentialEnded && camera.Taps == 2 && state.BattleCount == 0 &&
            state.Fleet1Location == new Cell(2, 1) &&
            combat.StageReturn?.Combats is [ { Return: CombatReturn.InStage, Rank.IsWinningRank: true } ],
            "Potential-boss search did not probe empty spawn before confirmed boss stage return");

        state = Prepare(map);
        state.Paths.ComputeFleetCosts([new(1, state.Fleet1Location)], state.Fleet1Location!.Value, true);
        camera = new Camera(state) { PotentialBossCombat = new(3, 1) };
        combat = Create(state, new() { EmotionMode = CampaignEmotionMode.Ignore }, camera);
        Check(await combat.ClearBossAsync() && camera.Taps == 2 && camera.Scans == 1 &&
            state.BattleCount == 1 && state.Fleet1Location == new Cell(3, 1),
            "Winning potential-boss combat returning to map was not scanned and reported");

        map = new MapDefinition("C1", "SP ++ MB", ["A1"], [], [new SpawnWave(0, Boss: 1)]);
        state = Prepare(map);
        state.Paths.ComputeFleetCosts([new(1, state.Fleet1Location)], state.Fleet1Location!.Value, true);
        camera = new Camera(state);
        combat = Create(state, new() { EmotionMode = CampaignEmotionMode.Ignore }, camera);
        Check(!await combat.ClearBossAsync() && camera.Taps == 0 && state.BattleCount == 0,
            "Potential-boss roadblock search clicked a spawn behind permanent land");

        foreach (var mode in Enum.GetValues<CampaignEmotionMode>())
        foreach (bool locked in new[] { false, true })
        {
            var emotionMap = new MapDefinition("B1", "SP ME", ["A1"], [], [new SpawnWave(0, Enemy: 1)]);
            state = Prepare(emotionMap);
            state[new(2, 1)].IsEnemy = true;
            state.Paths.ComputeFleetCosts([new(1, state.Fleet1Location)], state.Fleet1Location!.Value, true);
            camera = new Camera(state);
            int waits = 0;
            combat = Create(state, new() { EmotionMode = mode, UseFleetLock = locked,
                Fleet2 = 2, FleetOrder = FleetOrder.Fleet1BossFleet2Mob }, camera, (fleet, token) =>
            {
                Check(fleet == 1 && camera.Taps == 0, "Map emotion wait lost logical fleet index or happened after movement");
                waits++; return ValueTask.CompletedTask;
            });
            Check(await combat.ClearEnemyAsync() && waits == (locked && mode.Calculates() ? 1 : 0),
                "On-map emotion wait ignored native mode/fleet-lock gating");
        }
        await FullClearActionsAsync();
        await RoadblockActionsAsync();
        await ExecutionChecksAsync(python);
        Console.WriteLine("Campaign map combat: route, priority, mystery, potential-boss search, scan and stage-return evidence passed offline; no entry or settlement verification.");
    }

    private static async Task FullClearActionsAsync()
    {
        var state = Prepare(new("D1", "SP -- -- --", ["A1"], [], []));
        var config = new CampaignConfiguration { HasFortress = true, EmotionMode = CampaignEmotionMode.Ignore };
        state[new(2, 1)].IsFortress = state[new(3, 1)].IsFortress = true;
        state[new(4, 1)].IsMechanismBlock = true;
        state.RefreshFleetPaths(config);
        var camera = new Camera(state); var combat = Create(state, config, camera);
        Check(await combat.ClearSirenAsync() && state.BattleCount == 1 && state.Fleet1Location == new Cell(2, 1) &&
            state[new(4, 1)].IsMechanismBlock, "First fortress fight released the final-fortress roadblock");
        Check(await combat.ClearSirenAsync() && state.BattleCount == 2 && state.Fleet1Location == new Cell(3, 1) &&
            !state[new(4, 1)].IsMechanismBlock && state[new(4, 1)].IsAccessible,
            "Final fortress fight did not reopen the shared path graph");
        Check(!await combat.ClearSirenAsync() && camera.Taps == 2, "Full-clear fought an already cleared fortress");

        state = Prepare(new("C2", "SP -- --\nME -- ME", ["A1"], [], []));
        config = new() { EmotionMode = CampaignEmotionMode.Ignore, Fleet2 = 2 };
        state.Fleet2Location = new(3, 1);
        state[new(1, 2)].IsEnemy = state[new(3, 2)].IsEnemy = true;
        state[new(1, 2)].Weight = 1; state[new(3, 2)].Weight = 100;
        state.RefreshFleetPaths(config);
        camera = new Camera(state); combat = Create(state, config, camera);
        Check(await combat.ClearAnyEnemyBySecondFleetCostAsync() && state.Fleet1Location == new Cell(3, 2) &&
            state[new(1, 2)].IsEnemy && state.BattleCount == 1, "Moving normal enemy dispatch mixed weight into cost_2 order");
    }

    private static async Task RoadblockActionsAsync()
    {
        foreach (bool observed in new[] { false, true })
        {
            var map = new MapDefinition("D1", "SP ME ME MB", [], [], [], weights: [10, 20, 1, 10]);
            var state = Prepare(map);
            state[new(2, 1)].IsEnemy = state[new(3, 1)].IsEnemy = true;
            state[new(4, 1)].IsBoss = observed;
            state.Paths.ComputeFleetCosts([new(1, state.Fleet1Location)], new(1, 1), false);
            var camera = new Camera(state) { StageForBoss = true };
            var combat = Create(state, new() { EmotionMode = CampaignEmotionMode.Ignore }, camera);
            Check(await combat.BruteClearBossAsync() && state.Fleet1Location == new Cell(2, 1) &&
                state[new(3, 1)].IsEnemy && state.BattleCount == 1 && camera.Taps == 1,
                "Lower-weight blocked enemy was clicked through a real blocker");
            Check(await combat.BruteClearBossAsync() && state.Fleet1Location == new Cell(3, 1) &&
                state.BattleCount == 2 && camera.Taps == 2, "Second blocker was not selected after confirmed first combat");
        }
        foreach (bool meet in new[] { false, true })
        {
            var map = new MapDefinition("E1", "SP ME SP ME MB", [], [], []);
            var state = Prepare(map); state.Fleet2Location = new(3, 1); state[new(3, 1)].IsFleet = true;
            state[new(2, 1)].IsEnemy = meet; state[new(5, 1)].IsBoss = true;
            state[new(4, 1)].IsEnemy = meet;
            var config = new CampaignConfiguration { Fleet2 = 2, EmotionMode = CampaignEmotionMode.Ignore };
            state.Paths.ComputeFleetCosts([new(1, state.Fleet1Location), new(2, state.Fleet2Location)], new(1, 1), false);
            var camera = new Camera(state) { StageForBoss = true };
            int switches = 0;
            var combat = Create(state, config, camera, switchFleet: (fleet, token) =>
            {
                Check(fleet == 2 && camera.Taps == 0, "Boss fleet switched after a grid action");
                switches++; state.FleetIndex = fleet;
                state.Paths.ComputeFleetCosts([new(1, state.Fleet1Location), new(2, state.Fleet2Location)], state.Fleet2Location.Value, false);
                return ValueTask.CompletedTask;
            });
            bool ended = false, cleared = false;
            try { cleared = await combat.BruteClearBossAsync(); } catch (CampaignEndedException) { ended = true; }
            Check(meet ? cleared && !ended && switches == 0 && state.Fleet1Location == new Cell(2, 1) :
                ended && !cleared && switches == 1 && combat.StageReturn is not null,
                "Boss dispatch did not clear between fleets first or switch to reachable boss");
        }
        var blocked = Prepare(new MapDefinition("C1", "SP ++ MB", [], [], []));
        blocked[new(3, 1)].IsBoss = true; blocked.Paths.ComputeCosts(new(1, 1));
        var blockedCamera = new Camera(blocked);
        bool refused = false;
        try { await Create(blocked, new(), blockedCamera).BruteClearBossAsync(); }
        catch (CampaignScriptException) { refused = true; }
        Check(refused && blockedCamera.Taps == 0, "Unreachable observed boss was clicked");
    }

    private static async Task ExecutionChecksAsync(string python)
    {
        var map = new MapDefinition("C1", "SP ME MB", ["B1"], ["B1"],
            [new SpawnWave(0, Enemy: 1), new SpawnWave(1, Boss: 1)]);
        var host = new Host();
        var rule = new TwoBattleRule(map);
        var execution = new CampaignExecution(rule, new() { EmotionMode = CampaignEmotionMode.Ignore },
            (state, config) => new InMapCampaignOperations(host, state, config, default, rule));
        Check(await execution.RunAsync() == CampaignLoopExit.Ended && host.Camera is { Taps: 2, Scans: 2 } &&
            execution.Context.State.BattleCount == 1 && host.FleetLockCalls == 1 && host.StrategyCalls == 1 &&
            execution.Context.Operations is InMapCampaignOperations
            { StageReturn: { Combats: [ { Return: CombatReturn.InStage, Rank.IsWinningRank: true } ] } },
            "Compiled campaign loop did not execute the in-map C# scan, combat and stage-return sequence");

        var mysteryMap = new MapDefinition("D1", "SP MM ME MB", ["B1"], ["B1"],
            [new SpawnWave(0, Enemy: 1, Mystery: 1), new SpawnWave(1, Boss: 1)]);
        var mysteryHost = new Host { HasMystery = true };
        var mysteryRule = new MysteryTwoBattleRule(mysteryMap);
        var mysteryExecution = new CampaignExecution(mysteryRule,
            new() { EmotionMode = CampaignEmotionMode.Ignore },
            (state, config) => new InMapCampaignOperations(mysteryHost, state, config, default, mysteryRule));
        Check(await mysteryExecution.RunAsync() == CampaignLoopExit.Ended &&
            mysteryHost.Camera is { Taps: 3, Scans: 2 } &&
            mysteryExecution.Context.State is { MysteryCount: 1, BattleCount: 1, AmmoCount: 3, FleetAmmo: 4 } &&
            !mysteryExecution.Context.State.Cells.Any(grid => grid.IsMystery),
            "Compiled rule did not pick up mystery before combat and boss return in one C# sortie");

        var task = new CampaignResumeTask();
        var request = new TaskRequest("resume", task.Kind,
            new JsonObject { ["campaign"] = "campaign_main/campaign_1_1" });
        var stage = ((InMapCampaignOperations)execution.Context.Operations).StageReturn;
        var initialFleet = ((InMapCampaignOperations)execution.Context.Operations).InitialFleet;
        var resumeService = new ResumeService(new(CampaignLoopExit.Ended, 1, stage, initialFleet));
        var result = await task.RunAsync(request,
            new TaskContext(null!, null!, null!, TimeSpan.FromMinutes(2),
                Campaign: resumeService), default);
        Check(resumeService.Configuration?.Retirement is { Mode: RetirementMode.OneClick, KeepLimitBreak: true },
            "Resume task lost native retirement defaults");
        var disabledInput = request.Input!.DeepClone().AsObject();
        disabledInput["retirement"] = new JsonObject { ["mode"] = "disabled" };
        _ = await task.RunAsync(request with { Input = disabledInput },
            new TaskContext(null!, null!, null!, TimeSpan.FromMinutes(2), Campaign: resumeService), default);
        Check(resumeService.Configuration?.Retirement.Mode == RetirementMode.Disabled,
            "Resume task re-enabled disabled retirement");
        Check(result is { Outcome: TaskOutcome.Failed, Reason: "sortie_settlement_unverified" } &&
            result.Evidence?["cleared"]?.GetValue<bool>() == false &&
            result.Evidence["settlementVerified"]?.GetValue<bool>() == false &&
            result.Evidence["campaignIdentityVerified"]?.GetValue<bool>() == false &&
            result.Evidence["stageReturn"] is not null &&
            result.Evidence["initialFleet"]?["displayedIndex"]?.GetValue<int>() == 1 &&
            result.Evidence["sortie"]?["outcome"]?.GetValue<string>() == "ended_unknown" &&
            result.Evidence["sortie"]?["campaign_end"]?.GetValue<bool>() == true &&
            result.Evidence["sortie"]?["end_evidence"]?["rank_source"]?.GetValue<string>() == "BATTLE_STATUS_",
            "Campaign task promoted a loop end or stage return into a cleared outcome");

        var compiled = RuleCatalog.Create("campaign_main/campaign_1_1");
        var settled = CampaignResumeTask.Describe("run", "campaign_run", compiled,
            new(CampaignLoopExit.Ended, 1, stage), true);
        Check(settled is { Outcome: TaskOutcome.Succeeded, Reason: "sortie_cleared" } &&
            settled.Evidence?["settlementVerified"]?.GetValue<bool>() == true &&
            settled.Evidence["sortie"]?["outcome"]?.GetValue<string>() == "cleared" &&
            settled.Evidence["sortie"]?["steps"]?[0]?["step"]?.GetValue<string>() == "execute_a_battle",
            "Verified C# battle status and fresh stage return did not close the sortie contract");
        await CheckContractAsync(python, settled);
        await CheckContractAsync(python, result);
        var observedStage = stage ?? throw new InvalidOperationException("The synthetic combat did not return a stage observation");
        var lastCombat = observedStage.Combats.Single();
        foreach (var invalid in new MapArrivalResult?[]
        {
            null,
            observedStage with { FreshFrames = 0 },
            observedStage with { FrameSequence = 0 },
            observedStage with { Encounter = MapEncounterKind.None },
            observedStage with { Outcome = MapArrivalOutcome.MapInterrupted },
            observedStage with { Combats = [lastCombat with { Rank = null }] },
            observedStage with { Combats = [lastCombat with { Rank = new CombatRankEvidence(CombatRank.C,
                CombatRankSource.BattleStatus, UiAssets.Combat.BATTLE_STATUS_C.Id) }] },
            observedStage with { Combats = [lastCombat with { Rank = new CombatRankEvidence(CombatRank.S,
                CombatRankSource.Experience, UiAssets.Combat.EXP_INFO_S.Id) }] },
            observedStage with { Combats = [lastCombat with { Rank = new CombatRankEvidence(CombatRank.S,
                CombatRankSource.BattleStatus, UiAssets.Combat.BATTLE_STATUS_A.Id) }] },
            observedStage with { Combats = [lastCombat with { CapturedFrames = 0 }] },
            observedStage with { HandledEncounters = [MapEncounterKind.ItemPopup, MapEncounterKind.Combat] }
        })
        {
            var uncertain = CampaignResumeTask.Describe("run", "campaign_run", compiled,
                new(CampaignLoopExit.Ended, 1, invalid), true);
            Check(uncertain.Outcome == TaskOutcome.Failed &&
                uncertain.Evidence?["sortie"]?["outcome"]?.GetValue<string>() == "ended_unknown",
                "Incomplete, stale, losing or unrelated battle evidence was promoted to a clear");
            await CheckContractAsync(python, uncertain);
        }
        Check(CampaignResumeTask.Describe("run", "campaign_run", compiled,
            new(CampaignLoopExit.Exhausted, 1, stage), true).Outcome == TaskOutcome.Failed,
            "A stage-return observation without a campaign end was promoted to a clear");

        var noReturn = await task.RunAsync(request,
            new TaskContext(null!, null!, null!, TimeSpan.FromMinutes(2),
                Campaign: new ResumeService(new(CampaignLoopExit.Ended, 0, null))), default);
        Check(noReturn.Evidence?["sortie"]?["outcome"]?.GetValue<string>() == "ended_unknown" &&
            noReturn.Evidence["sortie"]?["end_evidence"] is null &&
            noReturn.Outcome == TaskOutcome.Failed,
            "CampaignEnd without a stage return acquired settlement evidence");

        var exhausted = await task.RunAsync(request,
            new TaskContext(null!, null!, null!, TimeSpan.FromMinutes(2),
                Campaign: new ResumeService(new(CampaignLoopExit.Exhausted, 20, null))), default);
        Check(exhausted is { Outcome: TaskOutcome.Failed, Reason: "campaign_loop_exhausted" } &&
            exhausted.Evidence?["sortie"]?["outcome"]?.GetValue<string>() == "incomplete" &&
            exhausted.Evidence["sortie"]?["stop_reason"]?.GetValue<string>() == "round_limit" &&
            exhausted.Evidence["sortie"]?["campaign_end"]?.GetValue<bool>() == false,
            "Round limit was recorded as a completed sortie");

        host = new Host { InMap = false };
        execution = new CampaignExecution(rule, new() { EmotionMode = CampaignEmotionMode.Ignore },
            (state, config) => new InMapCampaignOperations(host, state, config, default, rule));
        bool rejected = false;
        try { await execution.RunAsync(); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected && host.Camera is null && !execution.Context.State.IsMapInitialized,
            "Campaign resume entered a map workflow without an observed in-map page");
    }

    private static async Task CheckContractAsync(string python, TaskResult result)
    {
        var sortie = result.Evidence?["sortie"] ?? throw new InvalidDataException("Missing C# sortie evidence");
        const string script = "import json,sys;sys.path.insert(0,sys.argv[1]);from sortie_contract import evaluate;print(json.dumps(evaluate(json.loads(sys.argv[2]))['violations']))";
        var output = await new ProcessRunner().RunAsync(python,
            ["-c", script, Path.GetFullPath("tools"), sortie.ToJsonString()], TimeSpan.FromSeconds(15));
        if (output.ExitCode != 0) throw new InvalidOperationException("Frozen sortie contract failed: " + output.Error);
        var violations = JsonSerializer.Deserialize<string[]>(Encoding.UTF8.GetString(output.Output)) ?? [];
        Check(violations.Length == 0, "New C# sortie evidence violates the frozen result contract: " + string.Join(", ", violations));
    }

    private sealed class ResumeService(CampaignResumeResult result) : ICampaignExecutionService
    {
        public CampaignConfiguration? Configuration { get; private set; }
        public ValueTask<CampaignResumeResult> ResumeInMapAsync(CampaignRule rule,
            CampaignConfiguration configuration, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Configuration = configuration; return ValueTask.FromResult(result); }
    }

    private sealed class TwoBattleRule(MapDefinition map) : CampaignRule
    {
        public override string Id => "test/two_battles";
        public override MapDefinition Map => map;
        public override ImmutableArray<SourceFile> Sources => [];
        protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; } =
            new Dictionary<int, BattleHook>
            {
                [0] = static context => context.Operations.ClearEnemyAsync(),
                [1] = static context => context.Operations.ClearBossAsync()
            };
    }

    private sealed class MysteryTwoBattleRule(MapDefinition map) : CampaignRule
    {
        public override string Id => "test/mystery_two_battles";
        public override MapDefinition Map => map;
        public override ImmutableArray<SourceFile> Sources => [];
        protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; } =
            new Dictionary<int, BattleHook>
            {
                [0] = static async context =>
                {
                    await context.Operations.ClearMysteriesAsync();
                    return await context.Operations.ClearEnemyAsync();
                },
                [1] = static context => context.Operations.ClearBossAsync()
            };
    }

    private sealed class Host : ICampaignInMapHost
    {
        public ValueTask InitializeLevelsAsync(CampaignState state, int fleet, CampaignConfiguration config, CancellationToken token)
        {
            Check(state.Health.Get(fleet) is not null && Camera is null, "Level initialization did not run after HP and before scanning");
            state.Levels.Reset(); return ValueTask.CompletedTask;
        }
        public ValueTask InitializeHealthAsync(CampaignState state, int fleet, CampaignConfiguration config, CancellationToken token)
        {
            Check(StrategyCalls == 1 && Camera is null, "Health initialization did not run after strategy and before scanning");
            state.Health.Commit(fleet, 1, [.9, 0, 0, .9, 0, 0], config.Health);
            return ValueTask.CompletedTask;
        }
        public ValueTask<CampaignWithdrawalEvidence> WithdrawAsync(string reason, CancellationToken token) => throw new InvalidOperationException();
        public bool InMap { get; init; } = true;
        public bool HasMystery { get; init; }
        public bool SimulateFleetSwitch { get; init; }
        public Func<Camera, CampaignConfiguration, Func<CancellationToken, ValueTask>, CampaignMapCombat>? CombatFactory { get; init; }
        public Func<Camera, bool>? CombatWhen { get; init; }
        public Func<int, Cell, MapScanMode, MapObservation>? ObservationFactory { get; init; }
        public int FleetLockCalls { get; private set; }
        public int StrategyCalls { get; private set; }
        public List<(int X, int Y)?> RefocusPresets { get; } = [];
        public List<int> RefocusBattleCounts { get; } = [];
        public Camera? Camera { get; private set; }
        public ValueTask<bool> VerifyInMapAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(InMap); }
        public ValueTask EnsureFleetLockAsync(bool enabled, CancellationToken token)
        { token.ThrowIfCancellationRequested(); FleetLockCalls++; return ValueTask.CompletedTask; }
        public ValueTask<FleetSelection> PrepareInitialFleetAsync(CampaignConfiguration configuration, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Check(FleetLockCalls == 1 && Camera is null, "Strategy did not run between fleet lock and map scanning");
            StrategyCalls++;
            return ValueTask.FromResult(new FleetSelection(1, 1, 0, 1));
        }
        public ValueTask<IMapScanCamera> CreateCameraAsync(CampaignState state,
            CampaignConfiguration configuration, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Camera = new Camera(state)
            {
                StageForBoss = true,
                CombatWhen = CombatWhen is null ? null : () => CombatWhen(Camera!),
                ObservationFactory = ObservationFactory ?? ((scan, position, mode) => new MapObservation(scan == 1
                    ? HasMystery
                        ? [new(new(0, 0), new(IsFleet: true, IsCurrentFleet: true)),
                           new(new(1, 0), new(IsMystery: true)),
                           new(new(2, 0), new(IsEnemy: true, EnemyScale: 1))]
                        : [new(new(0, 0), new(IsFleet: true, IsCurrentFleet: true)),
                           new(new(1, 0), new(IsEnemy: true, EnemyScale: 1))]
                    : HasMystery
                        ? [new(new(2, 0), new(IsFleet: true, IsCurrentFleet: true)),
                           new(new(3, 0), new(IsBoss: true))]
                        : [new(new(1, 0), new(IsFleet: true, IsCurrentFleet: true)),
                            new(new(2, 0), new(IsBoss: true))], position, new(1, 0), mode))
            };
            return ValueTask.FromResult<IMapScanCamera>(Camera);
        }
        public ValueTask RefocusBossAsync(IMapScanCamera camera, (int X, int Y)? preset, CancellationToken token)
        {
            Check(ReferenceEquals(Camera, camera), "Refocus used another camera");
            RefocusPresets.Add(preset);
            RefocusBattleCounts.Add(Camera!.State.BattleCount);
            return Camera!.RelocalizeAsync(token);
        }
        public CampaignMapCombat CreateCombat(IMapScanCamera camera, CampaignConfiguration configuration,
            Func<CancellationToken, ValueTask> refocusBoss)
            => Camera == camera ? CombatFactory?.Invoke(Camera, configuration, refocusBoss) ?? Create(Camera.State, configuration, Camera, switchFleet: SimulateFleetSwitch ? (fleet, token) =>
            {
                token.ThrowIfCancellationRequested();
                Camera.State.FleetIndex = fleet;
                Camera.State.RefreshFleetPaths(configuration);
                return ValueTask.CompletedTask;
            } : null, refocusBoss: refocusBoss) :
                throw new InvalidOperationException("Wrong camera instance");
    }

    private static CampaignState Prepare(MapDefinition map, bool walls = false)
    {
        var state = new CampaignState(map);
        state.InitializeMapData(new(PoorMapData: true, Walls: walls));
        state.Fleet1Location = new(1, 1);
        state[new(1, 1)].IsFleet = state[new(1, 1)].IsCurrentFleet = true;
        return state;
    }

    private static CampaignMapCombat Create(CampaignState state, CampaignConfiguration config, Camera camera,
        Func<int, CancellationToken, ValueTask>? waitEmotion = null,
        Func<int, CancellationToken, ValueTask>? switchFleet = null,
        Func<CancellationToken, ValueTask>? refocusBoss = null)
    {
        var movement = new MapMovement(state, config, camera, () =>
            new MapArrivalCheck(camera, state, camera.InMapAsync, camera.Clock,
                new Probe(camera), new Handler(camera), recoverAfterCombat: refocusBoss is null ? null :
                    new MapCombatRecovery(state, camera, refocusBoss).RecoverAsync),
            movableScan: new(state, config, new(state, camera, camera.Clock)),
            carrierScanner: new(state, camera, camera.Clock));
        return new(state, config, movement, new MapScanner(state, camera, camera.Clock), waitEmotion, switchFleet,
            token => camera.EnsureEdgesAsync(true, token));
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance() => _ticks += TimeSpan.FromSeconds(.25).Ticks;
    }

    private sealed class Camera(CampaignState state) : IMapScanCamera, IMapArrivalCamera
    {
        public CampaignState State => state;
        public Clock Clock { get; } = new();
        public Cell Position { get; private set; } = new(1, 1);
        public Cell? Destination { get; private set; }
        public bool ReturnStage { get; init; }
        public bool StageForBoss { get; init; }
        public Cell? PotentialBossCombat { get; init; }
        public Func<bool>? CombatWhen { get; init; }
        public Func<long, FleetMarker>? MarkerAtFrame { get; init; }
        public Action<string>? Trace { get; init; }
        public string? Failure { get; init; }
        public CancellationTokenSource? Cancellation { get; init; }
        public Func<int, Cell, MapScanMode, MapObservation>? ObservationFactory { get; init; }
        public bool ReturningToStage => ReturnStage || StageForBoss && Destination is { } cell && state[cell].IsBoss;
        public CombatRank? Rank { get; init; } = CombatRank.S;
        public int Taps { get; private set; }
        public int Scans { get; private set; }
        public long FrameSequence { get; private set; } = 1;
        public bool Suspended { get; private set; }
        public bool Invalidated { get; private set; }
        public void Suspend() => Suspended = true;
        public void Invalidate() => Invalidated = true;
        public ValueTask FocusAsync(Cell destination, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Position = destination; return ValueTask.CompletedTask; }
        public ValueTask CenterAsync(double tolerance, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
        public ValueTask<MapObservation> ObserveAsync(MapScanMode mode, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Trace?.Invoke("scan");
            if (Failure == "scan") throw new IOException("Synthetic scan failure");
            Scans++;
            return ValueTask.FromResult(ObservationFactory?.Invoke(Scans, Position, mode) ??
                new MapObservation([], Position, new(0, 0), mode));
        }
        public ValueTask EnsureEdgesAsync(bool skipFirstUpdate, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Trace?.Invoke(skipFirstUpdate ? "edges" : "edges:refresh");
            if (Failure == "edges") throw new IOException("Synthetic edge failure");
            return ValueTask.CompletedTask;
        }
        public ValueTask PrepareTapAsync(Cell destination, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); if (Invalidated || Suspended) throw new InvalidOperationException(); return ValueTask.CompletedTask; }
        public ValueTask TapCellAsync(Cell destination, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (Failure == "tap") throw new IOException("Synthetic tap failure");
            Destination = destination; Taps++; Trace?.Invoke("tap:" + destination); return ValueTask.CompletedTask;
        }
        public ValueTask RefreshImageAsync(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); if (Failure != "stale") FrameSequence++; Clock.Advance();
            if (Failure == "cancel") Cancellation!.Cancel();
            return ValueTask.CompletedTask;
        }
        public ValueTask<FleetMarker> ReadFleetMarkerAsync(Cell destination, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(MarkerAtFrame?.Invoke(FrameSequence) ??
            new FleetMarker(Failure != "timeout", Failure != "timeout")); }
        public ValueTask<FleetMarker> ReadCenterMarkerAsync(CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new FleetMarker(true, true)); }
        public ValueTask RelocalizeAsync(CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Suspended = false; FrameSequence++; Clock.Advance(); return ValueTask.CompletedTask; }
        public ValueTask AnchorAtAsync(Cell location, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Position = location; return ValueTask.CompletedTask; }
        public ValueTask<bool> InMapAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(true); }
        public bool IsCombatDestination => CombatWhen?.Invoke() ?? (Destination is { } cell &&
            (state[cell].IsEnemy || state[cell].IsBoss || state[cell].IsSiren || state[cell].IsFortress || state[cell].IsCaughtBySiren ||
             PotentialBossCombat == cell && state[cell].MayBoss));
    }

    private sealed class Probe(Camera camera) : IMapEncounterProbe
    {
        private bool _fired;
        public ValueTask InitializeAsync(long frameSequence, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
        public ValueTask<MapEncounterKind> InspectAsync(long frameSequence, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (_fired || camera.Destination is not { } destination)
                return ValueTask.FromResult(MapEncounterKind.None);
            var encounter = camera.IsCombatDestination ? MapEncounterKind.Combat :
                camera.State[destination].IsMystery ? MapEncounterKind.ItemPopup : MapEncounterKind.None;
            if (encounter == MapEncounterKind.None) return ValueTask.FromResult(encounter);
            _fired = true;
            return ValueTask.FromResult(encounter);
        }
    }

    private sealed class Handler(Camera camera, Action<CombatFlowResult>? onCombat = null) : IMapEncounterHandler
    {
        public ValueTask<MapEncounterHandling> HandleAsync(MapEncounterKind encounter, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (encounter == MapEncounterKind.ItemPopup && camera.Suspended)
                return ValueTask.FromResult(new MapEncounterHandling(MapEncounterContinuation.InMap));
            if (encounter != MapEncounterKind.Combat || !camera.Suspended) throw new InvalidDataException();
            var returned = camera.ReturningToStage ? CombatReturn.InStage : CombatReturn.InMap;
            var rank = camera.Rank is { } value
                ? new CombatRankEvidence(value, CombatRankSource.BattleStatus, UiAssets.Combat.BATTLE_STATUS_S.Id) : null;
            var result = new CombatFlowResult(returned, rank, false, false, 1);
            onCombat?.Invoke(result);
            return ValueTask.FromResult(new MapEncounterHandling(camera.ReturningToStage ?
                MapEncounterContinuation.InStage : MapEncounterContinuation.InMap,
                result));
        }
    }
}
