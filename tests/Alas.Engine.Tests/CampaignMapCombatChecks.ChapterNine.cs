using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Rules;
using Alas.Engine.Rules.Main;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static partial class CampaignMapCombatChecks
{
    public static async Task ChapterNineChecksAsync(string python, string upstream, string artifacts)
    {
        int overlays = 0;
        foreach (var server in Enum.GetValues<GameServer>())
        {
            string output = Path.Combine(artifacts, server.ToString().ToLowerInvariant() + "-chapter9.json");
            var run = await new ProcessRunner().RunAsync(python,
                [Path.Combine(AppContext.BaseDirectory, "native_main_chapter_reference.py"), upstream, server.ToString().ToLowerInvariant(), output, "9"], TimeSpan.FromMinutes(1));
            Check(run.ExitCode == 0, "Native chapter-nine declarations failed: " + run.Error);
            var native = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
            foreach (var entry in native["chapters"]!.AsArray())
                CompareChapterTwoConfig(RuleCatalog.Create(entry!["id"]!.GetValue<string>()), entry["config"]!);
            overlays += await MapEncounterProbeChecks.OverlayRulesAsync(python, upstream, artifacts, server, native);
        }
        var mysteryInput = new CampaignConfiguration { HasMystery = false };
        Check(new Campaign92().Configure(mysteryInput).HasMystery && !new Campaign93().Configure(mysteryInput).HasMystery,
            "Chapter mystery override did not preserve inherited input semantics");
        await DynamicChapterWeightsAsync(python, upstream, artifacts);
        await ChapterTwoCampaignsAsync(9);
        Console.WriteLine($"Chapter nine: four Config/calibration/edge declarations, {overlays} native overlay decisions, seven persistent weight/target transitions, atomic weight validation, sortie isolation and four compiled campaign paths passed offline.");
    }

    private static async Task DynamicChapterWeightsAsync(string python, string upstream, string artifacts)
    {
        string output = Path.Combine(artifacts, "chapter9-weights.json");
        var run = await new ProcessRunner().RunAsync(python,
            [Path.Combine(AppContext.BaseDirectory, "native_chapter_nine_reference.py"), upstream, output], TimeSpan.FromMinutes(1));
        Check(run.ExitCode == 0, "Native dynamic weights failed: " + run.Error);
        var execution = await PrepareDualFleetHookAsync(new Campaign92(), 2);
        var state = execution.Context.State;
        var initial = state.Cells.Select(cell => cell.Weight).ToImmutableArray();
        foreach (var sample in JsonNode.Parse(await File.ReadAllTextAsync(output))!.AsArray())
        {
            state.ResetMap(); state.BattleCount = 0;
            state.Fleet1Location = Cell.Parse("C1");
            state.Fleet2Location = sample!["second"] is { } second ? Cell.Parse(second.GetValue<string>()) : null;
            state[Cell.Parse("C2")].IsEnemy = state[Cell.Parse("F1")].IsEnemy = true;
            state.RefreshFleetPaths(execution.Context.Config);
            bool value = await execution.ExecuteBattleAsync();
            Check(value == sample["value"]!.GetValue<bool>() && state.BattleCount == 1 &&
                state.Fleet1Location?.ToString() == sample["target"]!.GetValue<string>() &&
                state.Cells.Select(cell => cell.Weight).SequenceEqual(sample["weights"]!.AsArray().Select(weight => weight!.GetValue<double>())),
                "Actual compiled hook did not apply persistent native weight changes before target selection: " + sample["second"]);
        }
        var before = state.Cells.Select(cell => (cell.Weight, cell.Cost, cell.IsEnemy, cell.IsFleet)).ToArray();
        foreach (var invalid in new ImmutableArray<double>[] { default, [], [1], initial.SetItem(0, double.NaN), initial.SetItem(initial.Length - 1, double.PositiveInfinity) })
        {
            bool rejected = false;
            try { state.SetWeights(invalid); } catch (ArgumentException) { rejected = true; }
            Check(rejected && state.Cells.Select(cell => (cell.Weight, cell.Cost, cell.IsEnemy, cell.IsFleet)).SequenceEqual(before),
                "Invalid weight update partially mutated campaign state");
        }
        state.SetWeights(initial.Select(weight => weight + .5).ToImmutableArray());
        var fresh = new CampaignState(execution.Context.State.Map);
        Check(fresh.Cells.Select(cell => cell.Weight).SequenceEqual(initial) && state.Map.Weights.SequenceEqual(initial),
            "Dynamic weights leaked into another sortie or compiled MAP declaration");
    }
}
