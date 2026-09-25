using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Alas.Campaign;
using RulePlan = Alas.Campaign.CampaignPlan;

namespace Alas.Core.Diagnostics;

/// <summary>
/// R5 影子模式入口：`Alas.Server r5-shadow --chapter &lt;章&gt; --level &lt;关&gt; --log &lt;上游运行日志&gt;`。
///
/// 它读**上游实际运行日志**，按 `BATTLE_N` 头部还原每轮的 battle_count，算出 C# 引擎在同一
/// battle_count 下会选哪个钩子（`CampaignBattleLoop.SelectHook`），与日志里的 `Using function: …`
/// 逐步比对，输出一致 / 不一致 / 跳过的明细。
///
/// **只算不执行**：不连设备、不改变任何运行状态；这是把域级开关接进生产路径之前的验证手段。
/// 用法：
///   Alas.Server r5-shadow --chapter campaign_main --level campaign_1_4 --log data/s3_native_1_4.log
/// </summary>
internal static class CampaignShadowCheck
{
    public static int Run(string dataDir, string? chapter, string? level, string? logPath, bool asJson,
                          string declaredVariant = CampaignShadow.DefaultVariant)
    {
        if (string.IsNullOrEmpty(chapter) || string.IsNullOrEmpty(level))
        {
            return Fail("用法：r5-shadow --chapter <章> --level <关> --log <上游运行日志>");
        }
        if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath))
        {
            return Fail($"找不到日志：{logPath}");
        }

        RulePlan plan;
        try
        {
            plan = CampaignPlanReader.Read(dataDir, chapter, level);
        }
        catch (Exception error) when (error is JsonException or IOException)
        {
            return Fail($"读不出关卡计划 {chapter}/{level}：{error.Message}");
        }

        var observation = UpstreamLogParser.Parse(File.ReadAllText(logPath));
        var comparison = CampaignShadow.Compare(plan, observation, declaredVariant);

        if (asJson)
        {
            Console.WriteLine(new JsonObject
            {
                ["chapter"] = comparison.Chapter,
                ["level"] = comparison.Level,
                ["campaign_end"] = observation.CampaignEnd,
                ["exhausted"] = observation.BattleFunctionExhausted,
                ["matched"] = comparison.Matched,
                ["mismatched"] = comparison.Mismatched,
                ["skipped"] = comparison.Skipped,
                ["variant"] = declaredVariant,
                ["rows"] = new JsonArray(comparison.Rows.Select(row => (JsonNode)new JsonObject
                {
                    ["battle_count"] = row.BattleCount,
                    ["shadow"] = row.Expected,
                    ["upstream"] = row.Actual,
                    ["verdict"] = row.Verdict,
                    ["note"] = row.Note,
                }).ToArray()),
            }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return comparison.Clean ? 0 : 1;
        }

        Console.WriteLine($"[影子比对] {comparison.Chapter}/{comparison.Level}：上游共 {observation.Rounds.Count} 轮出击" +
                          $"（Campaign end={observation.CampaignEnd}，耗尽={observation.BattleFunctionExhausted}，" +
                          $"本次声明变体 {declaredVariant}）");
        Console.WriteLine($"{"轮次",-6}{"battle",-8}{"影子会选",-16}{"上游实际",-24}{"判定",-8}说明");
        foreach (var row in comparison.Rows)
        {
            Console.WriteLine($"{row.BattleCount + 1,-6}{row.BattleCount,-8}{row.Expected,-16}" +
                              $"{row.Actual ?? "—",-24}{row.Verdict,-8}{row.Note ?? ""}");
        }
        Console.WriteLine();
        Console.WriteLine($"[结论   ] 一致 {comparison.Matched} / 不一致 {comparison.Mismatched} / 跳过 {comparison.Skipped}");
        return comparison.Clean ? 0 : 1;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"[错误   ] {message}");
        return 1;
    }
}
