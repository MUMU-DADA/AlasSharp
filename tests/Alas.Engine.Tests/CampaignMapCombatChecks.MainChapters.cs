using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static partial class CampaignMapCombatChecks
{
    public static async Task MainChapterChecksAsync(string python, string upstream, string artifacts)
    {
        int overlays = 0;
        foreach (var server in Enum.GetValues<GameServer>())
        {
            string output = Path.Combine(artifacts, server.ToString().ToLowerInvariant() + "-main-chapters.json");
            var run = await new ProcessRunner().RunAsync(python,
                [Path.Combine(AppContext.BaseDirectory, "native_main_chapter_reference.py"), upstream, server.ToString().ToLowerInvariant(), output], TimeSpan.FromMinutes(1));
            Check(run.ExitCode == 0, "Native main-chapter reference failed: " + run.Error);
            var native = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
            foreach (var source in new[] { MapEncounterProbe.AmbushSource, MapEnemySearching.Source, GridRecognitionRules.Source })
                Check(native["sources"]![source.Path]!.GetValue<string>() == source.Sha256, "Chapter overlay/config source drifted: " + source.Path);
            foreach (var entry in native["chapters"]!.AsArray())
            {
                var rule = RuleCatalog.Create(entry!["id"]!.GetValue<string>());
                CompareChapterTwoConfig(rule, entry["config"]!);
                Check(rule.Overlays.Ambush == entry["attributes"]!["MAP_AMBUSH_OVERLAY_TRANSPARENCY_THRESHOLD"]!.GetValue<double>() &&
                    rule.Overlays.AirRaid == entry["attributes"]!["MAP_AIR_RAID_OVERLAY_TRANSPARENCY_THRESHOLD"]!.GetValue<double>() &&
                    rule.Overlays.EnemySearching == entry["attributes"]!["MAP_ENEMY_SEARCHING_OVERLAY_TRANSPARENCY_THRESHOLD"]!.GetValue<double>(),
                    "Campaign class attributes were confused with Config or lost inheritance: " + rule.Id);
            }
            overlays += await MapEncounterProbeChecks.OverlayRulesAsync(python, upstream, artifacts, server, native);
        }
        await ChapterTwoCampaignsAsync(4);
        await ChapterTwoCampaignsAsync(5);
        Console.WriteLine($"Main chapters: eight inherited Config/attribute declarations, {overlays} native overlay decisions with real pure-CV colors across four servers, actual session composition and eight compiled campaign/carrier/boss-return paths passed offline; no live acceptance.");
    }
}
