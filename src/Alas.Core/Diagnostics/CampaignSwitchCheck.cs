using System.Text.Json.Nodes;
using Alas.Runtime;

namespace Alas.Core.Diagnostics;

/// <summary>
/// R5 域级开关的自检入口：`Alas.Server r5-switch [--json]`。
///
/// 打印每个域（`loop` / `path` / `primitives`）当前的执行后端、是否已接进生产路径、以及取值来源说明。
/// 用于**在切换前/后都能一眼看清"现在到底跑的是谁"**——把开关状态变成可核对的事实，而不是口头约定。
///
/// 只读：不连设备、不改变任何运行状态。
/// </summary>
internal static class CampaignSwitchCheck
{
    public static int Run(bool asJson)
    {
        var snapshot = CampaignEngineSwitch.Snapshot();
        if (asJson)
        {
            var domains = new JsonArray();
            foreach (var domain in snapshot)
            {
                domains.Add(new JsonObject
                {
                    ["domain"] = domain.Name,
                    ["mode"] = domain.Mode.ToString(),
                    ["wired"] = domain.Wired,
                    ["note"] = domain.Note,
                });
            }
            Console.WriteLine(new JsonObject
            {
                ["allow_csharp"] = Environment.GetEnvironmentVariable(CampaignEngineSwitch.AllowCSharpVariable) == "1",
                ["domains"] = domains,
            }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        Console.WriteLine($"{"域",-14}{"当前后端",-12}{"已接生产路径",-14}说明");
        foreach (var domain in snapshot)
        {
            Console.WriteLine($"{domain.Name,-14}{domain.Mode,-12}{(domain.Wired ? "是" : "否（仅离线）"),-14}{domain.Note}");
        }
        Console.WriteLine();
        Console.WriteLine($"[闸门   ] {CampaignEngineSwitch.AllowCSharpVariable}=" +
                          (Environment.GetEnvironmentVariable(CampaignEngineSwitch.AllowCSharpVariable) == "1"
                              ? "1（已放行 csharp）" : "未设置（csharp 会被拒绝并退回影子模式）"));
        Console.WriteLine("[默认   ] 不改行为：loop=shadow（仍跑上游，多记一份 C# 决策），path/primitives 未接线");
        return 0;
    }
}
