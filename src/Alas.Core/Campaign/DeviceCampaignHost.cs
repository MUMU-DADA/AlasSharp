using System.Text.Json.Nodes;

namespace Alas.Campaign;

/// <summary>
/// **真机宿主**（路线 a）：实现 <see cref="ICampaignPrimitiveHost"/>，把每个成员翻成对上游方法的调用，
/// 经 <see cref="ICampaignCallChannel"/> 发出。C# 决定"做哪个原语、对哪个格子"，上游方法负责
/// "怎么在设备上做"——不在 C# 里复刻地图点击几何、相机与进战流程（那属于被禁止的逐地图/逐界面适配）。
///
/// 与 <see cref="RecordingCampaignHost"/>（离线干跑用）的关系：**同一套原语逻辑，只换宿主**。
/// 因此核对方式也是现成的：同关卡同状态下，两边发出的原语序列必须一致
/// （`r5-device` 与 `r5-run` 的对照就是这个）。
///
/// 两条纪律：
/// <list type="number">
///   <item>状态只**读**（`battle_count` / `fleet_*_location` / `camera` …），动作只调上游方法；</item>
///   <item>上游没有对应方法的成员**如实抛 <see cref="NotSupportedException"/>**（带上原因），不静默降级。</item>
/// </list>
/// </summary>
public sealed class DeviceCampaignHost : ICampaignPrimitiveHost
{
    private readonly ICampaignCallChannel _channel;
    private readonly List<string> _logs = [];
    private readonly List<CampaignGrid> _grids;

    public DeviceCampaignHost(ICampaignCallChannel channel, IReadOnlyList<CampaignGrid> grids,
                              CampaignRuntimeConfig? config = null)
    {
        _channel = channel;
        _grids = [.. grids];
        Config = config ?? new CampaignRuntimeConfig();
    }

    public IReadOnlyList<CampaignGrid> Grids => _grids;

    public CampaignRuntimeConfig Config { get; }

    /// <summary>宿主的运行日志（C# 侧记录，便于与上游日志对齐排查）。</summary>
    public IReadOnlyList<string> Logs => _logs;

    // ------------------------------------------------------------------ 状态（只读）
    public int BattleCount => ReadInt("battle_count");

    public int FleetCurrentIndex => ReadInt("fleet_current_index", 1);

    public string Fleet1Location => ReadString("fleet_1_location");

    public string Fleet2Location => ReadString("fleet_2_location");

    /// <summary>上游 `Camera.camera`（记录下来的机位）；读不到时给占位符而不是空串。</summary>
    public string CameraLocation => ReadString("camera", "<未记录>");

    /// <summary>弹药数：上游 `Map.ammo_count`（`pick_up_ammo` 里就用它）。</summary>
    public int AmmoCount
    {
        get => ReadInt("ammo_count");
        set { }                       // 上游自己维护；C# 侧不写回（避免与上游状态打架）
    }

    /// <summary>舰队弹药：上游 `Map.fleet_ammo`。</summary>
    public int FleetAmmo
    {
        get => ReadInt("fleet_ammo");
        set { }
    }

    public CampaignGrid GridAt(string location) =>
        Grids.FirstOrDefault(grid => grid.Location == location)
        ?? throw new NotSupportedException($"地图状态里没有格子 {location}（不返回默认值糊过去）");

    public bool EndRequested { get; private set; }

    public string? EndReason { get; private set; }

    /// <summary>
    /// 已拾取记帐（上游在关卡层维护 `picked_light_house` / `picked_flare`；这里镜像一份用于**决策**，
    /// 真正"是否已拾取"仍由上游自己那份决定——两边都不会重复拾取）。
    /// </summary>
    public ISet<string> PickedLightHouse { get; } = new HashSet<string>(StringComparer.Ordinal);

