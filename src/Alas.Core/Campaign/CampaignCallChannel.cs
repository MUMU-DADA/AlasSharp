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

    /// <summary>
    /// 给上游对象的属性赋值（点号路径的最后一段，如 <c>map.C1.is_flare</c>）。
    /// 上游自己的 helper 就是这么改地图状态的（`pick_up_flare` 里 `grid.is_flare = True`）；
    /// C# 侧替换了这些 helper，需要这条**受限写入**通道把同样的状态同步过去。
    /// 实现方必须与设备动作同一把锁（见 `tools/alas_vision.py` 的 `set` 分支）。
    /// </summary>
    void Set(string name, JsonNode? value);

    /// <summary>
    /// 取**上游地图的实时状态**（只读 op `s3_campaign_grids`）：每格的标志与成本场。
    /// 上游的移动/识别会改它自己的 `CampaignMap`（`Fleet.goto` 的 `wipe_out()` 与 `is_fleet` 重设、
    /// `find_path_initial` 重写 `cost*`），所以设备侧每次动作之后都要重新取一次，
    /// 否则 C# 的决策基于过期状态。录制渠道返回构造时给的桩状态。
    /// </summary>
    IReadOnlyList<CampaignGrid> ReadGrids();
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
    private IReadOnlyList<CampaignGrid> _grids = [];

    public RecordingCampaignCallChannel(IReadOnlyDictionary<string, JsonNode?>? state = null) =>
        _state = state is null ? new Dictionary<string, JsonNode?>() : new Dictionary<string, JsonNode?>(state);

    /// <summary>给 <see cref="ReadGrids"/> 用的桩状态（默认空：表示"没有上游地图状态可读"）。</summary>
    public RecordingCampaignCallChannel WithGrids(IReadOnlyList<CampaignGrid> grids)
    {
        _grids = grids;
        return this;
    }

    public IReadOnlyList<CampaignGrid> ReadGrids() => _grids;

    /// <summary>离线对照显式采用成功动作计数反馈；地图仍是固定夹具，不表示真实结算。</summary>
    public bool SimulateSuccessfulActions { get; init; }

    public List<CampaignCallRecord> Calls { get; } = [];

    public JsonNode? Call(string name, IReadOnlyList<JsonNode?> args,
                          IReadOnlyList<KeyValuePair<string, JsonNode?>> kwargs)
    {
        Calls.Add(new CampaignCallRecord(name,
            args.Select(Describe).ToArray(),
            kwargs.ToDictionary(pair => pair.Key, pair => Describe(pair.Value))));
        if (name == "withdraw") throw new CampaignControlFlowSignal("CampaignEnd", "Withdraw");
        if (name == "fleet_ensure")
        {
            int index = kwargs.Single(pair => pair.Key == "index").Value!.GetValue<int>();
            int before = _state["fleet_current_index"]!.GetValue<int>();
            _state["fleet_current_index"] = JsonValue.Create(index);
            return JsonValue.Create(before != index);
        }
        if (name == "submarine_move_near_boss") return JsonValue.Create(false);
        // The recorder models the selected-enemy endpoint's boolean result.
        if (name.EndsWith("clear_chosen_enemy", StringComparison.Ordinal))
        {
            // 录制渠道要提供与上游战斗结算一致的反馈：成功清敌后 battle_count 增长。
            // 这样 r5-device 与 r5-run 使用同一份状态演进，比较的是宿主逻辑而不是两个
            // 不同的静态桩。
            if (SimulateSuccessfulActions)
            {
                int count = _state["battle_count"]!.GetValue<int>();
                _state["battle_count"] = JsonValue.Create(count + 1);
                int? fleet = name.Split('.')[0] switch
                {
                    "fleet_1" => 1, "fleet_2" => 2,
                    "fleet_boss" => _state["fleet_boss_index"]!.GetValue<int>(),
                    _ => null,
                };
                if (fleet is int index) _state["fleet_current_index"] = JsonValue.Create(index);
            }
            return JsonValue.Create(true);
        }
        if (name == "clear_chosen_mystery" && SimulateSuccessfulActions)
            _state["mystery_count"] = JsonValue.Create(_state["mystery_count"]!.GetValue<int>() + 1);
        if (name is "picked_flare.append" or "picked_light_house.append")
        {
            var entries = _state[name.Split('.')[0]] as JsonArray
                ?? throw new InvalidDataException($"录制状态缺少 {name}");
            string location = args.Single()!.GetValue<string>();
            if (!location.StartsWith('#') || !_grids.Any(grid => grid.Location == location[1..]))
                throw new InvalidDataException($"录制地图没有拾取目标 {location}");
            entries.Add(location[1..]);
        }
        return null;
    }

    public JsonNode? Read(string name) => _state.GetValueOrDefault(name);

    /// <summary>写入也记进 <see cref="Calls"/>（名字前缀 <c>set:</c>），便于离线断言"状态确实同步了"。</summary>
    public void Set(string name, JsonNode? value)
    {
        if (name.StartsWith("map.", StringComparison.Ordinal))
        {
            string[] parts = name.Split('.');
            if (parts.Length != 3 || !_grids.Any(grid => grid.Location == parts[1]))
                throw new InvalidDataException($"录制地图没有写入目标 {name}");
            var changed = _grids.Select(grid => grid.Location == parts[1]
                ? CampaignPrimitives.ApplyFlag(grid, parts[2], value!.GetValue<bool>()) : grid).ToArray();
            _grids = changed;
        }
        Calls.Add(new CampaignCallRecord($"set:{name}", [Describe(value)], new Dictionary<string, string>()));
        _state[name] = value?.DeepClone();
    }

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
