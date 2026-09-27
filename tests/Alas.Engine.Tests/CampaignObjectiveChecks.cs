using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static partial class CampaignObjectiveChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static async Task Rejects<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private sealed record Objective(string Achievement, int Star, bool Story, double Percentage, bool[] Stars, bool Safe);
    private sealed record Progression(string Name, string Folder, bool Across, string[] Custom, string[] Files);
    private sealed record Targeting(bool Siren, bool Fortress, int Fleet2, Dictionary<string, object>[] Cells);

    public static async Task RunAsync(string python, string upstream, string artifacts)
    {
        var objectives = new List<Objective>();
        foreach (var achievement in Enum.GetValues<MapAchievement>())
        foreach (int star in Enumerable.Range(0, 4))
        foreach (bool story in new[] { false, true })
        foreach (double percentage in new[] { 0, .95, .95001, .99 })
        foreach (bool safe in new[] { false, true })
        for (int flags = 0; flags < 8; flags++)
            objectives.Add(new(achievement.Name(), star, story, percentage,
                Enumerable.Range(0, 3).Select(i => (flags & 1 << i) != 0).ToArray(), safe));
        var progression = new List<Progression>();
        string[] names = ["1-1", "1-4", "16-4", "campaign_7_4", "A1", "A3", "B3", "SP4", "HT5", "T6", "b-1", "HT-1", " sp 2\t", "unknown"];
        foreach (string name in names)
        foreach (string folder in new[] { "campaign_main", "event_fixture" })
        foreach (bool across in new[] { false, true })
        foreach (string[] custom in new string[][] { [], ["A3 > SP4 > SP5"], ["A3 > B1", "A3 > C1"], ["HT5 > end"] })
        foreach (string[] files in new string[][] { [], ["a2", "b1", "c1", "sp3", "sp4", "sp5", "ht6", "end"] })
            progression.Add(new(name, folder, across, custom, files));
        var random = new Random(927);
        var targeting = new List<Targeting>();
        for (int i = 0; i < 256; i++)
            targeting.Add(new((i & 1) != 0, (i & 2) != 0, (i & 4) != 0 ? 2 : 0,
                Enumerable.Range(0, 6).Select(_ => new Dictionary<string, object>
                {
                    ["is_enemy"] = random.Next(2) == 0, ["is_siren"] = random.Next(3) == 0,
                    ["is_fortress"] = random.Next(3) == 0, ["is_boss"] = random.Next(4) == 0,
                    ["cost"] = random.Next(3) == 0 ? 9999 : random.Next(1, 10), ["cost_2"] = random.Next(1, 4), ["weight"] = random.Next(1, 4)
                }).ToArray()));
        string inputs = Path.Combine(artifacts, "objectives-input.json"), output = Path.Combine(artifacts, "native-objectives.json");
        await File.WriteAllTextAsync(inputs, JsonSerializer.Serialize(new { objectives, progression, targeting }, Json));
        var process = await new ProcessRunner().RunAsync(python,
            [Path.Combine(AppContext.BaseDirectory, "native_objectives_reference.py"), upstream, inputs, output], TimeSpan.FromMinutes(2));
        Check(process.ExitCode == 0, "Native objective reference failed: " + process.Error);
        var reference = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
        foreach (var source in new[] { CampaignObjectives.Source, CampaignMapCombat.Source, CampaignState.InitializationSource, CampaignAchievement.Source })
            Check(reference["sources"]![source.Path]!.GetValue<string>() == source.Sha256, "Objective source drifted");
        for (int i = 0; i < objectives.Count; i++)
        {
            var sample = objectives[i]; var expected = reference["objectives"]![i]!;
            var info = new CampaignMapInfo(1, sample.Percentage, sample.Stars[0], sample.Stars[1], sample.Stars[2], sample.Safe, true);
            var config = CampaignObjectives.Apply(new() { MapAchievement = CampaignObjectives.Parse(sample.Achievement),
                AllEnemiesStar = sample.Star, HasMapStory = sample.Story, PreparationInfo = info });
            Check(config.ClearAllThisTime == expected["clearAll"]!.GetValue<bool>() && config.HasMapStory == expected["story"]!.GetValue<bool>() &&
                CampaignObjectives.Reached(config.MapAchievement, info) == expected["stop"]!.GetValue<bool>(), "Objective state differs: " + i);
        }
        for (int i = 0; i < progression.Count; i++)
        {
            var sample = progression[i];
            Check(CampaignObjectives.NextStage(sample.Name, sample.Folder, new() { StageIncreaseAcrossAB = sample.Across,
                StageIncreaseCustom = sample.Custom.ToImmutableArray() }, sample.Files) == reference["progression"]![i]!.GetValue<string>(),
                "Stage progression differs: " + i);
        }
        for (int i = 0; i < targeting.Count; i++)
        {
            var sample = targeting[i]; var expected = reference["targeting"]![i]!;
            var state = new CampaignState(new MapDefinition("F1", "-- -- -- -- -- --", [], [], []));
            for (int j = 0; j < sample.Cells.Length; j++)
                foreach (var patch in sample.Cells[j])
                {
                    string property = string.Concat(patch.Key.Split('_').Select(s => char.ToUpperInvariant(s[0]) + s[1..]));
                    var member = typeof(CellState).GetProperty(property)!;
                    member.SetValue(state.Cells[j], Convert.ChangeType(patch.Value, member.PropertyType));
                }
            var config = new CampaignConfiguration { HasSiren = sample.Siren, HasFortress = sample.Fortress, Fleet2 = sample.Fleet2 };
            bool Matches(CellState? selected, string key) => selected is null ? expected[key]!.AsArray().Count == 0 :
                expected[key]!.AsArray().Any(n => n!.GetValue<string>() == selected.Location.ToString());
            Check(Matches(CampaignTargeting.Siren(state, config), "siren") &&
                Matches(CampaignTargeting.AnyEnemyBySecondFleetCost(state, config), "any"),
                "Native full-clear target differs: " + i);
        }
        await StoreChecksAsync(artifacts);
        await StopChecksAsync();
        await CampaignStageSelectorChecks.ObjectiveTaskChecksAsync();
        await SessionChecksAsync(python, upstream, artifacts);
        Console.WriteLine($"Campaign objectives: {objectives.Count} native star/stop states, {progression.Count} stage transitions, {targeting.Count} target selections; config/stop/task checks passed offline.");
    }
}
