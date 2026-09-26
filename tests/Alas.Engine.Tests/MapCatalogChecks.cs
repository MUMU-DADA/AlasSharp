using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Rules;
using Alas.Engine.Rules.Main;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static class MapCatalogChecks
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private static readonly Dictionary<string, PropertyInfo> Fields = typeof(CellState).GetProperties()
        .ToDictionary(p => p.Name.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant());
    private static bool[][] Flags(CampaignState state) => state.Cells.Select(g => new[]
    {
        g.IsLand, g.IsSpawnPoint, g.IsSubmarineSpawnPoint, g.MayEnemy, g.MayBoss,
        g.MayMystery, g.MayAmmo, g.MaySiren, g.MayAmbush
    }).ToArray();
    private static string[] Cells(IEnumerable<Cell> cells) => cells.Select(c => c.ToString()).ToArray();
    private static object Snapshot(MapDefinition map)
    {
        var state = new CampaignState(map);
        var normal = Flags(state);
        state.LoadMapData(useLoop: true);
        var loop = Flags(state);
        state.Paths.InitializeConnections(walls: true, portals: true);
        return new
        {
            name = map.Name, shape = new[] { map.Shape.Column, map.Shape.Row }, flags = normal, loop_flags = loop,
            has_loop = !map.LoopTiles.IsEmpty, weights = map.Weights, cameras = Cells(map.Cameras),
            spawn_cameras = Cells(map.SpawnCameras), waves = map.Waves, loop_waves = map.LoopWaves,
            covered = Cells(map.Covered),
            camera_sight = new[] { map.CameraSight.Left, map.CameraSight.Top, map.CameraSight.Right, map.CameraSight.Bottom },
            swipe_preset = map.SwipePreset is { } swipe ? new[] { swipe.X, swipe.Y } : null,
            connections = state.Paths.Connections.ToDictionary(p => p.Key.ToString(), p => Cells(p.Value).Order(StringComparer.Ordinal).ToArray()),
            portals = map.Portals.Select(p => new[] { p.From.ToString(), p.To.ToString() }),
            land_based = map.Mechanisms.LandBased.Select(p => new[] { p.Origin.ToString(), p.Direction.ToString().ToLowerInvariant() }),
            mazes = map.Mechanisms.Mazes.Select(g => Cells(g)),
            fortress = new[] { Cells(map.Mechanisms.FortressEnemies), Cells(map.Mechanisms.FortressBlocks) },
            bouncing = map.Mechanisms.BouncingRoutes.Select(g => Cells(g)),
            ignored_count = map.IgnoredPredictions.Length,
            grid_class = state.Cells[0] is W15CellState ? "campaign.campaign_main.campaign_15_base.W15GridInfo" : "module.map_detection.grid_info.GridInfo"
        };
    }

    public static async Task RunAsync(string python, string upstream, string artifacts)
    {
        foreach (var source in CampaignMapCatalog.Sources)
            if (Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(upstream, source.Path)))) != source.Sha256)
                throw new InvalidOperationException($"Compiled map dependency drifted: {source.Path}");
        var declaredSources = CampaignMapCatalog.Sources.Select(s => s.Path).Where(p => p.StartsWith("campaign/", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        var actualSources = Directory.EnumerateFiles(Path.Combine(upstream, "campaign"), "*.py", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(upstream, p).Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
        if (!declaredSources.SetEquals(actualSources)) throw new InvalidOperationException("Campaign source inventory drifted");
        string output = Path.Combine(artifacts, "native-maps.json");
        var result = await new ProcessRunner().RunAsync(python,
            [Path.Combine(AppContext.BaseDirectory, "native_map_reference.py"), upstream, output], TimeSpan.FromSeconds(120));
        if (result.ExitCode != 0) throw new InvalidOperationException("Native map reference failed: " + result.Error);
        var reference = JsonNode.Parse(await File.ReadAllTextAsync(output))!.AsObject();
        var native = reference["records"]!.AsObject();
        if (!CampaignMapCatalog.Ids.SequenceEqual(native.Select(p => p.Key).Order(StringComparer.Ordinal)))
            throw new InvalidOperationException("Native map inventory differs from the compiled catalog");
        int compared = 0, probes = 0, fullModules = 0, declarationOnly = 0;
        var failures = new JsonArray();
        foreach (string id in CampaignMapCatalog.Ids)
        {
            var map = CampaignMapCatalog.Get(id).Map;
            var expected = native[id]!.AsObject();
            var actual = JsonSerializer.SerializeToNode(Snapshot(map), Json)!.AsObject();
            foreach (var pair in actual)
            {
                compared++;
                if (!JsonNode.DeepEquals(pair.Value, expected[pair.Key]))
                    failures.Add(new JsonObject { ["id"] = id, ["field"] = pair.Key,
                        ["csharp"] = pair.Value?.DeepClone(), ["native"] = expected[pair.Key]?.DeepClone() });
            }
            foreach (var probe in expected["ignored"]!.AsArray())
            {
                var cell = Cell.Parse(probe!["cell"]!.GetValue<string>());
                var info = probe["info"]!.Deserialize<CellObservation>(Json)!;
                bool matched = map.IgnoredPredictions.Any(rule => rule.Cell == cell && rule.Matches(info));
                if (matched != probe["matches"]!.GetValue<bool>())
                    failures.Add(new JsonObject { ["id"] = id, ["field"] = "ignored", ["probe"] = probe.DeepClone() });
                probes++;
            }
            if (expected["scope"]!.GetValue<string>() == "full_module") fullModules++;
            else declarationOnly++;
        }
        await File.WriteAllTextAsync(Path.Combine(artifacts, "map-mismatches.json"), failures.ToJsonString());
        if (failures.Count != 0) throw new InvalidOperationException($"Native MAP mismatch: {failures.Count}; inspect map-mismatches.json");

        int custom = 0;
        foreach (var sample in reference["custom_grid"]!.AsArray())
        {
            var map = new MapDefinition("C1", "MS ++ ++", [], [], [], createCell: static (cell, tile) => new W15CellState(cell, tile));
            var state = new CampaignState(map);
            var target = state[new(1, 1)];
            var direct = new W15CellState(new(1, 1), MapTile.Siren);
            foreach (var pair in sample!["before"]!.AsObject())
            {
                var property = Fields[pair.Key.Replace("_", "", StringComparison.Ordinal)];
                var value = pair.Value?.Deserialize(property.PropertyType, Json);
                property.SetValue(target, value); property.SetValue(direct, value);
            }
            var info = sample["info"]!.Deserialize<CellObservation>(Json)!;
            var mode = Enum.Parse<MapScanMode>(sample["mode"]!.GetValue<string>(), true);
            bool merged = direct.Merge(info, mode);
            var observations = new List<MapCellObservation> { new(new(0, 0), info) };
            for (int x = 1; x <= sample["conflicts"]!.GetValue<int>(); x++) observations.Add(new(new(x, 0), new(IsBoss: true)));
            var applied = state.ApplyObservation(new(observations, new(1, 1), new(0, 0), mode));
            JsonNode Values(CellState grid, JsonNode expected) => JsonSerializer.SerializeToNode(expected.AsObject().ToDictionary(
                p => p.Key, p => Fields[p.Key.Replace("_", "", StringComparison.Ordinal)].GetValue(grid)))!;
            if (merged != sample["merged"]!.GetValue<bool>() || applied.Accepted != sample["accepted"]!.GetValue<bool>() ||
                !JsonNode.DeepEquals(Values(direct, sample["direct"]!), sample["direct"]) ||
                !JsonNode.DeepEquals(Values(target, sample["frame"]!), sample["frame"]))
                throw new InvalidOperationException($"Native custom grid merge/preflight differs at case {custom}");
            custom++;
        }
        await File.WriteAllTextAsync(Path.Combine(artifacts, "map-summary.json"), JsonSerializer.Serialize(new
        {
            maps = native.Count, fields = compared, ignoredPredictions = probes, customGridScenarios = custom,
            fullModules, declarationOnly, importFailures = reference["import_failures"],
            scope = "Offline MAP declarations and grid merge only; no campaign/device completion"
        }));
        Console.WriteLine($"Native MAP: {native.Count} declarations / {compared} fields / {probes} ignore probes; custom grid merge+frame: {custom} passed.");
        Console.WriteLine($"Import scope: {fullModules} full modules, {declarationOnly} MAP-only dependency slices; retired entrance imports remain unavailable. No device validation.");
    }
}
