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
///   <item>计数和位置读取上游状态；已迁移原语产生的弹药与格子标志写入须同步上游；</item>
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
        PickedLightHouse = new NativePickedGrids(channel, "picked_light_house");
        PickedFlare = new NativePickedGrids(channel, "picked_flare");
    }

    public IReadOnlyList<CampaignGrid> Grids => _grids;

    public CampaignRuntimeConfig Config { get; }

    /// <summary>宿主的运行日志（C# 侧记录，便于与上游日志对齐排查）。</summary>
    public IReadOnlyList<string> Logs => _logs;

    // ------------------------------------------------------------------ 状态（只读）
    public int BattleCount => ReadInt("battle_count");

    public int FleetCurrentIndex => ReadInt("fleet_current_index");

    public string Fleet1Location => ReadString("fleet_1_location");

    public string Fleet2Location => ReadString("fleet_2_location");

    /// <summary>上游 `Camera.camera` 转换后的格点；未定位为空串，读取错误直接报错。</summary>
    public string CameraLocation => ReadString("camera");

    /// <summary>弹药数：上游 `Map.ammo_count`（`pick_up_ammo` 里就用它）。</summary>
    public int AmmoCount
    {
        get => ReadInt("ammo_count");
        set => _channel.Set("ammo_count", JsonValue.Create(value));
    }

    /// <summary>舰队弹药：上游 `Map.fleet_ammo`。</summary>
    public int FleetAmmo
    {
        get => ReadInt("fleet_ammo");
        set => _channel.Set("fleet_ammo", JsonValue.Create(value));
    }

    public CampaignGrid GridAt(string location) =>
        Grids.FirstOrDefault(grid => grid.Location == location)
        ?? throw new NotSupportedException($"地图状态里没有格子 {location}（不返回默认值糊过去）");

    public bool EndRequested { get; private set; }

    public string? EndReason { get; private set; }

    /// <summary>
    /// 已拾取记账直接读取上游列表；追加复用原生 list.append，不维护会漂移的本地副本。
    /// </summary>
    public ISet<string> PickedLightHouse { get; }

    public ISet<string> PickedFlare { get; }

    /// <summary>巡逻路线：来自关卡导出的 `MAP.bouncing_enemy_data`（静态规则，不走宿主）。</summary>
    public IReadOnlyList<IReadOnlyList<string>> BouncingRoutes { get; init; } = [];

    // ------------------------------------------------------------------ 动作（调上游方法）
    public bool EnsureFleet(int index)
    {
        var result = _channel.Call("fleet_ensure", [], [new("index", JsonValue.Create(index))]);
        RefreshFromUpstream();
        return result?.GetValue<bool>() ?? throw new InvalidDataException("fleet_ensure 未返回布尔结果");
    }

    public bool ClearChosenEnemy(CampaignGrid grid, string expected, string fleet = "")
    {
        var (name, kwargs) = WithExpected(fleet, "clear_chosen_enemy", expected);
        var value = _channel.Call(name, [Node($"#{grid.Location}")], kwargs);
        RefreshFromUpstream();
        return value?.GetValue<bool>() ?? throw new InvalidDataException("clear_chosen_enemy 未返回布尔结果");
    }

    /// <summary>上游 <c>mystery_count</c>：清掉一个神秘格子就 +1。</summary>
    public int MysteryCount => ReadInt("mystery_count");

    public bool ClearChosenMystery(CampaignGrid grid)
    {
        _channel.Call("clear_chosen_mystery", [Node($"#{grid.Location}")], []);
        RefreshFromUpstream();
        return true;
    }

    public bool SubmarineMoveNearBoss(CampaignGrid grid)
    {
        var result = _channel.Call("submarine_move_near_boss", [Node($"#{grid.Location}")], []);
        RefreshFromUpstream();
        return result?.GetValue<bool>() ?? throw new InvalidDataException("submarine_move_near_boss 未返回布尔结果");
    }

    public bool Goto(CampaignGrid grid, string expected = "")
    {
        var (name, kwargs) = WithExpected("", "goto", expected);
        _channel.Call(name, [Node($"#{grid.Location}")], kwargs);
        RefreshFromUpstream();
        return true;
    }

    public void EnsureNoInfoBar() => _channel.Call("ensure_no_info_bar", [], []);

    public bool UpdateMap()
    {
        try { _channel.Call("update", [], []); }
        catch (CampaignNativeException error) when (error.NativeType == "MapDetectionError")
        {
            // 唯一可恢复类别：原生重对焦按 preset 处理 MapDetectionError。
            _logs.Add(error.Message);
            return false;
        }
        return RefreshFromUpstream();
    }

    public void MapSwipe((int X, int Y) preset) =>
        _channel.Call("map_swipe", [new JsonArray { preset.X, preset.Y }], []);

    public void FocusTo(string camera) => _channel.Call("focus_to", [Node($"#{camera}")], []);

    public void EnsureEdgeInsight() => _channel.Call("ensure_edge_insight", [], []);

    /// <summary>
    /// 从**上游地图**重新取一次状态（只读 op `s3_campaign_grids`）。
    /// 上游的移动/识别会改它自己的 `CampaignMap`（`Fleet.goto` 结尾的 `wipe_out()` 与 `is_fleet` 重设、
    /// `find_path_initial` 重写成本场），所以每次设备动作之后都要刷新，否则后续决策基于过期状态。
    /// 取不到地图时拒绝继续；不能把过期快照当作新状态。
    /// </summary>
    public bool RefreshFromUpstream()
    {
        var refreshed = _channel.ReadGrids();
        if (refreshed.Count == 0)
        {
            throw new InvalidDataException("refresh_from_upstream：上游未提供地图状态");
        }
        _grids.Clear();
        _grids.AddRange(refreshed);
        _logs.Add($"refresh_from_upstream：{_grids.Count} 格");
        return true;
    }

    /// <summary>
    /// 上游 `raise CampaignEnd()`（钩子里的控制流信号）→ 结束本关。**不发设备动作**：
    /// 上游那个异常只是让 `run()` 返回，不是点撤退（点撤退的是 `withdraw()`）。
    /// </summary>
    public void RequestCampaignEnd(string reason)
    {
        EndRequested = true;
        EndReason = reason;
    }

    public void SetGridFlag(CampaignGrid grid, string flag, bool value)
    {
        _channel.Set($"map.{grid.Location}.{flag}", JsonValue.Create(value));
        // 改自己的模型 + **同步到上游地图对象**：这些标志会被上游自己的代码读到
        // （`is_flare` → `Map.find_path` 的航点绕行；`may_bouncing_enemy` → 巡逻敌人路线筛选），
        // 不同步就会与上游状态分叉。标志名白名单在 `CampaignPrimitives.ApplyFlag` 里。
        for (int i = 0; i < _grids.Count; i++)
        {
            if (_grids[i].Location == grid.Location)
            {
                _grids[i] = CampaignPrimitives.ApplyFlag(_grids[i], flag, value);
            }
        }
    }

    public void ClearCaughtBySirenFlags()
    {
        for (int i = 0; i < _grids.Count; i++)
        {
            if (_grids[i].IsCaughtBySiren) SetGridFlag(_grids[i], "is_caught_by_siren", false);
        }
    }

    public void Withdraw()
    {
        try { _channel.Call("withdraw", [], []); }
        catch (CampaignControlFlowSignal signal) when (signal.Kind == "CampaignEnd")
        {
            EndRequested = true;
            EndReason = signal.Message;
            return;
        }
        throw new InvalidOperationException("withdraw 未返回 CampaignEnd，不能宣称已退出本关");
    }

    public void Log(string message)
    {
        _logs.Add(message);
        _channel.Call("logger.info", [Node(message)], []);
    }

    // ------------------------------------------------------------------ 内部
    private static (string Name, List<KeyValuePair<string, JsonNode?>> Kwargs) WithExpected(
        string fleet, string method, string expected)
    {
        // 原语层用短名 `boss` 表示上游的 `fleet_boss` 属性；`fleet_1`/`fleet_2`
        // 已经是上游属性名。保留点号路径让上游 property 自己完成必要的切队。
        string prefix = fleet switch
        {
            "boss" => "fleet_boss",
            _ => fleet,
        };
        string name = string.IsNullOrEmpty(prefix) ? method : $"{prefix}.{method}";
        var kwargs = new List<KeyValuePair<string, JsonNode?>>();
        if (!string.IsNullOrEmpty(expected)) kwargs.Add(new("expected", Node(expected)));
        return (name, kwargs);
    }

    private static JsonNode? Node(string value) => JsonValue.Create(value);

    private int ReadInt(string name) => _channel.Read(name)?.GetValue<int>()
        ?? throw new InvalidDataException($"上游缺少整数字段 {name}");

    private string ReadString(string name) => _channel.Read(name)?.GetValue<string>()
        ?? throw new InvalidDataException($"上游缺少字符串字段 {name}");
}
