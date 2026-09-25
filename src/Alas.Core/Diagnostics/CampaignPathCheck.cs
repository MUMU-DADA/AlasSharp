using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Alas.Campaign;

namespace Alas.Core.Diagnostics;

/// <summary>
/// R5 寻路的离线入口：`Alas.Server r5-path --fixture &lt;fixture.json&gt;`。
///
/// 按上游 <c>CampaignMap.find_path_initial()</c> / <c>_find_path()</c> 的语义，从地图令牌网格算出
/// **成本场与连接**，并可选地回溯出一条路线。**不连设备、不读识别结果**——只做地图上的算术，
/// 用于与上游实现逐格对拍。
///
/// 夹具里的地图用上游自己的令牌写法（`--` 空地 / `++` 陆地 / `ME` 可能有敌 / `MY` 可能有神秘 /
/// `MS` 可能有塞壬 / `SP` 出生点 / `__` 潜艇出生点），并按上游 <c>GridInfo.decode()</c> 推导 may_ambush。
///
/// 用法：
///   Alas.Server r5-path --fixture tools/diagnostics/r5-path-fixture.json
/// </summary>
internal static class CampaignPathCheck
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>上游 <c>GridInfo.decode()</c> 的令牌表。</summary>
    private static readonly Dictionary<string, string> Tokens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["++"] = "is_land",
        ["SP"] = "is_spawn_point",
        ["__"] = "is_submarine_spawn_point",
        ["ME"] = "may_enemy",
        ["MB"] = "may_boss",
        ["MM"] = "may_mystery",
        ["MA"] = "may_ammo",
        ["MS"] = "may_siren",
    };

    public static int Run(string fixturePath, int repeat = 1)
    {
        if (!File.Exists(fixturePath)) return Fail($"找不到夹具：{fixturePath}");
        if (repeat < 1) return Fail("--repeat 必须 >= 1");
        PathFixture fixture;
        try
        {
            fixture = JsonSerializer.Deserialize<PathFixture>(File.ReadAllText(fixturePath), Options)
                      ?? throw new JsonException("夹具为空");
        }
        catch (JsonException error)
        {
            return Fail($"夹具解析失败：{error.Message}");
        }

        var results = new JsonArray();
        foreach (var testCase in fixture.Cases)
        {
            var grids = BuildGrids(testCase);
            CampaignCostField field = null!;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            for (int iteration = 0; iteration < repeat; iteration++)
            {
                field = CampaignPathfinder.FindPathInitial(grids, testCase.Start,
                                                           testCase.HasAmbush, testCase.HasEnemy);
            }
            stopwatch.Stop();
            double perOperationMs = stopwatch.Elapsed.TotalMilliseconds / repeat;

            var costs = new JsonObject();
            foreach (var (location, cost) in field.Costs.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                costs[location] = cost;
            }
            var connections = new JsonObject();
            foreach (var (location, connection) in field.Connections.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                connections[location] = connection;
            }

            JsonNode? route = null;
            if (!string.IsNullOrEmpty(testCase.Destination))
            {
                route = new JsonArray(CampaignPathfinder.FindPath(field, testCase.Destination)
                    .Select(item => (JsonNode)item!).ToArray());
            }

            results.Add(new JsonObject
            {
                ["name"] = testCase.Name,
                ["start"] = testCase.Start,
                ["grids"] = grids.Count,
                ["repeat"] = repeat,
                ["elapsed_ms"] = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3),
                ["per_operation_ms"] = Math.Round(perOperationMs, 4),
                ["costs"] = costs,
                ["connections"] = connections,
                ["route"] = route,
            });
        }

        Console.WriteLine(new JsonObject
        {
            ["fixture"] = Path.GetFileName(fixturePath),
            ["cases"] = results,
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    /// <summary>把夹具的令牌网格解成格子集合：令牌决定 may_*/is_land，随后套用显式覆盖。</summary>
    private static IReadOnlyList<CampaignGrid> BuildGrids(PathCase testCase)
    {
        var grids = new List<CampaignGrid>();
        var rows = testCase.Rows ?? [];
        for (int y = 0; y < rows.Count; y++)
        {
            string[] tokens = rows[y].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            for (int x = 0; x < tokens.Length; x++)
            {
                string token = tokens[x];
                var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (Tokens.TryGetValue(token, out string? flag)) flags.Add(flag);
                string location = CampaignLocations.ToNode(x, y);
                var overrides = testCase.Cells?.FirstOrDefault(cell => cell.Location == location);

                bool mayEnemy = flags.Contains("may_enemy") || (overrides?.MayEnemy ?? false);
                bool mayBoss = flags.Contains("may_boss") || (overrides?.MayBoss ?? false);
                bool mayMystery = flags.Contains("may_mystery") || (overrides?.MayMystery ?? false);
                bool mayAmmo = flags.Contains("may_ammo") || (overrides?.MayAmmo ?? false);
                bool maySiren = flags.Contains("may_siren") || (overrides?.MaySiren ?? false);
                grids.Add(new CampaignGrid(
                    location,
                    IsEnemy: overrides?.IsEnemy ?? false,
                    IsSiren: overrides?.IsSiren ?? false,
                    IsBoss: overrides?.IsBoss ?? false,
                    IsLand: flags.Contains("is_land"),
                    MayEnemy: mayEnemy,
                    MayBoss: mayBoss,
                    MayMystery: mayMystery,
                    MayAmmo: mayAmmo,
                    MaySiren: maySiren,
                    IsMechanismBlock: overrides?.IsMechanismBlock ?? false,
                    // 上游 decode()：may_ambush = not (may_enemy or may_boss or may_mystery or may_mystery)
                    MayAmbush: overrides?.MayAmbush ?? !(mayEnemy || mayBoss || mayMystery || mayMystery)));
            }
        }
        return grids;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"[错误   ] {message}");
        return 1;
    }

    private sealed class PathFixture
    {
        [JsonPropertyName("cases")] public List<PathCase> Cases { get; init; } = [];
    }

    private sealed class PathCase
    {
        [JsonPropertyName("name")] public string Name { get; init; } = "";
        [JsonPropertyName("rows")] public List<string>? Rows { get; init; }
        [JsonPropertyName("start")] public string Start { get; init; } = "A1";
        [JsonPropertyName("destination")] public string? Destination { get; init; }
        [JsonPropertyName("has_ambush")] public bool HasAmbush { get; init; }
        [JsonPropertyName("has_enemy")] public bool HasEnemy { get; init; } = true;
        [JsonPropertyName("cells")] public List<PathCell>? Cells { get; init; }
    }

    private sealed class PathCell
    {
        [JsonPropertyName("location")] public string Location { get; init; } = "";
        [JsonPropertyName("is_enemy")] public bool? IsEnemy { get; init; }
        [JsonPropertyName("is_siren")] public bool? IsSiren { get; init; }
        [JsonPropertyName("is_boss")] public bool? IsBoss { get; init; }
        [JsonPropertyName("is_mechanism_block")] public bool? IsMechanismBlock { get; init; }
        [JsonPropertyName("may_enemy")] public bool? MayEnemy { get; init; }
        [JsonPropertyName("may_boss")] public bool? MayBoss { get; init; }
        [JsonPropertyName("may_mystery")] public bool? MayMystery { get; init; }
        [JsonPropertyName("may_ammo")] public bool? MayAmmo { get; init; }
        [JsonPropertyName("may_siren")] public bool? MaySiren { get; init; }
        [JsonPropertyName("may_ambush")] public bool? MayAmbush { get; init; }
    }
}
