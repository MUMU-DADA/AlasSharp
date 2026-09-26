using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static class PathChecks
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly Dictionary<string, PropertyInfo> CellProperties = typeof(CellState).GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Where(p => p.CanWrite).ToDictionary(p => Snake(p.Name), StringComparer.Ordinal);
    private static string Snake(string value)
        => string.Concat(value.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
    private static Cell Parse(string value) => Cell.Parse(value);
    private static IEnumerable<string> Nodes(JsonNode? value) => value?.AsArray().Select(v => v!.GetValue<string>()) ?? [];
    private static string[] Route(JsonNode? value) => Nodes(value).ToArray();

    public static async Task RunAsync(string python, string upstream, string artifacts)
    {
        var source = MapPathfinder.Source;
        if (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(upstream, source.Path)))) != source.Sha256)
            throw new InvalidOperationException("Map source drifted");
        string output = Path.Combine(artifacts, "native-path.json");
        var reference = await new ProcessRunner().RunAsync(python,
            [Path.Combine(AppContext.BaseDirectory, "native_path_reference.py"), upstream, output], TimeSpan.FromMinutes(2));
        if (reference.ExitCode != 0) throw new InvalidOperationException("Native path reference failed: " + reference.Error);
        var cases = JsonNode.Parse(await File.ReadAllTextAsync(output))!.AsArray();
        int compared = 0, routes = 0, strictRoutes = 0, fields = 0, repaired = 0, equivalentPaths = 0, snapshots = 0;
        foreach (var expected in cases)
        {
            var sample = expected!["sample"]!;
            var map = BuildMap(sample);
            var state = new CampaignState(map);
            state.LoadMapData(sample["useLoop"]?.GetValue<bool>() ?? false);
            state.Paths.InitializeConnections(sample["enableWalls"]!.GetValue<bool>(), sample["enablePortals"]!.GetValue<bool>());
            foreach (var patch in sample["cells"]!.AsArray())
            {
                var cell = state[Parse(patch!["cell"]!.GetValue<string>())];
                foreach (var value in patch["values"]!.AsObject())
                {
                    if (!CellProperties.TryGetValue(value.Key, out var property)) throw new InvalidDataException("Unknown cell patch: " + value.Key);
                    property.SetValue(cell, value.Value!.Deserialize(property.PropertyType, Json));
                }
            }
            state.LoadMechanisms(sample["enableMechanisms"]!["land_based"]!.GetValue<bool>(),
                sample["enableMechanisms"]!["maze"]!.GetValue<bool>(),
                sample["enableMechanisms"]!["fortress"]!.GetValue<bool>(), sample["enableMechanisms"]!["bouncing_enemy"]!.GetValue<bool>());
            CompareSnapshot(state, expected["declaration"]!, sample["name"]!.GetValue<string>());
            snapshots++;
            if (state.MazeRound != expected["mazeRound"]!.GetValue<int>()) throw new InvalidOperationException("Maze cycle length differs");
            var links = expected["links"]!.AsObject();
            foreach (var link in links)
            {
                string[] actual = state.Paths.Connections[Parse(link.Key)].Select(c => c.ToString()).Order(StringComparer.Ordinal).ToArray();
                string[] want = Route(link.Value).Order(StringComparer.Ordinal).ToArray();
                if (!actual.SequenceEqual(want)) throw new InvalidOperationException($"Map connections differ in {sample["name"]}/{link.Key}");
                fields++;
            }
            var start = Parse(sample["start"]!.GetValue<string>());
            state.Paths.ComputeCosts(start, sample["hasAmbush"]!.GetValue<bool>(), sample["hasEnemy"]!.GetValue<bool>());
            var native = expected["native"]!.AsArray().ToDictionary(n => n!["cell"]!.GetValue<string>());
            var optimum = expected["optimum"]!.AsObject();
            foreach (var cell in state.Cells)
            {
                int actual = cell.Cost;
                int want = native[cell.Location.ToString()]!["cost"]!.GetValue<int>();
                int shortest = optimum[cell.Location.ToString()]!.GetValue<int>();
                if (actual != shortest) throw new InvalidOperationException($"C# weighted cost differs from oracle in {sample["name"]}/{cell.Location}: {actual}/{shortest}");
                if (actual > want || actual != want && !sample["hasAmbush"]!.GetValue<bool>())
                    throw new InvalidOperationException($"C# cost regressed above upstream in {sample["name"]}/{cell.Location}");
                if (actual < want) repaired++;
                compared++;
            }
            foreach (var routeCase in expected["routes"]!.AsArray())
            {
                var destination = Parse(routeCase!["destination"]!.GetValue<string>());
                var actualFull = state.Paths.FullPath(destination)?.Select(c => c.ToString()).ToArray();
                var wantFull = routeCase["full"] is null ? null : Route(routeCase["full"]);
                if (actualFull is not null)
                    ValidatePath(state, actualFull.Select(Parse).ToArray(), start, destination,
                        sample["hasAmbush"]!.GetValue<bool>(), sample["hasEnemy"]!.GetValue<bool>());
                var route = state.Paths.FindRoute(destination, routeCase["step"]!.GetValue<int>(), routeCase["turning"]!.GetValue<bool>());
                if (route.IsReachable != (wantFull is not null)) throw new InvalidOperationException($"Route reachability differs in {sample["name"]}/{destination}");
                routes++;
                var wantedNodes = Route(routeCase["nodes"]);
                if (route.Waypoints.Select(c => c.ToString()).SequenceEqual(wantedNodes)) strictRoutes++;
                else if ((actualFull is null && wantFull is null) || actualFull is not null && wantFull is not null && actualFull.SequenceEqual(wantFull))
                    throw new InvalidOperationException($"Route nodes differ on the same full path: {sample["name"]}/{destination}");
                else equivalentPaths++;
            }
            // Isolate waypoint semantics from the algorithm's legitimate equal-cost path choice.
            foreach (var entry in native.Values)
            {
                var cell = state[Parse(entry!["cell"]!.GetValue<string>())];
                cell.Cost = entry["cost"]!.GetValue<int>();
                cell.Connection = entry["previous"] is null ? null : Parse(entry["previous"]!.GetValue<string>());
            }
            foreach (var routeCase in expected["routes"]!.AsArray())
            {
                var route = state.Paths.FindRoute(Parse(routeCase!["destination"]!.GetValue<string>()),
                    routeCase["step"]!.GetValue<int>(), routeCase["turning"]!.GetValue<bool>());
                if (!route.Waypoints.Select(c => c.ToString()).SequenceEqual(Route(routeCase["nodes"])))
                    throw new InvalidOperationException($"Native waypoint replay differs: {sample["name"]}/{routeCase["destination"]}/{routeCase["step"]}/{routeCase["turning"]}");
            }
            var fleets = new[] { new KeyValuePair<int, Cell?>(1, start), new(2, Parse(sample["second"]!.GetValue<string>())) };
            state.Paths.ComputeFleetCosts(fleets, start, sample["hasAmbush"]!.GetValue<bool>());
            var expectedMulti = expected["optimumMulti"]!.AsArray();
            foreach (var grid in state.Cells)
            {
                if (grid.Cost1 != expectedMulti[0]![grid.Location.ToString()]!.GetValue<int>() ||
                    grid.Cost2 != expectedMulti[1]![grid.Location.ToString()]!.GetValue<int>() || grid.Cost != grid.Cost1)
                    throw new InvalidOperationException($"Multi-fleet costs differ in {sample["name"]}/{grid.Location}");
            }
            if (expected["trigger"] is { } trigger) state[Parse(trigger.GetValue<string>())].WipeOut();
            CompareSnapshot(state, expected["wiped"]!, "wiped");
            state.ResetMap();
            CompareSnapshot(state, expected["reset"]!, "reset");
            snapshots += 2;
        }
        BoundaryChecks();
        Console.WriteLine($"Native map/path: {cases.Count} cases / {compared} cost cells ({repaired} native early-stop costs corrected) / {fields} adjacency sets / {snapshots} mechanism snapshots. {routes} exact waypoint replays on native full paths; independent paths: {strictRoutes} same nodes, {equivalentPaths} different valid paths. Multi-fleet, immutable declarations and failure boundaries passed; no device actions.");
    }

    private static void ValidatePath(CampaignState state, IReadOnlyList<Cell> path, Cell start, Cell destination, bool ambush, bool enemies)
    {
        if (path[0] != start || path[^1] != destination || path.Distinct().Count() != path.Count)
            throw new InvalidOperationException("Path endpoints or uniqueness differ");
        int total = 0;
        for (int i = 1; i < path.Count; i++)
        {
            var cell = state[path[i]];
            if (!state.Paths.Connections[path[i - 1]].Contains(path[i]) || cell.IsLand || cell.IsMechanismBlock ||
                i < path.Count - 1 && enemies && !cell.IsSea) throw new InvalidOperationException("Path crossed a missing edge or blocker");
            total += cell.MayAmbush && ambush ? 10 : 1;
            if (total != cell.Cost) throw new InvalidOperationException("Path entry costs differ from the computed cost field");
        }
    }

    private static void CompareSnapshot(CampaignState state, JsonNode expected, string name)
    {
        static JsonNode? Group(IReadOnlyList<CellState>? cells) => cells is null ? null : JsonSerializer.SerializeToNode(cells.Select(c => c.Location.ToString()).Order(StringComparer.Ordinal));
        foreach (var entry in expected.AsArray())
        {
            var cell = state[Parse(entry!["cell"]!.GetValue<string>())];
            foreach (var pair in entry["values"]!.AsObject())
                if (!JsonNode.DeepEquals(JsonSerializer.SerializeToNode(CellProperties[pair.Key].GetValue(cell)), pair.Value))
                    throw new InvalidOperationException($"Map state differs: {name}/{cell.Location}/{pair.Key}");
            var actual = new JsonObject { ["portal"] = cell.PortalLink?.ToString(), ["rounds"] = JsonSerializer.SerializeToNode(cell.MazeRound),
                ["nearby"] = Group(cell.MazeNearby), ["triggers"] = Group(cell.MechanismTrigger), ["blocks"] = Group(cell.MechanismBlock) };
            foreach (var pair in actual)
                if (!JsonNode.DeepEquals(pair.Value, entry[pair.Key])) throw new InvalidOperationException($"Map linkage differs: {name}/{cell.Location}/{pair.Key}");
        }
    }

    private static void BoundaryChecks()
    {
        static void Reject<T>(Action action) where T : Exception
        {
            try { action(); } catch (T) { return; }
            throw new InvalidOperationException("Expected rejection: " + typeof(T).Name);
        }
        var map = new MapDefinition("C1", "SP -- --", [], [], [], portals: [new("A1", "B1")]);
        var state = new CampaignState(map);
        Reject<InvalidOperationException>(() => state.Paths.ComputeCosts(Parse("A1")));
        state.Paths.InitializeConnections(portals: true);
        if (!state.Paths.Connections[Parse("A1")].Contains(Parse("B1"))) throw new InvalidOperationException("Portal edge missing");
        state.Paths.InitializeConnections(portals: false);
        if (state.Paths.Connections[Parse("A1")].Contains(Parse("B1")) || !state.Paths.Connections[Parse("B1")].Contains(Parse("A1")) ||
            state[Parse("A1")].IsPortal || state[Parse("A1")].PortalLink is not null) throw new InvalidOperationException("Disabled adjacent portal lost directed semantics");
        state.Paths.ComputeCosts(Parse("A1"), false);
        var unreachable = state.Paths.FindRoute(Parse("C1"));
        if (unreachable.IsReachable || unreachable.FullPath is not null || !unreachable.Waypoints.SequenceEqual(new[] { Parse("C1") }))
            throw new InvalidOperationException("Unreachable destination fallback became a reachable route");
        state[Parse("B1")].Connection = Parse("C1"); state[Parse("C1")].Connection = Parse("B1");
        Reject<InvalidDataException>(() => state.Paths.FullPath(Parse("C1")));
        Reject<ArgumentOutOfRangeException>(() => state.Paths.FindRoute(Parse("B1"), -1));
        Reject<ArgumentException>(() => state.Paths.ComputeFleetCosts([new(1, Parse("A1")), new(1, Parse("B1"))], Parse("A1"), false));
        Reject<ArgumentException>(() => new MapDefinition("B1", "-- --", [], [], [], weights: [1]));
        Reject<ArgumentException>(() => new MapDefinition("C1", "-- -- --", [], [], [], walls: [new("A1", "C1")]));
        var mutable = new[] { Parse("A1"), Parse("B1") };
        var mechanism = new MapMechanisms(mazes: [mutable]);
        mutable[0] = Parse("C1");
        if (mechanism.Mazes[0][0] != Parse("A1")) throw new InvalidOperationException("Mechanism declaration was mutable");
        state.Paths.ComputeFleetCosts([new(1, Parse("A1")), new(2, null)], Parse("A1"), false);
        if (state.Cells.Any(c => c.Cost2 != 9999)) throw new InvalidOperationException("Absent fleet acquired a cost field");
    }

    private static MapDefinition BuildMap(JsonNode sample)
    {
        var shape = sample["shape"]!.GetValue<string>();
        var weights = sample["weights"]?.AsArray().Select(n => n!.GetValue<double>()) ?? null;
        var walls = sample["walls"]!.AsArray().Select(e => new MapEdge(e![0]!.GetValue<string>(), e[1]!.GetValue<string>()));
        var portals = sample["portals"]!.AsArray().Select(e => new MapEdge(e![0]!.GetValue<string>(), e[1]!.GetValue<string>()));
        var mechanisms = sample["mechanisms"]!;
        var land = mechanisms["land"]!.AsArray().Select(m => new LandMechanism(Parse(m!["origin"]!.GetValue<string>()), Enum.Parse<MapDirection>(m["direction"]!.GetValue<string>(), true)));
        var mazes = mechanisms["mazes"]!.AsArray().Select(g => g!.AsArray().Select(c => Parse(c!.GetValue<string>())));
        var fortressEnemies = Nodes(mechanisms["fortressEnemies"]).Select(Parse);
        var fortressBlocks = Nodes(mechanisms["fortressBlocks"]).Select(Parse);
        var bouncing = mechanisms["bouncing"]!.AsArray().Select(g => g!.AsArray().Select(c => Parse(c!.GetValue<string>())));
        var declarations = new MapMechanisms(land, mazes, fortressEnemies, fortressBlocks, bouncing);
        return new MapDefinition(shape, sample["tiles"]!.GetValue<string>(), [], [], [], sample["loop"]?.GetValue<string>(), weights, walls, portals, declarations);
    }
}
