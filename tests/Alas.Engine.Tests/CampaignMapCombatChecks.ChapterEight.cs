using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static partial class CampaignMapCombatChecks
{
    private static async Task ChapterEightChecksAsync(string python, string upstream, string artifacts)
    {
        int overlays = 0;
        JsonNode? bossRoad = null;
        foreach (var server in Enum.GetValues<GameServer>())
        {
            string output = Path.Combine(artifacts, server.ToString().ToLowerInvariant() + "-chapter8.json");
            var run = await new ProcessRunner().RunAsync(python,
                [Path.Combine(AppContext.BaseDirectory, "native_main_chapter_reference.py"), upstream, server.ToString().ToLowerInvariant(), output, "8"], TimeSpan.FromMinutes(1));
            Check(run.ExitCode == 0, "Native chapter-eight declarations failed: " + run.Error);
            var native = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
            foreach (var entry in native["chapters"]!.AsArray())
            {
                var rule = RuleCatalog.Create(entry!["id"]!.GetValue<string>());
                if (entry["bossRoad"] is { } road) bossRoad = road;
                CompareChapterTwoConfig(rule, entry["config"]!);
                Check(rule.Overlays.Ambush == entry["attributes"]!["MAP_AMBUSH_OVERLAY_TRANSPARENCY_THRESHOLD"]!.GetValue<double>() &&
                    rule.Overlays.AirRaid == entry["attributes"]!["MAP_AIR_RAID_OVERLAY_TRANSPARENCY_THRESHOLD"]!.GetValue<double>() &&
                    rule.Overlays.EnemySearching == entry["attributes"]!["MAP_ENEMY_SEARCHING_OVERLAY_TRANSPARENCY_THRESHOLD"]!.GetValue<double>(),
                    "Chapter-eight overlay declaration differs: " + rule.Id);
            }
            overlays += await MapEncounterProbeChecks.OverlayRulesAsync(python, upstream, artifacts, server, native);
        }
        await ChapterTwoCampaignsAsync(8);
        // 8-1 directly invokes brute_clear_boss: the current fleet clears a blocked
        // road before the boss fleet takes over. 7-1 explicitly switches first.
        var execution = await PrepareDualFleetHookAsync(new Alas.Engine.Rules.Main.Campaign81(), 2);
        var state = execution.Context.State;
        state.ResetMap(); state.BattleCount = 4;
        state.Fleet1Location = Cell.Parse("F2"); state.Fleet2Location = Cell.Parse("A2");
        foreach (string cell in new[] { "G1", "G2", "G3" }) state[Cell.Parse(cell)].IsEnemy = true;
        state[Cell.Parse("H1")].IsBoss = true;
        state.RefreshFleetPaths(execution.Context.Config);
        Check(await execution.ExecuteBattleAsync() && state.FleetIndex == bossRoad!["fleet"]!.GetValue<int>() &&
            state.Fleet1Location == Cell.Parse(bossRoad["target"]!.GetValue<string>()) &&
            state.Fleet2Location == Cell.Parse("A2") && state.BattleCount == 5,
            $"Compiled 8-1 boss-road dispatch differs: active={state.FleetIndex}, first={state.Fleet1Location}, second={state.Fleet2Location}, battles={state.BattleCount}");
        Console.WriteLine($"Chapter eight: four inherited Config/swipe/attribute declarations, {overlays} native overlay decisions across four servers, four compiled campaign paths and current-fleet boss-road dispatch passed offline.");
    }
}
