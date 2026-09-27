using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Rules;
using Alas.Engine.Rules.Main;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static partial class CampaignMapCombatChecks
{
    public static async Task ChaptersElevenTwelveAsync(string python, string upstream, string artifacts)
    {
        int overlays = 0;
        foreach (var server in Enum.GetValues<GameServer>())
        {
            string output = Path.Combine(artifacts, server.ToString().ToLowerInvariant() + "-chapters11-12.json");
            var run = await new ProcessRunner().RunAsync(python,
                [Path.Combine(AppContext.BaseDirectory, "native_main_chapter_reference.py"), upstream, server.ToString().ToLowerInvariant(), output, "11", "12"], TimeSpan.FromMinutes(1));
            Check(run.ExitCode == 0, "Native chapters eleven/twelve declarations failed: " + run.Error);
            var native = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
            foreach (var entry in native["chapters"]!.AsArray())
                CompareChapterTwoConfig(RuleCatalog.Create(entry!["id"]!.GetValue<string>()), entry["config"]!);
            overlays += await MapEncounterProbeChecks.OverlayRulesAsync(python, upstream, artifacts, server, native);
        }
        foreach (int chapter in new[] { 11, 12 }) await ChapterTwoCampaignsAsync(chapter);
        await UnconditionalBossRoadAsync();
        await BlockedBossRoadReturnAsync();
        Console.WriteLine($"Chapters eleven/twelve: eight native Config declarations, {overlays} native overlays across four servers, eight compiled campaigns including 11-2 refocus and 12-4 supply, unconditional 11-3 boss-road priority and blocked 11-4 false-return preservation passed offline.");
    }

    private static async Task UnconditionalBossRoadAsync()
    {
        // Both targets are reachable. Native 11-3 still clears the road before
        // switching to the boss fleet; an observed-boss accessibility gate is wrong here.
        var execution = await PrepareDualFleetHookAsync(new Campaign113(), 2);
        var state = execution.Context.State;
        state.ResetMap(); state.BattleCount = 6;
        state.Fleet1Location = Cell.Parse("C6"); state.Fleet2Location = Cell.Parse("E5");
        state[Cell.Parse("D6")].IsEnemy = true;
        state[Cell.Parse("D5")].IsBoss = true;
        state.RefreshFleetPaths(execution.Context.Config);
        Check(execution.Context.Operations.CheckAccessibility(Cell.Parse("D5"), 2), "Boss priority fixture is blocked");
        Check(await execution.ExecuteBattleAsync() && state.BattleCount == 7 && state.FleetIndex == 1 &&
            state.Fleet1Location == Cell.Parse("D6") && state[Cell.Parse("D5")].IsBoss &&
            ((InMapCampaignOperations)execution.Context.Operations).StageReturn is null,
            "Compiled 11-3 skipped the unconditional road battle for a reachable boss");
    }

    private static async Task BlockedBossRoadReturnAsync()
    {
        var rule = new Campaign114();
        var execution = await PrepareDualFleetHookAsync(rule, 2);
        var state = execution.Context.State;
        state.ResetMap(); state.BattleCount = 6;
        state.Fleet1Location = Cell.Parse("E4"); state.Fleet2Location = Cell.Parse("F4");
        state[Cell.Parse("A1")].IsBoss = true;
        state[Cell.Parse("A2")].IsEnemy = state[Cell.Parse("B2")].IsEnemy = true;
        state.RefreshFleetPaths(execution.Context.Config);
        Check(!execution.Context.Operations.CheckAccessibility(Cell.Parse("A1"), 2), "False-return fixture boss is reachable");
        // The blockers are outside the declared road. Native returns false;
        // it must not switch fleet or start probing another boss spawn here.
        Check(!await rule.DispatchAsync(execution.Context) && state.BattleCount == 6 && state.FleetIndex == 1 &&
            state.Fleet1Location == Cell.Parse("E4") && state.Fleet2Location == Cell.Parse("F4") &&
            ((InMapCampaignOperations)execution.Context.Operations).StageReturn is null,
            "Compiled 11-4 continued boss search after a blocked-road false return");
    }
}
