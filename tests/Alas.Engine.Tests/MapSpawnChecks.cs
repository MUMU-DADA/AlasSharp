using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static class MapSpawnChecks
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };
    private static readonly string[] CellFields =
    [
        "is_land", "is_spawn_point", "is_submarine_spawn_point", "may_enemy", "may_boss",
        "may_mystery", "may_siren", "may_ammo", "may_ambush", "is_enemy", "is_boss",
        "is_siren", "is_mystery", "is_fleet", "is_current_fleet", "is_fortress",
        "may_bouncing_enemy", "is_mechanism_block", "may_carrier"
    ];
    private static readonly Dictionary<string, PropertyInfo> CellProperties = typeof(CellState)
        .GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .ToDictionary(Snake, StringComparer.Ordinal);

    public static async Task RunAsync(string python, string upstream, string artifacts)
    {
        foreach (var source in new[] { CampaignState.SpawnSource, CampaignState.InitializationSource,
                     CellState.Source, MapScanner.Source, MapScanner.SelectionSource })
        {
            string path = Path.Combine(upstream, source.Path);
            await using var stream = File.OpenRead(path);
            string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
            if (hash != source.Sha256) throw new InvalidOperationException($"Spawn source drifted: {source.Path}");
        }

        string output = Path.Combine(artifacts, "native-spawn.json");
        var process = await new ProcessRunner().RunAsync(python,
            [Path.Combine(AppContext.BaseDirectory, "native_spawn_reference.py"), upstream, output],
            TimeSpan.FromMinutes(2));
        if (process.ExitCode != 0) throw new InvalidOperationException("Native spawn reference failed: " + process.Error);
        var native = JsonNode.Parse(await File.ReadAllTextAsync(output))!.AsArray();
        Check(native.Count > 0, "Native spawn reference returned no scenarios");
        var failures = new JsonArray();
        int compared = 0;
        foreach (var expected in native)
        {
            var actual = Execute(expected!["sample"]!);
            var want = expected.DeepClone()!;
            want.AsObject().Remove("sample");
            if (!JsonNode.DeepEquals(actual, want))
                failures.Add(new JsonObject
                {
                    ["name"] = expected["sample"]!["name"]!.GetValue<string>(),
                    ["csharp"] = actual,
                    ["native"] = want
                });
            compared++;
        }
        await File.WriteAllTextAsync(Path.Combine(artifacts, "spawn-mismatches.json"), failures.ToJsonString(Json));
        if (failures.Count != 0)
            throw new InvalidOperationException($"Native spawn mismatch: {failures.Count}/{compared}; inspect spawn-mismatches.json");

        RunLocalChecks();
        Console.WriteLine($"Native map spawn: {compared} initialization/census/prediction cases passed; no device or settlement validation.");
    }

    private static JsonObject Execute(JsonNode sample)
    {
        var state = new CampaignState(BuildMap(sample))
        {
            BattleCount = 9, MysteryCount = 9, SirenCount = 9, CarrierCount = 9, FleetIndex = 2,
            Fleet1Location = new(2, 2), Fleet2Location = new(2, 2), SubmarineLocation = new(2, 2)
        };
        ApplyPatches(state, sample["before"]);
        state.InitializeMapData(new MapInitialization(
            ClearMode: sample["clear"]!.GetValue<bool>(), PoorMapData: sample["poor"]!.GetValue<bool>(),
            Fortress: sample["fortress"]!.GetValue<bool>(), BouncingEnemy: sample["bouncing"]!.GetValue<bool>()));
        var initialized = SnapshotInitialized(state);
        foreach (var loop in sample["reload"]!.AsArray()) state.LoadSpawnData(loop!.GetValue<bool>());
        ApplyPatches(state, sample["patches"]);

        var result = new JsonObject
        {
            ["initialized"] = initialized,
            ["stack"] = Waves(state.SpawnStack),
            ["active"] = Waves(state.ActiveWaves),
            ["covered"] = Nodes(state.CoveredCells().Select(cell => cell.Location))
        };
        var progress = new MapProgress(sample["battle"]!.GetValue<int>(), sample["mystery"]!.GetValue<int>(),
            sample["siren"]!.GetValue<int>(), sample["carrier"]!.GetValue<int>());
        var mode = ParseMode(sample["mode"]!.GetValue<string>());
        try { result["census"] = Census(state.GetMissing(progress, mode)); }
        catch (InvalidOperationException) when (state.SpawnStack.IsEmpty) { result["census"] = null; }
        try
        {
            result["none"] = state.MissingIsNone(progress, mode);
            var before = SnapshotCells(state);
            var predictions = state.PredictMissing(progress, mode);
            var after = SnapshotCells(state);
            var changed = Nodes(state.Cells.Where((_, index) => !JsonNode.DeepEquals(before[index], after[index]))
                .Select(cell => cell.Location));
            Check(JsonNode.DeepEquals(Nodes(predictions), changed), "Prediction report differs from mutated cells");
            result["changed"] = changed;
            result["after"] = after;
            result["error"] = null;
        }
        catch (InvalidOperationException) when (state.SpawnStack.IsEmpty && !state.PoorMapData)
        {
            result["none"] = null; result["changed"] = new JsonArray();
            result["after"] = SnapshotCells(state); result["error"] = "empty-spawn-table";
        }
        return result;
    }

    private static void RunLocalChecks()
    {
        var map = new MapDefinition("B3", "SP ME\nMB MS\n-- --", ["A1", "B1"], ["A1"],
            [new(0, 1), new(1, 1, 1), new(2, 0, 0, 1)], loopTiles: "SP Me\nMB MS\n-- --",
            loopWaves: [new(0, 2), new(1, 1), new(2, 0, 0, 1)],
            mechanisms: new MapMechanisms(fortressEnemies: [Cell.Parse("A3")], fortressBlocks: [Cell.Parse("B3")],
                bouncingRoutes: [[Cell.Parse("A2"), Cell.Parse("B2")]]), covered: ["A1", "B1", "A2", "B2"]);
        var state = new CampaignState(map);
        state.InitializeMapData(new(Fortress: true, BouncingEnemy: true));
        Check(state.ActiveWaves.SequenceEqual(map.Waves) && state.SpawnStack.Length == 3, "Base spawn stack was not loaded");
        var initial = state.GetMissing(new MapProgress());
        Check(initial.Missing.Enemy == 1 && initial.Missing.Boss == 0,
            $"Initial spawn census drifted: {initial.Missing}");
        var clear = new CampaignState(map);
        clear.InitializeMapData(new MapInitialization(ClearMode: true, PoorMapData: true, Fortress: true, BouncingEnemy: true));
        Check(!clear.PoorMapData && clear.ActiveWaves.SequenceEqual(map.LoopWaves) && clear.Mechanisms.FortressEnemies.IsEmpty,
            "Clear mode did not use loop declarations or clear runtime mechanisms");
        var carrierMap = new MapDefinition("B1", "SP --", ["A1"], [], [new(0)], covered: ["A1", "B1"]);
        var carrier = new CampaignState(carrierMap);
        carrier.InitializeMapData(new());
        var carrierCensus = carrier.GetMissing(new MapProgress(Carrier: 1), MapScanMode.Carrier);
        Check(carrierCensus.Possible.Carrier == 2 && carrierCensus.Missing.Carrier == 1, "Carrier census drifted");
        Check(carrier.PredictMissing(new(Carrier: 1), MapScanMode.Carrier).Count == 0 && !carrier.HasNonBossEnemy,
            "An ambiguous carrier count caused a false prediction");
        Check(carrier.PredictMissing(new(Carrier: 2), MapScanMode.Carrier).Count == 2,
            "Uniquely determined carrier spawns were not predicted");
        Check(!new CampaignState(carrierMap).HasNonBossEnemy && map.Mechanisms.FortressEnemies.Length == 1,
            "Sortie state mutated shared map declarations");
        Reject<InvalidOperationException>(() => state.InitializeMapData(new()));
        Reject<ArgumentOutOfRangeException>(() => state.GetMissing(new(Battle: -1)));
        Reject<ArgumentOutOfRangeException>(() => state.PredictMissing(new(), (MapScanMode)99));
        var empty = new CampaignState(new MapDefinition("A1", "--", [], [], []));
        empty.InitializeMapData(new());
        Reject<InvalidOperationException>(() => empty.GetMissing(new()));
        Reject<InvalidOperationException>(() => empty.MissingIsNone(new()));
        Reject<InvalidOperationException>(() => empty.PredictMissing(new()));
        var overflow = new CampaignState(new MapDefinition("A1", "--", [], [], [new(0, int.MaxValue), new(1, 1)]));
        Reject<OverflowException>(() => overflow.LoadSpawnData());
        Check(overflow.SpawnStack.IsEmpty, "Failed spawn accumulation partially published its stack");
    }

    private static MapDefinition BuildMap(JsonNode sample)
    {
        static IEnumerable<SpawnWave> ParseWaves(JsonNode? node)
        {
            if (node is null) yield break;
            foreach (var row in node.AsArray())
                yield return new SpawnWave(row!["battle"]!.GetValue<int>(), row["enemy"]?.GetValue<int>() ?? 0,
                    row["mystery"]?.GetValue<int>() ?? 0, row["boss"]?.GetValue<int>() ?? 0,
                    row["siren"]?.GetValue<int>() ?? 0);
        }
        static IEnumerable<Cell> Cells(JsonNode? node)
            => node?.AsArray().Select(value => Cell.Parse(value!.GetValue<string>())) ?? [];
        var mechanisms = new MapMechanisms(fortressEnemies: Cells(sample["fortress_enemies"]),
            fortressBlocks: Cells(sample["fortress_blocks"]), bouncingRoutes: sample["bouncing_routes"]!.AsArray().Select(Cells));
        return new MapDefinition(sample["shape"]!.GetValue<string>(), sample["tiles"]!.GetValue<string>(), [], [],
            ParseWaves(sample["waves"]), sample["loop"]?.GetValue<string>(), mechanisms: mechanisms,
            loopWaves: ParseWaves(sample["loop_waves"]), covered: Cells(sample["covered"]).Select(cell => cell.ToString()));
    }

    private static void ApplyPatches(CampaignState state, JsonNode? patches)
    {
        if (patches is null) return;
        foreach (var patch in patches.AsArray())
        {
            var cell = state[Cell.Parse(patch!["cell"]!.GetValue<string>())];
            foreach (var value in patch["state"]!.AsObject())
            {
                if (!CellProperties.TryGetValue(value.Key, out var property) || !property.CanWrite)
                    throw new InvalidDataException($"Unknown writable cell property: {value.Key}");
                property.SetValue(cell, value.Value!.Deserialize(property.PropertyType, Json));
            }
        }
    }

    private static JsonObject SnapshotInitialized(CampaignState state) => new()
    {
        ["cells"] = SnapshotCells(state), ["poor"] = state.PoorMapData, ["complete"] = state.HasCompleteSpawnDeclarations,
        ["stack"] = Waves(state.SpawnStack), ["active"] = Waves(state.ActiveWaves),
        ["progress"] = new JsonArray(state.BattleCount, state.MysteryCount, state.SirenCount, state.CarrierCount),
        ["fleets"] = new JsonArray(Fleet(state.Fleet1Location), Fleet(state.Fleet2Location), Fleet(state.SubmarineLocation)),
        ["fleet_index"] = state.FleetIndex, ["ammo"] = state.AmmoCount,
        ["fortress"] = new JsonArray(state.Mechanisms.FortressEnemies.Length, state.Mechanisms.FortressBlocks.Length),
        ["bouncing"] = state.Mechanisms.BouncingRoutes.Length
    };

    private static JsonArray SnapshotCells(CampaignState state)
    {
        var cells = new JsonArray();
        foreach (var cell in state.Cells)
        {
            var row = new JsonObject();
            foreach (string field in CellFields)
                row[field] = JsonValue.Create((bool)CellProperties[field].GetValue(cell)!);
            cells.Add(row);
        }
        return cells;
    }

    private static JsonArray Nodes(IEnumerable<Cell> cells) => new(cells.Select(cell => cell.ToString())
        .Order(StringComparer.Ordinal).Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());

    private static JsonArray Waves(IEnumerable<SpawnWave> waves)
    {
        var result = new JsonArray();
        foreach (var wave in waves)
            result.Add(new JsonObject { ["battle"] = wave.Battle, ["enemy"] = wave.Enemy, ["mystery"] = wave.Mystery,
                ["siren"] = wave.Siren, ["boss"] = wave.Boss });
        return result;
    }

    private static JsonObject Census(SpawnCensus census)
    {
        static JsonObject Amounts(SpawnAmounts value) => new()
        {
            ["enemy"] = value.Enemy, ["mystery"] = value.Mystery, ["siren"] = value.Siren,
            ["boss"] = value.Boss, ["carrier"] = value.Carrier
        };
        var missing = Amounts(census.Missing); missing["battle"] = census.Battle;
        return new JsonObject { ["may"] = Amounts(census.Possible), ["missing"] = missing };
    }

    private static JsonArray Fleet(Cell? value) => value is { } cell ? new(cell.Column - 1, cell.Row - 1) : [];
    private static MapScanMode ParseMode(string value) => Enum.Parse<MapScanMode>(value, true);
    private static string Snake(PropertyInfo property) => string.Concat(property.Name.Select((c, i) =>
        i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected rejection: " + typeof(T).Name);
    }
}
