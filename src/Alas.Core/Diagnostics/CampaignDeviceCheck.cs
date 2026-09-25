using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Campaign;

namespace Alas.Core.Diagnostics;

/// <summary>
/// R5 设备宿主核对：`Alas.Server r5-device --chapter &lt;章&gt; --level &lt;关&gt; [--frame|--detection] [--json]`。
///
/// 用**录制渠道**（只记不发）驱动真机宿主 <see cref="DeviceCampaignHost"/> 跑一遍关卡循环，打印它会发出的
/// **上游方法调用序列**（方法名 + 参数引用形式）。用途：在接设备之前把"C# 到底会怎么调上游"变成可核对
/// 的事实；与 `r5-run`（录制宿主的动作序列）对照即可验证"同一套原语逻辑、只换宿主"。
///
/// **不连设备**：渠道是录制的，方法调用不会真的发出去。
/// </summary>
internal static class CampaignDeviceCheck
{
    public static int Run(string dataDir, string repoDir, string toolsDir,
                          CampaignDryRunHelper.Options options, bool asJson)
    {
        Alas.Campaign.CampaignPlan? plan;
        IReadOnlyList<CampaignGrid> grids;
        IReadOnlyList<string> unknownFlags;
        string? detectionSource;
        try
        {
            if (!CampaignDryRunHelper.TryBuildState(dataDir, repoDir, toolsDir, options,
                                                    out plan, out grids, out detectionSource, out unknownFlags,
                                                    out string? error))
            {
                return Fail(error ?? "造状态失败");
            }
        }
        catch (Exception failure) when (failure is JsonException or IOException)
        {
            return Fail(failure.Message);
        }
        if (plan is null) return Fail("关卡计划为空");

        var channel = new RecordingCampaignCallChannel();
        var host = new DeviceCampaignHost(channel, grids, CampaignDryRunHelper.BuildConfig(options))
        {
            BouncingRoutes = plan.Map?.BouncingEnemyData ?? [],
        };
        var run = CampaignBattleLoop.Run(plan, host);

        if (asJson)
        {
            Console.WriteLine(new JsonObject
            {
                ["chapter"] = run.Chapter,
                ["level"] = run.Level,
                ["outcome"] = run.Outcome.ToString(),
                ["detection"] = detectionSource,
                ["calls"] = new JsonArray(channel.Calls.Select(call => (JsonNode)new JsonObject
                {
                    ["name"] = call.Name,
                    ["args"] = new JsonArray(call.Args.Select(arg => (JsonNode?)JsonValue.Create(arg)).ToArray()),
                    ["kwargs"] = new JsonObject(call.Kwargs.Select(pair =>
                        new KeyValuePair<string, JsonNode?>(pair.Key, JsonValue.Create(pair.Value))).ToArray()),
                }).ToArray()),
            }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        Console.WriteLine($"[设备宿主] {run.Chapter}/{run.Level}：{channel.Calls.Count} 次上游调用" +
                          (detectionSource is null ? "（未叠加识别）" : $"（识别来源 {detectionSource}）"));
        Console.WriteLine($"[结论   ] {run.Outcome}" + (run.Detail is null ? "" : $"（{run.Detail}）"));
        foreach (var call in channel.Calls)
        {
            Console.WriteLine($"  {call}");
        }
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"[错误   ] {message}");
        return 1;
    }
}
