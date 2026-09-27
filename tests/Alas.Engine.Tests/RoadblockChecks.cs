using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static class RoadblockChecks
{
    private sealed record Patch(string Cell, Dictionary<string, bool> Flags);
    private sealed record Sample(string Shape, string Tiles, string[][] Walls, string[][] Portals, Patch[] Patches,
        string Target, string Fleet1 = "A1", string Fleet2 = "C3", int Active = 1, int Fleet = 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    public static async Task RunAsync(string python, string upstream, string artifacts)
    {
        Check(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(
            Path.Combine(upstream, FleetRoles.BossSource.Path)))) == FleetRoles.BossSource.Sha256, "Fleet role source drifted");
        var samples = new List<Sample>();
        Patch Flag(string cell, params string[] flags) => new(cell, flags.ToDictionary(key => key, _ => true));
        const string tiles = "-- -- --\n-- -- --\n-- -- --";
        // Ordered equal-size solutions, serial blockers, destination enemy, fixed blockers, and directed shortcut.
        samples.Add(new("C3", tiles, [], [], [Flag("B1", "is_enemy"), Flag("A2", "is_enemy")], "C3"));
        samples.Add(new("D1", "-- -- -- --", [], [], [Flag("B1", "is_enemy"), Flag("C1", "is_enemy"), Flag("D1", "is_boss")],
            "D1", Fleet2: "D1"));
        samples.Add(new("C3", tiles, [], [["A1", "C3"]], [Flag("C3", "is_enemy")], "C3"));
        foreach (string blocker in new[] { "is_land", "is_mechanism_block", "is_siren", "is_boss", "is_fortress" })
            samples.Add(new("C1", "-- -- --", [], [], [Flag("B1", blocker, "is_enemy")], "C1", Fleet2: "C1"));
        var random = new Random(270927);
        var cells = Enumerable.Range(1, 3).SelectMany(y => Enumerable.Range(1, 3).Select(x => new Cell(x, y))).ToArray();
        for (int i = 0; i < 240; i++)
        {
            var patches = cells.OrderBy(_ => random.Next()).Take(4).Select(c => Flag(c.ToString(), "is_enemy")).ToList();
            foreach (string kind in new[] { "is_land", "is_mechanism_block", "is_siren", "is_fortress", "is_boss" })
                if (random.Next(4) == 0) patches.Add(Flag(cells[random.Next(cells.Length)].ToString(), kind));
            var walls = cells.Where(c => c.Column < 3 && random.Next(5) == 0)
                .Select(c => new[] { c.ToString(), new Cell(c.Column + 1, c.Row).ToString() }).ToArray();
            string[][] portals = i % 3 == 0 ? [[cells[random.Next(9)].ToString(), cells[random.Next(9)].ToString()]] : [];
            samples.Add(new("C3", tiles, walls, portals, patches.ToArray(), cells[random.Next(9)].ToString(),
                Active: i % 2 + 1, Fleet: i / 2 % 2 + 1));
        }
        string inputs = Path.Combine(artifacts, "roadblocks-input.json"), output = Path.Combine(artifacts, "native-roadblocks.json");
        await File.WriteAllTextAsync(inputs, JsonSerializer.Serialize(samples, Json));
        var process = await new ProcessRunner().RunAsync(python,
            [Path.Combine(AppContext.BaseDirectory, "native_roadblock_reference.py"), upstream, inputs, output], TimeSpan.FromMinutes(2));
        Check(process.ExitCode == 0, "Native roadblock reference failed: " + process.Error);
        var reference = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
        Check(reference["source"]!.GetValue<string>() == MapRoadblocks.Source.Sha256, "Roadblock source drifted");
        int nativeMutations = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];
            var map = new MapDefinition(sample.Shape, sample.Tiles, [], [], [],
                walls: sample.Walls.Select(e => new MapEdge(e[0], e[1])), portals: sample.Portals.Select(e => new MapEdge(e[0], e[1])));
            var state = new CampaignState(map);
            state.InitializeMapData(new(PoorMapData: true, Walls: true, Portals: true));
            foreach (var patch in sample.Patches)
                foreach (var flag in patch.Flags)
                {
                    string property = string.Concat(flag.Key.Split('_').Select(s => char.ToUpperInvariant(s[0]) + s[1..]));
                    typeof(CellState).GetProperty(property)!.SetValue(state[Cell.Parse(patch.Cell)], flag.Value);
                }
            state.Fleet1Location = Cell.Parse(sample.Fleet1); state.Fleet2Location = Cell.Parse(sample.Fleet2); state.FleetIndex = sample.Active;
            state.Paths.ComputeFleetCosts([new(1, state.Fleet1Location), new(2, state.Fleet2Location)],
                sample.Active == 1 ? state.Fleet1Location.Value : state.Fleet2Location.Value, false);
            // Snapshot all publicly readable cell properties, including predecessor, fleet costs and mechanism groups.
            var properties = typeof(CellState).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead).ToArray();
            var snapshot = state.Cells.Select(c => properties.Select(p => p.GetValue(c)).ToArray()).ToArray();
            var actual = MapRoadblocks.Find(state, Cell.Parse(sample.Target), sample.Fleet);
            var expected = reference["results"]![i]!;
            Check(actual.Reachable == expected["reachable"]!.GetValue<bool>() &&
                actual.Enemies.Select(c => c.ToString()).SequenceEqual(expected["enemies"]!.AsArray().Select(n => n!.GetValue<string>())),
                "Minimum roadblock set differs: " + i);
            Check(state.FleetIndex == sample.Active && state.Cells.Select((c, index) =>
                properties.Select(p => p.GetValue(c)).SequenceEqual(snapshot[index])).All(v => v), "Roadblock query mutated authoritative state");
            if (expected["mutated"]!.GetValue<bool>()) nativeMutations++;
        }
        Check(nativeMutations > 0, "Oracle did not expose upstream hypothetical path mutations");
        // Dense corridor is linear in map size here; native Cartesian products are intentionally not run.
        var dense = new CampaignState(new MapDefinition("BX1", string.Join(' ', Enumerable.Repeat("--", 76)), [], [], []));
        dense.InitializeMapData(new(PoorMapData: true)); dense.Fleet1Location = new(1, 1);
        foreach (var cell in dense.Cells.Where(c => c.Location.Column is > 1 and < 76)) cell.IsEnemy = true;
        Check(MapRoadblocks.Find(dense, new(76, 1), 1).Enemies.Count == 74, "Large minimal roadblock set overflowed");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        bool rejected = false;
        try { MapRoadblocks.Find(dense, new(76, 1), 1, cancelled.Token); } catch (OperationCanceledException) { rejected = true; }
        Check(rejected, "Roadblock search ignored cancellation");
        Console.WriteLine($"Roadblocks: {samples.Count} native sets matched; {nativeMutations} hypothetical native path mutations avoided; dense/cancellation checks passed.");
    }
}
