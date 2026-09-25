using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Alas.Campaign;
using RulePlan = Alas.Campaign.CampaignPlan;

namespace Alas.Core.Diagnostics;

/// <summary>
/// R5 原语执行的离线入口：`Alas.Server r5-exec --fixture &lt;fixture.json&gt;`。
///
/// 它用夹具里的地图状态与运行时配置，按计划契约**驱动已登记的原语**跑完一个钩子，输出
/// 每一步的结果、干跑记录到的动作与日志。**不连设备、不执行任何游戏动作**：
/// 动作只经过 <see cref="RecordingCampaignHost"/> 记录。
///
/// 用途：把"计划 → 原语 → 动作"这条链在离线可复现地跑通，并暴露真正的阻塞点
/// （实参未求值 / 原语未实现 / 未移植分支）。
///
/// 用法：
///   Alas.Server r5-exec --fixture tools/diagnostics/r5-execution-fixture.json
/// </summary>
internal static class CampaignExecutionCheck
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
        ExecutionFixture fixture;
        try
        {
            fixture = JsonSerializer.Deserialize<ExecutionFixture>(File.ReadAllText(fixturePath), Options)
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

            var battle = plan.Header.Battles.FirstOrDefault(item => item.Method == testCase.Hook);
            if (battle is null)
            {
                return Fail($"{testCase.Chapter}/{testCase.Level} 的 {testCase.Hook} 不在导出里");
            }

            var grids = (testCase.Grids ?? []).Select(Grid);
            var config = new CampaignRuntimeConfig(
                EnemyPriority: testCase.Config?.EnemyPriority,
                MapClearAllThisTime: testCase.Config?.MapClearAllThisTime ?? false,
                MapHasSiren: testCase.Config?.MapHasSiren ?? false,
                MapHasFortress: testCase.Config?.MapHasFortress ?? false,
                Fleet2: testCase.Config?.Fleet2 ?? false,
                FleetBoss: testCase.Config?.FleetBoss ?? false,
                MapHasMovableNormalEnemy: testCase.Config?.MapHasMovableNormalEnemy ?? false);
            var host = new RecordingCampaignHost(grids, config)
            {
                FleetCurrentIndex = testCase.Config?.FleetCurrentIndex ?? 1,
                BattleCount = testCase.Config?.BattleCount ?? 0,
            };
            var execution = CampaignHookRunner.Run(plan, battle, host);

            results.Add(new JsonObject
            {
                ["name"] = testCase.Name,
                ["hook"] = execution.Method,
                ["return"] = execution.ReturnValue,
                ["completed"] = execution.Completed,
                ["blocked"] = execution.BlockedReason,
                ["steps"] = new JsonArray(execution.StepLog.Select(text => (JsonNode)text!).ToArray()),
                ["actions"] = new JsonArray(execution.Actions.Select(text => (JsonNode)text!).ToArray()),
                ["logs"] = new JsonArray(execution.Logs.Select(text => (JsonNode)text!).ToArray()),
            });
        }

        Console.WriteLine(new JsonObject
        {
            ["fixture"] = Path.GetFileName(fixturePath),
            ["implemented_primitives"] = new JsonArray(
                CampaignPrimitiveRegistry.ImplementedOps.Select(op => (JsonNode)op!).ToArray()),
            ["cases"] = results,
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static CampaignGrid Grid(ExecutionGrid grid) => new(
        grid.Location ?? "",
        IsEnemy: grid.IsEnemy,
        IsBoss: grid.IsBoss,
        IsSiren: grid.IsSiren,
        IsMystery: grid.IsMystery,
        IsAmmo: grid.IsAmmo,
        IsFortress: grid.IsFortress,
        MayBoss: grid.MayBoss,
        IsCaughtBySiren: grid.IsCaughtBySiren,
        EnemyScale: grid.EnemyScale,
        EnemyGenre: grid.EnemyGenre,
        Weight: grid.Weight,
        Cost: grid.Cost,
        Cost2: grid.Cost2 ?? 9999);

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"[错误   ] {message}");
        return 1;
    }

    private sealed class ExecutionFixture
    {
        [JsonPropertyName("cases")] public List<ExecutionCase> Cases { get; init; } = [];
    }

    private sealed class ExecutionCase
    {
        [JsonPropertyName("name")] public string Name { get; init; } = "";
        [JsonPropertyName("chapter")] public string Chapter { get; init; } = "";
        [JsonPropertyName("level")] public string Level { get; init; } = "";
        [JsonPropertyName("hook")] public string Hook { get; init; } = "";
        [JsonPropertyName("config")] public ExecutionConfig? Config { get; init; }
        [JsonPropertyName("grids")] public List<ExecutionGrid>? Grids { get; init; }
    }

    private sealed class ExecutionConfig
    {
        [JsonPropertyName("enemy_priority")] public string? EnemyPriority { get; init; }
        [JsonPropertyName("map_clear_all_this_time")] public bool? MapClearAllThisTime { get; init; }
        [JsonPropertyName("map_has_siren")] public bool? MapHasSiren { get; init; }
        [JsonPropertyName("map_has_fortress")] public bool? MapHasFortress { get; init; }
        [JsonPropertyName("fleet_2")] public bool? Fleet2 { get; init; }
        [JsonPropertyName("fleet_boss")] public bool? FleetBoss { get; init; }
        [JsonPropertyName("fleet_current_index")] public int? FleetCurrentIndex { get; init; }
        [JsonPropertyName("battle_count")] public int? BattleCount { get; init; }
        [JsonPropertyName("map_has_movable_normal_enemy")] public bool? MapHasMovableNormalEnemy { get; init; }
    }

    private sealed class ExecutionGrid
    {
        [JsonPropertyName("location")] public string? Location { get; init; }
        [JsonPropertyName("is_enemy")] public bool IsEnemy { get; init; }
        [JsonPropertyName("is_boss")] public bool IsBoss { get; init; }
        [JsonPropertyName("is_siren")] public bool IsSiren { get; init; }
        [JsonPropertyName("is_mystery")] public bool IsMystery { get; init; }
        [JsonPropertyName("is_ammo")] public bool IsAmmo { get; init; }
        [JsonPropertyName("is_fortress")] public bool IsFortress { get; init; }
        [JsonPropertyName("may_boss")] public bool MayBoss { get; init; }
        [JsonPropertyName("is_caught_by_siren")] public bool IsCaughtBySiren { get; init; }
        [JsonPropertyName("enemy_scale")] public int EnemyScale { get; init; }
        [JsonPropertyName("enemy_genre")] public string? EnemyGenre { get; init; }
        [JsonPropertyName("weight")] public int Weight { get; init; }
        [JsonPropertyName("cost")] public int Cost { get; init; }
        [JsonPropertyName("cost_2")] public int? Cost2 { get; init; }
    }
}
