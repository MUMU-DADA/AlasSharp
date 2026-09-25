using System.Text.Json.Nodes;

namespace Alas.Campaign;

/// <summary>
/// 设备宿主的**调用渠道**：把"调用上游某个方法"这件事抽象出来。
///
/// 两个实现：
/// <list type="bullet">
///   <item><see cref="VisionCampaignCallChannel"/>：真机渠道，走宿主 `s3_campaign_call`（点号路径、
///         `#节点`/`#grids:`/`#roads:` 引用、`kwargs`、`allow_actions`）；</item>
///   <item><see cref="RecordingCampaignCallChannel"/>：**录制渠道**，只记不发——用于离线核对
///         "设备宿主会不会按同样的顺序调同样的上游方法"，不碰设备。</item>
/// </list>
/// 引用形式与宿主侧的 `_campaign_arg` 一一对应（见 `docs/upstream-engine-rewrite.md` 的"宿主调用翻译"）。
/// </summary>
public interface ICampaignCallChannel
{
    /// <summary>调用上游方法（`name` 支持点号路径，如 `fleet_2.clear_boss`）。</summary>
    JsonNode? Call(string name, IReadOnlyList<JsonNode?> args,
                   IReadOnlyList<KeyValuePair<string, JsonNode?>> kwargs);

    /// <summary>读上游实例上的属性/状态（只读，不发设备动作）。</summary>
    JsonNode? Read(string name);
}

/// <summary>一次录制的调用。</summary>
public sealed record CampaignCallRecord(string Name, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Kwargs)
{
    public override string ToString() =>
        $"{Name}({string.Join(", ", Args.Concat(Kwargs.Select(pair => $"{pair.Key}={pair.Value}")))})";
}

/// <summary>录制渠道：只记不发（离线核对用）。`Read` 返回调用方给的**桩状态**。</summary>
public sealed class RecordingCampaignCallChannel : ICampaignCallChannel
{
    private readonly Dictionary<string, JsonNode?> _state;

    public RecordingCampaignCallChannel(IReadOnlyDictionary<string, JsonNode?>? state = null) =>
        _state = state is null ? new Dictionary<string, JsonNode?>() : new Dictionary<string, JsonNode?>(state);

    public List<CampaignCallRecord> Calls { get; } = [];

    public JsonNode? Call(string name, IReadOnlyList<JsonNode?> args,
                          IReadOnlyList<KeyValuePair<string, JsonNode?>> kwargs)
    {
        Calls.Add(new CampaignCallRecord(name,
            args.Select(Describe).ToArray(),
            kwargs.ToDictionary(pair => pair.Key, pair => Describe(pair.Value))));
        return null;
    }

    public JsonNode? Read(string name) => _state.GetValueOrDefault(name);

    /// <summary>
    /// 记录用的人读形式：**字符串取原值**（`ToJsonString()` 会把 `#D2` 记成 `"\"#D2\""`，
    /// 对照时要再剥一层引号——实测踩过），其它类型用 JSON 文本。
    /// </summary>
    private static string Describe(JsonNode? value) => value switch
    {
        null => "null",
        JsonValue json when json.TryGetValue<string>(out string? text) => text,
        _ => value.ToJsonString(),
    };
}
