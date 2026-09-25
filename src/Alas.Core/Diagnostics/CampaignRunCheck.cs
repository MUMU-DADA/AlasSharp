using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Alas.Campaign;
using Alas.MapDetection;
using Alas.Vision;
using RulePlan = Alas.Campaign.CampaignPlan;

namespace Alas.Core.Diagnostics;

/// <summary>
/// R5 端到端干跑：`Alas.Server r5-run --chapter &lt;章&gt; --level &lt;关&gt; [--frame &lt;帧&gt; | --detection &lt;识别.json&gt;]`。
///
/// 把重写引擎的整条链在**一张真实地图帧**上闭合：
/// <list type="number">
///   <item>读关卡计划（静态规则）；</item>
///   <item>造引擎状态：声明地图 + 舰队位置 + 成本场（<see cref="CampaignMapState.FromPlan"/>）；</item>
///   <item>有帧就**进程内跑地图识别**（上游 `module/map_detection`），把识别出的
///         `is_enemy` / `is_boss` / `is_fleet` 等运行期标志叠加进状态；</item>
///   <item>跑 C# 的关卡循环（<see cref="CampaignBattleLoop.Run"/>）——**干跑**，动作只被记录。</item>
/// </list>
///
/// **不连设备、不点任何东西**：帧来自磁盘，动作只进录制宿主。这条链跑通，才谈得上"原语动作层对拍"。
/// 用法：
///   Alas.Server r5-run --chapter campaign_main --level campaign_2_1 --frame data/fixtures/map_2_1.png
/// </summary>
internal static class CampaignRunCheck
{
    public static int Run(string dataDir, string repoDir, string toolsDir,
                          string? chapter, string? level, string? chapterModule,
                          string? frame, string? detectionPath,
                          string? fleet1, string? fleet2, int currentFleet, bool hasAmbush,
                          bool clearAll, bool poorMapData, bool useFleet2, bool fleetBoss,
                          bool hasSiren, bool hasFortress, string mode, bool asJson)
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
            return Fail("用法：r5-run (--chapter <章> --level <关> | --chapter-module <模块名>) " +
                        "[--frame <地图帧> | --detection <识别.json>] [--fleet-1 <格> --fleet-2 <格>] ...");
        }
        if (plan is null) return Fail("关卡计划为空");

        var grids = CampaignMapState.FromPlan(plan, fleet1, fleet2, hasAmbush, currentFleet);
        if (grids.Count == 0) return Fail($"{plan.Chapter}/{plan.Level} 的导出里没有可用 map_data");

        IReadOnlyList<string> unknownFlags = [];
        string? detectionSource = null;
        if (!string.IsNullOrEmpty(frame))
        {
            if (!File.Exists(frame)) return Fail($"找不到地图帧：{frame}");
            using IVisionEngine vision = InProcessVisionEngine.StartFromAlasFork(repoDir, toolsDir);
            vision.LoadScreenshot(frame);
            // 宿主侧按**完整模块名**加载章节（`campaign.campaign_main.campaign_2_1`）；
            // 只给目录名会报 ModuleNotFoundError（实测）。
            string detectionChapter = chapterModule ?? $"campaign.{plan.Chapter}.{plan.Level}";
            var detection = new MapDetectionClient(vision).DetectMap(mode, detectionChapter);
            if (detection.ExecutionError is { } error) return Fail($"地图识别失败：{error}");
            if (!detection.Detected) return Fail($"地图未检出：{detection.Reason ?? detection.Load}");
            grids = CampaignMapState.OverlayDetection(grids, detection.GridFlags, out unknownFlags);
            detectionSource = $"{Path.GetFileName(frame)}（识别到 {detection.GridCount} 格）";
        }
        else if (!string.IsNullOrEmpty(detectionPath))
        {
            if (!File.Exists(detectionPath)) return Fail($"找不到识别结果：{detectionPath}");
            var flags = JsonSerializer.Deserialize<DetectionFile>(File.ReadAllText(detectionPath),
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                })?.GridFlags;
            grids = CampaignMapState.OverlayDetection(grids, flags, out unknownFlags);
            detectionSource = Path.GetFileName(detectionPath);
        }

        var config = new CampaignRuntimeConfig(
            MapClearAllThisTime: clearAll,
            MapHasSiren: hasSiren,
            MapHasFortress: hasFortress,
            Fleet2: useFleet2,
            FleetBoss: fleetBoss,
            MapHasAmbush: hasAmbush,
            PoorMapData: poorMapData);
        var host = new RecordingCampaignHost(grids, config)
        {
            BouncingRoutes = plan.Map?.BouncingEnemyData ?? [],
            Fleet1Location = fleet1 ?? "",
            Fleet2Location = fleet2 ?? "",
            FleetCurrentIndex = currentFleet,
        };
        var run = CampaignBattleLoop.Run(plan, host);

        if (asJson)
        {
            Console.WriteLine(new JsonObject
            {
                ["chapter"] = run.Chapter,
                ["level"] = run.Level,
                ["outcome"] = run.Outcome.ToString(),
                ["detail"] = run.Detail,
                ["variant"] = CampaignBattleLoop.BattleFunctionVariant(config),
                ["detection"] = detectionSource,
                ["unknown_flags"] = new JsonArray(unknownFlags.Select(flag => (JsonNode)flag!).ToArray()),
                ["rounds"] = new JsonArray(run.Rounds.Select(round => (JsonNode)new JsonObject
                {
                    ["index"] = round.Index,
                    ["battle_count"] = round.BattleCount,
                    ["hook"] = round.Hook,
                    ["result"] = round.Result,
                    ["blocked"] = round.BlockedReason,
                }).ToArray()),
                ["actions"] = new JsonArray(host.Actions.Select(text => (JsonNode)text!).ToArray()),
            }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        Console.WriteLine($"[关卡   ] {run.Chapter}/{run.Level}：{grids.Count} 格" +
                          (detectionSource is null ? "（未叠加识别）" : $"，识别来源 {detectionSource}"));
        if (unknownFlags.Count > 0)
        {
            Console.WriteLine($"[识别   ] 认不出的标志：{string.Join(", ", unknownFlags)}");
        }
        Console.WriteLine($"[结论   ] {run.Outcome}" + (run.Detail is null ? "" : $"（{run.Detail}）"));
        foreach (var round in run.Rounds)
        {
            Console.WriteLine($"  第 {round.Index + 1} 轮 battle_count={round.BattleCount} → {round.Hook}：" +
                              (round.BlockedReason ?? (round.Result == true ? "真" : "假")));
        }
        if (host.Actions.Count > 0)
        {
            Console.WriteLine("[干跑动作]");
            foreach (string action in host.Actions)
            {
                Console.WriteLine($"  {action}");
            }
        }
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"[错误   ] {message}");
        return 1;
    }

    private sealed class DetectionFile
    {
        [JsonPropertyName("grid_flags")] public Dictionary<string, List<string>>? GridFlags { get; init; }
    }
}
