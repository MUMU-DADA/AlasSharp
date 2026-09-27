using System.Text.Json.Nodes;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static partial class CampaignMapCombatChecks
{
    public static async Task BossRefocusExecutionChecksAsync(JsonArray gates)
    {
        foreach (var gate in gates)
        foreach (bool loop in new[] { false, true })
        foreach (double hp in new[] { 0.0, .005, .01, .9 })
        {
            var waves = gate!["waves"]!.AsArray().Select(row => new SpawnWave(row!["battle"]!.GetValue<int>(),
                Enemy: row["enemy"]?.GetValue<int>() ?? 0, Boss: row["boss"]?.GetValue<int>() ?? 0)).ToArray();
            var map = new MapDefinition("B1", "SP MB", ["A1"], [], loop ? [new(0), new(1, Boss: 1)] : waves,
                loopWaves: loop ? waves : null);
            // Empty loop tables natively retain ordinary spawns; keep both empty in this sample.
            if (loop && waves.Length == 0) map = new("B1", "SP MB", ["A1"], [], []);
            var state = new CampaignState(map);
            state.InitializeMapData(new(ClearMode: loop, PoorMapData: true));
            state.BattleCount = gate["battle"]!.GetValue<int>();
            state.Health.Commit(1, 1, [hp, 0, 0, 0, 0, 0], new());
            var camera = new Camera(state);
            int refocus = 0, reads = 0;
            var recovery = new MapCombatRecovery(state, camera, async token =>
            {
                Check(state.BattleCount == gate["battle"]!.GetValue<int>(), "Boss hook received an unconfirmed battle commit");
                refocus++; await camera.RelocalizeAsync(token);
            }, token =>
            {
                Check(refocus == 1 && camera.FrameSequence > 1, "Empty HP reread ran before camera recovery");
                reads++; state.Health.Commit(1, camera.FrameSequence, [.9, 0, 0, 0, 0, 0], new());
                return ValueTask.CompletedTask;
            });
            await recovery.RecoverAsync(default);
            bool expected = gate["refocus"]!.GetValue<bool>();
            Check(refocus == (expected ? 1 : 0) && reads == (expected && hp < .01 ? 1 : 0) && camera.FrameSequence > 1 &&
                state.BattleCount == gate["battle"]!.GetValue<int>(), "Boss spawn gate used cumulative rows, wrong loop data or HP threshold");
        }

        // Exercise an actual compiled chapter override through real CampaignExecution,
        // operations, map movement and arrival; camera/combat I/O remains synthetic.
        var actualRule = RuleCatalog.Create("campaign_main/campaign_1_1");
        var host = new Host
        {
            ObservationFactory = (scan, position, mode) => new(scan == 1
                ? [new(new(1 - position.Column, 0), new(IsFleet: true, IsCurrentFleet: true)),
                   new(new(6 - position.Column, 0), new(IsEnemy: true, EnemyScale: 1))]
                : [new(new(7 - position.Column, 0), new(IsBoss: true))], position, new(0, 0), mode)
        };
        var run = new CampaignExecution(actualRule, new() { EmotionMode = CampaignEmotionMode.Ignore, BossAppearRefocusSwipe = (2, 1) },
            (state, config) => new InMapCampaignOperations(host, state, config, default, actualRule));
        Check(await run.RunAsync() == CampaignLoopExit.Ended && run.Context.State.BattleCount == 1 &&
            host.RefocusPresets.SequenceEqual(new (int X, int Y)?[] { (-3, 0) }) && host.RefocusBattleCounts.SequenceEqual([0]),
            "Compiled chapter boss override was replaced by configuration/default recovery or called on stage return");
        var plainMap = new MapDefinition("C1", "SP ME MB", ["B1"], ["B1"], [new(0, Enemy: 1), new(1, Boss: 1)]);
        var plain = new TwoBattleRule(plainMap);
        var configured = new Host();
        var configuredRun = new CampaignExecution(plain, new() { EmotionMode = CampaignEmotionMode.Ignore, BossAppearRefocusSwipe = (2, 1) },
            (state, config) => new InMapCampaignOperations(configured, state, config, default, plain));
        Check(await configuredRun.RunAsync() == CampaignLoopExit.Ended &&
            configured.RefocusPresets.SequenceEqual(new (int X, int Y)?[] { (2, 1) }),
            "Default CampaignRule did not retain the configuration refocus preset");
        await BossRefocusArrivalFailuresAsync();
    }

    private static async Task BossRefocusArrivalFailuresAsync()
    {
        foreach (string failure in new[] { "geometry", "stale", "cancel", "hp", "none" })
        {
            var state = Prepare(new("B1", "SP ME", ["A1"], [], [new(0, Enemy: 1), new(1, Boss: 1)]));
            state[new(2, 1)].IsEnemy = true; state.RefreshFleetPaths(new());
            state.Health.Commit(1, 1, [failure == "hp" ? 0 : .9, 0, 0, 0, 0, 0], new());
            var camera = new Camera(state);
            using var cancel = new CancellationTokenSource();
            int calls = 0, reads = 0;
            var recovery = new MapCombatRecovery(state, camera, async token =>
            {
                calls++;
                Check(state.BattleCount == 0 && state.FleetAmmo == 5 && state.Fleet1Location == new Cell(1, 1),
                    "Map counters moved before boss camera/arrival confirmation");
                if (failure == "geometry") throw new MapGeometryException("Synthetic refocus failure");
                if (failure == "cancel") { cancel.Cancel(); token.ThrowIfCancellationRequested(); }
                if (failure != "stale") await camera.RelocalizeAsync(token);
            }, token =>
            {
                reads++;
                if (failure == "hp") throw new IOException("Synthetic health reread failure");
                return ValueTask.CompletedTask;
            });
            var movement = new MapMovement(state, new() { EmotionMode = CampaignEmotionMode.Ignore }, camera,
                () => new(camera, state, camera.InMapAsync, camera.Clock, new Probe(camera), new Handler(camera), recovery.RecoverAsync));
            bool failed = false;
            try
            {
                Check((await movement.FightAsync(new(2, 1), token: cancel.Token)).Outcome == MapMoveOutcome.Committed,
                    "Successful boss recovery lost combat arrival");
            }
            catch (Exception e) when (e is MapGeometryException or OperationCanceledException or InvalidDataException or IOException) { failed = true; }
            Check(calls == 1 && reads == (failure == "hp" ? 1 : 0) && failed == (failure != "none") &&
                state.BattleCount == (failed ? 0 : 1) && state.FleetAmmo == (failed ? 5 : 4) &&
                state.MovementInvalidated == failed && camera.Invalidated == failed && state[new(2, 1)].IsEnemy == failed,
                "Boss recovery failure committed state, lost failure or re-used uncertain view: " + failure);
        }
        foreach (bool stage in new[] { false, true })
        foreach (CombatRank? rank in new CombatRank?[] { CombatRank.S, CombatRank.C, null })
        {
            var state = Prepare(new("B1", "SP ME", ["A1"], [], [new(0, Enemy: 1), new(1, Boss: 1)]));
            state[new(2, 1)].IsEnemy = true; state.RefreshFleetPaths(new());
            var camera = new Camera(state) { ReturnStage = stage, Rank = rank };
            int calls = 0;
            var movement = new MapMovement(state, new(), camera, () =>
                new(camera, state, camera.InMapAsync, camera.Clock, new Probe(camera), new Handler(camera), async token =>
                { calls++; await camera.RelocalizeAsync(token); }));
            var result = await movement.FightAsync(new(2, 1));
            Check(calls == (!stage && rank == CombatRank.S ? 1 : 0) &&
                (result.Outcome == MapMoveOutcome.Committed) == (!stage && rank == CombatRank.S),
                "Defeat, missing rank or stage return ran winning-map recovery");
        }
    }
}
