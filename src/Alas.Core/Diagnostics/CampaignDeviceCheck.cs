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

        // 状态类宿主操作的自检：`ClearCaughtBySirenFlags` 对应上游"逐格置假"那段，**不该有设备动作**。
        // 两个宿主都要覆盖：设备宿主（0 次渠道调用）与干跑宿主（模型也要真的被改，否则轨迹与上游不一致）。
        var stateChannel = new RecordingCampaignCallChannel();
        var stateGrids = grids.Take(3)
            .Select((grid, index) => grid with { IsCaughtBySiren = index < 2 })
            .ToArray();
        var stateHost = new DeviceCampaignHost(stateChannel, stateGrids);
        int caughtBefore = stateHost.Grids.Count(grid => grid.IsCaughtBySiren);
        stateHost.ClearCaughtBySirenFlags();
        int caughtAfter = stateHost.Grids.Count(grid => grid.IsCaughtBySiren);
        int stateCalls = stateChannel.Calls.Count;

        var dryHost = new RecordingCampaignHost(stateGrids);
        dryHost.ClearCaughtBySirenFlags();
        int dryCaughtAfter = dryHost.Grids.Count(grid => grid.IsCaughtBySiren);
        if (caughtAfter != 0 || dryCaughtAfter != 0 || stateCalls != 0)
        {
            return Fail($"ClearCaughtBySirenFlags 语义不符：设备宿主置假前 {caughtBefore} / 后 {caughtAfter}，" +
                        $"干跑宿主后 {dryCaughtAfter}，设备调用 {stateCalls}（都应清空且 0 次调用）");
        }

        if (asJson)
        {
            Console.WriteLine(new JsonObject
            {
                ["chapter"] = run.Chapter,
                ["level"] = run.Level,
                ["outcome"] = run.Outcome.ToString(),
                ["detection"] = detectionSource,
                ["state_ops"] = new JsonObject
                {
                    ["clear_caught_by_siren"] = new JsonObject
                    {
                        ["caught_before"] = caughtBefore,
                        ["caught_after"] = caughtAfter,
                        ["dry_run_caught_after"] = dryCaughtAfter,
                        ["device_calls"] = stateCalls,
                    },
                },
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
        Console.WriteLine($"[状态操作] clear_caught_by_siren：置假前 {caughtBefore} → 后 {caughtAfter}，" +
                          $"设备调用 {stateCalls} 次（应为 0）");
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
