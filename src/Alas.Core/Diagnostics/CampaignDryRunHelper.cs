using System.Text.Json;
using Alas.Campaign;
using Alas.MapDetection;
using Alas.Vision;

namespace Alas.Core.Diagnostics;

/// <summary>一次 C# 干跑的结果（状态 + 轨迹 + 动作），供 `r5-run` / `r5-diff` 共用。</summary>
internal sealed record CampaignDryRunResult(
    Alas.Campaign.CampaignPlan Plan,
    IReadOnlyList<CampaignGrid> Grids,
    string? DetectionSource,
    IReadOnlyList<string> UnknownFlags,
    CampaignLevelRun Run,
    RecordingCampaignHost Host);

/// <summary>
/// 「造引擎状态 + 跑关卡循环干跑」的共用实现：`r5-run`（看轨迹）与 `r5-diff`（与上游动作对照）都走这里，
/// 保证两边用的是同一套状态构造与同一套循环，不会出现"对照器跑的是另一套逻辑"。
/// </summary>
internal static class CampaignDryRunHelper
{
    public sealed record Options(
        string? Chapter, string? Level, string? ChapterModule,
        string? Frame, string? DetectionPath,
        string? Fleet1, string? Fleet2, int CurrentFleet, bool HasAmbush,
        bool ClearAll, bool PoorMapData, bool UseFleet2, bool FleetBoss,
        bool HasSiren, bool HasFortress, string Mode);

    /// <summary>造状态并干跑；失败时返回错误文本（调用方转成退出码）。</summary>
    public static bool TryExecute(string dataDir, string repoDir, string toolsDir, Options options,
                                  out CampaignDryRunResult? result, out string? error)
    {
        result = null;
        error = null;

        Alas.Campaign.CampaignPlan? plan;
        if (!string.IsNullOrEmpty(options.ChapterModule)
            && CampaignPlanReader.TryReadModule(dataDir, options.ChapterModule, out plan))
        {
            // 已解析
        }
        else if (!string.IsNullOrEmpty(options.Chapter) && !string.IsNullOrEmpty(options.Level))
        {
            try
            {
                plan = CampaignPlanReader.Read(dataDir, options.Chapter, options.Level);
            }
            catch (Exception failure) when (failure is JsonException or IOException)
            {
                error = $"读不出关卡计划 {options.Chapter}/{options.Level}：{failure.Message}";
                return false;
            }
        }
        else
        {
            error = "需要关卡：--chapter <章> --level <关> 或 --chapter-module <模块名>";
            return false;
        }
        if (plan is null)
        {
            error = "关卡计划为空";
            return false;
        }

        var grids = CampaignMapState.FromPlan(plan, options.Fleet1, options.Fleet2, options.HasAmbush,
                                              options.CurrentFleet);
        if (grids.Count == 0)
        {
            error = $"{plan.Chapter}/{plan.Level} 的导出里没有可用 map_data";
            return false;
        }

        IReadOnlyList<string> unknownFlags = [];
        string? detectionSource = null;
        if (!string.IsNullOrEmpty(options.Frame))
        {
            if (!File.Exists(options.Frame))
            {
                error = $"找不到地图帧：{options.Frame}";
                return false;
            }
            using IVisionEngine vision = InProcessVisionEngine.StartFromAlasFork(repoDir, toolsDir);
            vision.LoadScreenshot(options.Frame);
            // 宿主侧按**完整模块名**加载章节（`campaign.campaign_main.campaign_2_1`）；
            // 只给目录名会报 ModuleNotFoundError（实测）。
            string detectionChapter = options.ChapterModule ?? $"campaign.{plan.Chapter}.{plan.Level}";
            var detection = new MapDetectionClient(vision).DetectMap(options.Mode, detectionChapter);
            if (detection.ExecutionError is { } detectionError)
            {
                error = $"地图识别失败：{detectionError}";
                return false;
            }
            if (!detection.Detected)
            {
                error = $"地图未检出：{detection.Reason ?? detection.Load}";
                return false;
            }
            grids = CampaignMapState.OverlayDetection(grids, detection.GridFlags, out unknownFlags);
            detectionSource = $"{Path.GetFileName(options.Frame)}（识别到 {detection.GridCount} 格）";
        }
        else if (!string.IsNullOrEmpty(options.DetectionPath))
        {
            if (!File.Exists(options.DetectionPath))
            {
                error = $"找不到识别结果：{options.DetectionPath}";
                return false;
            }
            Dictionary<string, List<string>>? flags;
            try
            {
                flags = JsonSerializer.Deserialize<DetectionFile>(File.ReadAllText(options.DetectionPath),
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        ReadCommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true,
                    })?.GridFlags;
            }
            catch (JsonException failure)
            {
                error = $"识别结果解析失败：{failure.Message}";
                return false;
            }
            grids = CampaignMapState.OverlayDetection(grids, flags, out unknownFlags);
            detectionSource = Path.GetFileName(options.DetectionPath);
        }

        var config = new CampaignRuntimeConfig(
            MapClearAllThisTime: options.ClearAll,
            MapHasSiren: options.HasSiren,
            MapHasFortress: options.HasFortress,
            Fleet2: options.UseFleet2,
            FleetBoss: options.FleetBoss,
            MapHasAmbush: options.HasAmbush,
            PoorMapData: options.PoorMapData);
        var host = new RecordingCampaignHost(grids, config)
        {
            BouncingRoutes = plan.Map?.BouncingEnemyData ?? [],
            Fleet1Location = options.Fleet1 ?? "",
            Fleet2Location = options.Fleet2 ?? "",
            FleetCurrentIndex = options.CurrentFleet,
        };
        var run = CampaignBattleLoop.Run(plan, host);
        result = new CampaignDryRunResult(plan, grids, detectionSource, unknownFlags, run, host);
        return true;
    }

    /// <summary>干跑动作 → 原语级轨迹（与上游日志解析出的轨迹同形，便于对照）。</summary>
    public static CampaignActionTrace ToTrace(CampaignDryRunResult dryRun)
    {
        var actions = dryRun.Host.Actions.Select(RawAction).ToArray();
        var primitives = actions.Select(action => action.Primitive).Distinct(StringComparer.Ordinal).ToArray();
        return new CampaignActionTrace(actions, primitives, []);
    }

    /// <summary>把录制宿主的动作行（如 `clear_chosen_enemy(D2, expected=boss)`）解回原语名与目标。</summary>
    private static CampaignAction RawAction(string raw)
    {
        string name = raw;
        string? target = null;
        int open = raw.IndexOf('(');
        if (open > 0)
        {
            name = raw[..open];
            int close = raw.IndexOfAny([',', ')'], open + 1);
            target = close > open + 1 ? raw[(open + 1)..close].Trim() : null;
        }
        return new CampaignAction(name, string.IsNullOrEmpty(target) ? null : target, raw);
    }

    private sealed class DetectionFile
    {
        [System.Text.Json.Serialization.JsonPropertyName("grid_flags")]
        public Dictionary<string, List<string>>? GridFlags { get; init; }
    }
}
