using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Alas.Campaign;
using RulePlan = Alas.Campaign.CampaignPlan;

namespace Alas.Core.Diagnostics;

/// <summary>
/// R5「识别结果 → 引擎状态」的离线入口：`Alas.Server r5-state --chapter &lt;章&gt; --level &lt;关&gt;`。
///
/// 用关卡**声明的静态地图**构造引擎要吃的格子集合（含 `is_fleet` 与按上游多舰队顺序算出的
/// `cost` / `cost_1` / `cost_2`），可选地叠加一份**识别结果**（`grid_flags` 形态的 JSON）。
/// `--json` 输出的字段与执行夹具的 `grids` 一致，可直接喂给 `r5-exec`（帧 → 状态 → 干跑 这条链）。
///
/// **不连设备**：要看真机识别结果时，先用 `map` 等命令把识别 JSON 落盘再传进来。
/// </summary>
internal static class CampaignStateCheck
{
    public static int Run(string dataDir, string? chapter, string? level, string? chapterModule,
                          string? fleet1, string? fleet2, bool hasAmbush, int currentFleet,
                          string? detectionPath, bool asJson)
    {
        RulePlan? plan;
        if (!string.IsNullOrEmpty(chapterModule))
        {
            if (!CampaignPlanReader.TryReadModule(dataDir, chapterModule, out plan))
            {
                return Fail($"按模块名读不出关卡计划：{chapterModule}");
            }
        }
        else if (!string.IsNullOrEmpty(chapter) && !string.IsNullOrEmpty(level))
        {
            try
            {
                plan = CampaignPlanReader.Read(dataDir, chapter, level);
            }
            catch (Exception error) when (error is JsonException or IOException)
            {
                return Fail($"读不出关卡计划 {chapter}/{level}：{error.Message}");
            }
        }
        else
        {
            return Fail("用法：r5-state (--chapter <章> --level <关> | --chapter-module <模块名>) " +
                        "[--fleet-1 <格> --fleet-2 <格>] [--map-has-ambush] [--detection <识别.json>] [--json]");
        }
        if (plan is null) return Fail("关卡计划为空");

        var grids = CampaignMapState.FromPlan(plan, fleet1, fleet2, hasAmbush, currentFleet);
        if (grids.Count == 0) return Fail($"{plan.Chapter}/{plan.Level} 的导出里没有可用 map_data");

        IReadOnlyList<string> unknownFlags = [];
        if (!string.IsNullOrEmpty(detectionPath))
        {
            if (!File.Exists(detectionPath)) return Fail($"找不到识别结果：{detectionPath}");
            Dictionary<string, List<string>>? flags;
            try
            {
                flags = JsonSerializer.Deserialize<DetectionFile>(File.ReadAllText(detectionPath),
                                                                  new JsonSerializerOptions
                                                                  {
                                                                      PropertyNameCaseInsensitive = true,
                                                                      ReadCommentHandling = JsonCommentHandling.Skip,
                                                                      AllowTrailingCommas = true,
                                                                  })?.GridFlags;
            }
            catch (JsonException error)
            {
                return Fail($"识别结果解析失败：{error.Message}");
            }
            grids = CampaignMapState.OverlayDetection(grids, flags, out unknownFlags);
        }

        if (asJson)
        {
            Console.WriteLine(new JsonObject
            {
                ["chapter"] = plan.Chapter,
                ["level"] = plan.Level,
                ["grids"] = new JsonArray(grids.Select(grid => (JsonNode)new JsonObject
                {
                    ["location"] = grid.Location,
                    ["is_enemy"] = grid.IsEnemy,
                    ["is_boss"] = grid.IsBoss,
                    ["is_siren"] = grid.IsSiren,
                    ["is_mystery"] = grid.IsMystery,
                    ["is_ammo"] = grid.IsAmmo,
                    ["is_fortress"] = grid.IsFortress,
                    ["is_fleet"] = grid.IsFleet,
                    ["is_cleared"] = grid.IsCleared,
                    ["is_land"] = grid.IsLand,
                    ["is_mechanism_block"] = grid.IsMechanismBlock,
                    ["may_enemy"] = grid.MayEnemy,
                    ["may_boss"] = grid.MayBoss,
                    ["may_mystery"] = grid.MayMystery,
                    ["may_siren"] = grid.MaySiren,
                    ["may_ammo"] = grid.MayAmmo,
                    ["may_ambush"] = grid.MayAmbush,
                    ["weight"] = grid.Weight,
                    ["cost"] = grid.Cost,
                    ["cost_1"] = grid.Cost1,
                    ["cost_2"] = grid.Cost2,
                }).ToArray()),
                ["unknown_flags"] = new JsonArray(unknownFlags.Select(flag => (JsonNode)flag!).ToArray()),
            }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        int land = grids.Count(grid => grid.IsLand);
        int enemy = grids.Count(grid => grid.IsEnemy);
        int fleet = grids.Count(grid => grid.IsFleet);
        int reachable = grids.Count(grid => grid.Cost < CampaignPathfinder.Unreachable);
        var costs = grids.Where(grid => grid.Cost < CampaignPathfinder.Unreachable)
                         .Select(grid => grid.Cost).OrderBy(value => value).ToArray();
        Console.WriteLine($"[状态   ] {plan.Chapter}/{plan.Level}：{grids.Count} 格（陆地 {land}，敌人 {enemy}，舰队 {fleet}）");
        Console.WriteLine($"[成本场 ] 可达 {reachable}/{grids.Count}" +
                          (costs.Length == 0 ? "" : $"，cost 中位 {costs[costs.Length / 2]}、最大 {costs[^1]}"));
        if (!string.IsNullOrEmpty(fleet1)) Console.WriteLine($"[舰队   ] fleet_1={fleet1}，fleet_2={fleet2 ?? "（未部署）"}，当前={currentFleet}");
        if (!string.IsNullOrEmpty(detectionPath))
        {
            Console.WriteLine($"[识别   ] 已叠加 {detectionPath}" +
                              (unknownFlags.Count == 0 ? "，没有认不出的标志" : $"，认不出的标志：{string.Join(", ", unknownFlags)}"));
        }
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"[错误   ] {message}");
        return 1;
    }

    /// <summary>识别结果文件里我们消费的字段：与 `map_detection_verify.json` 的 `grid_flags` 同形。</summary>
    private sealed class DetectionFile
    {
        [JsonPropertyName("grid_flags")] public Dictionary<string, List<string>>? GridFlags { get; init; }
    }
}
