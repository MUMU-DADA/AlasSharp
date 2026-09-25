using System.Text.Json.Nodes;

namespace Alas.Campaign;

/// <summary>原语执行时读到的运行时配置（只含已移植分支需要的字段，逐项对应上游 config）。</summary>
public sealed record CampaignRuntimeConfig(
    string? EnemyPriority = null,
    bool MapClearAllThisTime = false,
    bool MapHasSiren = false,
    bool MapHasFortress = false,
    bool Fleet2 = false,
    bool FleetBoss = false,
    bool MapHasLandBased = false,
    bool MapHasMovableEnemy = false,
    bool MapHasMovableNormalEnemy = false,
    bool MapHasAmbush = false,
    bool MapHasBouncingEnemy = false,
    bool PoorMapData = false,
    /// <summary>上游 `config.MAP_BOSS_APPEAR_REFOCUS_SWIPE`：boss 出现后重对焦用的滑动量；未配置为 null。</summary>
    (int X, int Y)? MapBossAppearRefocusSwipe = null,
    bool ErrorHandleError = true)
{
    /// <summary>上游 <c>fleet_boss_index</c>：<c>FLEET_BOSS == 2 and FLEET_2</c> 时是 2，否则 1。</summary>
    public int FleetBossIndex => FleetBoss && Fleet2 ? 2 : 1;
}

/// <summary>
/// 原语执行上下文：地图状态 + 运行时配置 + 设备动作。
///
/// **动作只走这个接口**——原语本身不含任何坐标、点击顺序或设备细节，因此可以离线干跑与对拍；
/// 真机实现由设备侧提供（`clear_chosen_enemy` 等在上游是 `MapOperation` 的点击/移动）。
/// </summary>
public interface ICampaignPrimitiveHost
{
    /// <summary>当前地图状态（真机来自识别，干跑来自夹具）。</summary>
    IReadOnlyList<CampaignGrid> Grids { get; }

    CampaignRuntimeConfig Config { get; }

    /// <summary>上游 <c>self.battle_count</c>：判断"猜 boss"是否打中（真机由战斗结果刷新）。</summary>
    int BattleCount { get; }

    /// <summary>上游 <c>self.fleet_current_index</c>：决定是否需要切换舰队。</summary>
    int FleetCurrentIndex { get; }

    /// <summary>
    /// 切换到指定舰队（上游 <c>Fleet.fleet_ensure(index)</c>）。
    /// 上游的 <c>fleet_1</c> / <c>fleet_2</c> / <c>fleet_boss</c> / <c>fleet_submarine</c> 是
    /// **返回 self 的 property**，只在当前舰队不同时才切换——所以 <c>self.fleet_boss.clear_boss()</c>
    /// 的语义就是"必要时切到 boss 舰队，再调用 <c>clear_boss()</c>"。
    /// </summary>
    bool EnsureFleet(int index);

    /// <summary>
    /// 清掉选中格子上的敌人（上游 <c>clear_chosen_enemy</c>）。
    /// <paramref name="expected"/> 与上游一致：空串表示普通战斗，另有 <c>siren</c>/<c>fortress</c>/<c>boss</c>
    /// （设备侧把空串映射成 <c>combat</c>、其它映射成 <c>combat_&lt;expected&gt;</c>）。
    /// <paramref name="fleet"/> 为空表示当前舰队，否则是上游的 <c>fleet_boss</c>/<c>fleet_1</c> 等。
    /// </summary>
    bool ClearChosenEnemy(CampaignGrid grid, string expected, string fleet = "");

    /// <summary>捡走选中格子上的神秘物资（上游 <c>clear_chosen_mystery</c>）。</summary>
    bool ClearChosenMystery(CampaignGrid grid);

    /// <summary>上游 <c>Fleet.submarine_move_near_boss</c>（属设备侧机动）。</summary>
    bool SubmarineMoveNearBoss(CampaignGrid grid);

    /// <summary>走到指定格子（上游 <c>Fleet.goto(grid, expected=…)</c>）。</summary>
    bool Goto(CampaignGrid grid, string expected = "");

    /// <summary>关掉可能弹出的信息条（上游 <c>ensure_no_info_bar()</c>）。</summary>
    void EnsureNoInfoBar();

    /// <summary>上游 <c>self.ammo_count</c>（可用于拾取弹药）。</summary>
    int AmmoCount { get; set; }

    /// <summary>上游 <c>self.fleet_ammo</c>（当前舰队剩余弹药）。</summary>
    int FleetAmmo { get; set; }

    /// <summary>上游 <c>self.fleet_1_location</c> / <c>fleet_2_location</c>（格子节点名）。</summary>
    string Fleet1Location { get; }

    string Fleet2Location { get; }

    /// <summary>按位置取地图状态里的格子；取不到即抛错（不返回默认值糊过去）。</summary>
    CampaignGrid GridAt(string location);

    /// <summary>相机当前的记录位置（上游 <c>Fleet.camera</c>；重对焦要回到这里）。</summary>
    string CameraLocation { get; }

    /// <summary>地图更新（上游 <c>Fleet.update()</c> → 识别）；返回是否成功，失败即上游的 <c>MapDetectionError</c>。</summary>
    bool UpdateMap();

    /// <summary>按 preset 滑动地图（上游 <c>Fleet.map_swipe(preset)</c>）。</summary>
    void MapSwipe((int X, int Y) preset);

    /// <summary>把相机移回记录的格（上游 <c>Fleet.focus_to(camera)</c>）。</summary>
    void FocusTo(string camera);

    /// <summary>相机对齐到双边缘（上游 <c>Camera.ensure_edge_insight()</c>，属设备侧手势）。</summary>
    void EnsureEdgeInsight();

    /// <summary>把全图 <c>is_caught_by_siren</c> 置假（上游在挣脱/判定失败后这么清标记）。</summary>
    void ClearCaughtBySirenFlags();

    /// <summary>
    /// 把格子标成"信号弹已放置"（上游 `pick_up_flare` 的第一行 `grid.is_flare = True`）。
    /// 这个标记影响上游 `Map.find_path` 的**航点绕行**（`way_node.is_flare`），所以必须同步到上游地图对象。
    /// </summary>
    void MarkFlare(CampaignGrid grid);

    /// <summary>撤退（上游 <c>MapOperation.withdraw()</c>，如 <c>capture_clear_boss</c> 结尾会撤退）。</summary>
    void Withdraw();

    /// <summary>
    /// 是否已请求结束本关——对应上游 <c>withdraw()</c> 检测到"已回到章节页"时抛出的 <c>CampaignEnd</c>
    /// （`module/map/map_operation.py:410`）。干跑宿主在 <see cref="Withdraw"/> 时置位，属**近似**：
    /// 真机上"何时算回到章节页"要用识别确认。
    /// </summary>
    bool EndRequested { get; }

    string? EndReason { get; }

    /// <summary>上游关卡基类里的 <c>picked_light_house</c> / <c>picked_flare</c> 记账表。</summary>
    ISet<string> PickedLightHouse { get; }

    ISet<string> PickedFlare { get; }

    /// <summary>上游 <c>MAP.bouncing_enemy_data</c>：巡逻敌人的路线（每条路线是一串格子节点名）。</summary>
    IReadOnlyList<IReadOnlyList<string>> BouncingRoutes { get; }

    void Log(string message);
}

/// <summary>干跑宿主：记录原语要做的动作，但**什么都不执行**（不连设备、不点屏幕）。</summary>
public sealed class RecordingCampaignHost : ICampaignPrimitiveHost
{
    private readonly List<CampaignGrid> _grids;

    public RecordingCampaignHost(IEnumerable<CampaignGrid> grids, CampaignRuntimeConfig? config = null)
    {
        _grids = [.. grids];
        Config = config ?? new CampaignRuntimeConfig();
    }

    /// <summary>
    /// 宿主自己的地图模型。**可变**：状态类操作（如清 `is_caught_by_siren` 标记）要在模型上生效，
    /// 否则干跑轨迹会与上游不一致——上游那边是直接改 `GridInfo` 对象（见设备宿主的同名方法）。
    /// </summary>
    public IReadOnlyList<CampaignGrid> Grids => _grids;

    public CampaignRuntimeConfig Config { get; }

    /// <summary>干跑时默认不推进战斗计数（真机由战斗结果刷新）。</summary>
    public int BattleCount { get; set; }

    /// <summary>干跑时的当前舰队索引（真机由设备侧维护）。</summary>
    public int FleetCurrentIndex { get; set; } = 1;

    /// <summary>干跑记录到的动作（按调用顺序）。</summary>
    public List<string> Actions { get; } = [];

    /// <summary>干跑记录到的日志（原语的判断依据，便于人工核对）。</summary>
    public List<string> Logs { get; } = [];

    /// <summary>
    /// 干跑时 <see cref="UpdateMap"/> 是否成功。默认成功；置假用来验证"识别失败 → 按 preset 滑动"
    /// 这条上游分支（<c>MapDetectionError</c>）。
    /// </summary>
    public bool UpdateMapSucceeds { get; set; } = true;

    /// <summary>干跑记录到的地图滑动（重对焦分支用）。</summary>
    public List<string> Swipes { get; } = [];

    /// <summary>与上游一致：只在当前舰队不同时才切换。</summary>
    public bool EnsureFleet(int index)
    {
        if (FleetCurrentIndex == index) return false;
        Actions.Add($"fleet_ensure({index})");
        FleetCurrentIndex = index;
        return true;
    }

    public bool ClearChosenEnemy(CampaignGrid grid, string expected, string fleet = "")
    {
        string suffix = string.IsNullOrEmpty(fleet) ? "" : $", fleet={fleet}";
        Actions.Add($"clear_chosen_enemy({grid.Location}, expected={expected}{suffix})");
        // 上游：打完一场战斗后 battle_count 增长（由 map_operation 维护）。干跑用它推进关卡循环，
        // 让"第 N 轮选哪个钩子"能被夹具验证；真机以战斗结果为准。
        BattleCount++;
        return true;
    }

    public bool ClearChosenMystery(CampaignGrid grid)
    {
        Actions.Add($"clear_chosen_mystery({grid.Location})");
        return true;
    }

    public bool SubmarineMoveNearBoss(CampaignGrid grid)
    {
        Actions.Add($"submarine_move_near_boss({grid.Location})");
        return true;
    }

    public bool Goto(CampaignGrid grid, string expected = "")
    {
        string suffix = string.IsNullOrEmpty(expected) ? "" : $", expected={expected}";
        Actions.Add($"goto({grid.Location}{suffix})");
        return true;
    }

    public void EnsureNoInfoBar() => Actions.Add("ensure_no_info_bar()");

    /// <summary>干跑时的弹药计数（真机由战斗结果刷新）。</summary>
    public int AmmoCount { get; set; } = 3;

    /// <summary>干跑时的舰队弹药（真机由战斗结果刷新）。</summary>
    public int FleetAmmo { get; set; } = 5;

