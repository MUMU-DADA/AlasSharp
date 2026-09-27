using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Rules;
using Alas.Engine.Rules.Main;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static partial class CampaignMapCombatChecks
{
    public static async Task ChapterTenChecksAsync(string python, string upstream, string artifacts)
    {
        int overlays = 0;
        foreach (var server in Enum.GetValues<GameServer>())
        {
            string output = Path.Combine(artifacts, server.ToString().ToLowerInvariant() + "-chapter10.json");
            var run = await new ProcessRunner().RunAsync(python,
                [Path.Combine(AppContext.BaseDirectory, "native_main_chapter_reference.py"), upstream, server.ToString().ToLowerInvariant(), output, "10"], TimeSpan.FromMinutes(1));
            Check(run.ExitCode == 0, "Native chapter-ten declarations failed: " + run.Error);
            var native = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
            foreach (var entry in native["chapters"]!.AsArray())
                CompareChapterTwoConfig(RuleCatalog.Create(entry!["id"]!.GetValue<string>()), entry["config"]!);
            overlays += await MapEncounterProbeChecks.OverlayRulesAsync(python, upstream, artifacts, server, native);
        }
        var input = new CampaignConfiguration { Vision = MapVisionOverrides.Default with { Backend = GridDetectionBackend.Perspective },
            BossFleet = 2, Submarine = 1, HasMystery = false };
        foreach (CampaignRule rule in new CampaignRule[] { new Campaign101(), new Campaign102(), new Campaign103(), new Campaign104() })
        {
            var configured = rule.Configure(input);
            Check(configured.BossFleet == 2 && configured.Submarine == 1 && !configured.HasMystery,
                "Chapter ten changed unrelated fleet/mystery input");
            Check(new MapDetectionRules().WithChapter(configured.Vision).Backend ==
                (rule is Campaign102 ? GridDetectionBackend.Homography : GridDetectionBackend.Perspective),
                "Explicit chapter detector backend did not override input or absent override reset it");
        }
        Check(new MapDetectionRules { Backend = GridDetectionBackend.Perspective }.WithChapter(MapVisionOverrides.Default).Backend == GridDetectionBackend.Perspective,
            "Missing vision backend override replaced detector input");
        await ChapterTwoCampaignsAsync(10);
        await TenthChapterMysteryAsync();
        Console.WriteLine($"Chapter ten: four native Config declarations, {overlays} native overlay decisions across four servers, backend override preservation, four compiled campaigns and road-free mystery collection passed offline.");
    }

    private static async Task TenthChapterMysteryAsync()
    {
        var execution = await PrepareDualFleetHookAsync(new Campaign103(), 1);
        var state = execution.Context.State;
        state.ResetMap(); state.BattleCount = 1;
        state.Fleet1Location = Cell.Parse("I2"); state.Fleet2Location = Cell.Parse("C5");
        state[Cell.Parse("I1")].IsMystery = true;
        state[Cell.Parse("H3")].IsEnemy = true;
        state.RefreshFleetPaths(execution.Context.Config);
        Check(await execution.ExecuteBattleAsync() && state.MysteryCount == 1 && !state[Cell.Parse("I1")].IsMystery &&
            state.BattleCount == 2 && state.Fleet1Location == Cell.Parse("H3"),
            "Compiled 10-3 did not collect a reachable mystery after empty road selections and before default battle");
    }
}
