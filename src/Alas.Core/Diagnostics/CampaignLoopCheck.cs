using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Alas.Campaign;
using RulePlan = Alas.Campaign.CampaignPlan;

namespace Alas.Core.Diagnostics;

/// <summary>
/// R5 关卡循环的离线入口：`Alas.Server r5-loop --fixture &lt;fixture.json&gt;`。
///
/// 它按上游 <c>CampaignBase.run()</c> 的语义跑**关卡循环**：轮次上限 20、每轮按 <c>battle_count</c>
/// 选钩子、<c>MapEnemyMoved</c> 重试、<c>CampaignEnd</c> 结束——动作仍只被干跑宿主记录。
/// **不连设备**：用于验证"关卡流程决策"（选哪个钩子、什么时候结束）而不是真的打关卡。
///
/// 用法：
///   Alas.Server r5-loop --fixture tools/diagnostics/r5-loop-fixture.json
/// </summary>
internal static class CampaignLoopCheck
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static int Run(string dataDir, string fixturePath)
    {
        if (!File.Exists(fixturePath)) return Fail($"找不到夹具：{fixturePath}");
        LoopFixture fixture;
        try
        {
            fixture = JsonSerializer.Deserialize<LoopFixture>(File.ReadAllText(fixturePath), Options)
                      ?? throw new JsonException("夹具为空");
        }
        catch (JsonException error)
        {
            return Fail($"夹具解析失败：{error.Message}");
        }

        var results = new JsonArray();
        foreach (var testCase in fixture.Cases)
        {
            RulePlan plan;
            try
            {
                plan = CampaignPlanReader.Read(dataDir, testCase.Chapter, testCase.Level);
            }
            catch (Exception error) when (error is JsonException or IOException)
            {
                return Fail($"读不出关卡计划 {testCase.Chapter}/{testCase.Level}：{error.Message}");
            }

            var config = new CampaignRuntimeConfig(
                EnemyPriority: testCase.Config?.EnemyPriority,
                MapClearAllThisTime: testCase.Config?.MapClearAllThisTime ?? false,
                MapHasSiren: testCase.Config?.MapHasSiren ?? false,
                MapHasFortress: testCase.Config?.MapHasFortress ?? false,
                Fleet2: testCase.Config?.Fleet2 ?? false,
                FleetBoss: testCase.Config?.FleetBoss ?? false,
                MapHasLandBased: testCase.Config?.MapHasLandBased ?? false,
                MapHasMovableEnemy: testCase.Config?.MapHasMovableEnemy ?? false,
                MapHasMovableNormalEnemy: testCase.Config?.MapHasMovableNormalEnemy ?? false,
                PoorMapData: testCase.Config?.PoorMapData ?? false,
                ErrorHandleError: testCase.Config?.ErrorHandleError ?? true);
            var host = new RecordingCampaignHost((testCase.Grids ?? []).Select(Grid), config)
            {
                BouncingRoutes = plan.Map?.BouncingEnemyData ?? [],
                BattleCount = testCase.Config?.BattleCount ?? 0,
                AmmoCount = testCase.Config?.AmmoCount ?? 3,
                FleetAmmo = testCase.Config?.FleetAmmo ?? 5,
                Fleet1Location = testCase.Config?.Fleet1Location ?? "",
                Fleet2Location = testCase.Config?.Fleet2Location ?? "",
                FleetCurrentIndex = testCase.Config?.FleetCurrentIndex ?? 1,
            };

            var level = CampaignBattleLoop.Run(plan, host);
            results.Add(new JsonObject
            {
                ["name"] = testCase.Name,
                ["outcome"] = level.Outcome.ToString(),
                ["detail"] = level.Detail,
                ["variant"] = CampaignBattleLoop.BattleFunctionVariant(config),
                ["invoked_ops"] = new JsonArray(host.InvokedOps.Distinct(StringComparer.Ordinal)
                    .Select(name => (JsonNode)name!).ToArray()),
                ["rounds"] = new JsonArray(level.Rounds.Select(round => (JsonNode)new JsonObject
                {
                    ["index"] = round.Index,
                    ["battle_count"] = round.BattleCount,
                    ["hook"] = round.Hook,
                    ["result"] = round.Result,
                    ["blocked"] = round.BlockedReason,
                }).ToArray()),
                ["invoked_ops"] = new JsonArray(host.InvokedOps.Distinct(StringComparer.Ordinal)
                    .Select(op => (JsonNode)op).ToArray()),
                ["actions"] = new JsonArray(host.Actions.Select(text => (JsonNode)text!).ToArray()),
                ["logs"] = new JsonArray(host.Logs.Select(text => (JsonNode)text!).ToArray()),
            });
        }

        Console.WriteLine(new JsonObject
        {
            ["fixture"] = Path.GetFileName(fixturePath),
            ["cases"] = results,
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static CampaignGrid Grid(LoopGrid grid) => new(
        grid.Location ?? "",
        IsEnemy: grid.IsEnemy,
        IsBoss: grid.IsBoss,
        IsSiren: grid.IsSiren,
        IsMystery: grid.IsMystery,
        MayBoss: grid.MayBoss,
        MayAmmo: grid.MayAmmo,
        IsFleet: grid.IsFleet,
        IsCaughtBySiren: grid.IsCaughtBySiren,
        EnemyScale: grid.EnemyScale,
        EnemyGenre: grid.EnemyGenre,
        Weight: grid.Weight,
        Cost: grid.Cost,
        Cost1: grid.Cost1 ?? 9999,
        Cost2: grid.Cost2 ?? 9999);

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"[错误   ] {message}");
        return 1;
    }

    private sealed class LoopFixture
    {
        [JsonPropertyName("cases")] public List<LoopCase> Cases { get; init; } = [];
    }

    private sealed class LoopCase
    {
        [JsonPropertyName("name")] public string Name { get; init; } = "";
        [JsonPropertyName("chapter")] public string Chapter { get; init; } = "";
        [JsonPropertyName("level")] public string Level { get; init; } = "";
        [JsonPropertyName("config")] public LoopConfig? Config { get; init; }
        [JsonPropertyName("grids")] public List<LoopGrid>? Grids { get; init; }
    }

    private sealed class LoopConfig
    {
        [JsonPropertyName("enemy_priority")] public string? EnemyPriority { get; init; }
        [JsonPropertyName("map_clear_all_this_time")] public bool? MapClearAllThisTime { get; init; }
        [JsonPropertyName("map_has_siren")] public bool? MapHasSiren { get; init; }
        [JsonPropertyName("map_has_fortress")] public bool? MapHasFortress { get; init; }
        [JsonPropertyName("fleet_2")] public bool? Fleet2 { get; init; }
        [JsonPropertyName("fleet_boss")] public bool? FleetBoss { get; init; }
        [JsonPropertyName("map_has_land_based")] public bool? MapHasLandBased { get; init; }
        [JsonPropertyName("map_has_movable_enemy")] public bool? MapHasMovableEnemy { get; init; }
        [JsonPropertyName("map_has_movable_normal_enemy")] public bool? MapHasMovableNormalEnemy { get; init; }
        [JsonPropertyName("poor_map_data")] public bool? PoorMapData { get; init; }
        [JsonPropertyName("error_handle_error")] public bool? ErrorHandleError { get; init; }
        [JsonPropertyName("battle_count")] public int? BattleCount { get; init; }
        [JsonPropertyName("ammo_count")] public int? AmmoCount { get; init; }
        [JsonPropertyName("fleet_ammo")] public int? FleetAmmo { get; init; }
        [JsonPropertyName("fleet_1_location")] public string? Fleet1Location { get; init; }
        [JsonPropertyName("fleet_2_location")] public string? Fleet2Location { get; init; }
        [JsonPropertyName("fleet_current_index")] public int? FleetCurrentIndex { get; init; }
    }

    private sealed class LoopGrid
    {
        [JsonPropertyName("location")] public string? Location { get; init; }
        [JsonPropertyName("is_enemy")] public bool IsEnemy { get; init; }
        [JsonPropertyName("is_boss")] public bool IsBoss { get; init; }
        [JsonPropertyName("is_siren")] public bool IsSiren { get; init; }
        [JsonPropertyName("is_mystery")] public bool IsMystery { get; init; }
        [JsonPropertyName("may_boss")] public bool MayBoss { get; init; }
        [JsonPropertyName("may_ammo")] public bool MayAmmo { get; init; }
        [JsonPropertyName("is_fleet")] public bool IsFleet { get; init; }
        [JsonPropertyName("is_caught_by_siren")] public bool IsCaughtBySiren { get; init; }
        [JsonPropertyName("enemy_scale")] public int EnemyScale { get; init; }
        [JsonPropertyName("enemy_genre")] public string? EnemyGenre { get; init; }
        [JsonPropertyName("weight")] public int Weight { get; init; }
        [JsonPropertyName("cost")] public int Cost { get; init; }
        [JsonPropertyName("cost_1")] public int? Cost1 { get; init; }
        [JsonPropertyName("cost_2")] public int? Cost2 { get; init; }
    }
}