    public ISet<string> PickedFlare { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>巡逻路线：来自关卡导出的 `MAP.bouncing_enemy_data`（静态规则，不走宿主）。</summary>
    public IReadOnlyList<IReadOnlyList<string>> BouncingRoutes { get; init; } = [];

    // ------------------------------------------------------------------ 动作（调上游方法）
    public bool EnsureFleet(int index)
    {
        // 上游 `Fleet.fleet_1` / `fleet_2` 是属性：会先 `fleet_ensure(index=…)` 再返回 self。
        // 所以访问这个属性就是"切队"这件事本身（`switch_to` 本身是 pass）。
        _channel.Call(FleetPath(index, "switch_to"), [], []);
        return true;
    }

    public bool ClearChosenEnemy(CampaignGrid grid, string expected, string fleet = "")
    {
        var (name, kwargs) = WithExpected(fleet, "clear_chosen_enemy", expected);
        _channel.Call(name, [Node($"#{grid.Location}")], kwargs);
        return true;
    }

    public bool ClearChosenMystery(CampaignGrid grid)
    {
        _channel.Call("clear_chosen_mystery", [Node($"#{grid.Location}")], []);
        return true;
    }

    public bool SubmarineMoveNearBoss(CampaignGrid grid)
    {
        _channel.Call("submarine_move_near_boss", [Node($"#{grid.Location}")], []);
        return true;
    }

    public bool Goto(CampaignGrid grid, string expected = "")
    {
        var (name, kwargs) = WithExpected("", "goto", expected);
        _channel.Call(name, [Node($"#{grid.Location}")], kwargs);
        return true;
    }

    public void EnsureNoInfoBar() => _channel.Call("ensure_no_info_bar", [], []);

    public bool UpdateMap()
    {
        // 上游 `update()` 失败会抛 `MapDetectionError`，宿主 `s3_campaign_call` 会把它变成 `error` 字段。
        var result = _channel.Call("update", [], []);
        return result is not JsonObject payload || !payload.ContainsKey("error");
    }

    public void MapSwipe((int X, int Y) preset) =>
        _channel.Call("map_swipe", [new JsonArray { preset.X, preset.Y }], []);

    public void FocusTo(string camera) => _channel.Call("focus_to", [Node($"#{camera}")], []);

    public void EnsureEdgeInsight() => _channel.Call("ensure_edge_insight", [], []);

    /// <summary>
    /// 把全图 <c>is_caught_by_siren</c> 置假。上游这一支**没有任何设备动作**：见
    /// `module/map/map.py` 的 `fleet_2_break_siren_caught`——"抓着舰队的不是 2 队"时直接
    /// `for grid in self.map: grid.is_caught_by_siren = False` 然后返回假。
    /// 所以这里只改宿主自己持有的地图模型（等价于上游改 `GridInfo` 对象），**不调用渠道**；
    /// 真机上下一次识别会把实际状态盖回来。
    /// </summary>
    /// <summary>
    /// 从**上游地图**重新取一次状态（只读 op `s3_campaign_grids`）。
    /// 上游的移动/识别会改它自己的 `CampaignMap`（`Fleet.goto` 结尾的 `wipe_out()` 与 `is_fleet` 重设、
    /// `find_path_initial` 重写成本场），所以每次设备动作之后都要刷新，否则后续决策基于过期状态。
    /// 取不到（空列表）时**保持原状态**并记一条日志，不把模型清空。
    /// </summary>
    public bool RefreshFromUpstream()
    {
        var refreshed = _channel.ReadGrids();
        if (refreshed.Count == 0)
        {
            _logs.Add("refresh_from_upstream：上游没给出地图状态，保持原状态");
            return false;
        }
        _grids.Clear();
        _grids.AddRange(refreshed);
        _logs.Add($"refresh_from_upstream：{_grids.Count} 格");
        return true;
    }

    public void MarkFlare(CampaignGrid grid)
    {
        // 改自己的模型 + **同步到上游地图对象**：`pick_up_flare` 设的这个标记会被
        // `Map.find_path` 的航点绕行读到（`way_node.is_flare`），不同步就会少绕一格。
        for (int i = 0; i < _grids.Count; i++)
        {
            if (_grids[i].Location == grid.Location) _grids[i] = _grids[i] with { IsFlare = true };
        }
        _channel.Set($"map.{grid.Location}.is_flare", JsonValue.Create(true));
    }

    public void ClearCaughtBySirenFlags()
    {
        for (int i = 0; i < _grids.Count; i++)
        {
            if (_grids[i].IsCaughtBySiren) _grids[i] = _grids[i] with { IsCaughtBySiren = false };
        }
    }

    public void Withdraw()
    {
        _channel.Call("withdraw", [], []);
        // 与录制宿主一致：撤退即"本关结束"（上游 `withdraw()` 检测到回到章节页会抛 `CampaignEnd`）
        EndRequested = true;
        EndReason ??= "Withdraw";
    }

    public void Log(string message)
    {
        _logs.Add(message);
        _channel.Call("logger.info", [Node(message)], []);
    }

    // ------------------------------------------------------------------ 内部
    private static string FleetPath(int index, string method) =>
        index == 2 ? $"fleet_2.{method}" : $"fleet_1.{method}";

    private static (string Name, List<KeyValuePair<string, JsonNode?>> Kwargs) WithExpected(
        string fleet, string method, string expected)
    {
        string name = string.IsNullOrEmpty(fleet) ? method : $"{fleet}.{method}";
        var kwargs = new List<KeyValuePair<string, JsonNode?>>();
        if (!string.IsNullOrEmpty(expected)) kwargs.Add(new("expected", Node(expected)));
        return (name, kwargs);
    }

    private static JsonNode? Node(string value) => JsonValue.Create(value);

    private int ReadInt(string name, int fallback = 0)
    {
        var value = _channel.Read(name);
        if (value is null) return fallback;
        try
        {
            return value.GetValue<int>();
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException)
        {
            return fallback;
        }
    }

    private string ReadString(string name, string fallback = "")
    {
        var value = _channel.Read(name);
        if (value is null) return fallback;
        try
        {
            return value.GetValue<string>() ?? fallback;
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }
}
