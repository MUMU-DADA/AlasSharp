using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Alas.Campaign;

namespace Alas.Core.Diagnostics;

/// <summary>
/// R5 端到端干跑：`Alas.Server r5-run --chapter &lt;章&gt; --level &lt;关&gt; [--frame &lt;帧&gt; | --detection &lt;识别.json&gt;]`。
///
/// 把重写引擎的整条链在**一张真实地图帧**上闭合：
/// <list type="number">
///   <item>读关卡计划（静态规则）；</item>
///   <item>造引擎状态：声明地图 + 舰队位置 + 成本场（占位实现见 <see cref="CampaignDryRunHelper"/>）；</item>
///   <item>有帧就**进程内跑地图识别**（上游 `module/map_detection`），把识别到的运行期标志叠加进状态；</item>
///   <item>跑 C# 的关卡循环——**干跑**，动作只被记录（不连设备、不点任何东西）。</item>
/// </list>
///
/// 用法：
///   Alas.Server r5-run --chapter campaign_main --level campaign_3_1 --frame data/fixtures/inmap_3-1.png
/// </summary>
internal static class CampaignRunCheck
{
    public static int Run(string dataDir, string repoDir, string toolsDir, CampaignDryRunHelper.Options options,
                          bool asJson)
    {
        if (!CampaignDryRunHelper.TryExecute(dataDir, repoDir, toolsDir, options, out var dryRun, out string? error))
        {
            return Fail(error ?? "干跑失败");
        }
        var run = dryRun!.Run;
        var host = dryRun.Host;

        if (asJson)
        {
            Console.WriteLine(new JsonObject
            {
                ["chapter"] = run.Chapter,
                ["level"] = run.Level,
                ["outcome"] = run.Outcome.ToString(),
                ["detail"] = run.Detail,
                ["detection"] = dryRun.DetectionSource,
                ["unknown_flags"] = new JsonArray(dryRun.UnknownFlags.Select(flag => (JsonNode)flag!).ToArray()),
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

        Console.WriteLine($"[关卡   ] {run.Chapter}/{run.Level}：{dryRun.Grids.Count} 格" +
                          (dryRun.DetectionSource is null ? "（未叠加识别）" : $"，识别来源 {dryRun.DetectionSource}"));
        if (dryRun.UnknownFlags.Count > 0)
        {
            Console.WriteLine($"[识别   ] 认不出的标志：{string.Join(", ", dryRun.UnknownFlags)}");
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
}
