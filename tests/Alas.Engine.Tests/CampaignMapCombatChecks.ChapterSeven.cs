using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static partial class CampaignMapCombatChecks
{
    private static async Task ChapterSevenChecksAsync(string python, string upstream, string artifacts)
    {
        int overlays = 0;
        foreach (var server in Enum.GetValues<GameServer>())
        {
            string output = Path.Combine(artifacts, server.ToString().ToLowerInvariant() + "-chapter7.json");
            var run = await new ProcessRunner().RunAsync(python,
                [Path.Combine(AppContext.BaseDirectory, "native_main_chapter_reference.py"), upstream, server.ToString().ToLowerInvariant(), output, "7"], TimeSpan.FromMinutes(1));
            Check(run.ExitCode == 0, "Native chapter-seven declarations failed: " + run.Error);
            var native = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
            foreach (var entry in native["chapters"]!.AsArray())
            {
                var rule = RuleCatalog.Create(entry!["id"]!.GetValue<string>());
                CompareChapterTwoConfig(rule, entry["config"]!);
                Check(rule.Overlays.Ambush == entry["attributes"]!["MAP_AMBUSH_OVERLAY_TRANSPARENCY_THRESHOLD"]!.GetValue<double>() &&
                    rule.Overlays.AirRaid == entry["attributes"]!["MAP_AIR_RAID_OVERLAY_TRANSPARENCY_THRESHOLD"]!.GetValue<double>() &&
                    rule.Overlays.EnemySearching == entry["attributes"]!["MAP_ENEMY_SEARCHING_OVERLAY_TRANSPARENCY_THRESHOLD"]!.GetValue<double>(),
                    "Chapter-seven overlay declaration differs: " + rule.Id);
            }
            overlays += await MapEncounterProbeChecks.OverlayRulesAsync(python, upstream, artifacts, server, native);
        }
        await ChapterTwoCampaignsAsync(7);
        await SeventhChapterDualFleetAsync();
        Console.WriteLine($"Chapter seven: four Config/attribute declarations, {overlays} native overlay decisions using pure CV across four servers, actual session composition, four compiled campaign paths and five dual-fleet boss hooks passed offline.");
    }

    private static async Task SeventhChapterDualFleetAsync()
    {
        foreach (string secondCell in new[] { "A3", "G3" })
        foreach (int bossFleet in new[] { 1, 2 })
        {
            var rule = new Alas.Engine.Rules.Main.Campaign72();
            var execution = await PrepareDualFleetHookAsync(rule, bossFleet);
            var state = execution.Context.State;
            state.ResetMap(); state.BattleCount = 5;
            state.Fleet1Location = Cell.Parse("H1"); state.Fleet2Location = Cell.Parse(secondCell);
            var mystery = Cell.Parse(secondCell == "A3" ? "A2" : "H3");
            state[mystery].IsMystery = true; state[Cell.Parse("D3")].IsBoss = true;
            state.RefreshFleetPaths(execution.Context.Config);
            bool ended = false;
            try { await execution.ExecuteBattleAsync(); } catch (CampaignEndedException) { ended = true; }
            var operations = (InMapCampaignOperations)execution.Context.Operations;
            Check(ended && operations.StageReturn?.Combats is [{ Rank.IsWinningRank: true, Return: CombatReturn.InStage }] &&
                state.MysteryCount == 1 && !state[mystery].IsMystery && state.Fleet2Location == mystery &&
                state.FleetIndex == bossFleet && state.BattleCount == 5,
                "Compiled 7-2 did not collect the ignored mystery with fleet 2 before switching to the selected boss fleet");
        }
        var brute = await PrepareDualFleetHookAsync(new Alas.Engine.Rules.Main.Campaign71(), 2);
        var bruteState = brute.Context.State;
        bruteState.ResetMap(); bruteState.BattleCount = 5;
        bruteState.Fleet1Location = Cell.Parse("F3"); bruteState.Fleet2Location = Cell.Parse("E3");
        bruteState[Cell.Parse("G3")].IsEnemy = true; bruteState[Cell.Parse("H3")].IsBoss = true;
        bruteState.RefreshFleetPaths(brute.Context.Config);
        Check(await brute.ExecuteBattleAsync() && bruteState.FleetIndex == 2 && bruteState.Fleet2Location == Cell.Parse("G3") &&
            bruteState.Fleet1Location == Cell.Parse("F3") && bruteState.BattleCount == 6,
            "Compiled 7-1 cleared the boss road before switching to its selected fleet");
    }

    private static async Task<CampaignExecution> PrepareDualFleetHookAsync(CampaignRule rule, int bossFleet)
    {
        var spawns = new CampaignState(rule.Map).Cells.Where(cell => cell.IsSpawnPoint).Take(2).Select(cell => cell.Location).ToArray();
        var host = new Host { SimulateFleetSwitch = true, ObservationFactory = (_, position, mode) => new(
            spawns.Select((cell, index) => new MapCellObservation(new(cell.Column - position.Column, cell.Row - position.Row),
                new(IsFleet: true, IsCurrentFleet: index == 0))).ToArray(), position, new(0, 0), mode) };
        var execution = new CampaignExecution(rule, new() { Fleet2 = 2, BossFleet = bossFleet, HasAmbush = false,
            EmotionMode = CampaignEmotionMode.Ignore, UseFleetLock = false },
            (state, config) => new InMapCampaignOperations(host, state, config, default, rule));
        var operations = execution.Context.Operations;
        await operations.EnterMapAsync(); await operations.HandleFleetLockAsync(); await operations.InitializeMapAsync(rule.Map);
        return execution;
    }
}
