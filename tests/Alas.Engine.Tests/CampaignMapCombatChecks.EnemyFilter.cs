using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Rules;
using Alas.Engine.Rules.Main;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static partial class CampaignMapCombatChecks
{
    private sealed record FilterSample(int Active, int Enemies, int Bosses, int Sirens, int[] Scales,
        string[] Genres, int[] Weights, string Expression, int Preserve, string Priority, bool Movable,
        bool ClearAll, bool SirenEnabled);

    public static async Task EnemyFilterChecksAsync(string python, string upstream, string artifacts)
    {
        var random = new Random(1314);
        string[] expressions = ["1L > 1M > 2L > 2M > 3L > 2E > 3E > 2C > 3C > 3M", "", " > > ", "unknown",
            " 1l ＞ 2M ﹥ 3c › 1L ˃ 0E ᐳ 2E ❯ 3M\t\r\n", "BO > SU > SI > 1E > 2E > 3E > 0E", "3L > 1L > 3L",
            EnemyFilter.StrongestFirst.Expression, EnemyFilter.WeakestFirst.Expression, "1L > 1 > L > 2E > 1L-suffix"];
        int[] preserves = [0, 1, 2, 20, -1, -20];
        string[] genres = ["Light", "Main", "Enemy", "Carrier", "", "Siren_DD"];
        var samples = new List<FilterSample>();
        foreach (string expression in expressions)
        foreach (int preserve in preserves)
        foreach (string priority in new[] { "default", "S3_enemy_first", "S1_enemy_first" })
        foreach (bool movable in new[] { false, true })
        foreach (int active in new[] { 1, 2 })
        {
            int enemies = samples.Count % 17 == 0 ? 0 : random.Next(256) & 254;
            samples.Add(new(active, enemies, samples.Count % 5 == 0 ? enemies & random.Next(256) : 0,
                samples.Count % 7 == 0 ? enemies & random.Next(256) : 0,
                Enumerable.Range(0, 9).Select(_ => random.Next(4)).ToArray(),
                Enumerable.Range(0, 9).Select(_ => genres[random.Next(genres.Length)]).ToArray(),
                Enumerable.Range(0, 9).Select(_ => random.Next(3) * 10).ToArray(),
                expression, preserve, priority, movable, random.Next(2) == 0, false));
        }
        string input = Path.Combine(artifacts, "enemy-filter-input.json"), output = Path.Combine(artifacts, "enemy-filter-native.json");
        await File.WriteAllTextAsync(input, JsonSerializer.Serialize(samples, TaskQueue.Json));
        var run = await new ProcessRunner().RunAsync(python,
            [Path.Combine(AppContext.BaseDirectory, "native_enemy_filter_reference.py"), upstream, input, output], TimeSpan.FromMinutes(1));
        Check(run.ExitCode == 0, "Native enemy filter failed: " + run.Error);
        var native = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
        foreach (var source in new[] { CampaignMapCombat.Source, EnemyFilter.Source, CellState.Source })
            Check(native["sources"]![source.Path]!.GetValue<string>() == source.Sha256, "Enemy filter source drifted");
        int battles = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            var sample = samples[i]; var expected = native["results"]![i]!;
            var config = new CampaignConfiguration { HasAmbush = false, Fleet2 = 2, EmotionMode = CampaignEmotionMode.Ignore,
                ClearAllThisTime = sample.ClearAll, HasMovableNormalEnemy = sample.Movable, HasSiren = sample.SirenEnabled,
                EnemyPriority = sample.Priority switch { "S3_enemy_first" => EnemyScalePriority.StrongestFirst,
                    "S1_enemy_first" => EnemyScalePriority.WeakestFirst, _ => EnemyScalePriority.Default } };
            var state = Prepare(new("C3", "-- -- --\n-- -- --\n-- -- --", ["B2"], [], [new SpawnWave(0, Enemy: 1)]));
            state.Fleet2Location = new(3, 3); state.FleetIndex = sample.Active;
            for (int j = 0; j < 9; j++)
            {
                var grid = state.Cells[j];
                grid.IsEnemy = (sample.Enemies & (1 << j)) != 0;
                grid.IsBoss = (sample.Bosses & (1 << j)) != 0;
                grid.IsSiren = (sample.Sirens & (1 << j)) != 0;
                grid.EnemyScale = sample.Scales[j]; grid.EnemyGenre = sample.Genres[j]; grid.Weight = sample.Weights[j];
            }
            state.RefreshFleetPaths(config);
            if (sample.Movable) state.Rounds.Initialize(config);
            Check(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(state.Cells.Select(grid => new[] { grid.Cost, grid.Cost1, grid.Cost2 })),
                expected["costs"]), "Enemy filter path precondition differs: " + i);
            var filter = new EnemyFilter(sample.Expression);
            var ordered = filter.Apply(state.Cells.Where(grid => grid.IsEnemy && grid.IsAccessible)
                .OrderBy(grid => grid.Weight).ThenBy(grid => grid.Cost), sample.Preserve);
            Check(ordered.Select(grid => grid.Location.ToString()).SequenceEqual(expected["ordered"]!.AsArray().Select(n => n!.GetValue<string>())),
                "Native enemy filter order/encoding/slice differs: " + i);
            var camera = new Camera(state);
            bool result = await Create(state, config, camera).ClearFilterEnemyAsync(filter, sample.Preserve);
            Check(result == expected["value"]!.GetValue<bool>() && camera.Destination?.ToString() == expected["target"]?.GetValue<string>() &&
                state.BattleCount == (result ? 1 : 0), "Native clear_filter_enemy target or combat commit differs: " + i);
            battles += state.BattleCount;
        }
        await EnemyFilterFailuresAsync();
        await ChapterThirteenAsync(python, upstream, artifacts);
        Console.WriteLine($"Enemy filter: {samples.Count} native order/slice and target comparisons ({battles} synthetic battles), priority overrides, movable cost_2, overlapping flags, failures and chapter thirteen passed offline; no live acceptance.");
    }

    private static async Task EnemyFilterFailuresAsync()
    {
        foreach (string failure in new[] { "tap", "scan", "timeout", "cancel", "rank", "no_rank" })
        {
            var state = Prepare(new("B1", "SP ME", ["A1"], [], []));
            var config = new CampaignConfiguration { HasAmbush = false, EmotionMode = CampaignEmotionMode.Ignore };
            state[new(2, 1)].IsEnemy = true; state[new(2, 1)].EnemyScale = 1; state[new(2, 1)].EnemyGenre = "Light";
            state.RefreshFleetPaths(config);
            using var cancellation = new CancellationTokenSource();
            var camera = new Camera(state) { Failure = failure, Cancellation = cancellation,
                Rank = failure == "rank" ? CombatRank.C : failure == "no_rank" ? null : CombatRank.S };
            var combat = Create(state, config, camera);
            Exception? caught = null;
            try { await combat.ClearFilterEnemyAsync(new("1L"), token: cancellation.Token); }
            catch (Exception error) { caught = error; }
            bool committed = failure == "scan";
            Check(caught is not null && combat.StageReturn is null && state.BattleCount == (committed ? 1 : 0) &&
                state[new(2, 1)].IsEnemy != committed && (failure != "cancel" || caught is OperationCanceledException),
                "Enemy filtering fabricated combat completion or swallowed failure: " + failure);
        }
    }

    private static async Task ChapterThirteenAsync(string python, string upstream, string artifacts)
    {
        int overlays = 0;
        foreach (var server in Enum.GetValues<GameServer>())
        {
            string output = Path.Combine(artifacts, server.ToString().ToLowerInvariant() + "-chapter13.json");
            var run = await new ProcessRunner().RunAsync(python,
                [Path.Combine(AppContext.BaseDirectory, "native_main_chapter_reference.py"), upstream, server.ToString().ToLowerInvariant(), output, "13"], TimeSpan.FromMinutes(1));
            Check(run.ExitCode == 0, "Native chapter thirteen declarations failed: " + run.Error);
            var native = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
            foreach (var entry in native["chapters"]!.AsArray())
                CompareChapterTwoConfig(RuleCatalog.Create(entry!["id"]!.GetValue<string>()), entry["config"]!);
            overlays += await MapEncounterProbeChecks.OverlayRulesAsync(python, upstream, artifacts, server, native);
        }
        await ChapterTwoCampaignsAsync(13, "Light");
        // Config leaves Siren disabled by default. Keep this gate even though
        // 13-3 calls clear_siren; explicitly enabled Siren takes priority.
        foreach (bool enabled in new[] { false, true })
        foreach (int battle in new[] { 0, 5 })
        {
            var rule = new Campaign133();
            var state = new CampaignState(rule.Map, rule);
            var config = rule.Configure(new() { HasSiren = enabled, HasAmbush = false, EmotionMode = CampaignEmotionMode.Ignore });
            var host = new Host { ObservationFactory = (_, position, mode) => new(
                [new(new(5 - position.Column, 6 - position.Row), new(IsFleet: true, IsCurrentFleet: true))], position, new(0, 0), mode) };
            var operations = new InMapCampaignOperations(host, state, config, default, rule);
            await operations.EnterMapAsync(); await operations.HandleFleetLockAsync();
            await operations.InitializeMapAsync(rule.Map);
            state.ResetMap(); state.Fleet1Location = Cell.Parse("E6"); state.BattleCount = battle;
            var siren = state[Cell.Parse("D6")]; var enemy = state[Cell.Parse("G6")];
            siren.IsSiren = true; enemy.IsEnemy = true; enemy.EnemyGenre = "Light"; enemy.EnemyScale = 1;
            state.RefreshFleetPaths(config);
            Check(await rule.DispatchAsync(new(state, config, operations)) &&
                state.Fleet1Location == (enabled ? siren.Location : enemy.Location) && state.BattleCount == battle + 1,
                "Chapter 13-3 Siren gate or filter short-circuit differs");
        }
        {
            var rule = new Campaign134();
            var state = new CampaignState(rule.Map, rule) { BattleCount = 3 };
            var operations = new ProbeOperations(new Scenario(rule.Id, TrueOperation: "clear_filter_enemy", CombatReturn: false)) { State = state };
            var config = rule.Configure(new() { EmotionMode = CampaignEmotionMode.Ignore });
            Check(await rule.DispatchAsync(new(state, config, operations)) && operations.Calls.Count >= 2 &&
                operations.Calls[0] == "pick_up_ammo" && operations.Calls.Any(call => call.StartsWith("enemy_filter:", StringComparison.Ordinal)),
                "Chapter 13-4 supply did not precede filtered combat");
        }
        Console.WriteLine($"Chapter thirteen: four inherited Config declarations, {overlays} four-server pure-CV overlays, four compiled campaign loops, Siren gates and 13-4 supply passed offline.");
    }
}
