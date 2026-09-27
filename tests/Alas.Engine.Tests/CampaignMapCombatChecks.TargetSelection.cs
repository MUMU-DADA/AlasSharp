using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static partial class CampaignMapCombatChecks
{
    private sealed record TargetSample(int Active, int Enemies, int Mysteries, int Cleared, int[] Scales, int[] Weights,
        string[][][] Roads, int[] Filter, bool Strongest, bool Weakest, bool ClearAll, string Priority, string[] Ignore, bool Nearby);

    public static async Task TargetSelectionChecksAsync(string python, string upstream, string artifacts)
    {
        var random = new Random(7174);
        var samples = new List<TargetSample>();
        for (int i = 0; i < 480; i++)
        {
            int enemies = random.Next(512) & 254;
            samples.Add(new(i % 2 + 1, enemies, random.Next(512) & 254 & ~enemies, random.Next(512),
                Enumerable.Range(0, 9).Select(_ => random.Next(4)).ToArray(),
                Enumerable.Range(1, 9).OrderBy(_ => random.Next()).Select(w => w * 10).ToArray(),
                i % 7 == 0 ? [[[], ["B1", "B1"], ["C2"]], [["B2"], ["B1", "C2"]]] :
                    [[["B1", "B2"], ["B2", "C1"], ["C2"]], [["B3"], ["A2", "B2"]]],
                new int[][] { [], [3], [1, 2], [0], [2, 3] }[i % 5], i % 4 < 2, i % 4 % 2 == 1, i % 3 == 0,
                new[] { "default", "S3_enemy_first", "S1_enemy_first" }[i / 4 % 3], i % 3 == 0 ? ["B1", "A3"] : [], i % 2 == 0));
        }
        string input = Path.Combine(artifacts, "selection-input.json"), output = Path.Combine(artifacts, "selection-native.json");
        await File.WriteAllTextAsync(input, JsonSerializer.Serialize(samples, TaskQueue.Json));
        var run = await new ProcessRunner().RunAsync(python,
            [Path.Combine(AppContext.BaseDirectory, "native_target_selection_reference.py"), upstream, input, output], TimeSpan.FromMinutes(1));
        Check(run.ExitCode == 0, "Native target selection failed: " + run.Error);
        var native = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
        foreach (var source in new[] { CampaignMapCombat.Source, RoadDefinition.Source })
            Check(native["sources"]![source.Path]!.GetValue<string>() == source.Sha256, "Target source drifted");
        int battles = 0, mysteries = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            var sample = samples[i]; var expected = native["results"]![i]!;
            var config = new CampaignConfiguration { HasAmbush = false, Fleet2 = 2, EmotionMode = CampaignEmotionMode.Ignore,
                ClearAllThisTime = sample.ClearAll, EnemyPriority = sample.Priority switch
                { "S3_enemy_first" => EnemyScalePriority.StrongestFirst, "S1_enemy_first" => EnemyScalePriority.WeakestFirst, _ => EnemyScalePriority.Default } };
            CampaignState Initial()
            {
                var state = Prepare(new("C3", "-- -- --\n-- -- --\n-- -- --", ["B2"], [], []));
                state.Fleet2Location = new(3, 3); state.FleetIndex = sample.Active;
                for (int j = 0; j < 9; j++)
                {
                    var grid = state.Cells[j];
                    grid.IsEnemy = (sample.Enemies & (1 << j)) != 0; grid.IsMystery = (sample.Mysteries & (1 << j)) != 0;
                    grid.IsCleared = (sample.Cleared & (1 << j)) != 0; grid.EnemyScale = sample.Scales[j]; grid.Weight = sample.Weights[j];
                }
                state.RefreshFleetPaths(config); return state;
            }
            var roads = sample.Roads.Select(road => new RoadDefinition(road.Select(group => group.Select(Cell.Parse).ToImmutableArray()).ToImmutableArray())).ToArray();
            var combined = roads[0].Combine(roads[1]);
            Check(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(combined.Groups.Select(group => group.Select(cell => cell.ToString()).Order(StringComparer.Ordinal))),
                expected["groups"]), "Native combined-road union differs: " + i);
            var selection = new EnemySelection(sample.Filter.ToImmutableArray(), sample.Strongest, sample.Weakest);
            for (int action = 0; action < 3; action++)
            {
                var state = Initial(); var camera = new Camera(state); var combat = Create(state, config, camera);
                bool value = action == 0 ? await combat.ClearEnemyAsync(selection) :
                    await combat.ClearRoadblocksAsync([combined], selection, potential: action == 2);
                var choice = expected["choices"]![action]!;
                Check(value == choice["value"]!.GetValue<bool>() && camera.Destination?.ToString() == choice["target"]?.GetValue<string>() &&
                    state.BattleCount == (value ? 1 : 0), $"Native filtered target differs: {i}/{action}");
                battles += state.BattleCount;
            }
            var mysteryState = Initial(); var trace = new List<string>();
            var mysteryCamera = new Camera(mysteryState) { Trace = entry => { if (entry.StartsWith("tap:")) trace.Add(entry[4..]); } };
            bool result = await Create(mysteryState, config, mysteryCamera).ClearMysteriesAsync(sample.Ignore.Select(Cell.Parse).ToArray(), sample.Nearby);
            Check(!result && trace.SequenceEqual(expected["mysteries"]!.AsArray().Select(x => x!.GetValue<string>())) &&
                (sample.Active == 1 ? mysteryState.Fleet1Location : mysteryState.Fleet2Location)?.ToString() == expected["fleet"]!.GetValue<string>() &&
                mysteryState.MysteryCount == trace.Count && mysteryState.BattleCount == 0, "Native ignored mystery selection differs: " + i);
            mysteries += mysteryState.MysteryCount;
        }
        await ChosenMysteryFailuresAsync();
        await ChapterSevenChecksAsync(python, upstream, artifacts);
        Console.WriteLine($"Target selection: {samples.Count} combined-road declarations, {samples.Count * 3} native filtered choices ({battles} synthetic battles), {mysteries} mystery pickups, chosen-fleet failures and chapter-seven Config/compositions passed offline; no live acceptance.");
    }

    private static async Task ChosenMysteryFailuresAsync()
    {
        foreach (string failure in new[] { "none", "tap", "timeout", "switch", "round" })
        {
            var config = new CampaignConfiguration { Fleet2 = 2, HasAmbush = false, HasMaze = failure == "round", EmotionMode = CampaignEmotionMode.Ignore };
            var state = Prepare(new("D1", "SP -- MM SP", ["A1"], [], []));
            state.Fleet2Location = new(4, 1); state[new(3, 1)].IsMystery = true;
            state.Rounds.Initialize(config);
            if (failure == "round") { state.Rounds.Advance(); state.Rounds.Advance(); }
            state.RefreshFleetPaths(config);
            var camera = new Camera(state) { Failure = failure };
            var combat = Create(state, config, camera, switchFleet: (fleet, _) =>
            { if (failure != "switch") { state.FleetIndex = fleet; state.RefreshFleetPaths(config); } return ValueTask.CompletedTask; });
            Exception? error = null;
            try { await combat.SwitchFleetAsync(2); await combat.ClearMysteryAsync(new(3, 1)); }
            catch (Exception caught) { error = caught; }
            bool committed = failure is "none" or "round";
            Check((failure == "none" ? error is null : error is not null) && (failure != "round" || error is MapEnemyMovedException) &&
                state.FleetIndex == (failure == "switch" ? 1 : 2) && state.Fleet2Location == new Cell(committed ? 3 : 4, 1) &&
                state.MysteryCount == (committed ? 1 : 0) && state.BattleCount == 0 && combat.StageReturn is null,
                "Chosen-fleet mystery fabricated completion or fleet restoration: " + failure);
        }
        // Native nearby is cost < 20; an ambush-weighted corridor includes a real boundary.
        var nearbyConfig = new CampaignConfiguration { HasAmbush = true, EmotionMode = CampaignEmotionMode.Ignore };
        var nearby = Prepare(new("D1", "SP -- -- --", ["A1"], [], []));
        nearby[new(3, 1)].IsMystery = true; nearby.RefreshFleetPaths(nearbyConfig);
        Check(nearby[new(3, 1)].Cost >= 20, "Nearby boundary fixture is not distant");
        var nearbyCamera = new Camera(nearby); var nearbyCombat = Create(nearby, nearbyConfig, nearbyCamera);
        Check(!await nearbyCombat.ClearMysteriesAsync([], nearby: true) && nearbyCamera.Taps == 0 && nearby.MysteryCount == 0,
            "Nearby filter collected a distant mystery");
        Check(!await nearbyCombat.ClearMysteriesAsync([], nearby: false) && nearby.MysteryCount == 1, "Explicit distant mystery was ignored");
    }
}
