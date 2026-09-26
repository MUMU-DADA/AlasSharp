using System.Text.Json.Nodes;
using Alas.Vision;

namespace Alas.Campaign;

/// <summary>原生宿主异常的明确类型与响应；错误文本不参与控制流判定。</summary>
public sealed class CampaignNativeException(string? nativeType, string response) : InvalidOperationException(response)
{
    public string? NativeType { get; } = nativeType;
}

/// <summary>
/// **真机渠道**：把 <see cref="ICampaignCallChannel"/> 接到视觉/设备宿主（`s3_campaign_call`）。
///
/// 安全边界（有意设计）：
/// <list type="bullet">
///   <item>本类尚未接入生产战役；双闸门开启后的 csharp 选项仍回到原生调度。
///         命令分支（`Alas.Server r5-*`）使用录制渠道，不能用离线诊断冒充真机验收；</item>
///   <item>`allow_actions=true` 是**显式**传的：本渠道的存在前提就是"调用方已经拿到动作授权"
///         （上游 `s3_campaign_call` 的只读白名单联锁仍会独立校验一次）。</item>
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
        var result = Response("s3_campaign_call", payload);
        if (result["campaign_end"]?.GetValue<bool>() == true)
            throw new CampaignControlFlowSignal("CampaignEnd", result["reason"]?.GetValue<string>() ?? "CampaignEnd");
        if (result["control_flow"]?.GetValue<string>() is { } signal)
            throw new CampaignControlFlowSignal(signal, result["reason"]?.GetValue<string>() ?? signal);
        if (!result.ContainsKey("value")) throw new InvalidDataException($"{name}: missing call value");
        return result["value"]?.DeepClone();
    }

    public JsonNode? Read(string name)
    {
        var payload = new JsonObject { ["name"] = name, ["read_only"] = true };
        var result = Response("s3_campaign_call", payload);
        if (result["callable"]?.GetValue<bool>() != false || !result.ContainsKey("value"))
            throw new InvalidDataException($"{name}: invalid state response");
        return result["value"]?.DeepClone();
    }

    /// <summary>
    /// 给上游对象的属性赋值（`s3_campaign_call` 的 `set` 形式）。与设备动作同一把锁：
    /// 带上 <c>allow_actions=true</c>，上游侧也要求它（写入会改变后续调用读到的状态）。
    /// </summary>
    public void Set(string name, JsonNode? value)
    {
        var payload = new JsonObject
        {
            ["name"] = name,
            ["set"] = value?.DeepClone(),
            ["allow_actions"] = true,
        };
        var result = Response("s3_campaign_call", payload);
        if (result["set"]?.GetValue<bool>() != true)
            throw new InvalidDataException($"{name}: state write was not acknowledged");
    }

    /// <summary>取上游地图的实时状态（只读 op `s3_campaign_grids`）并解析成 C# 的格子模型。</summary>
    public IReadOnlyList<CampaignGrid> ReadGrids()
    {
        var payload = Response("s3_campaign_grids", new JsonObject());
        var grids = CampaignMapState.FromUpstream(payload, out var unknownFlags);
        if (unknownFlags.Count > 0)
        {
            // 认不出的标志**不静默丢**：记在日志里，便于发现上游新增了标志而 C# 还没接
            Console.Error.WriteLine($"[warn] s3_campaign_grids 里有 C# 不认识的格子标志: {string.Join(", ", unknownFlags)}");
        }
        return grids;
    }

    private JsonObject Response(string operation, JsonObject arguments)
    {
        var result = _vision.CallTyped<JsonNode>(operation, arguments) as JsonObject
            ?? throw new InvalidDataException($"{operation}: expected response object");
        if (result["error"] is not null)
            throw new CampaignNativeException(result["exception_type"]?.GetValue<string>(),
                $"{operation}: {result.ToJsonString()}");
        if (result["refused"]?.GetValue<bool>() == true)
            throw new InvalidOperationException($"{operation}: {result.ToJsonString()}");
        return result;
    }
}
