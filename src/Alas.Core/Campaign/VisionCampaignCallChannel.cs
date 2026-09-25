using System.Text.Json.Nodes;
using Alas.Vision;

namespace Alas.Campaign;

/// <summary>
/// **真机渠道**：把 <see cref="ICampaignCallChannel"/> 接到视觉/设备宿主（`s3_campaign_call`）。
///
/// 安全边界（有意设计）：
/// <list type="bullet">
///   <item>本类**只被"由 C# 执行关卡循环"的路径构造**，而那条路径只有在
///         `ALAS_ENGINE_LOOP=csharp` **且** `ALAS_ENGINE_ALLOW_CSHARP=1` 时才可能进入
///         （见 <c>Runtime/CampaignEngineSwitch</c>）；命令分支（`Alas.Server r5-*`）一律不构造它——
///         `tools/diagnostics/verify_r5_switch.py` 里有静态断言，防止它被误接到诊断入口上；</item>
///   <item>`allow_actions=true` 是**显式**传的：本渠道的存在前提就是"调用方已经拿到动作授权"
///         （上游 `s3_campaign_call` 的危险前缀联锁仍会独立校验一次）。</item>
/// </list>
///
/// 读取状态（<see cref="Read"/>）走同一个 op：上游对**非可调用**的名字返回 `{callable:false, value:…}`，
/// 这正是"只读实例属性"的通道，不发任何设备动作。
/// </summary>
public sealed class VisionCampaignCallChannel : ICampaignCallChannel
{
    private readonly IVisionEngine _vision;

    public VisionCampaignCallChannel(IVisionEngine vision) => _vision = vision;

    public JsonNode? Call(string name, IReadOnlyList<JsonNode?> args,
                          IReadOnlyList<KeyValuePair<string, JsonNode?>> kwargs)
    {
        var payload = new JsonObject
        {
            ["name"] = name,
            ["allow_actions"] = true,
            ["args"] = new JsonArray(args.Select(arg => arg?.DeepClone()).ToArray()),
            ["kwargs"] = new JsonObject(kwargs.Select(pair =>
                new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value?.DeepClone())).ToArray()),
        };
        return _vision.CallTyped<JsonNode>("s3_campaign_call", payload);
    }

    public JsonNode? Read(string name)
    {
        var payload = new JsonObject { ["name"] = name };
        var result = _vision.CallTyped<JsonNode>("s3_campaign_call", payload);
        return result is JsonObject envelope && envelope.TryGetPropertyValue("value", out var value)
            ? value
            : null;
    }
}
