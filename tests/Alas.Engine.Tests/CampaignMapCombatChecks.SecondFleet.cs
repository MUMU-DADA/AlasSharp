using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static partial class CampaignMapCombatChecks
{
    private sealed record SecondFleetSample(string Operation, int Boss, int Second, int Active,
        int Enemies, int Land, int[] Weights, string Target);

    public static async Task SecondFleetChecksAsync(string python, string upstream, string artifacts)
    {
        var random = new Random(301304);
        var samples = new List<SecondFleetSample>();
        for (int i = 0; i < 320; i++)
        {
            int enemies = random.Next(512) & 254;
            int land = random.Next(512) & 254 & ~enemies;
            int[] weights = Enumerable.Range(0, 9).Select(_ => random.Next(1, 5) * 10).ToArray();
            foreach (string operation in new[] { "push", "rescue" })
                samples.Add(new(operation, i % 5 == 0 ? 1 : 2, i % 7 == 0 ? 0 : 2, i % 2 + 1,
                    enemies, land, weights, "C1"));
        }
        string input = Path.Combine(artifacts, "second-fleet-input.json"), output = Path.Combine(artifacts, "second-fleet-native.json");
        await File.WriteAllTextAsync(input, JsonSerializer.Serialize(samples, TaskQueue.Json));
        var nativeRun = await new ProcessRunner().RunAsync(python,
            [Path.Combine(AppContext.BaseDirectory, "native_second_fleet_reference.py"), upstream, input, output], TimeSpan.FromMinutes(1));
        Check(nativeRun.ExitCode == 0, "Second-fleet native oracle failed: " + nativeRun.Error);
        var native = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
        foreach (var source in new[] { CampaignMapCombat.Source, CampaignState.InitializationSource })
            Check(native["sources"]![source.Path]!.GetValue<string>() == source.Sha256, "Second-fleet native source drifted");
        int moves = 0, fights = 0, restoredCosts = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            var sample = samples[i]; var expected = native["results"]![i]!;
            var config = new CampaignConfiguration { HasAmbush = false, Fleet2 = sample.Second, BossFleet = sample.Boss,
                EnemyPriority = EnemyScalePriority.StrongestFirst, EmotionMode = CampaignEmotionMode.Ignore };
            var state = Prepare(new("C3", "-- -- --\n-- -- --\n-- -- --", ["B2"], [], []));
            state.Fleet2Location = new(3, 3); state.FleetIndex = sample.Active;
            for (int j = 0; j < 9; j++)
            {
                state.Cells[j].IsEnemy = (sample.Enemies & (1 << j)) != 0;
                state.Cells[j].IsLand = (sample.Land & (1 << j)) != 0;
                state.Cells[j].Weight = sample.Weights[j]; state.Cells[j].EnemyScale = j % 3 + 1;
            }
            state.RefreshFleetPaths(config);
            var trace = new List<string>();
            var camera = new Camera(state) { Trace = entry =>
            {
                if (entry.StartsWith("tap:", StringComparison.Ordinal)) trace.Add((sample.Operation == "push" ? "move:" : "fight:") + entry[4..]);
            } };
            var combat = Create(state, config, camera, switchFleet: (fleet, token) =>
            {
                trace.Add("switch:" + fleet); state.FleetIndex = fleet; state.RefreshFleetPaths(config);
                return ValueTask.CompletedTask;
            });
            bool actual = sample.Operation == "push" ? await combat.PushSecondFleetForwardAsync() : await combat.RescueSecondFleetAsync(Cell.Parse(sample.Target));
            Check(actual == expected["result"]!.GetValue<bool>() && state.FleetIndex == expected["active"]!.GetValue<int>() &&
                trace.SequenceEqual(expected["trace"]!.AsArray().Select(x => x!.GetValue<string>())),
                $"Second-fleet selection/order differs in case {i}: {string.Join(',', trace)}; native={expected}");
            if (actual && sample.Operation == "push") moves++;
            if (actual && sample.Operation == "rescue") fights++;
            Check(state.BattleCount == (actual && sample.Operation == "rescue" ? 1 : 0), "Advance or failed rescue fabricated a battle");
            if (expected["hypotheticalCosts"]!.GetValue<bool>()) restoredCosts++;
        }
        for (int i = 0; i < 4; i++) CompareChapterTwoConfig(RuleCatalog.Create($"campaign_main/campaign_3_{i + 1}"), native["configs"]![i]!);
        await SecondFleetInteractionsAsync();
        await SecondFleetFailuresAsync();
        await ChapterTwoCampaignsAsync(3);
        await CombatFlowChecks.ChapterTwoExperienceAsync();
        Console.WriteLine($"Second fleet: {samples.Count} native selections ({moves} advances, {fights} rescues, {restoredCosts} hypothetical cost restorations), interaction/failure boundaries, four inherited chapter configurations and four compiled campaign compositions passed offline; no live acceptance.");
    }

    private static async Task SecondFleetInteractionsAsync()
    {
        foreach (string tile in new[] { "--", "MM", "MA" })
        {
            var config = new CampaignConfiguration { HasAmbush = false, BossFleet = 2, Fleet2 = 2, HasFleetStep = true, Fleet2Step = 1,
                EmotionMode = CampaignEmotionMode.Ignore };
            var state = Prepare(new("D1", $"SP {tile} -- SP", ["A1"], [], []));
            state.Fleet2Location = new(4, 1); state[new(2, 1)].Weight = 1;
            state[new(2, 1)].IsMystery = tile == "MM"; state[new(2, 1)].IsAmmo = tile == "MA";
            state.RefreshFleetPaths(config);
            var camera = new Camera(state);
            var combat = Create(state, config, camera, switchFleet: (fleet, _) =>
            { state.FleetIndex = fleet; state.RefreshFleetPaths(config); return ValueTask.CompletedTask; });
            Check(await combat.PushSecondFleetForwardAsync() && state.Fleet2Location == new Cell(2, 1) && state.FleetIndex == 1 &&
                state.BattleCount == 0 && state.MysteryCount == (tile == "MM" ? 1 : 0) && state.AmmoCount == 3 && state.FleetAmmo == 5 &&
                combat.AmmoPickups.Count == 0 && camera.Taps == (tile == "MA" ? 3 : 2),
                "Raw second-fleet movement lost step/mystery/supply semantics: " + tile);
        }
    }

    private static async Task SecondFleetFailuresAsync()
    {
        foreach (string failure in new[] { "unobserved-switch", "partial-switch", "tap", "timeout", "round", "restore" })
        {
            var config = new CampaignConfiguration { HasAmbush = false, Fleet2 = 2, BossFleet = 2, HasMaze = failure == "round",
                EmotionMode = CampaignEmotionMode.Ignore };
            var state = Prepare(new("C1", "SP -- SP", ["A1"], [], []));
            state.Fleet2Location = new(3, 1); state[new(2, 1)].Weight = 1;
            if (failure == "round")
            {
                state.Rounds.Initialize(config); state.Rounds.Advance(); state.Rounds.Advance();
            }
            state.RefreshFleetPaths(config);
            var camera = new Camera(state) { Failure = failure };
            var switches = new List<int>();
            var combat = Create(state, config, camera, switchFleet: (fleet, _) =>
            {
                switches.Add(fleet);
                if (failure == "unobserved-switch") return ValueTask.CompletedTask;
                if (failure == "restore" && fleet == 1) throw new IOException("Synthetic restoration failure");
                state.FleetIndex = fleet; state.RefreshFleetPaths(config);
                if (failure == "partial-switch") throw new IOException("Synthetic partial switch");
                return ValueTask.CompletedTask;
            });
            Exception? error = null;
            try { await combat.PushSecondFleetForwardAsync(); } catch (Exception caught) { error = caught; }
            bool moved = failure is "round" or "restore";
            Check(error is not null && (failure != "round" || error is MapEnemyMovedException) &&
                state.Fleet2Location == new Cell(moved ? 2 : 3, 1) && state.FleetIndex == (failure == "unobserved-switch" ? 1 : 2) &&
                switches.SequenceEqual(failure == "restore" ? new[] { 2, 1 } : [2]) && state.BattleCount == 0,
                $"Failed advance restored unobserved identity or fabricated progress: {failure}, {error?.GetType().Name}");
        }
    }
}
