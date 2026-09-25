using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Campaign;

namespace Alas.Core.Diagnostics;

/// <summary>
/// R5 钩子选择查询：`Alas.Server r5-hooks --chapter &lt;章&gt; --level &lt;关&gt; [--battle-counts 0,1,2] [--json]`。
///
/// 打印"给定 `battle_count` 时 C# 引擎会选哪个钩子"（`CampaignBattleLoop.SelectHook`）。用途：与
/// **上游自己的 `battle_function`** 逐点对拍（见 `tools/diagnostics/r5_decision_sweep.py`）——
/// 那边的基准是上游真实代码，不是我们复刻的选择规则。
///
/// 只读：不连设备、不改变任何运行状态。
/// </summary>
internal static class CampaignHooksCheck
{
    public static int Run(string dataDir, string? chapter, string? level, string? counts, bool asJson)
    {
        if (string.IsNullOrEmpty(chapter) || string.IsNullOrEmpty(level))
        {
            return Fail("用法：r5-hooks --chapter <章> --level <关> [--battle-counts 0,1,2,3] [--json]");
        }
        Alas.Campaign.CampaignPlan plan;
        try
        {
            plan = CampaignPlanReader.Read(dataDir, chapter, level);
        }
        catch (Exception error) when (error is JsonException or IOException)
        {
            return Fail($"读不出关卡计划 {chapter}/{level}：{error.Message}");
        }

        var values = new List<int>();
        foreach (string token in (counts ?? "0,1,2,3,4").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(token.Trim(), out int value)) values.Add(value);
        }
        var rows = new JsonArray();
        foreach (int battleCount in values)
        {
            rows.Add(new JsonObject
            {
                ["battle_count"] = battleCount,
                ["hook"] = CampaignBattleLoop.SelectHook(plan, battleCount),
            });
        }
        if (asJson)
        {
            Console.WriteLine(new JsonObject
            {
                ["chapter"] = plan.Chapter,
                ["level"] = plan.Level,
                ["hooks"] = rows,
            }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        foreach (var row in rows)
        {
            Console.WriteLine($"  battle_count={row!["battle_count"]} → {row["hook"]}");
        }
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"[错误   ] {message}");
        return 1;
    }
}
