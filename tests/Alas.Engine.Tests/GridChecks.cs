using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static class GridChecks
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true };
    private static readonly Dictionary<string, PropertyInfo> Fields = typeof(CellState).GetProperties()
        .ToDictionary(p => p.Name.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant());
    private static MapTile Tile(string value) => value switch
    {
        "--" => MapTile.Water, "++" => MapTile.Land, "SP" => MapTile.Spawn, "__" => MapTile.SubmarineSpawn,
        "ME" => MapTile.Enemy, "Me" => MapTile.LowPriorityEnemy, "MB" => MapTile.Boss,
        "MM" => MapTile.Mystery, "MA" => MapTile.Ammo, "MS" => MapTile.Siren,
        _ => throw new InvalidOperationException("Unknown fixture token")
    };
    public static async Task RunAsync(string python, string upstream, string artifacts)
    {
        var source = CellState.Source;
        if (Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(upstream, source.Path)))) != source.Sha256)
            throw new InvalidOperationException("GridInfo source drifted");
        string output = Path.Combine(artifacts, "native-grid.json");
        var result = await new ProcessRunner().RunAsync(python,
            [Path.Combine(AppContext.BaseDirectory, "native_grid_reference.py"), upstream, output], TimeSpan.FromSeconds(90));
        if (result.ExitCode != 0) throw new InvalidOperationException("Native grid reference failed: " + result.Error);
        int count = 0;
        foreach (var expected in JsonNode.Parse(await File.ReadAllTextAsync(output))!.AsArray())
        {
            var sample = expected!["sample"]!;
            var grid = new CellState(new(1, 1), Tile(sample["token"]!.GetValue<string>()));
            var trigger = new CellState(new(2, 1), MapTile.Water) { IsMechanismTrigger = true };
            var block = new CellState(new(3, 1), MapTile.Water) { IsMechanismBlock = true };
            foreach (var pair in sample["before"]!.AsObject())
            {
                var property = Fields[pair.Key.Replace("_", "", StringComparison.Ordinal)];
                property.SetValue(grid, pair.Value?.Deserialize(property.PropertyType, Json));
            }
            if (grid.IsMechanismTrigger) { grid.MechanismTrigger = [grid, trigger]; grid.MechanismBlock = [block]; }
            var names = expected["frames"]![0]!["values"]!.AsObject().Select(p => p.Key).ToArray();
            JsonNode Snapshot()
            {
                var values = names.ToDictionary(name => name, name => name == "str" ? grid.Encode() :
                    Fields[name.Replace("_", "", StringComparison.Ordinal)].GetValue(grid));
                return JsonSerializer.SerializeToNode(new { values, covered = grid.CoveredOffsets().Select(c => new[] { c.X, c.Y }),
                    trigger = trigger.IsMechanismTrigger, block = block.IsMechanismBlock,
                    trigger_group = grid.MechanismTrigger is not null, block_group = grid.MechanismBlock is not null, distance = grid.DistanceTo(block) })!;
            }
            var frames = new JsonArray { Snapshot() };
            bool merged = grid.Merge(sample["info"]!.Deserialize<CellObservation>(Json)!, Enum.Parse<MapScanMode>(sample["mode"]!.GetValue<string>(), true));
            frames.Add(Snapshot());
            grid.LoadDeclaration(Tile(sample["reload"]!.GetValue<string>())); frames.Add(Snapshot());
            grid.WipeOut(); frames.Add(Snapshot());
            grid.Reset(); frames.Add(Snapshot());
            if (merged != expected["merged"]!.GetValue<bool>() || !JsonNode.DeepEquals(frames, expected["frames"]))
            {
                await File.WriteAllTextAsync(Path.Combine(artifacts, "grid-mismatch.json"), new JsonObject
                    { ["expected"] = expected.DeepClone(), ["frames"] = frames, ["merged"] = merged }.ToJsonString());
                throw new InvalidOperationException($"Grid state differs at case {count}; inspect grid-mismatch.json");
            }
            count++;
        }
        var map = new MapDefinition("J1", "-- ++ SP __ ME Me MB MM MA MS", [], [], []);
        var state = new CampaignState(map);
        var other = new CampaignState(map);
        if (state.Cells[0] != other.Cells[0] || state.Cells[0] == state.Cells[1] ||
            new HashSet<CellState> { state.Cells[0], other.Cells[0] }.Count != 1)
            throw new InvalidOperationException("Grid coordinate equality differs from upstream");
        if (state.Cells.Any(c => c.Weight != 10 || c.IsEnemy || c.IsBoss || c.IsSiren) ||
            !state[new(5, 1)].MayEnemy || !state[new(6, 1)].MayEnemy || !state[new(4, 1)].IsSubmarineSpawnPoint || !state[new(10, 1)].MaySiren)
            throw new InvalidOperationException("Campaign map declarations became observations or lost native defaults");
        state[new(1, 1)].IsFleet = state[new(1, 1)].IsCurrentFleet = true;
        state.ResetCurrentFleet();
        if (!state[new(1, 1)].IsFleet || state[new(1, 1)].IsCurrentFleet) throw new InvalidOperationException("Fleet reset changed non-current fleet state");
        state[new(5, 1)].Merge(new(IsEnemy: true, EnemyScale: 2, EnemyGenre: "Light"));
        if (other.Cells.Any(c => c.IsEnemy || c.IsFleet)) throw new InvalidOperationException("Sortie map state leaked into another run");
        state.ResetMap();
        if (state.Cells.Any(c => c.IsEnemy || c.IsFleet) || !state[new(5, 1)].MayEnemy) throw new InvalidOperationException("Reset changed declarations or kept observations");
        Console.WriteLine($"Native GridInfo: {count} scenarios / {count * 5} state snapshots passed; map declarations, reset and sortie isolation passed. No routing or device validation.");
    }
}
