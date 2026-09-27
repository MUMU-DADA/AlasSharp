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
    private sealed record PositionSample(int Second, int Active, int Enemies, int Mysteries, int Cleared,
        int[] Weights, string[] Cells, string[][][] Roads, string Priority);

    public static async Task FleetPositionChecksAsync(string python, string upstream, string artifacts)
    {
        var random = new Random(6164);
        var samples = new List<PositionSample>();
        string[] available = ["B1", "C1", "A2", "B2", "C2", "A3", "B3", "C3"];
        for (int i = 0; i < 360; i++)
        {
            int enemies = random.Next(512) & 254;
            samples.Add(new(i % 7 == 0 ? 0 : 2, i % 2 + 1, enemies, random.Next(512) & 254 & ~enemies,
                i % 5 == 0 ? 511 : random.Next(512), Enumerable.Range(0, 9).Select(_ => random.Next(1, 4) * 10).ToArray(),
                available.OrderBy(_ => random.Next()).Take(i % 6).ToArray(),
                [[["B1", "B2"], ["A2", "B2"], ["C1", "C2"], ["B3"]]],
                new[] { "default", "S3_enemy_first", "S1_enemy_first" }[i % 3]));
        }
        // A cleared cell remains eligible when every declared candidate is cleared.
        samples.Add(new(2, 1, 0, 0, 511, Enumerable.Repeat(10, 9).ToArray(), ["B1", "B2"], [], "default"));
        // With a mixture, skip a cleared cell even when it appears first in the rule.
        samples.Add(new(2, 1, 0, 0, 2, Enumerable.Repeat(10, 9).ToArray(), ["B1", "B2"], [], "default"));
        string input = Path.Combine(artifacts, "position-input.json"), output = Path.Combine(artifacts, "position-native.json");
        await File.WriteAllTextAsync(input, JsonSerializer.Serialize(samples, TaskQueue.Json));
        var run = await new ProcessRunner().RunAsync(python,
            [Path.Combine(AppContext.BaseDirectory, "native_fleet_position_reference.py"), upstream, input, output], TimeSpan.FromMinutes(1));
        Check(run.ExitCode == 0, "Native fleet positioning failed: " + run.Error);
        var native = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
        Check(native["source"]!.GetValue<string>() == CampaignMapCombat.Source.Sha256, "Positioning native source drifted");
        int moves = 0, battles = 0, mysteries = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            var sample = samples[i]; var alternatives = native["results"]![i]!.AsArray();
            var config = new CampaignConfiguration { HasAmbush = false, Fleet2 = sample.Second, BossFleet = 1,
                EmotionMode = CampaignEmotionMode.Ignore, EnemyPriority = sample.Priority switch
                { "S3_enemy_first" => EnemyScalePriority.StrongestFirst, "S1_enemy_first" => EnemyScalePriority.WeakestFirst, _ => EnemyScalePriority.Default } };
            var state = Prepare(new("C3", "-- -- --\n-- -- --\n-- -- --", ["B2"], [], []));
            state.Fleet2Location = new(3, 3); state.FleetIndex = sample.Active;
            for (int j = 0; j < 9; j++)
            {
                var grid = state.Cells[j];
                grid.IsEnemy = (sample.Enemies & (1 << j)) != 0; grid.IsMystery = (sample.Mysteries & (1 << j)) != 0;
                grid.IsCleared = (sample.Cleared & (1 << j)) != 0; grid.Weight = sample.Weights[j]; grid.EnemyScale = j % 3 + 1;
            }
            state.RefreshFleetPaths(config);
            var trace = new List<string>();
            var camera = new Camera(state) { Trace = entry =>
            { if (entry.StartsWith("tap:", StringComparison.Ordinal)) trace.Add($"goto:{state.FleetIndex}:" + entry[4..]); } };
            var combat = Create(state, config, camera, switchFleet: (fleet, _) =>
            { trace.Add("switch:" + fleet); state.FleetIndex = fleet; state.RefreshFleetPaths(config); return ValueTask.CompletedTask; });
            var roads = sample.Roads.Select(road => new RoadDefinition(road.Select(group => group.Select(Cell.Parse).ToImmutableArray()).ToImmutableArray())).ToArray();
            bool value = await combat.PositionSecondFleetAsync(sample.Cells.Select(Cell.Parse).ToArray(), roads);
            Check(alternatives.Any(expected => value == expected!["value"]!.GetValue<bool>() && state.FleetIndex == expected["active"]!.GetValue<int>() &&
                state.Fleet1Location?.ToString() == expected["fleet1"]!.GetValue<string>() && state.Fleet2Location?.ToString() == expected["fleet2"]!.GetValue<string>() &&
                state.BattleCount == expected["battles"]!.GetValue<int>() && state.MysteryCount == expected["mysteries"]!.GetValue<int>() &&
                trace.SequenceEqual(expected["trace"]!.AsArray().Select(item => item!.GetValue<string>()))),
                $"Fleet positioning differs at {i}: trace={string.Join(',', trace)}; native={alternatives}");
            moves += state.Fleet2Location != new Cell(3, 3) ? 1 : 0; battles += state.BattleCount; mysteries += state.MysteryCount;
            if (i >= 360)
                Check(!value && state.Fleet2Location == Cell.Parse(i == 360 ? "B1" : "B2"),
                    "Cleared candidate eligibility or declared order changed");
        }
        await PositionFailuresAsync();
        string configFile = Path.Combine(artifacts, "chapter6-config.json");
        var configRun = await new ProcessRunner().RunAsync(python,
            [Path.Combine(AppContext.BaseDirectory, "native_main_chapter_reference.py"), upstream, "cn", configFile, "6"], TimeSpan.FromMinutes(1));
        Check(configRun.ExitCode == 0, "Native chapter-six declarations failed: " + configRun.Error);
        foreach (var entry in JsonNode.Parse(await File.ReadAllTextAsync(configFile))!["chapters"]!.AsArray())
            CompareChapterTwoConfig(RuleCatalog.Create(entry!["id"]!.GetValue<string>()), entry["config"]!);
        await ChapterTwoCampaignsAsync(6);
        Console.WriteLine($"Fleet positioning: {samples.Count} native traces ({moves} relocations, {battles} roadblock battles, {mysteries} mysteries), failure boundaries, four chapter-six Config declarations and compiled late-mystery/pre-boss supply paths passed offline; no live acceptance.");
    }

    private static async Task PositionFailuresAsync()
    {
        foreach (string failure in new[] { "tap", "timeout", "switch", "round", "occupied", "road-scan" })
        {
            var config = new CampaignConfiguration { Fleet2 = 2, BossFleet = 1, HasAmbush = false, HasMaze = failure == "round",
                EmotionMode = CampaignEmotionMode.Ignore };
            var state = Prepare(new("D1", "SP -- -- SP", ["A1"], [], []));
            state.Fleet2Location = new(4, 1);
            if (failure == "round") { state.Rounds.Initialize(config); state.Rounds.Advance(); state.Rounds.Advance(); }
            if (failure == "road-scan") state[new(2, 1)].IsEnemy = true;
            state.RefreshFleetPaths(config);
            var camera = new Camera(state) { Failure = failure == "road-scan" ? "scan" : failure };
            var switches = new List<int>();
            var combat = Create(state, config, camera, switchFleet: (fleet, _) =>
            {
                switches.Add(fleet);
                if (failure == "switch") return ValueTask.CompletedTask;
                state.FleetIndex = fleet; state.RefreshFleetPaths(config); return ValueTask.CompletedTask;
            });
            Exception? error = null;
            try { await combat.PositionSecondFleetAsync(failure == "road-scan" ? [] : [new Cell(failure == "occupied" ? 1 : 2, 1)], [new([[new Cell(2, 1)]])]); }
            catch (Exception caught) { error = caught; }
            Check(error is not null && (failure != "round" || error is MapEnemyMovedException) &&
                state.Fleet2Location == new Cell(failure == "round" ? 2 : 4, 1) &&
                state.BattleCount == (failure == "road-scan" ? 1 : 0) && combat.StageReturn is null &&
                !switches.Contains(1) && (failure != "occupied" || camera.Taps == 0),
                "Positioning failure fabricated movement, rolled back confirmed battle, or restored a fleet: " + failure);
        }
    }
}