    public ISet<string> PickedLightHouse { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>干跑时由夹具给出的巡逻路线（真机来自关卡导出的 <c>MAP.bouncing_enemy_data</c>）。</summary>
    public IReadOnlyList<IReadOnlyList<string>> BouncingRoutes { get; set; } = [];

    public ISet<string> PickedFlare { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>干跑时的两支舰队位置（真机由设备侧维护）。</summary>
    public string Fleet1Location { get; set; } = "";

    public string Fleet2Location { get; set; } = "";

    public CampaignGrid GridAt(string location) =>
        Grids.FirstOrDefault(grid => grid.Location == location)
        ?? throw new NotSupportedException($"地图状态里没有格子 {location}（识别结果可能未覆盖）");

    /// <summary>干跑没有真实相机位置：用占位符而不是空串，避免看起来像"有一个空的机位"。</summary>
    public string CameraLocation { get; set; } = "<未记录>";

    public bool UpdateMap()
    {
        Actions.Add("update_map()");
        return UpdateMapSucceeds;
    }

    public void MapSwipe((int X, int Y) preset)
    {
        Swipes.Add($"map_swipe({preset.X}, {preset.Y})");
        Actions.Add($"map_swipe({preset.X}, {preset.Y})");
    }

    public void FocusTo(string camera) => Actions.Add($"focus_to({camera})");

    public void EnsureEdgeInsight() => Actions.Add("ensure_edge_insight()");

    public void ClearCaughtBySirenFlags()
    {
        Actions.Add("clear_caught_by_siren_flags()");
        // 与设备宿主同一语义：改自己的模型（上游就是逐格改 `GridInfo`），不产生设备动作。
        for (int i = 0; i < _grids.Count; i++)
        {
            if (_grids[i].IsCaughtBySiren) _grids[i] = _grids[i] with { IsCaughtBySiren = false };
        }
    }

    public void MarkFlare(CampaignGrid grid)
    {
        // 上游 `pick_up_flare` 的第一行 `grid.is_flare = True`：只改模型，不产生设备动作。
        // 这个标记影响上游 `Map.find_path` 的航点绕行，所以设备宿主还要把它同步到上游地图对象（见那边）。
        Actions.Add($"mark_flare({grid.Location})");
        for (int i = 0; i < _grids.Count; i++)
        {
            if (_grids[i].Location == grid.Location) _grids[i] = _grids[i] with { IsFlare = true };
        }
    }

    public void Withdraw()
    {
        Actions.Add("withdraw()");
        // 上游 withdraw() 在检测到已回到章节页时 raise CampaignEnd；干跑以"撤退即结束"近似。
        EndRequested = true;
        EndReason ??= "Withdraw";
    }

    /// <summary>上游 <c>withdraw()</c> 抛 <c>CampaignEnd</c> 的干跑近似（见接口注释）。</summary>
    public bool EndRequested { get; private set; }

    public string? EndReason { get; private set; }

    public void RequestEnd(string reason)
    {
        EndRequested = true;
        EndReason ??= reason;
    }

    public void Log(string message) => Logs.Add(message);
}

/// <summary>
/// 原语实现，逐条对应上游 <c>module/map/map.py</c> / <c>module/campaign/campaign_base.py</c>：
/// <list type="bullet">
///   <item><c>clear_enemy(**kwargs)</c>：选一个非 boss 敌人 → <c>clear_chosen_enemy</c> → 真/假；</item>
///   <item><c>battle_default()</c>（CampaignBase）：<c>clear_enemy()</c> 成功即真，否则记 "No battle executed." 并返回假；</item>
///   <item><c>clear_all_mystery(**kwargs)</c>：<c>sort=('cost',)</c> 循环捡完所有神秘格子，**恒返回假**；</item>
///   <item><c>clear_filter_enemy(string, preserve)</c>：按过滤串选目标 → <c>clear_chosen_enemy</c>。</item>
/// </list>
/// 失败与边界一律**显式**报出（<see cref="NotSupportedException"/> 或 <c>Blocked</c>），不猜语义。
/// 说明：<c>CampaignTargetDecision.Unsupported</c> 现在**没有生产者**——各原语的可选分支都已移植，
/// 下面那两处 "走到未移植分支" 的抛出是**防御性**的（真出现就说明决策层新增了未接线分支）。
/// </summary>
public static class CampaignPrimitives
{
    /// <summary>干跑时的循环上限，避免"清不完的神秘格子"把进程拖死。</summary>
    private const int MaxMysteryRounds = 100;

    /// <summary>上游 <c>Map.clear_enemy(**kwargs)</c>。</summary>
    public static bool ClearEnemy(ICampaignPrimitiveHost host, CampaignTargetOptions? options = null)
    {
        var decision = CampaignTargetSelector.SelectEnemyTarget(
            new CampaignGridSet(host.Grids), host.Config.EnemyPriority, host.Config.MapClearAllThisTime, options);
        if (decision.Unsupported is not null)
            throw new NotSupportedException($"clear_enemy 走到未移植分支：{decision.Branch}——{decision.Unsupported}");
        if (decision.Target is null)
        {
            host.Log($"clear_enemy：无目标（{decision.Branch}）");
            return false;
        }
        host.Log($"clear_enemy：选中 {decision.Target.Location}（{decision.Target.FilterKey}，{decision.Branch}）");
        return host.ClearChosenEnemy(decision.Target, "");
    }

    /// <summary>上游 <c>Map.clear_any_enemy(**kwargs)</c>：敌人 + （有塞壬时）塞壬 + （有要塞时）要塞。</summary>
    public static bool ClearAnyEnemy(ICampaignPrimitiveHost host, CampaignTargetOptions? options = null)
    {
        var all = new CampaignGridSet(host.Grids);
        var grids = all.Select(new CampaignGridFilter(IsEnemy: true, IsBoss: false));
        if (host.Config.MapHasSiren)
            grids = grids.Add(all.Select(new CampaignGridFilter(IsSiren: true)));
        if (host.Config.MapHasFortress)
            grids = grids.Add(all.Select(new CampaignGridFilter(IsFortress: true)));

        var selected = CampaignTargetSelector.SelectGrids(grids, options);
        if (selected.IsEmpty)
        {
            host.Log("clear_any_enemy：无目标");
            return false;
        }
        var target = selected[0];
        string expected = target.IsFortress ? "fortress" : target.IsSiren ? "siren" : "";
        host.Log($"clear_any_enemy：选中 {target.Location}（{target.FilterKey}，expected={expected}）");
        return host.ClearChosenEnemy(target, expected);
    }

    /// <summary>上游 <c>Map.clear_siren(**kwargs)</c>：无塞壬/要塞配置时直接返回假。</summary>
    public static bool ClearSiren(ICampaignPrimitiveHost host, CampaignTargetOptions? options = null)
    {
        if (!host.Config.MapHasSiren && !host.Config.MapHasFortress)
        {
            host.Log("clear_siren：地图没有塞壬也没有要塞，直接返回假");
            return false;
        }
        if (host.Config.Fleet2)
        {
            options = (options ?? new CampaignTargetOptions()) with { Sort = ["weight", "cost_2"] };
        }
        var all = new CampaignGridSet(host.Grids);
        var grids = all.Select(new CampaignGridFilter(IsSiren: true));
        if (host.Config.MapHasFortress)
        {
            grids = grids.Add(all.Select(new CampaignGridFilter(IsFortress: true)));
        }
        var selected = CampaignTargetSelector.SelectGrids(grids, options);
        if (selected.IsEmpty)
        {
            host.Log("clear_siren：无目标");
            return false;
        }
        var target = selected[0];
        string expected = target.IsFortress ? "fortress" : "siren";
        host.Log($"clear_siren：选中 {target.Location}（expected={expected}）");
        return host.ClearChosenEnemy(target, expected);
    }

    /// <summary>
    /// 上游 <c>Map.clear_boss()</c>：先找 boss（含"被塞壬抓住的 may_boss"），找不到就退回
    /// <see cref="ClearPotentialBoss"/>。上游注释里这个方法已标记为 deprecated（复杂地图建议 brute_clear_boss），
    /// 但关卡覆写里仍有 575 处调用，因此按原样移植。
    /// </summary>
    public static bool ClearBoss(ICampaignPrimitiveHost host)
    {
        var all = new CampaignGridSet(host.Grids);
        var grids = all.Select(new CampaignGridFilter(IsBoss: true, IsAccessible: true));
        grids = grids.Add(all.Select(new CampaignGridFilter(MayBoss: true, IsCaughtBySiren: true)));
        host.Log($"clear_boss：boss 候选 {grids.Count} 个");
        if (grids.IsEmpty)
        {
            grids = grids.Add(all.Select(new CampaignGridFilter(MayBoss: true, IsEnemy: true, IsAccessible: true)));
            host.Log("clear_boss：BOSS not detected, using may_boss grids.");
        }
        if (!grids.IsEmpty)
        {
            host.SubmarineMoveNearBoss(grids[0]);
            var sorted = grids.Sort("weight", "cost");
            host.Log($"clear_boss：打 {sorted[0].Location}");
            host.ClearChosenEnemy(sorted[0], "boss");
        }
        host.Log("clear_boss：BOSS not detected, trying all boss spawn point.");
        return ClearPotentialBoss(host);
    }

    /// <summary>
    /// 上游 <c>Map.clear_potential_boss()</c>：依次踩可达的 may_boss 格子，用 <c>battle_count</c> 判断
    /// 是否猜中；都猜不中时走**不可达 may_boss 的兜底**——找挡路敌人（`brute_find_roadblocks`）
    /// 按 weight/cost 排序后交给 **1 队**清掉第一个（两条分支都已移植）。
    /// 唯一的前置要求：要有舰队起点（真机由 `map_init` 填）；没有时如实停下报原因，不猜。
    /// </summary>
    public static bool ClearPotentialBoss(ICampaignPrimitiveHost host)
    {
        var all = new CampaignGridSet(host.Grids);
        var reachable = all.Select(new CampaignGridFilter(MayBoss: true, IsAccessible: true)).Sort("weight", "cost");
        int battleCount = host.BattleCount;
        string expected = all.Select(new CampaignGridFilter(MayBoss: true)).Count == 1 ? "boss" : "";
        foreach (var grid in reachable.Grids)
        {
            host.Log($"clear_potential_boss：踩 {grid.Location}（expected={expected}）");
            host.ClearChosenEnemy(grid, expected, fleet: "boss");
            if (host.BattleCount > battleCount)
            {
                host.Log("Boss guessing correct.");
                return true;
            }
            host.Log("Boss guessing incorrect.");
        }

        // 兜底：不可达的 may_boss 格子——先找挡在路上的敌人（`brute_find_roadblocks`），
        // 按 weight/cost 排序后由 **1 队**清掉第一个，然后返回真（上游就这么写）。
        var unreachable = all.Select(new CampaignGridFilter(MayBoss: true, IsAccessible: false))
                             .Sort("weight", "cost");
        if (!unreachable.IsEmpty)
        {
            // 没有舰队起点就没法算路障：如实停下并报明原因，不猜也不静默跳过。
            // （真机跑起来舰队位置由 `map_init` 填；只有诊断夹具才可能为空。）
            string start = FleetStart(host, host.Config.FleetBossIndex);
            if (string.IsNullOrEmpty(start) || !host.Grids.Any(grid => grid.Location == start))
            {
                throw new NotSupportedException(
                    $"clear_potential_boss：{unreachable.Count} 个不可达 may_boss 格子需要找路障，" +
                    $"但舰队起点为空或不在图上（start={start}）");
            }
        }
        foreach (var grid in unreachable.Grids)
        {
            host.Log($"clear_potential_boss：{grid.Location} 不可达，找路障");
            var search = CampaignBruteFinder.FindRoadblocks(host.Grids, grid.Location,
                FleetStart(host, host.Config.FleetBossIndex), host.Config.MapHasAmbush,
                liveCost: LiveCostFor(host, grid, host.Config.FleetBossIndex));
            if (!search.Found) continue;
            var roadblocks = new CampaignGridSet(search.Roadblocks).Sort("weight", "cost");
            host.Log($"clear_potential_boss：清路障 {roadblocks[0].Location}（fleet_1）");
            host.ClearChosenEnemy(roadblocks[0], expected, fleet: "fleet_1");
            return true;
        }
        return false;
    }

    /// <summary>上游 <c>Map.clear_roadblocks(roads, **kwargs)</c>：把所有路段的路障拼起来选一个打掉。</summary>
    public static bool ClearRoadblocks(ICampaignPrimitiveHost host, IReadOnlyList<CampaignRoad> roads,
                                       CampaignTargetOptions? options = null)
    {
        var all = new CampaignGridSet(host.Grids);
        var grids = CampaignGridSet.Empty;
        foreach (var road in roads) grids = grids.Add(road.Roadblocks(all));
        return ClearRoadblockTargets(host, grids, options, "clear_roadblocks");
    }

    /// <summary>上游 <c>Map.clear_potential_roadblocks(roads, **kwargs)</c>：避免"只剩一格空"的路障。</summary>
    public static bool ClearPotentialRoadblocks(ICampaignPrimitiveHost host, IReadOnlyList<CampaignRoad> roads,
                                                CampaignTargetOptions? options = null)
    {
        var all = new CampaignGridSet(host.Grids);
        var grids = CampaignGridSet.Empty;
        foreach (var road in roads) grids = grids.Add(road.PotentialRoadblocks(all));
        return ClearRoadblockTargets(host, grids, options, "clear_potential_roadblocks");
    }

    /// <summary>上游 <c>Map.clear_first_roadblocks(roads, **kwargs)</c>：保证每个路障块都有一个已清格子。</summary>
    public static bool ClearFirstRoadblocks(ICampaignPrimitiveHost host, IReadOnlyList<CampaignRoad> roads,
                                            CampaignTargetOptions? options = null)
    {
        var all = new CampaignGridSet(host.Grids);
        var grids = CampaignGridSet.Empty;
        foreach (var road in roads) grids = grids.Add(road.FirstRoadblocks(all));
        // 上游 clear_first_roadblocks 不做优先级覆盖，直接 select_grids
        return ClearRoadblockTargets(host, grids, options, "clear_first_roadblocks", applyPriority: false);
    }

    /// <summary>三个路段原语共用的收尾：按优先级配置（或调用参数）选择 → 清掉第一个。</summary>
    private static bool ClearRoadblockTargets(ICampaignPrimitiveHost host, CampaignGridSet grids,
                                              CampaignTargetOptions? options, string label,
                                              bool applyPriority = true)
    {
        options ??= new CampaignTargetOptions();
        if (applyPriority)
        {
            if (host.Config.EnemyPriority == "S3_enemy_first") options = options with { Strongest = true };
            else if (host.Config.EnemyPriority == "S1_enemy_first") options = options with { Weakest = true };
            else if (host.Config.MapClearAllThisTime) options = options with { Strongest = true };
        }
        var selected = CampaignTargetSelector.SelectGrids(grids, options);
        if (selected.IsEmpty)
        {
            host.Log($"{label}：无目标");
            return false;
        }
        host.Log($"{label}：选中 {selected[0].Location}");
        return host.ClearChosenEnemy(selected[0], "");
    }

    /// <summary>
    /// 上游 <c>Map.pick_up_ammo(grid=None)</c>：没指定格子时自动找 <c>may_ammo</c>；
    /// 只在"有弹药且格子可达"时走过去并回收弹药。上游结尾**没有** return True（落到方法末尾返回 None），
    /// 这里按"是否真的拾取"返回布尔，并在日志里标明上游的真实返回值语义。
    /// </summary>
    public static bool PickUpAmmo(ICampaignPrimitiveHost host, CampaignGrid? grid = null)
    {
        var all = new CampaignGridSet(host.Grids);
        if (grid is null)
        {
            var candidates = all.Select(new CampaignGridFilter(MayAmmo: true));
            if (candidates.IsEmpty)
            {
                host.Log("pick_up_ammo：Map has no ammo.");
                return false;
            }
            grid = candidates[0];
        }

        if (host.AmmoCount > 0 && grid.IsAccessible)
        {
            host.Log($"pick_up_ammo：Pick up ammo: {grid.Location}");
            host.Goto(grid);
            host.EnsureNoInfoBar();
            int recover = 5 - host.FleetAmmo;
            recover = recover > 3 ? 3 : recover;
            host.Log($"pick_up_ammo：Got ammo {recover}");
            host.AmmoCount -= recover;
            host.FleetAmmo += recover;
            return true;
        }
        host.Log($"pick_up_ammo：跳过（ammo_count={host.AmmoCount}，accessible={grid.IsAccessible}）");
        return false;
    }

    /// <summary>
    /// 上游**关卡基类**里的 <c>pick_up_light_house(grid)</c>（如
    /// `campaign/campaign_main/campaign_14_base.py`）：已拾取过就跳过，否则走过去并记账，**恒返回假**。
    /// 注意：这个原语定义在关卡树而不是 `module/` 里——属"关卡侧 helper"，迁移方向见文档 P2-7。
    /// </summary>
    public static bool PickUpLightHouse(ICampaignPrimitiveHost host, CampaignGrid grid)
    {
        if (host.PickedLightHouse.Contains(grid.Location))
        {
            host.Log($"pick_up_light_house：{grid.Location} already picked up");
        }
        else if (grid.IsAccessible)
        {
            host.Log($"pick_up_light_house：Pick up light house on {grid.Location}");
            host.Goto(grid);
            host.PickedLightHouse.Add(grid.Location);
            host.EnsureNoInfoBar();
        }
        else
        {
            host.Log($"pick_up_light_house：{grid.Location} not accessible, will check in next battle");
        }
        return false;
    }

    /// <summary>
    /// 上游**关卡基类**里的 <c>pick_up_flare(grid)</c>：会把该格子标记成 flare，已拾取过就跳过，
    /// 否则走过去并记账，**恒返回假**（来源同 <see cref="PickUpLightHouse"/>）。
    /// </summary>
    public static bool PickUpFlare(ICampaignPrimitiveHost host, CampaignGrid grid)
    {
        // 上游第一行就是 `grid.is_flare = True`（在"已拾取/可达"判断**之前**），照抄顺序
        host.MarkFlare(grid);
        if (host.PickedFlare.Contains(grid.Location))
        {
            host.Log($"pick_up_flare：Flares {grid.Location} already picked up");
        }
        else if (grid.IsAccessible)
        {
            host.Log($"pick_up_flare：Pick up flares on {grid.Location}");
            host.Goto(grid);
            host.PickedFlare.Add(grid.Location);
        }
        else
        {
            host.Log($"pick_up_flare：Flares {grid.Location} not accessible, will check in next battle");
        }
        return false;
    }

    /// <summary>
    /// 上游 <c>Map.capture_clear_boss()</c>（deprecated 但仍有 12 处调用）：先打 boss / 被塞壬抓住的 may_boss，
    /// 都没有则退回 may_boss+enemy+accessible；最后**无条件撤退**（`withdraw()`）。上游没有 return，
    /// 落到方法末尾为 None（假）。
    /// </summary>
    public static bool CaptureClearBoss(ICampaignPrimitiveHost host)
    {
        var all = new CampaignGridSet(host.Grids);
        var grids = all.Select(new CampaignGridFilter(IsBoss: true, IsAccessible: true));
        grids = grids.Add(all.Select(new CampaignGridFilter(MayBoss: true, IsCaughtBySiren: true)));
        host.Log($"capture_clear_boss：Is boss: {grids.Count} 个");
        if (grids.IsEmpty)
        {
            grids = grids.Add(all.Select(new CampaignGridFilter(MayBoss: true, IsEnemy: true, IsAccessible: true)));
            host.Log("capture_clear_boss：Boss not detected, using may_boss grids.");
        }
        if (!grids.IsEmpty)
        {
            var sorted = grids.Sort("weight", "cost");
            host.Log($"capture_clear_boss：打 {sorted[0].Location}");
            host.ClearChosenEnemy(sorted[0], "");
        }
        host.Log("capture_clear_boss：Grand Capture detected, Withdrawing.");
        host.Withdraw();
        return false;
    }

    /// <summary>
    /// 上游 <c>Map.fleet_2_push_forward()</c>：让第二舰队往 weight 最低的可达海域推进
    /// （9 章道中战最小化路线规划）。`fleet_boss_index != 2` 时直接返回假。
    /// </summary>
    public static bool Fleet2PushForward(ICampaignPrimitiveHost host)
    {
        if (host.Config.FleetBossIndex != 2) return false;
        host.Log("fleet_2_push_forward：Fleet_2 push forward");

        var all = new CampaignGridSet(host.Grids);
        var grids = all.Select(new CampaignGridFilter(IsLand: false)).Sort("weight", "cost");
        if (grids.IsEmpty)
        {
            throw new NotSupportedException("fleet_2_push_forward：地图上没有非陆地格子（上游此处会 IndexError）");
        }
        var fleet2 = host.GridAt(host.Fleet2Location);
        if (fleet2.Weight <= grids[0].Weight)
        {
            host.Log("fleet_2_push_forward：Fleet_2 pushed to destination");
            host.EnsureFleet(1);
            return false;
        }

        var fleets = new CampaignGridSet([host.GridAt(host.Fleet1Location), fleet2]);
        grids = grids.Select(new CampaignGridFilter(IsAccessible2: true, IsSea: true)).Delete(fleets);
        if (grids.IsEmpty)
        {
            host.Log("fleet_2_push_forward：Fleet_2 has no where to push");
            return false;
        }
        if (fleet2.Weight <= grids[0].Weight)
        {
            host.Log("fleet_2_push_forward：Fleet_2 pushed to closest grid");
            return false;
        }

        host.Log($"fleet_2_push_forward：Push forward: {grids[0].Location}");
        host.EnsureFleet(2);
        host.Goto(grids[0]);
        host.EnsureFleet(1);
        return true;
    }

    /// <summary>
    /// 上游 <c>Map.fleet_2_protect()</c>：道中队在 boss 队附近游走、清掉靠近的塞壬/敌人。
    /// 上游最多循环 20 次（每次 goto 后重新扫描地图）；干跑宿主不改变地图状态，因此一轮后即停并记日志。
    /// </summary>
    public static bool Fleet2Protect(ICampaignPrimitiveHost host)
    {
        if (!host.Config.Fleet2 || !host.Config.MapHasMovableEnemy) return false;

        var options = new CampaignTargetOptions { Sort = ["cost_2", "cost_1"] };
        for (int round = 0; round < 20; round++)
        {
            var all = new CampaignGridSet(host.Grids);
            if (all.Select(new CampaignGridFilter(IsSiren: true)).IsEmpty) return false;

            var nearby = all.Select(new CampaignGridFilter(Cost2: 1))
                            .Add(all.Select(new CampaignGridFilter(Cost2: 2)));
            var approaching = CampaignGridSet.Empty;
            if (host.Config.MapHasMovableEnemy)
                approaching = approaching.Add(nearby.Select(new CampaignGridFilter(IsSiren: true)));
            if (host.Config.MapHasMovableNormalEnemy)
                approaching = approaching.Add(nearby.Select(new CampaignGridFilter(IsEnemy: true)));

            if (!approaching.IsEmpty)
            {
                var targets = CampaignTargetSelector.SelectGrids(approaching, options);
                host.Log($"fleet_2_protect：清靠近的 {targets[0].Location}");
                host.ClearChosenEnemy(targets[0], "siren");
                return true;
            }

            var move = nearby.Delete(all.Select(new CampaignGridFilter(IsFleet: true)));
            var moveTargets = CampaignTargetSelector.SelectGrids(move, options);
            if (moveTargets.IsEmpty)
            {
                throw new NotSupportedException("fleet_2_protect：附近没有可去的格子（上游此处会 IndexError）");
            }
            host.Log($"fleet_2_protect：游走到 {moveTargets[0].Location}（第 {round + 1} 轮）");
            host.Goto(moveTargets[0]);
            if (host is RecordingCampaignHost)
            {
                host.Log("fleet_2_protect：干跑宿主不刷新地图状态，一轮后停止（真机由识别刷新后继续）");
                return false;
            }
        }
        return false;
    }

    /// <summary>上游 <c>clear_chosen_enemy(grid, expected='')</c>：打指定格子（设备侧动作交给宿主）。</summary>
    public static bool ClearChosenEnemy(ICampaignPrimitiveHost host, CampaignGrid grid, string expected = "")
    {
        host.Log($"clear_chosen_enemy：targetEnemyScale={host.Config.EnemyPriority}，格子 {grid.Location}");
        return host.ClearChosenEnemy(grid, expected);
    }

    /// <summary>
    /// 上游 <c>Fleet.switch_to()</c>：**上游实现就是 <c>pass</c>**（空方法）——切舰队发生在
    /// <c>fleet_1</c> / <c>fleet_2</c> 这些 property 的取值上，因此这里也只是一个返回假的空操作，
    /// 前缀切舰队由注册表的舰队前缀规则完成。
    /// </summary>
    public static bool SwitchTo(ICampaignPrimitiveHost host, string fleet)
    {
        host.Log($"switch_to：上游 Fleet.switch_to() 是 pass（切舰队已由 {fleet} 前缀完成）");
        return false;
    }

    /// <summary>
    /// 上游关卡基类里的 <c>clear_map_items(grids)</c>（如 `campaign/event_20221124_cn/campaign_base.py`）：
    /// 按 <c>cost</c> 升序逐个走过去；上游没有 return（落到末尾为假）。
    /// </summary>
    public static bool ClearMapItems(ICampaignPrimitiveHost host, IReadOnlyList<CampaignGrid> grids)
    {
        var ordered = grids.OrderBy(grid => grid.Cost).ToArray();
        foreach (var grid in ordered)
        {
            host.Log($"clear_map_items：Clear map item on {grid.Location}");
            host.Goto(grid);
        }
        return false;
    }

    /// <summary>
    /// 上游 <c>Map.clear_mechanism(grids=None)</c>：无 <c>MAP_HAS_LAND_BASED</c> 直接返回假；
    /// 选中可触发且未被阻挡的机关格 → 走过去 → **上游在这里 <c>raise MapEnemyMoved</c>**。
    /// 该异常由尚未迁移的战役循环处理，这里如实转成控制流信号。
    /// </summary>
    public static bool ClearMechanism(ICampaignPrimitiveHost host, IReadOnlyList<CampaignGrid>? grids = null)
    {
        if (!host.Config.MapHasLandBased) return false;

        var pool = grids is null ? new CampaignGridSet(host.Grids) : new CampaignGridSet(grids);
        var selected = CampaignTargetSelector.SelectGrids(
            pool.Select(new CampaignGridFilter(IsMechanismTrigger: true, IsMechanismBlock: false)),
            new CampaignTargetOptions { Sort = ["weight", "cost"] });
        if (selected.IsEmpty)
        {
            host.Log("clear_mechanism：Mechanism all cleared");
            return false;
        }

        var target = selected[0];
        host.Log($"clear_mechanism：Clear mechanism: {target.Location}");
        host.Goto(target);
        host.Log($"clear_mechanism：trigger={target.IsMechanismTrigger}，block={target.IsMechanismBlock}");
        throw new CampaignControlFlowSignal("MapEnemyMoved",
            "清除机关后上游抛 MapEnemyMoved（战役循环需要重新识别地图）——该控制流尚未迁移");
    }

    /// <summary>
    /// 上游 <c>Map.brute_clear_boss()</c>：用暴力找路障的方式清 boss
    /// （先找挡住 boss 的敌人 → 若两支舰队之间有路障先让它们会合 → 否则直接打路障；
    /// 没找到路障就退回 <c>fleet_boss.clear_boss()</c>）。
    /// </summary>
    public static bool BruteClearBoss(ICampaignPrimitiveHost host)
    {
        var all = new CampaignGridSet(host.Grids);
        var boss = all.Select(new CampaignGridFilter(IsBoss: true));
        if (!boss.IsEmpty)
        {
            host.Log("Brute clear BOSS");
            var search = CampaignBruteFinder.FindRoadblocks(host.Grids, boss[0].Location,
                FleetStart(host, host.Config.FleetBossIndex), host.Config.MapHasAmbush,
                liveCost: LiveCostFor(host, boss[0], host.Config.FleetBossIndex));
            if (search.Exhausted && !search.Found)
            {
                host.Log("brute_clear_boss：Enemy roadblock try exhausted.");
            }
            if (search.Found)
            {
                if (BruteFleetMeet(host)) return true;
                var sorted = new CampaignGridSet(search.Roadblocks).Sort("weight", "cost");
                // 子集本身也记日志：真机排查与离线对拍都要看"上游/C# 各自认哪些格子是路障"
                host.Log($"brute_clear_boss：路障子集 [{string.Join(", ", sorted.Grids.Select(g => g.Location))}]");
                host.Log($"Brute clear BOSS roadblocks：打 {sorted[0].Location}");
                host.ClearChosenEnemy(sorted[0], "");
                return true;
            }
            host.EnsureFleet(host.Config.FleetBossIndex);
            return ClearBoss(host);
        }

        var caught = all.Select(new CampaignGridFilter(MayBoss: true, IsCaughtBySiren: true));
        if (!caught.IsEmpty)
        {
            host.Log("brute_clear_boss：BOSS appear on fleet grid");
            host.EnsureFleet(2);
            return host.ClearChosenEnemy(caught[0], "");
        }
        host.Log("brute_clear_boss：BOSS not detected, trying all boss spawn point.");
        return ClearPotentialBoss(host);
    }

    /// <summary>上游 <c>Map.brute_fleet_meet()</c>：为会合清掉两支舰队之间的路障。</summary>
    public static bool BruteFleetMeet(ICampaignPrimitiveHost host)
    {
        if (host.Config.FleetBossIndex != 2 || string.IsNullOrEmpty(host.Fleet2Location))
        {
            // 说明为什么跳过：这两个条件任一不满足时，上游同样不会走会合清障这条路。
            host.Log($"brute_fleet_meet：跳过（fleet_boss_index={host.Config.FleetBossIndex}，" +
                     $"fleet_2 位置「{host.Fleet2Location}」）");
            return false;
        }
        var search = CampaignBruteFinder.FindRoadblocks(host.Grids, host.Fleet2Location,
            FleetStart(host, 1), host.Config.MapHasAmbush,
            liveCost: LiveCostFor(host, host.GridAt(host.Fleet2Location), 1));
        if (!search.Found)
        {
            host.Log($"brute_fleet_meet：两队之间未找到路障（{FleetStart(host, 1)} → {host.Fleet2Location}，" +
                     (search.AlreadyAccessible ? "目标已可达" : "枚举未命中") + "）");
            return false;
        }
        host.Log("Brute clear roadblocks between fleets.");
        var sorted = new CampaignGridSet(search.Roadblocks).Sort("weight", "cost");
        host.Log($"brute_fleet_meet：打 {sorted[0].Location}");
        host.ClearChosenEnemy(sorted[0], "");
        return true;
    }

    /// <summary>上游 <c>Map.fleet_2_rescue(grid)</c>：用道中队清掉挡在目标格前的敌人。</summary>
    public static bool Fleet2Rescue(ICampaignPrimitiveHost host, CampaignGrid grid)
    {
        if (host.Config.FleetBossIndex != 2) return false;
        var search = CampaignBruteFinder.FindRoadblocks(host.Grids, grid.Location,
            FleetStart(host, 2), host.Config.MapHasAmbush, liveCost: LiveCostFor(host, grid, 2));
        if (!search.Found) return false;
        host.Log("Fleet_2 rescue");
        // 上游 self.select_grids(grids)：按**恢复后**的成本场过滤 is_accessible，再按 weight/cost 排序取第一个。
        var restored = CampaignPathfinder.FindPathInitial(host.Grids, FleetStart(host, host.FleetCurrentIndex),
                                                          host.Config.MapHasAmbush, hasEnemy: true);
        var accessible = new CampaignGridSet(search.Roadblocks
            .Where(item => restored.CostOf(item.Location) < CampaignPathfinder.Unreachable));
        var selected = CampaignTargetSelector.SelectGrids(accessible, new CampaignTargetOptions { IsAccessible = false });
        if (selected.IsEmpty) return false;
        host.Log($"fleet_2_rescue：打 {selected[0].Location}");
        host.ClearChosenEnemy(selected[0], "");
        return true;
    }

    /// <summary>按舰队索引取该舰队所在格（上游 <c>find_path_initial()</c> 用 <c>fleet_current</c> 作起点）。</summary>
    private static string FleetStart(ICampaignPrimitiveHost host, int fleetIndex) =>
        fleetIndex == 2 ? host.Fleet2Location : host.Fleet1Location;

    /// <summary>
    /// 目标格在**指定舰队**成本场里的现成代价——对应上游 `brute_find_roadblocks(grid, fleet=f)` 的入口判断：
    /// 它先 `fleet_current_index = f` 再 `find_path_initial()`，于是 `grid.is_accessible` 读的是 **f 队**的场
    /// （`cost_1` / `cost_2`；当前队时就是 `cost`）。
    /// 宿主没有维护该队的场（`9999`）时，按上游同一口径**现算一个**，不猜。
    /// </summary>
    private static int LiveCostFor(ICampaignPrimitiveHost host, CampaignGrid grid, int fleetIndex)
    {
        if (fleetIndex == host.FleetCurrentIndex) return grid.Cost;
        int perFleet = fleetIndex == 2 ? grid.Cost2 : grid.Cost1;
        if (perFleet < CampaignPathfinder.Unreachable) return perFleet;
        return CampaignPathfinder
            .FindPathInitial(host.Grids, FleetStart(host, fleetIndex), host.Config.MapHasAmbush, hasEnemy: true)
            .CostOf(grid.Location);
    }

    /// <summary>
    /// 上游 <c>Map.clear_bouncing_enemy()</c>：找一条"有可达巡逻敌人"的路线，沿路线循环走过去，
    /// 直到 <c>battle_count</c> 增长（打掉了巡逻敌人）或超过 12 次尝试。
    /// 上游在成功时会把该路线的 <c>may_bouncing_enemy</c> 置假并重新识别——干跑不改变地图状态，
    /// 因此这里只记日志（真机由识别刷新）。
    /// </summary>
    public static bool ClearBouncingEnemy(ICampaignPrimitiveHost host)
    {
        if (!host.Config.MapHasBouncingEnemy) return false;

        IReadOnlyList<string>? route = null;
        foreach (var candidate in host.BouncingRoutes)
        {
            var grids = new CampaignGridSet(candidate
                .Select(host.GridAt)
                .Where(grid => grid is not null)!);
            if (!grids.Select(new CampaignGridFilter(MayBouncingEnemy: true, IsAccessible: true)).IsEmpty)
            {
                route = candidate;
                break;
            }
        }
        if (route is null) return false;

        host.Log($"Clear bouncing enemy: {string.Join(" ", route)}");
        int previous = host.BattleCount;
        for (int n = 0; ; n++)
        {
            string location = route[n % route.Count];
            host.Goto(host.GridAt(location), "combat_nothing");
            if (host.BattleCount > previous)
            {
                host.Log($"Cleared an bouncing enemy（{location}）");
                return true;
            }
            if (n >= 12)
            {
                host.Log("Failed to clear bouncing enemy after 12 trial");
                return false;
            }
        }
    }

    /// <summary>
    /// 上游 <c>Map.fleet_2_step_on(grids, roadblocks)</c>：让道中队踩到能降低另一队伏击率的位置；
    /// 走不过去时就清路障。`FLEET_2` 未开启、或 2 队已经在其中任一格上时返回假。
    /// </summary>
    public static bool Fleet2StepOn(ICampaignPrimitiveHost host, IReadOnlyList<CampaignGrid> grids,
                                    IReadOnlyList<CampaignRoad> roads)
    {
        if (!host.Config.Fleet2) return false;
        if (grids.Any(grid => host.Fleet2Location == grid.Location)) return false;

        bool allCleared = grids.All(grid => grid.IsCleared);
        host.Log("Fleet 2 step on");
        foreach (var grid in grids)
        {
            if (grid.IsEnemy || (!allCleared && grid.IsCleared)) continue;
            if (CheckAccessibility(host, grid, 2))
            {
                host.Log($"Fleet_2 step on {grid.Location}");
                host.EnsureFleet(2);
                host.Goto(grid);
                host.EnsureFleet(1);
                return false;
            }
        }

        host.Log("Fleet_2 step on got roadblocks.");
        host.EnsureFleet(1);
        bool cleared = ClearRoadblocks(host, roads);
        ClearAllMystery(host);
        return cleared;
    }

    /// <summary>
    /// 上游 <c>Fleet.check_accessibility(grid, fleet)</c>：按**该舰队**的成本场判断可达性
    /// （当前舰队直接看现有成本；否则临时换舰队重算再恢复）。
    /// </summary>
    private static bool CheckAccessibility(ICampaignPrimitiveHost host, CampaignGrid grid, int fleetIndex)
    {
        if (fleetIndex == host.FleetCurrentIndex) return grid.IsAccessible;
        var field = CampaignPathfinder.FindPathInitial(host.Grids, FleetStart(host, fleetIndex),
                                                      host.Config.MapHasAmbush, hasEnemy: true);
        return field.CostOf(grid.Location) < CampaignPathfinder.Unreachable;
    }

    /// <summary>上游 <c>Map.fleet_2_break_siren_caught()</c>：2 队被塞壬抓住时先挣脱。</summary>
    public static bool Fleet2BreakSirenCaught(ICampaignPrimitiveHost host)
    {
        if (host.Config.FleetBossIndex != 2) return false;
        if (!host.Config.MapHasSiren || !host.Config.MapHasMovableEnemy) return false;

        var all = new CampaignGridSet(host.Grids);
        var caught = all.Select(new CampaignGridFilter(IsCaughtBySiren: true));
        if (caught.IsEmpty)
        {
            host.Log("No fleet caught by siren.");
            return false;
        }
        if (string.IsNullOrEmpty(host.Fleet2Location) ||
            !caught.Grids.Any(grid => grid.Location == host.Fleet2Location))
        {
            host.Log("Appear caught by siren, but not fleet_2.");
            host.ClearCaughtBySirenFlags();
            return false;
        }

        host.Log($"Break siren caught, fleet_2: {host.Fleet2Location}");
        host.EnsureFleet(2);
        host.EnsureEdgeInsight();
        host.ClearChosenEnemy(host.GridAt(host.Fleet2Location), "");
        host.EnsureFleet(1);
        host.ClearCaughtBySirenFlags();
        return true;
    }

    /// <summary>上游 <c>CampaignBase.battle_boss()</c>：`brute_clear_boss()` 打成就真，否则记 No battle executed.</summary>
    public static bool BattleBoss(ICampaignPrimitiveHost host)
    {
        if (BruteClearBoss(host)) return true;
        host.Log("No battle executed.");
        return false;
    }

    /// <summary>
    /// 上游 <c>battle_function</c> 变体 <c>battle_with_poor_map_data</c>
    /// （`@Config.when(POOR_MAP_DATA=True, MAP_CLEAR_ALL_THIS_TIME=False)`）。
    /// </summary>
    public static bool PoorMapDataVariant(ICampaignPrimitiveHost host)
    {
        host.Log("Using function: battle_with_poor_map_data");
        if (Fleet2BreakSirenCaught(host)) return true;
        ClearAllMystery(host);
        if (host.BattleCount >= 3) PickUpAmmo(host);

        var all = new CampaignGridSet(host.Grids);
        if (!all.Select(new CampaignGridFilter(IsBoss: true)).IsEmpty)
        {
            if (BruteClearBoss(host)) return true;
        }
        else
        {
            if (ClearSiren(host)) return true;
            return ClearEnemy(host);
        }
        return false;
    }

    /// <summary>
    /// 上游 <c>battle_function</c> 变体 <c>clear_all</c>（`@Config.when(MAP_CLEAR_ALL_THIS_TIME=True)`）。
    /// </summary>
    public static bool ClearAllVariant(ICampaignPrimitiveHost host)
    {
        host.Log("Using function: clear_all");
        if (Fleet2BreakSirenCaught(host)) return true;
        ClearAllMystery(host);
        if (host.BattleCount >= 3) PickUpAmmo(host);

        var all = new CampaignGridSet(host.Grids);
        var remain = all.Select(new CampaignGridFilter(IsEnemy: true))
            .Add(all.Select(new CampaignGridFilter(IsSiren: true)))
            .Add(all.Select(new CampaignGridFilter(IsFortress: true)))
            .Delete(all.Select(new CampaignGridFilter(IsBoss: true)));
        host.Log($"Enemy remain: {remain.Count}");

        if (remain.Count > 0)
        {
            if (host.Config.MapHasMovableNormalEnemy)
            {
                if (ClearAnyEnemy(host, new CampaignTargetOptions { Sort = ["cost_2"] })) return true;
                return BattleDefault(host);
            }
            if (ClearBouncingEnemy(host)) return true;
            if (ClearSiren(host)) return true;
            ClearMechanism(host);
            return BattleDefault(host);
        }
        return BattleBoss(host);
    }

    /// <summary>
    /// 上游 <c>Fleet.handle_boss_appear_refocus(preset=None)</c>（关卡覆写只是 `return super().X(preset)`）：
    /// 记下当前相机位置 → 有非零 preset 时先 <c>update()</c>，**识别失败**（上游 <c>MapDetectionError</c>）
    /// 则按 preset 滑动再对齐边缘；没有 preset 时只 update + 对齐边缘 → 最后 <c>focus_to(记录的相机位置)</c>。
    ///
    /// 返回假：上游这个方法是语句级调用、返回 None，不是"打成了"的那种真。
    /// </summary>
    public static bool HandleBossAppearRefocus(ICampaignPrimitiveHost host, (int X, int Y)? preset)
    {
        string camera = host.CameraLocation;
        var swipe = preset ?? host.Config.MapBossAppearRefocusSwipe;
        if (swipe is not null && (swipe.Value.X != 0 || swipe.Value.Y != 0))
        {
            if (!host.UpdateMap())
            {
                host.Log($"MapDetectionError occurs after boss appear, trying swipe preset ({swipe.Value.X}, {swipe.Value.Y})");
                host.MapSwipe(swipe.Value);
            }
            host.EnsureEdgeInsight();
        }
        else
        {
            host.UpdateMap();
            host.EnsureEdgeInsight();
        }
        host.Log("Refocus to previous camera position.");
        host.FocusTo(camera);
        return false;
    }

    /// <summary>上游 <c>CampaignBase.battle_default()</c>。</summary>
    public static bool BattleDefault(ICampaignPrimitiveHost host)
    {
        if (ClearEnemy(host)) return true;
        host.Log("battle_default：No battle executed.");
        return false;
    }

    /// <summary>上游 <c>Map.clear_all_mystery(**kwargs)</c>：恒返回假。</summary>
    public static bool ClearAllMystery(ICampaignPrimitiveHost host, CampaignTargetOptions? options = null)
    {
        options = (options ?? new CampaignTargetOptions()) with { Sort = ["cost"] };
        for (int round = 0; round < MaxMysteryRounds; round++)
        {
            var grids = new CampaignGridSet(host.Grids).Select(new CampaignGridFilter(IsMystery: true));
            var selected = CampaignTargetSelector.SelectGrids(grids, options);
            if (selected.IsEmpty) break;
            host.Log($"clear_all_mystery：选中 {selected[0].Location}（第 {round + 1} 轮）");
            host.ClearChosenMystery(selected[0]);
            // 干跑宿主不会真的改变地图状态：一轮之后即停止，避免死循环（真机由识别刷新状态）。
            if (host is RecordingCampaignHost) break;
        }
        return false;
    }

    /// <summary>上游 <c>Map.clear_filter_enemy(string, preserve)</c>。</summary>
    public static bool ClearFilterEnemy(ICampaignPrimitiveHost host, string filter, int preserve)
    {
        // 上游：`MAP_HAS_MOVABLE_NORMAL_ENEMY` 时**整个过滤串被忽略**，直接转成
        // `clear_any_enemy(sort=('cost_2',))`。`cost_2` 排序键早已支持，所以这条分支现在直接委托，
        // 不再报"未移植"。
        if (host.Config.MapHasMovableNormalEnemy)
        {
            host.Log("clear_filter_enemy：MAP_HAS_MOVABLE_NORMAL_ENEMY → clear_any_enemy(sort=('cost_2',))");
            return ClearAnyEnemy(host, new CampaignTargetOptions(Sort: ["cost_2"]));
        }
        var decision = CampaignTargetSelector.SelectFilterEnemyTarget(
            new CampaignGridSet(host.Grids), filter, preserve,
            host.Config.EnemyPriority, hasMovableNormalEnemy: false);
        if (decision.Unsupported is not null)
            throw new NotSupportedException($"clear_filter_enemy 走到未移植分支：{decision.Branch}——{decision.Unsupported}");
        if (decision.Target is null)
        {
            host.Log($"clear_filter_enemy：无目标（{decision.Branch}）");
            return false;
        }
        host.Log($"clear_filter_enemy：选中 {decision.Target.Location}（{decision.Target.FilterKey}，{decision.Branch}）");
        return host.ClearChosenEnemy(decision.Target, "");
    }
}

/// <summary>
/// 上游用异常做控制流的信号（如 <c>clear_mechanism</c> 结尾的 <c>raise MapEnemyMoved</c>）。
/// 战役循环（尚未迁移）会捕获它并重新识别地图——在那之前，执行器把它转成**阻塞原因**如实报出，
/// 而不是假装继续往下跑。
/// </summary>
public sealed class CampaignControlFlowSignal(string kind, string message) : Exception(message)
{
    public string Kind { get; } = kind;
}

/// <summary>一次钩子执行的结果：返回值、逐步记录、干跑动作与日志；被阻塞时给出原因。</summary>
public sealed record CampaignHookExecution(
    string Chapter,
    string Level,
    string Method,
    bool? ReturnValue,
    string? BlockedReason,
    IReadOnlyList<string> StepLog,
    IReadOnlyList<string> Actions,
    IReadOnlyList<string> Logs)
{
    public bool Completed => BlockedReason is null;
}

/// <summary>
/// 钩子执行循环：按计划契约驱动原语（`call` 前置 → `conditional` 尝试并短路 → `terminal` 兜底 →
/// `super_delegate` 委托），动作全部经 <see cref="ICampaignPrimitiveHost"/> 发出。
///
/// 诚实边界：实参含未求值表达式（导出里的 <c>"&lt;expr&gt;"</c>）或原语未实现时**停止执行**并给出原因，
/// 不跳过、不猜测——否则会发出错误的设备动作。
/// </summary>
public static class CampaignHookRunner
{
    /// <summary>跨钩子调用的最大递归深度（上游存在 `self.battle_0()` 这种调用同关卡其它钩子的写法）。</summary>
    private const int MaxCallDepth = 3;

    public static CampaignHookExecution Run(CampaignPlan plan, CampaignPlanBattle battle,
                                            ICampaignPrimitiveHost host) =>
        Run(plan, battle, host, depth: 0);

    private static CampaignHookExecution Run(CampaignPlan plan, CampaignPlanBattle battle,
                                             ICampaignPrimitiveHost host, int depth)
    {
        var stepLog = new List<string>();
        var actions = new List<string>();
        int actionsBefore = host is RecordingCampaignHost recording ? recording.Actions.Count : 0;

        foreach (var step in battle.Steps)
        {
            if (step.Kind == "super_delegate")
            {
                // 委托父类：`super().X(...)` 在上游会调用**基类实现**（这 7 处实测都是 Fleet 的实现）。
                // 实参里的参数引用用钩子签名的默认值还原，然后按普通原语执行——不再当"本层未执行"跳过。
                var delegated = CampaignPrimitiveRegistry.ResolveSuperDelegate(plan, battle, step);
                if (delegated.Step is null)
                {
                    stepLog.Add($"{step.Op}: {delegated.Reason}");
                    return Result(plan, battle, null, delegated.Reason, stepLog, host, actionsBefore, actions);
                }
                if (!CampaignPrimitiveRegistry.TryGet(delegated.Step.Op, out var basePrimitive))
                {
                    string reason = $"父类实现 {delegated.Step.Op} 尚未迁移（super 委托无法执行）";
                    stepLog.Add($"{step.Op}: {reason}");
                    return Result(plan, battle, null, reason, stepLog, host, actionsBefore, actions);
                }
                try
                {
                    basePrimitive.Execute(host, delegated.Step);
                    stepLog.Add($"{step.Op} → 父类实现 {delegated.Step.Op}：已执行" +
                                  (delegated.Note is null ? "" : $"（{delegated.Note}）"));
                }
                catch (CampaignControlFlowSignal signal)
                {
                    stepLog.Add($"{step.Op}: 控制流信号 {signal.Kind}");
                    return Result(plan, battle, null, signal.Message, stepLog, host, actionsBefore, actions);
                }
                catch (NotSupportedException error)
                {
                    stepLog.Add($"{step.Op}: {error.Message}");
                    return Result(plan, battle, null, error.Message, stepLog, host, actionsBefore, actions);
                }
                continue;
            }

            var role = step.Kind switch
            {
                "conditional" => CampaignStepRole.Attempt,
                "terminal" => CampaignStepRole.Fallback,
                _ => CampaignStepRole.Setup,
            };

            if (UnevaluatedArguments(step) is { } argument)
            {
                stepLog.Add($"{step.Op}: 实参未求值（{argument}），停止执行");
                return Result(plan, battle, null, $"实参未求值：{argument}", stepLog, host, actionsBefore, actions);
            }

            // 跨钩子调用：`self.battle_0()` 这类步骤（上游确实存在），在本关卡内递归执行那个钩子。
            var nested = plan.Header.Battles.FirstOrDefault(item => item.Method == step.Op);
            if (nested is not null && !CampaignPrimitiveRegistry.IsImplemented(step.Op))
            {
                if (depth >= MaxCallDepth)
                {
                    stepLog.Add($"{step.Op}: 跨钩子调用超过 {MaxCallDepth} 层，停止执行");
                    return Result(plan, battle, null, $"跨钩子调用超过 {MaxCallDepth} 层",
                                  stepLog, host, actionsBefore, actions);
                }
                var inner = Run(plan, nested, host, depth + 1);
                stepLog.Add($"{step.Op}: 跨钩子调用 → {(inner.ReturnValue is null ? "未完成" : inner.ReturnValue.Value ? "真" : "假")}");
                if (inner.BlockedReason is not null)
                {
                    return Result(plan, battle, null, $"跨钩子 {step.Op} 被阻塞：{inner.BlockedReason}",
                                  stepLog, host, actionsBefore, actions);
                }
                if (role == CampaignStepRole.Attempt && inner.ReturnValue == true)
                {
                    return Result(plan, battle, true, null, stepLog, host, actionsBefore, actions);
                }
                if (role == CampaignStepRole.Fallback)
                {
                    return Result(plan, battle, inner.ReturnValue, null, stepLog, host, actionsBefore, actions);
                }
                continue;
            }

            if (!CampaignPrimitiveRegistry.TryGet(step.Op, out var primitive))
            {
                stepLog.Add($"{step.Op}: 原语未实现，停止执行");
                return Result(plan, battle, null, $"原语 {step.Op} 未实现", stepLog, host, actionsBefore, actions);
            }

            bool executed;
            try
            {
                executed = primitive.Execute(host, step);
            }
            catch (NotSupportedException error)
            {
                stepLog.Add($"{step.Op}: {error.Message}");
                return Result(plan, battle, null, error.Message, stepLog, host, actionsBefore, actions);
            }
            catch (CampaignControlFlowSignal signal)
            {
                stepLog.Add($"{step.Op}: 控制流信号 {signal.Kind}");
                return Result(plan, battle, null, signal.Message, stepLog, host, actionsBefore, actions);
            }

            stepLog.Add($"{step.Op}: {(executed ? "真" : "假")}（{Role(role)}）");

            if (role == CampaignStepRole.Attempt && executed)
            {
                return Result(plan, battle, true, null, stepLog, host, actionsBefore, actions);
            }
            if (role == CampaignStepRole.Fallback)
            {
                return Result(plan, battle, executed, null, stepLog, host, actionsBefore, actions);
            }
        }

        // 没有 terminal 兜底且所有尝试都没成功：上游会落到方法末尾（返回 None，视为假）
        return Result(plan, battle, false, null, stepLog, host, actionsBefore, actions);
    }

    private static CampaignHookExecution Result(CampaignPlan plan, CampaignPlanBattle battle, bool? returned,
                                                string? blocked, List<string> stepLog, ICampaignPrimitiveHost host,
                                                int actionsBefore, List<string> actions)
    {
        if (host is RecordingCampaignHost recording)
        {
            actions.AddRange(recording.Actions.Skip(actionsBefore));
        }
        var logs = host is RecordingCampaignHost recorder ? recorder.Logs.ToList() : [];
        return new CampaignHookExecution(plan.Chapter, plan.Level, battle.Method, returned, blocked,
                                         stepLog, actions, logs);
    }

    private static string Role(CampaignStepRole role) => role switch
    {
        CampaignStepRole.Setup => "前置",
        CampaignStepRole.Attempt => "尝试",
        CampaignStepRole.Fallback => "兜底",
        _ => "委托",
    };

    /// <summary>找出未求值的实参（导出器写 <c>"&lt;expr&gt;"</c>），没有则返回 null。</summary>
    private static string? UnevaluatedArguments(CampaignPlanStep step)
    {
        foreach (var value in step.Args?.Positional ?? [])
        {
            if (IsPlaceholder(value)) return "位置参数 <expr>";
        }
        if (step.Args is { } args)
        {
            foreach (var (name, value) in args.Keyword)
            {
                if (IsPlaceholder(value)) return $"关键字参数 {name}=<expr>";
            }
        }
        return null;
    }

    private static bool IsPlaceholder(JsonNode? value) =>
        value is JsonValue json && json.TryGetValue(out string? text) && text == "<expr>";
}

/// <summary>一个已登记的原语：名字、说明、实现，以及"实参是否必须求值后才能执行"。</summary>
public sealed record CampaignPrimitive(
    string Op,
    string Description,
    bool NeedsArguments,
    Func<ICampaignPrimitiveHost, CampaignPlanStep, bool> Execute);

/// <summary>`super().X(...)` 的解析结果：可执行的基类步骤，或"为什么不能执行"。</summary>
public sealed record CampaignSuperDelegate(CampaignPlanStep? Step, string Reason, string? Note = null);

/// <summary>
/// 原语注册表：C# 引擎已实现的原语。P2 按域往里加，每加一个都要带对拍夹具与真实路径证据。
/// 这里刻意不做"按关卡/按编号"的特例分支——注册表只回答"这个原语实现了没有、怎么执行"。
/// </summary>
public static class CampaignPrimitiveRegistry
{
    /// <summary>
    /// 解析 `super().X(...)`：把 op 归一成基类方法名，并把实参里的**参数引用**（`{"__param__": name}`）
    /// 用钩子签名的默认值还原。还原不了（参数没有字面量默认值）就返回原因——不猜一个值去执行。
    /// </summary>
    public static CampaignSuperDelegate ResolveSuperDelegate(CampaignPlan plan, CampaignPlanBattle battle,
                                                             CampaignPlanStep step)
    {
        const string prefix = "super().";
        if (!step.Op.StartsWith(prefix, StringComparison.Ordinal) || step.Op.Length == prefix.Length)
        {
            return new CampaignSuperDelegate(null, $"无法识别的 super 委托写法：{step.Op}");
        }
        string target = step.Op[prefix.Length..];

        string? note = null;
        CampaignPlanStepArgs? args = step.Args;
        if (args is not null && (args.Keyword.Count > 0 || args.Positional.Count > 0))
        {
            var positional = new List<JsonNode?>();
            foreach (var value in args.Positional)
            {
                if (ParameterName(value) is { } name)
                {
                    if (!battle.Parameters.TryGetValue(name, out var fallback) || fallback is null)
                    {
                        return new CampaignSuperDelegate(null,
                            $"super 委托实参 {name} 没有字面量默认值，无法还原（{step.Op}）");
                    }
                    positional.Add(fallback.DeepClone());
                    note = $"实参 {name} 用签名默认值 {fallback.ToJsonString()}";
                    continue;
                }
                positional.Add(value?.DeepClone());
            }
            var keyword = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            foreach (var (key, value) in args.Keyword)
            {
                if (ParameterName(value) is { } name)
                {
                    if (!battle.Parameters.TryGetValue(name, out var fallback) || fallback is null)
                    {
                        return new CampaignSuperDelegate(null,
                            $"super 委托关键字实参 {name} 没有字面量默认值，无法还原（{step.Op}）");
                    }
                    keyword[key] = fallback.DeepClone();
                    note = $"关键字实参 {name} 用签名默认值 {fallback.ToJsonString()}";
                    continue;
                }
                keyword[key] = value?.DeepClone();
            }
            args = new CampaignPlanStepArgs { Positional = positional, Keyword = keyword };
        }
        return new CampaignSuperDelegate(new CampaignPlanStep { Kind = "call", Op = target, Args = args },
                                         "ok", note);
    }

    private static string? ParameterName(JsonNode? value) =>
        value is JsonObject payload && payload.TryGetPropertyValue("__param__", out var name)
            ? name?.GetValue<string>()
            : null;

    private static readonly Dictionary<string, CampaignPrimitive> Table = new(StringComparer.Ordinal)
    {
        ["clear_enemy"] = new CampaignPrimitive(
            "clear_enemy", "选一个非 boss 敌人并清掉（上游 Map.clear_enemy）", NeedsArguments: false,
            (host, step) => CampaignPrimitives.ClearEnemy(host, DecodeOptions(step))),
        ["battle_default"] = new CampaignPrimitive(
            "battle_default", "默认战斗：clear_enemy 成功即真（上游 CampaignBase.battle_default）", false,
            (host, _) => CampaignPrimitives.BattleDefault(host)),
        ["clear_all_mystery"] = new CampaignPrimitive(
            "clear_all_mystery", "捡完所有神秘格子，恒返回假（上游 Map.clear_all_mystery）", false,
            (host, _) => CampaignPrimitives.ClearAllMystery(host)),
        ["clear_filter_enemy"] = new CampaignPrimitive(
            "clear_filter_enemy", "按过滤串选敌人并清掉（上游 Map.clear_filter_enemy）", NeedsArguments: true,
            (host, step) => CampaignPrimitives.ClearFilterEnemy(host, DecodeFilter(step), DecodePreserve(step))),
        ["clear_any_enemy"] = new CampaignPrimitive(
            "clear_any_enemy", "敌人+塞壬+要塞一起选（上游 Map.clear_any_enemy）", false,
            (host, step) => CampaignPrimitives.ClearAnyEnemy(host, DecodeOptions(step))),
        ["clear_siren"] = new CampaignPrimitive(
            "clear_siren", "打塞壬/要塞，无配置直接返回假（上游 Map.clear_siren）", false,
            (host, step) => CampaignPrimitives.ClearSiren(host, DecodeOptions(step))),
        ["clear_boss"] = new CampaignPrimitive(
            "clear_boss", "找 boss 并清掉，找不到踩 may_boss（上游 Map.clear_boss）", false,
            (host, _) => CampaignPrimitives.ClearBoss(host)),
        ["clear_roadblocks"] = new CampaignPrimitive(
            "clear_roadblocks", "打掉路段里的路障（上游 Map.clear_roadblocks）", NeedsArguments: true,
            (host, step) => CampaignPrimitives.ClearRoadblocks(host, DecodeRoads(step), DecodeOptions(step))),
        ["clear_potential_roadblocks"] = new CampaignPrimitive(
            "clear_potential_roadblocks", "避免只剩一格空的路障（上游 Map.clear_potential_roadblocks）",
            NeedsArguments: true,
            (host, step) => CampaignPrimitives.ClearPotentialRoadblocks(host, DecodeRoads(step), DecodeOptions(step))),
        ["clear_first_roadblocks"] = new CampaignPrimitive(
            "clear_first_roadblocks", "保证每个路障块有一个已清格子（上游 Map.clear_first_roadblocks）",
            NeedsArguments: true,
            (host, step) => CampaignPrimitives.ClearFirstRoadblocks(host, DecodeRoads(step), DecodeOptions(step))),
        ["pick_up_ammo"] = new CampaignPrimitive(
            "pick_up_ammo", "捡弹药（上游 Map.pick_up_ammo）", false,
            (host, step) => CampaignPrimitives.PickUpAmmo(host, DecodeGrid(host, step))),
        ["pick_up_light_house"] = new CampaignPrimitive(
            "pick_up_light_house", "捡灯塔（上游关卡基类 helper，恒返回假）", NeedsArguments: true,
            (host, step) => CampaignPrimitives.PickUpLightHouse(host, RequireGrid(host, step))),
        ["pick_up_flare"] = new CampaignPrimitive(
            "pick_up_flare", "捡信号弹（上游关卡基类 helper，恒返回假）", NeedsArguments: true,
            (host, step) => CampaignPrimitives.PickUpFlare(host, RequireGrid(host, step))),
        ["capture_clear_boss"] = new CampaignPrimitive(
            "capture_clear_boss", "打 boss 后退（上游 Map.capture_clear_boss，deprecated）", false,
            (host, _) => CampaignPrimitives.CaptureClearBoss(host)),
        ["fleet_2_push_forward"] = new CampaignPrimitive(
            "fleet_2_push_forward", "道中队推进到 weight 最低的可达海域（上游 Map.fleet_2_push_forward）", false,
            (host, _) => CampaignPrimitives.Fleet2PushForward(host)),
        ["fleet_2_protect"] = new CampaignPrimitive(
            "fleet_2_protect", "道中队游走清靠近的塞壬/敌人（上游 Map.fleet_2_protect）", false,
            (host, _) => CampaignPrimitives.Fleet2Protect(host)),
        ["clear_potential_boss"] = new CampaignPrimitive(
            "clear_potential_boss", "依次踩可达 may_boss 格子（上游 Map.clear_potential_boss）", false,
            (host, _) => CampaignPrimitives.ClearPotentialBoss(host)),
        ["clear_chosen_enemy"] = new CampaignPrimitive(
            "clear_chosen_enemy", "打指定格子（上游 Map.clear_chosen_enemy 的动作入口）", NeedsArguments: true,
            (host, step) => CampaignPrimitives.ClearChosenEnemy(host, RequireGrid(host, step))),
        ["switch_to"] = new CampaignPrimitive(
            "switch_to", "上游 Fleet.switch_to() 是 pass（切舰队由前缀完成）", false,
            (host, step) => CampaignPrimitives.SwitchTo(host, CampaignPrimitiveRegistry.SplitFleetPrefix(step.Op).Prefix)),
        ["clear_map_items"] = new CampaignPrimitive(
            "clear_map_items", "按 cost 升序清掉指定格子上的物资（上游关卡基类 helper）", NeedsArguments: true,
            (host, step) => CampaignPrimitives.ClearMapItems(host, RequireGrids(host, step))),
        ["clear_mechanism"] = new CampaignPrimitive(
            "clear_mechanism", "清机关并抛 MapEnemyMoved 信号（上游 Map.clear_mechanism）", false,
            (host, step) => CampaignPrimitives.ClearMechanism(host, OptionalGrids(host, step))),
        ["brute_clear_boss"] = new CampaignPrimitive(
            "brute_clear_boss", "暴力找路障后清 boss（上游 Map.brute_clear_boss）", false,
            (host, _) => CampaignPrimitives.BruteClearBoss(host)),
        ["brute_fleet_meet"] = new CampaignPrimitive(
            "brute_fleet_meet", "为两支舰队会合清路障（上游 Map.brute_fleet_meet）", false,
            (host, _) => CampaignPrimitives.BruteFleetMeet(host)),
        ["fleet_2_rescue"] = new CampaignPrimitive(
            "fleet_2_rescue", "道中队救援：清掉挡在目标格前的敌人（上游 Map.fleet_2_rescue）",
            NeedsArguments: true,
            (host, step) => CampaignPrimitives.Fleet2Rescue(host, RequireGrid(host, step))),
        ["fleet_2_step_on"] = new CampaignPrimitive(
            "fleet_2_step_on", "道中队踩到能降低伏击率的位置，否则清路障（上游 Map.fleet_2_step_on）",
            NeedsArguments: true,
            (host, step) => CampaignPrimitives.Fleet2StepOn(host, RequireGrids(host, step), DecodeRoads(step))),
        ["clear_bouncing_enemy"] = new CampaignPrimitive(
            "clear_bouncing_enemy", "清巡逻敌人（上游 Map.clear_bouncing_enemy）", false,
            (host, _) => CampaignPrimitives.ClearBouncingEnemy(host)),
        ["fleet_2_break_siren_caught"] = new CampaignPrimitive(
            "fleet_2_break_siren_caught", "2 队被塞壬抓住时挣脱（上游 Map.fleet_2_break_siren_caught）", false,
            (host, _) => CampaignPrimitives.Fleet2BreakSirenCaught(host)),
        ["handle_boss_appear_refocus"] = new CampaignPrimitive(
            "handle_boss_appear_refocus", "boss 出现后重对焦（上游 Fleet.handle_boss_appear_refocus）", true,
            (host, step) => CampaignPrimitives.HandleBossAppearRefocus(host, DecodeSwipe(step))),
        ["battle_boss"] = new CampaignPrimitive(
            "battle_boss", "打 boss：brute_clear_boss 打成就真（上游 CampaignBase.battle_boss）", false,
            (host, _) => CampaignPrimitives.BattleBoss(host)),
    };

    /// <summary>已实现的原语名（排序返回，便于输出与对拍）。不含舰队前缀组合。</summary>
    public static IReadOnlyList<string> ImplementedOps =>
        Table.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// 上游把 <c>fleet_1</c> / <c>fleet_2</c> / <c>fleet_boss</c> / <c>fleet_submarine</c> 做成
    /// **返回 self 的 property**（必要时先切舰队），所以 <c>fleet_boss.clear_boss()</c> 这类调用等价于
    /// "确保在 boss 舰队 + 执行 <c>clear_boss()</c>"。这里按同一规律解析前缀，不做逐关卡特例。
    /// </summary>
    private static readonly Dictionary<string, string> FleetPrefixes = new(StringComparer.Ordinal)
    {
        ["fleet_1"] = "1",
        ["fleet_2"] = "2",
        ["fleet_submarine"] = "submarine",
        ["fleet_boss"] = "boss",
    };

    /// <summary>是否为"舰队前缀 + 原语"的调用形态（如 <c>fleet_boss.clear_boss</c>）。</summary>
    public static bool IsFleetPrefixed(string op)
    {
        int dot = op.IndexOf('.');
        return dot > 0 && FleetPrefixes.ContainsKey(op[..dot]);
    }

    /// <summary>拆出舰队前缀与底层原语名。</summary>
    public static (string Prefix, string Inner) SplitFleetPrefix(string op)
    {
        int dot = op.IndexOf('.');
        return dot > 0 ? (op[..dot], op[(dot + 1)..]) : ("", op);
    }

    /// <summary>前缀对应的舰队索引；<c>fleet_boss</c> 按上游 <c>fleet_boss_index</c> 规则取 2 或 1。</summary>
    private static int? FleetIndexFor(string prefix, CampaignRuntimeConfig config) => prefix switch
    {
        "fleet_1" => 1,
        "fleet_2" => 2,
        "fleet_boss" => config.FleetBoss && config.Fleet2 ? 2 : 1,
        _ => null,
    };

    /// <summary>该原语（含舰队前缀组合）当前是否可实现。</summary>
    public static bool IsImplemented(string op)
    {
        if (Table.ContainsKey(op)) return true;
        if (!IsFleetPrefixed(op)) return false;
        var (prefix, inner) = SplitFleetPrefix(op);
        // fleet_submarine 只是"当前对象"，没有上游的切换索引；其余前缀会切舰队。
        return (prefix == "fleet_submarine" || FleetIndexFor(prefix, new CampaignRuntimeConfig()) is not null)
               && Table.ContainsKey(inner);
    }

    public static bool TryGet(string op, out CampaignPrimitive primitive)
    {
        if (Table.TryGetValue(op, out primitive!)) return true;
        if (!IsFleetPrefixed(op))
        {
            primitive = null!;
            return false;
        }
        var (prefix, inner) = SplitFleetPrefix(op);
        if (!Table.TryGetValue(inner, out var target))
        {
            primitive = null!;
            return false;
        }
        primitive = new CampaignPrimitive(
            op, $"切到 {prefix} 后执行 {inner}（上游 {prefix} 是返回 self 的 property）",
            target.NeedsArguments,
            (host, step) =>
            {
                int? index = FleetIndexFor(prefix, host.Config);
                if (index is int value) host.EnsureFleet(value);
                return target.Execute(host, step);
            });
        return true;
    }

    /// <summary>把计划步骤的关键字实参解成选择条件（<c>scale</c> / <c>genre</c> / <c>preserve</c> 等）。</summary>
    private static CampaignTargetOptions DecodeOptions(CampaignPlanStep step)
    {
        var options = new CampaignTargetOptions();
        if (step.Args is not { } args) return options;
        if (args.Keyword.TryGetValue("scale", out var scaleNode) && scaleNode is JsonArray scaleArray)
        {
            options = options with
            {
                Scale = scaleArray.OfType<JsonValue>().Select(value => value.GetValue<int>()).ToArray(),
                // 上游：元组是并集、列表是"取到即止"。导出里无法区分，默认按**列表**（取到即止）处理，
                // 并在说明里标注；两者只有在 scale 同时命中多档时结果才不同。
                ScaleInOrder = true,
            };
        }
        if (args.Keyword.TryGetValue("genre", out var genreNode) && genreNode is JsonArray genreArray)
        {
            options = options with
            {
                Genre = genreArray.OfType<JsonValue>().Select(value => value.GetValue<string>()).ToArray(),
                GenreInOrder = true,
            };
        }
        if (args.Keyword.TryGetValue("weakest", out var weakest) && weakest is JsonValue weakValue
            && weakValue.TryGetValue(out bool weakFlag))
        {
            options = options with { Weakest = weakFlag };
        }
        if (args.Keyword.TryGetValue("strongest", out var strongest) && strongest is JsonValue strongValue
            && strongValue.TryGetValue(out bool strongFlag))
        {
            options = options with { Strongest = strongFlag };
        }
        if (args.Keyword.TryGetValue("sort", out var sortNode) && sortNode is JsonArray sortArray)
        {
            options = options with
            {
                Sort = sortArray.OfType<JsonValue>().Select(value => value.GetValue<string>()).ToArray(),
            };
        }
        return options;
    }

    /// <summary>
    /// 解码 `{"__grid__": [x, y]}` 单格实参，并**在宿主的地图状态里查回真实格子**——
    /// 上游传的是 `GridInfo` 对象（带 cost/可达性等属性），绝不能拿一个"默认 cost=0"的空壳去判断，
    /// 否则会把不可达格子当成可达（实测踩过：fixture 里 cost=9999 的 A9 被判成可拾取）。
    /// 地图状态里没有这个格子时如实报错，不用默认值糊过去。
    /// </summary>
    private static CampaignGrid? DecodeGrid(ICampaignPrimitiveHost host, CampaignPlanStep step)
    {
        foreach (var value in step.Args?.Positional ?? [])
        {
            if (value is not JsonObject payload || payload["__grid__"] is not JsonArray cell) continue;
            string location = CampaignLocations.ToNode(cell[0]!.GetValue<int>(), cell[1]!.GetValue<int>());
            var grid = host.Grids.FirstOrDefault(item => item.Location == location);
            if (grid is null)
            {
                throw new NotSupportedException(
                    $"{step.Op} 的格子实参 {location} 不在当前地图状态里（识别结果可能没覆盖该格子）");
            }
            return grid;
        }
        return null;
    }

    /// <summary>要求必须有单格实参（`pick_up_light_house` / `pick_up_flare` 都是显式传格子）。</summary>
    private static CampaignGrid RequireGrid(ICampaignPrimitiveHost host, CampaignPlanStep step) =>
        DecodeGrid(host, step) ?? throw new NotSupportedException(
            $"{step.Op} 的格子实参在导出里不是 __grid__ 结构——需要导出器解析格子符号后才能执行");

    /// <summary>解码 `{"__grids__": [[x, y], …]}` 多格实参（`clear_map_items([F1, I1])`），并查回真实格子。</summary>
    /// <summary>
    /// 解 `handle_boss_appear_refocus` 的 preset：位置实参里的 `[x, y]` 对；没有实参 → null
    /// （上游 `preset=None`，此时走"只 update + 对齐边缘"那条分支）。形状不对就抛，不猜。
    /// </summary>
    private static (int X, int Y)? DecodeSwipe(CampaignPlanStep step)
    {
        var positional = step.Args?.Positional;
        if (positional is null || positional.Count == 0) return null;
        if (positional[0] is not JsonArray pair || pair.Count != 2
            || pair[0] is null || pair[1] is null)
        {
            throw new NotSupportedException($"preset 实参形状不支持（{step.Op}）：{positional[0]?.ToJsonString()}");
        }
        return (pair[0]!.GetValue<int>(), pair[1]!.GetValue<int>());
    }

    private static IReadOnlyList<CampaignGrid>? DecodeGrids(ICampaignPrimitiveHost host, CampaignPlanStep step)
    {
        foreach (var value in step.Args?.Positional ?? [])
        {
            if (value is not JsonObject payload || payload["__grids__"] is not JsonArray cells) continue;
            var grids = new List<CampaignGrid>();
            foreach (var cell in cells.OfType<JsonArray>())
            {
                string location = CampaignLocations.ToNode(cell[0]!.GetValue<int>(), cell[1]!.GetValue<int>());
                grids.Add(host.Grids.FirstOrDefault(item => item.Location == location)
                          ?? throw new NotSupportedException($"{step.Op} 的格子 {location} 不在当前地图状态里"));
            }
            return grids;
        }
        return null;
    }

    private static IReadOnlyList<CampaignGrid> RequireGrids(ICampaignPrimitiveHost host, CampaignPlanStep step) =>
        DecodeGrids(host, step) ?? throw new NotSupportedException(
            $"{step.Op} 的格子表实参在导出里不是 __grids__ 结构——需要导出器解析格子符号后才能执行");

    /// <summary>`clear_mechanism(grids=None)` 允许不传格子；传了但不是符号表时如实报错。</summary>
    private static IReadOnlyList<CampaignGrid>? OptionalGrids(ICampaignPrimitiveHost host, CampaignPlanStep step)
    {
        if (step.Args?.Positional is not { Count: > 0 }) return null;
        return DecodeGrids(host, step) ?? throw new NotSupportedException(
            $"{step.Op} 的格子表实参在导出里不是 __grids__ 结构——需要导出器解析格子符号后才能执行");
    }

    /// <summary>
    /// 解码路段实参：导出器把 `road_main = RoadGrids([[H3, B6, C5]])` 这类模块级路段解析成
    /// `{"__roads__": [路段, ...]}`，每个路段是若干 block，每个 block 是 `[x, y]` 坐标数组。
    /// </summary>
    private static IReadOnlyList<CampaignRoad> DecodeRoads(CampaignPlanStep step)
    {
        // 路段实参可能在位置参数（`clear_roadblocks([road_main])`），
        // 也可能在关键字参数（`fleet_2_step_on(step_on, roadblocks=[roadblocks_d4])`）。
        var candidates = new List<JsonNode?>(step.Args?.Positional ?? []);
        if (step.Args is { } args) candidates.AddRange(args.Keyword.Values);

        foreach (var value in candidates)
        {
            // 空数组：上游存在 `roadblocks=[]`（"这条路没有路障"），字面量导出就是 `[]`
            if (value is JsonArray empty && empty.Count == 0) return [];
            if (value is not JsonObject payload || !payload.ContainsKey("__roads__")) continue;
            if (payload["__roads__"] is not JsonArray roads) continue;
            var parsed = new List<CampaignRoad>();
            foreach (var road in roads)
            {
                if (road is not JsonArray blocks) continue;
                var parsedBlocks = new List<IReadOnlyList<string>>();
                foreach (var block in blocks)
                {
                    if (block is not JsonArray cells) continue;
                    parsedBlocks.Add(cells.OfType<JsonArray>()
                        .Select(cell => CampaignLocations.ToNode(cell[0]!.GetValue<int>(), cell[1]!.GetValue<int>()))
                        .ToArray());
                }
                parsed.Add(new CampaignRoad(parsedBlocks));
            }
            if (parsed.Count > 0) return parsed;
            // 空路段表也是合法输入（上游存在 `roadblocks=[]`），表示"这条路没有路障"
            return parsed;
        }
        throw new NotSupportedException(
            $"路段实参在导出里不是 __roads__ 结构（{step.Op}）——需要导出器解析 RoadGrids 后才能执行");
    }

    private static string DecodeFilter(CampaignPlanStep step)    {
        var positional = step.Args?.Positional ?? [];
        foreach (var value in positional)
        {
            if (value is JsonValue json && json.TryGetValue(out string? text) && text != "<expr>") return text;
        }
        throw new NotSupportedException(
            $"clear_filter_enemy 的过滤串在导出里是未求值表达式（{step.Op}）——需要导出器求值后才能执行");
    }

    private static int DecodePreserve(CampaignPlanStep step)
    {
        if (step.Args?.Keyword.TryGetValue("preserve", out var node) == true && node is JsonValue value
            && value.TryGetValue(out int preserve))
        {
            return preserve;
        }
        return 0;
    }
}
