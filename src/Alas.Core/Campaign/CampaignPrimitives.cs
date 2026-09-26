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
    bool ErrorHandleError = true,
    /// <summary>上游任务级配置 `Campaign_UseClearMode`（用户是否开启"快进/清图模式"）。</summary>
    bool CampaignUseClearMode = false,
    /// <summary>
    /// 上游 `map_has_clear_mode`：由 UI 识别得到（`AUTO_SEARCH.appear` / `CLEAR_MODE.appear`）。
    /// **离线拿不到**，所以用可空表示"未知"；`CampaignUseClearMode` 为假时用不到它。
    /// </summary>
    bool? MapHasClearMode = null)
{
    /// <summary>上游 <c>fleet_boss_index</c>：<c>FLEET_BOSS == 2 and FLEET_2</c> 时是 2，否则 1。</summary>
    public int FleetBossIndex => FleetBoss && Fleet2 ? 2 : 1;

    /// <summary>
    /// 上游 `self.map_is_clear_mode`（`module/handler/fast_forward.py:219-236`）：
    /// `map_has_clear_mode and config.Campaign_UseClearMode`。
    /// **只在没开快进时可用**：`Campaign_UseClearMode=True` 时上游还会把一批 `MAP_HAS_*` 关掉再跑，
    /// 那套覆盖属于运行准备阶段（P3 接线时对齐），在它对齐前执行器会拒绝执行而不是给一个不对的值。
    /// </summary>
    public bool? MapIsClearMode => CampaignUseClearMode ? null : false;
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
    /// 改一个格子的布尔标志（同步到**上游地图对象**）。上游有些被 C# 替换掉的方法会顺手写状态，
    /// 例如 `pick_up_flare` 的 `grid.is_flare = True`（影响 `Map.find_path` 的航点绕行）、
    /// `clear_bouncing_enemy` 成功后的 `may_bouncing_enemy = False`；不写回去就会与上游状态分叉。
    /// 白名单只允许模型里有的标志名，拼错直接报错，不静默当作没发生。
    /// </summary>
    /// <summary>
    /// 请求**结束本关**（上游钩子里 `raise CampaignEnd()` 的语义：`run()` 捕获后正常返回）。
    /// 与 <see cref="Withdraw"/> 不同：这是**控制流信号**，不点撤退、不发设备动作。
    /// </summary>
    /// <summary>
    /// 已清掉的神秘格子数（上游 `mystery_count`：地图初始化置 0，每清一个 +1）。
    /// 关卡里会读它（`campaign_8_2`：`if self.mystery_count < 1 and …`）。
    /// </summary>
    int MysteryCount { get; }

    void RequestCampaignEnd(string reason);

    void SetGridFlag(CampaignGrid grid, string flag, bool value);

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

    /// <summary>上游 <c>mystery_count</c>：清掉一个神秘格子就 +1。</summary>
    public int MysteryCount { get; private set; }

    public bool ClearChosenMystery(CampaignGrid grid)
    {
        MysteryCount++;
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

    public void RequestCampaignEnd(string reason)
    {
        EndRequested = true;
        EndReason = reason;
    }

    /// <summary>
    /// **诊断侧**记录：执行器实际调用过的原语名（覆盖率检查用它精确统计"夹具跑到了哪些原语"，
    /// 不再靠静态扫描夹具文本去猜）。生产路径不读它。
    /// </summary>
    public List<string> InvokedOps { get; } = new();

    public void SetGridFlag(CampaignGrid grid, string flag, bool value)
    {
        // 只改模型，不产生设备动作（上游那几个 helper 也是直接改 `GridInfo`）。
        // 设备宿主额外把它同步到上游地图对象（见那边）。
        Actions.Add($"set_flag({grid.Location},{flag}={value})");
        for (int i = 0; i < _grids.Count; i++)
        {
            if (_grids[i].Location == grid.Location) _grids[i] = CampaignPrimitives.ApplyFlag(_grids[i], flag, value);
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
    internal static void RecordInvocation(ICampaignPrimitiveHost host, string method)
    {
        if (host is RecordingCampaignHost recorder) recorder.InvokedOps.Add(method);
    }

    /// <summary>按标志名写格子；名字不在模型里就报错（拼错不许静默）。</summary>
    internal static CampaignGrid ApplyFlag(CampaignGrid grid, string flag, bool value) => flag switch
    {
        "is_flare" => grid with { IsFlare = value },
        "may_bouncing_enemy" => grid with { MayBouncingEnemy = value },
        "is_caught_by_siren" => grid with { IsCaughtBySiren = value },
        "is_cleared" => grid with { IsCleared = value },
        "is_enemy" => grid with { IsEnemy = value },
        // 识别提示类（上游 `for grid in self.map: grid.may_siren = True` 这种整图设置用）
        "may_siren" => grid with { MaySiren = value },
        "may_enemy" => grid with { MayEnemy = value },
        "may_boss" => grid with { MayBoss = value },
        "may_mystery" => grid with { MayMystery = value },
        "may_ambush" => grid with { MayAmbush = value },
        "may_ammo" => grid with { MayAmmo = value },
        "is_spawn_point" => grid with { IsSpawnPoint = value },
        "is_submarine_spawn_point" => grid with { IsSubmarineSpawnPoint = value },
        _ => throw new NotSupportedException($"未知的格子标志 {flag}（不在模型里；要同步新标志时在 ApplyFlag 里显式加）"),
    };

    /// <summary>
    /// 上游 `CampaignMap.select(**kwargs)`：按属性筛格子（`is_boss=True` 这类）。
    /// 判定与上游 `SelectedGrids.select` 一致：**类型相同且值相等**才算命中。
    /// 布尔、整数和字符串保持原值；未迁移的属性和容器类型显式报错。
    /// </summary>
    public static CampaignGridSet MapSelect(ICampaignPrimitiveHost host, CampaignPlanStep step)
    {
        RecordInvocation(host, "map.select");
        if (step.Args?.Positional.Count > 0)
            throw new NotSupportedException("map.select 只接受属性关键字参数");
        var predicates = new List<(Func<CampaignGrid, object?> Read, object? Expected)>();
        foreach (var (key, value) in step.Args?.Keyword ?? new Dictionary<string, JsonNode?>())
        {
            object? expected;
            if (value is null) expected = null;
            else if (value is JsonValue json && json.TryGetValue<bool>(out bool flag)) expected = flag;
            else if (value is JsonValue number && number.TryGetValue<int>(out int integer)) expected = integer;
            else if (value is JsonValue text && text.TryGetValue<string>(out string? stringValue)) expected = stringValue;
            else throw new NotSupportedException($"map.select 的 {key} 实参类型未迁移");
            predicates.Add((GridAttribute(key), expected));
        }
        return new CampaignGridSet(host.Grids.Where(grid => predicates.All(predicate =>
        {
            object? actual = predicate.Read(grid);
            return actual?.GetType() == predicate.Expected?.GetType() && Equals(actual, predicate.Expected);
        })));
    }

    private static Func<CampaignGrid, object?> GridAttribute(string key) => key switch
    {
        "is_enemy" => grid => grid.IsEnemy,
        "is_boss" => grid => grid.IsBoss,
        "is_siren" => grid => grid.IsSiren,
        "is_fortress" => grid => grid.IsFortress,
        "is_mystery" => grid => grid.IsMystery,
        "is_ammo" => grid => grid.IsAmmo,
        "is_fleet" => grid => grid.IsFleet,
        "is_current_fleet" => grid => grid.IsCurrentFleet,
        "is_submarine" => grid => grid.IsSubmarine,
        "is_flare" => grid => grid.IsFlare,
        "is_cleared" => grid => grid.IsCleared,
        "is_caught_by_siren" => grid => grid.IsCaughtBySiren,
        "is_land" => grid => grid.IsLand,
        "is_spawn_point" => grid => grid.IsSpawnPoint,
        "is_submarine_spawn_point" => grid => grid.IsSubmarineSpawnPoint,
        "is_sea" => grid => grid.IsSea,
        "is_mechanism_block" => grid => grid.IsMechanismBlock,
        "is_mechanism_trigger" => grid => grid.IsMechanismTrigger,
        "is_missile_attack" => grid => grid.IsMissileAttack,
        "may_enemy" => grid => grid.MayEnemy,
        "may_boss" => grid => grid.MayBoss,
        "may_siren" => grid => grid.MaySiren,
        "may_mystery" => grid => grid.MayMystery,
        "may_ammo" => grid => grid.MayAmmo,
        "may_ambush" => grid => grid.MayAmbush,
        "may_bouncing_enemy" => grid => grid.MayBouncingEnemy,
        "is_accessible" => grid => grid.IsAccessible,
        "is_accessible_1" => grid => grid.IsAccessible1,
        "is_accessible_2" => grid => grid.IsAccessible2,
        "is_nearby" => grid => grid.IsNearby,
        "enemy_scale" => grid => grid.EnemyScale,
        "enemy_genre" => grid => grid.EnemyGenre,
        "weight" => grid => grid.Weight,
        "cost" => grid => grid.Cost,
        "cost_1" => grid => grid.Cost1,
        "cost_2" => grid => grid.Cost2,
        _ => throw new NotSupportedException($"map.select 的键 {key} 不在已迁移属性中"),
    };

    /// <summary>同上，但直接吃"标志 → 真假"的字典（诊断命令与对拍用）。</summary>
    public static CampaignGridSet MapSelect(ICampaignPrimitiveHost host, IReadOnlyDictionary<string, bool> flags)
        => MapSelect(host, new CampaignPlanStep
        {
            Kind = "call",
            Op = "map.select",
            Args = new CampaignPlanStepArgs
            {
                Keyword = flags.ToDictionary(pair => pair.Key, pair => (JsonNode?)JsonValue.Create(pair.Value)),
            },
        });

    /// <summary>干跑时的循环上限，避免"清不完的神秘格子"把进程拖死。</summary>
    private const int MaxMysteryRounds = 100;

    /// <summary>上游 <c>Map.clear_enemy(**kwargs)</c>。</summary>
    public static bool ClearEnemy(ICampaignPrimitiveHost host, CampaignTargetOptions? options = null)
    {
        RecordInvocation(host, "clear_enemy");
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
        RecordInvocation(host, "clear_any_enemy");
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
        RecordInvocation(host, "clear_siren");
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
        RecordInvocation(host, "clear_boss");
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
        RecordInvocation(host, "clear_potential_boss");
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
        RecordInvocation(host, "clear_roadblocks");
        var all = new CampaignGridSet(host.Grids);
        var grids = CampaignGridSet.Empty;
        foreach (var road in roads) grids = grids.Add(road.Roadblocks(all));
        return ClearRoadblockTargets(host, grids, options, "clear_roadblocks");
    }

    /// <summary>上游 <c>Map.clear_potential_roadblocks(roads, **kwargs)</c>：避免"只剩一格空"的路障。</summary>
    public static bool ClearPotentialRoadblocks(ICampaignPrimitiveHost host, IReadOnlyList<CampaignRoad> roads,
                                                CampaignTargetOptions? options = null)
    {
        RecordInvocation(host, "clear_potential_roadblocks");
        var all = new CampaignGridSet(host.Grids);
        var grids = CampaignGridSet.Empty;
        foreach (var road in roads) grids = grids.Add(road.PotentialRoadblocks(all));
        return ClearRoadblockTargets(host, grids, options, "clear_potential_roadblocks");
    }

    /// <summary>上游 <c>Map.clear_first_roadblocks(roads, **kwargs)</c>：保证每个路障块都有一个已清格子。</summary>
    public static bool ClearFirstRoadblocks(ICampaignPrimitiveHost host, IReadOnlyList<CampaignRoad> roads,
                                            CampaignTargetOptions? options = null)
    {
        RecordInvocation(host, "clear_first_roadblocks");
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
        RecordInvocation(host, "pick_up_ammo");
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
        RecordInvocation(host, "pick_up_light_house");
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
        RecordInvocation(host, "pick_up_flare");
        // 上游第一行就是 `grid.is_flare = True`（在"已拾取/可达"判断**之前**），照抄顺序
        host.SetGridFlag(grid, "is_flare", true);
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
        RecordInvocation(host, "capture_clear_boss");
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
        RecordInvocation(host, "fleet_2_push_forward");
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
        RecordInvocation(host, "fleet_2_protect");
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
        RecordInvocation(host, "clear_chosen_enemy");
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
        RecordInvocation(host, "switch_to");
        host.Log($"switch_to：上游 Fleet.switch_to() 是 pass（切舰队已由 {fleet} 前缀完成）");
        return false;
    }

    /// <summary>
    /// 上游关卡基类里的 <c>clear_map_items(grids)</c>（如 `campaign/event_20221124_cn/campaign_base.py`）：
    /// 按 <c>cost</c> 升序逐个走过去；上游没有 return（落到末尾为假）。
    /// </summary>
    public static bool ClearMapItems(ICampaignPrimitiveHost host, IReadOnlyList<CampaignGrid> grids)
    {
        RecordInvocation(host, "clear_map_items");
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
        RecordInvocation(host, "clear_mechanism");
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
        RecordInvocation(host, "brute_clear_boss");
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
        RecordInvocation(host, "brute_fleet_meet");
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
        RecordInvocation(host, "fleet_2_rescue");
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

    /// <summary>
    /// 上游 <c>Fleet.check_accessibility(grid, fleet=None)</c>：格子对**指定舰队**是否可达。
    /// `fleet` 为空 → 直接看当前成本场；`'boss'` → 用 `fleet_boss_index`；数字串/数字 → 该舰队。
    /// 上游实现里"切舰队 → `find_path_initial()` → 看 `is_accessible` → 切回 → 再 `find_path_initial()`"
    /// 全是状态操作、没有设备动作；C# 用无状态的 `LiveCostFor`（按舰队取现成的场，缺了就按上游口径现算）
    /// 得到同一结果，因此**不需要**切回的模拟。
    /// </summary>
    public static bool CheckAccessibility(ICampaignPrimitiveHost host, CampaignGrid grid, string? fleet = null)
    {
        RecordInvocation(host, "check_accessibility");
        if (string.IsNullOrEmpty(fleet)) return grid.IsAccessible;
        int index = fleet == "boss"
            ? host.Config.FleetBossIndex
            : int.TryParse(fleet, out int parsed) ? parsed : host.FleetCurrentIndex;
        if (index == host.FleetCurrentIndex) return grid.IsAccessible;
        return LiveCostFor(host, grid, index) < CampaignPathfinder.Unreachable;
    }

    /// <summary>
    /// 上游 <c>Fleet.fleet_at(grid, fleet=None)</c>：舰队是不是就在这一格。
    /// `fleet` 为空看当前舰队；1/2 看对应舰队。纯状态判断，没有设备动作。
    /// </summary>
    public static bool FleetAt(ICampaignPrimitiveHost host, CampaignGrid grid, string? fleet = null)
    {
        RecordInvocation(host, "fleet_at");
        string location = fleet switch
        {
            "1" => host.Fleet1Location,
            "2" => host.Fleet2Location,
            null or "" => host.FleetCurrentIndex == 2 ? host.Fleet2Location : host.Fleet1Location,
            _ => throw new NotSupportedException($"fleet_at 的 fleet 只支持 1/2/空，收到 {fleet}"),
        };
        return string.Equals(location, grid.Location, StringComparison.Ordinal);
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
        RecordInvocation(host, "clear_bouncing_enemy");
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
        RecordInvocation(host, "fleet_2_step_on");
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
        RecordInvocation(host, "fleet_2_break_siren_caught");
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
        RecordInvocation(host, "battle_boss");
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
        RecordInvocation(host, "battle_with_poor_map_data");
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
        RecordInvocation(host, "clear_all");
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
        RecordInvocation(host, "handle_boss_appear_refocus");
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
        RecordInvocation(host, "battle_default");
        if (ClearEnemy(host)) return true;
        host.Log("battle_default：No battle executed.");
        return false;
    }

    /// <summary>上游 <c>Map.clear_all_mystery(**kwargs)</c>：恒返回假。</summary>
    public static bool ClearAllMystery(ICampaignPrimitiveHost host, CampaignTargetOptions? options = null)
    {
        RecordInvocation(host, "clear_all_mystery");
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
        RecordInvocation(host, "clear_filter_enemy");
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
/// 执行器保留类型交给关卡循环处理：CampaignEnd 展开整个调用栈；MapEnemyMoved 按战斗计数重试。
/// 单钩子诊断仍保留信号原因，不能用这类信号推断成功结算。
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

    /// <summary>显式 return 与分支体自然结束必须区分，None 也会终止整个钩子。</summary>
    public bool DidReturn { get; init; }

    /// <summary>上游异常控制流；不得用错误消息的子串推断信号类型。</summary>
    public string? Signal { get; init; }
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
    /// <summary>
    /// 求 `branch` 的条件：局部变量真假，或一次原语调用的真假（`negate` 取反）。
    /// 取不到（变量没绑过、原语没实现、实参解析不了）就返回 <c>null</c> + 原因，由调用方**阻塞报出**，
    /// 不猜一个真假继续往下跑。
    /// </summary>
    private static (bool? Value, string Why) EvaluateBranch(CampaignPlanStep step,
                                                            ICampaignPrimitiveHost host,
                                                            Dictionary<string, object?> env,
                                                            Dictionary<string, object?> state,
                                                            CampaignPlan plan, int depth)
    {
        var test = step.Test;
        if (test is null) return (null, "branch 没有 test 字段");
        return EvaluateTest(test, host, env, state, plan, depth);
    }

    /// <summary>求一个条件（`and`/`or` 复合在此递归；`negate` 在叶子最后取反）。</summary>
    private static (bool? Value, string Why) EvaluateTest(CampaignPlanStepTest test,
                                                         ICampaignPrimitiveHost host,
                                                         Dictionary<string, object?> env,
                                                         Dictionary<string, object?> state,
                                                         CampaignPlan plan, int depth)
    {
        bool value;
        string why;
        if (test.Local is { Length: > 0 } name)
        {
            if (!env.TryGetValue(name, out var bound)) return (null, $"局部变量 {name} 没有绑定值");
            value = Truthy(bound);
            why = $"局部变量 {name} = {Describe(bound)}";
        }
        else if (test.BattleCount is { } countTest && countTest["op"] is { } opNode
                 && countTest["value"] is { } valueNode
                 && opNode.GetValue<string>() is { } compare
                 && valueNode.GetValue<int>() is int threshold)
        {
            // `self.battle_count >= 3` 这类状态比较：左侧就是宿主的 BattleCount
            int count = host.BattleCount;
            value = compare switch
            {
                ">=" => count >= threshold,
                ">" => count > threshold,
                "<=" => count <= threshold,
                "<" => count < threshold,
                "==" => count == threshold,
                "!=" => count != threshold,
                _ => throw new NotSupportedException($"不支持的 battle_count 比较 {compare}"),
            };
            why = $"battle_count({count}) {compare} {threshold}";
        }
        else if (test.BattleCountIn is { } allowed)
        {
            value = allowed.Contains(host.BattleCount);
            why = $"battle_count({host.BattleCount}) in {string.Join(",", allowed)}";
        }
        else if (test.Expr is { } expression)
        {
            // `if self.<属性>:` —— 取一个值表达式的真假（见 EvaluateExpr 的白名单）
            var (resolved, resolvedWhy) = EvaluateExpr(expression, host, state, env, plan, depth);
            if (resolved is null) return (null, resolvedWhy);
            value = Truthy(resolved);
            why = $"{resolvedWhy} → {value}";
        }
        else if (test.Runtime is { Length: > 0 } runtime)
        {
            // 运行期标志。语义见 `CampaignRuntimeConfig.MapIsClearMode`：默认（没开快进）**确定**为假；
            // 开了快进但还没识别到 `map_has_clear_mode` 时**停下报原因**——猜一个真假会让分支走错。
            if (runtime != "map_is_clear_mode")
                return (null, $"未知的运行期标志 {runtime}");
            if (host.Config.CampaignUseClearMode)
            {
                // 保真缺口如实挡住：上游开启快进时 `handle_fast_forward` 还会把一批 `MAP_HAS_*`
                // （ambush / movable enemy / portal / fortress / bouncing …）改成 False 再跑，
                // C# 侧尚未对齐这套覆盖 —— 给一个"忽略覆盖"的取值会让分支走错，所以拒绝执行。
                return (null, "map_is_clear_mode：Campaign_UseClearMode 已开，但上游那批 "
                              + "MAP_HAS_* 覆盖还没在 C# 侧对齐，拒绝执行");
            }
            value = false;   // 没开快进：上游 `handle_fast_forward` 直接置假
            why = "runtime.map_is_clear_mode = False（Campaign_UseClearMode=False）";
        }
        else if (test.Grid is { } gridNode && test.Attribute is { Length: > 0 } attribute)
        {
            // `<GRID>.is_xxx`：从**当前地图状态**取那个格子，再看属性（白名单外显式报错）
            string location = GridLocationOf(gridNode);
            if (location.Length == 0) return (null, "格子条件里的格子实参解不出位置");
            var grid = host.Grids.FirstOrDefault(item => item.Location == location);
            if (grid is null) return (null, $"条件里的格子 {location} 不在当前地图状态里");
            value = attribute switch
            {
                "is_accessible" => grid.IsAccessible,
                "is_enemy" => grid.IsEnemy,
                "is_boss" => grid.IsBoss,
                "is_siren" => grid.IsSiren,
                "is_fortress" => grid.IsFortress,
                "is_mystery" => grid.IsMystery,
                "is_ammo" => grid.IsAmmo,
                "is_land" => grid.IsLand,
                "is_cleared" => grid.IsCleared,
                "is_fleet" => grid.IsFleet,
                "may_enemy" => grid.MayEnemy,
                "may_boss" => grid.MayBoss,
                "may_siren" => grid.MaySiren,
                "may_ambush" => grid.MayAmbush,
                _ => throw new NotSupportedException($"branch 条件里的格子属性 {attribute} 还没映射"),
            };
            why = $"{location}.{attribute} = {value}";
        }
        else if (test.Config is { Length: > 0 } configKey)
        {
            // `self.config.<KEY>`：映射到 `CampaignRuntimeConfig` 的字段。没映射的键**显式报错**——
            // 猜一个默认值会让分支走错，比停下来更糟。
            object? configValue = configKey switch
            {
                "MAP_HAS_MOVABLE_ENEMY" => host.Config.MapHasMovableEnemy,
                "MAP_HAS_MOVABLE_NORMAL_ENEMY" => host.Config.MapHasMovableNormalEnemy,
                "MAP_CLEAR_ALL_THIS_TIME" => host.Config.MapClearAllThisTime,
                // 上游 `FLEET_BOSS` 是**整数** 1/2；C# 配置里存的是 `== 2` 的布尔，
                // 这里按同一编码还原（比较条件要的是整数，不能拿布尔硬比）
                "FLEET_BOSS" => host.Config.FleetBoss ? 2 : 1,
                "FLEET_2" => host.Config.Fleet2,
                "MAP_HAS_SIREN" => host.Config.MapHasSiren,
                "MAP_HAS_FORTRESS" => host.Config.MapHasFortress,
                "MAP_HAS_AMBUSH" => host.Config.MapHasAmbush,
                "MAP_HAS_BOUNCING_ENEMY" => host.Config.MapHasBouncingEnemy,
                _ => throw new NotSupportedException(
                    $"branch 条件里的 config 键 {configKey} 还没有映射到 CampaignRuntimeConfig"),
            };
            value = Truthy(configValue);
            why = $"config.{configKey} = {configValue}";
        }
        else if (test.Call is { } call)
        {
            var asStep = new CampaignPlanStep { Kind = "call", Op = call.Op, Args = call.Args };
            CampaignPlanStep resolved;
            try
            {
                resolved = SubstituteLocals(asStep, env);
            }
            catch (NotSupportedException error)
            {
                return (null, error.Message);
            }
            try
            {
                value = Truthy(Invoke(plan, resolved, host, state, depth));
            }
            catch (NotSupportedException error)
            {
                return (null, error.Message);
            }
            why = $"{call.Op} → {value}";
        }
        else
        {
            return (null, "branch 的 test 既不是局部变量也不是原语调用");
        }
        return (test.Negate ? !value : value, why);
    }

    /// <summary>
    /// 条件里的格子实参 → 位置名。支持两种形状：`{"__grid__": [x, y]}`（数组，导出器给的原样）
    /// 与 `{"location": "C1"}`。**别的形状一律返回空串**，由调用方报"解不出位置"，
    /// 不猜格子（实测踩过：`__grid__` 是数组，用只认 `location` 的解码会解出空串）。
    /// </summary>
    private static string GridLocationOf(JsonNode? node)
    {
        if (node is not JsonObject payload) return "";
        if (payload["__grid__"] is JsonArray cell && cell.Count >= 2
            && cell[0] is JsonValue xNode && xNode.TryGetValue<int>(out int x)
            && cell[1] is JsonValue yNode && yNode.TryGetValue<int>(out int y))
        {
            return CampaignLocations.TryToNode(x, y, out string location) ? location : "";
        }
        if (payload["location"] is JsonValue value && value.TryGetValue<string>(out string? text))
        {
            return text ?? "";
        }
        return "";
    }

    /// <summary>
    /// 求一个**值表达式**（导出器 `_state_expression` 的对应实现）。白名单：
    /// `{"literal": …}` / `{"state": 名字}` / `{"config": 键}` / `{"runtime": "map_is_clear_mode"}` /
    /// `{"not": …}` / `{"and": […]}` / `{"or": […]}`。
    /// **缺初值的属性、没映射的配置键一律返回 null + 原因**（调用方阻塞报出），不猜值。
    /// </summary>
    private static (object? Value, string Why) EvaluateExpr(JsonNode? expr,
                                                            ICampaignPrimitiveHost host,
                                                            Dictionary<string, object?> state,
                                                            Dictionary<string, object?> env,
                                                            CampaignPlan plan, int depth)
    {
        if (expr is not JsonObject node) return (null, "值表达式不是对象");

        if (node.ContainsKey("literal"))
        {
            // 注意 `{"literal": null}`（上游 `ignore = None`）：JSON null 用 `is { }` 匹配不到，
            // 必须按 ContainsKey 判断，否则会掉到"形态不在白名单里"（实测踩过）。
            if (node["literal"] is not { } literal) return (PythonNone, "literal None");
            if (literal is JsonValue value)
            {
                if (value.TryGetValue<bool>(out bool flag)) return (flag, $"literal {flag}");
                if (value.TryGetValue<int>(out int number)) return (number, $"literal {number}");
                if (value.TryGetValue<string>(out string? text)) return (text, $"literal '{text}'");
            }
            return (null, "literal 不是布尔/整数/字符串");
        }
        if (node["state"] is JsonValue stateNode && stateNode.TryGetValue<string>(out string? name))
        {
            if (!state.TryGetValue(name!, out var bound))
            {
                return (null, $"实例属性 self.{name} 没有初值也没有被赋值过（不猜，按阻塞处理）");
            }
            return (bound, $"self.{name} = {Describe(bound)}");
        }
        if (node["config"] is JsonValue configNode && configNode.TryGetValue<string>(out string? key))
        {
            object? resolved = key switch
            {
                "MAP_HAS_MOVABLE_ENEMY" => host.Config.MapHasMovableEnemy,
                "MAP_HAS_MOVABLE_NORMAL_ENEMY" => host.Config.MapHasMovableNormalEnemy,
                "MAP_CLEAR_ALL_THIS_TIME" => host.Config.MapClearAllThisTime,
                // 上游 `FLEET_BOSS` 是**整数** 1/2；C# 配置里存的是 `== 2` 的布尔，
                // 这里按同一编码还原（比较条件要的是整数，不能拿布尔硬比）
                "FLEET_BOSS" => host.Config.FleetBoss ? 2 : 1,
                "FLEET_2" => host.Config.Fleet2,
                "MAP_HAS_SIREN" => host.Config.MapHasSiren,
                "MAP_HAS_FORTRESS" => host.Config.MapHasFortress,
                "MAP_HAS_AMBUSH" => host.Config.MapHasAmbush,
                "MAP_HAS_BOUNCING_ENEMY" => host.Config.MapHasBouncingEnemy,
                _ => null,
            };
            if (resolved is null) return (null, $"值表达式里的 config 键 {key} 还没有映射");
            return (resolved, $"config.{key} = {resolved}");
        }
        if (node["call"] is JsonObject callNode && callNode["op"] is JsonValue callOp
            && callOp.TryGetValue<string>(out string? callName))
        {
            // 调用作为值：调一次原语，拿它的真假继续求值（条件里 `self.fleet_at(A3, fleet=2) and …`）
            var callStep = new CampaignPlanStep { Kind = "call", Op = callName, Args = null };
            if (callNode["args"] is { } callArgs)
            {
                callStep = new CampaignPlanStep
                {
                    Kind = "call",
                    Op = callName,
                    Args = System.Text.Json.JsonSerializer.Deserialize<CampaignPlanStepArgs>(callArgs.ToJsonString()),
                };
            }
            CampaignPlanStep resolvedStep;
            try
            {
                resolvedStep = SubstituteLocals(callStep, env);
            }
            catch (NotSupportedException error)
            {
                return (null, error.Message);
            }
            try
            {
                object callResult = Invoke(plan, resolvedStep, host, state, depth) ?? PythonNone;
                return (callResult, $"{callName} → {Describe(callResult)}");
            }
            catch (NotSupportedException error)
            {
                return (null, error.Message);
            }
        }
        if (node["grids"] is JsonArray cells)
        {
            // `SelectedGrids([A2, H3])`：显式格列表 → 格子集合
            var collected = new List<CampaignGrid>();
            foreach (var cell in cells)
            {
                string location = GridLocationOf(cell);
                if (location.Length == 0) return (null, "grids 值表达式里有解不出位置的格子");
                var grid = host.Grids.FirstOrDefault(item => item.Location == location);
                if (grid is null) return (null, $"值表达式里的格子 {location} 不在当前地图状态里");
                collected.Add(grid);
            }
            return (new CampaignGridSet(collected), $"SelectedGrids([{string.Join(",", collected.Select(g => g.Location))}])");
        }
        if (node["local_index"] is JsonObject offset && offset["name"] is JsonValue localNameNode
            && localNameNode.TryGetValue<string>(out string? localList)
            && offset["index"] is JsonValue indexNode2 && indexNode2.TryGetValue<int>(out int offsetIndex))
        {
            // `boss = boss[0]`：局部集合取下标
            if (!env.TryGetValue(localList!, out var bound) || bound is not CampaignGridSet list || list.IsEmpty)
            {
                return (null, $"局部名 {localList} 不是可用的格子集合（取下标失败）");
            }
            int normalizedIndex = offsetIndex < 0 ? list.Count + offsetIndex : offsetIndex;
            if (normalizedIndex < 0 || normalizedIndex >= list.Count)
            {
                return (null, $"局部名 {localList} 的下标 {offsetIndex} 越界（共 {list.Count} 格）");
            }
            return (list[normalizedIndex], $"{localList}[{offsetIndex}] = {list[normalizedIndex].Location}");
        }
        if (node["local"] is JsonValue localValueNode && localValueNode.TryGetValue<string>(out string? localRef))
        {
            if (!env.TryGetValue(localRef!, out var boundValue))
            {
                return (null, $"局部名 {localRef} 没有绑定值");
            }
            return (boundValue, $"{localRef} = {Describe(boundValue)}");
        }
        if (node["grid"] is { } gridValueNode)
        {
            string location = GridLocationOf(gridValueNode);
            var grid = host.Grids.FirstOrDefault(item => item.Location == location);
            if (grid is null) return (null, $"值表达式里的格子 {location} 不在当前地图状态里");
            return (grid, location);
        }
        if (node["host_value"] is JsonValue hostNode && hostNode.TryGetValue<string>(out string? hostName))
        {
            return hostName switch
            {
                "battle_count" => (host.BattleCount, $"host.battle_count = {host.BattleCount}"),
                "mystery_count" => (host.MysteryCount, $"host.mystery_count = {host.MysteryCount}"),
                _ => (null, $"未知的宿主值 {hostName}"),
            };
        }
        if (node["grid_attr"] is JsonObject gridAttr && gridAttr["name"] is JsonValue attrName
            && attrName.TryGetValue<string>(out string? attribute) && gridAttr["grid"] is { } gridNode2)
        {
            string location = GridLocationOf(gridNode2);
            var grid = host.Grids.FirstOrDefault(item => item.Location == location);
            if (grid is null) return (null, $"值表达式里的格子 {location} 不在当前地图状态里");
            object? read = attribute switch
            {
                "enemy_scale" => grid.EnemyScale,
                "weight" => grid.Weight,
                "cost" => grid.Cost,
                "cost_1" => grid.Cost1,
                "cost_2" => grid.Cost2,
                "enemy_genre" => grid.EnemyGenre,
                "is_accessible" => grid.IsAccessible,
                "is_enemy" => grid.IsEnemy,
                "is_boss" => grid.IsBoss,
                "is_siren" => grid.IsSiren,
                "is_fortress" => grid.IsFortress,
                "is_mystery" => grid.IsMystery,
                "is_ammo" => grid.IsAmmo,
                "is_land" => grid.IsLand,
                "is_cleared" => grid.IsCleared,
                "is_fleet" => grid.IsFleet,
                "may_enemy" => grid.MayEnemy,
                "may_boss" => grid.MayBoss,
                "may_siren" => grid.MaySiren,
                "may_ambush" => grid.MayAmbush,
                _ => null,
            };
            if (read is null) return (null, $"值表达式里的格子属性 {attribute} 还没映射");
            return (read, $"{location}.{attribute} = {read}");
        }
        if (node["compare"] is JsonObject compare && compare["op"] is JsonValue opValue
            && opValue.TryGetValue<string>(out string? compareOp))
        {
            var (left, leftWhy) = EvaluateExpr(compare["left"], host, state, env, plan, depth);
            if (left is null) return (null, leftWhy);
            var (right, rightWhy) = EvaluateExpr(compare["right"], host, state, env, plan, depth);
            if (right is null) return (null, rightWhy);
            // 只做**整数**比较（上游这些条件都是整数/枚举比较）；类型不对就报错，不做隐式转换
            // 格子比较按位置（`boss == A1`）
            if (left is CampaignGrid leftGrid && right is CampaignGrid rightGrid)
            {
                bool same = string.Equals(leftGrid.Location, rightGrid.Location, StringComparison.Ordinal);
                bool gridResult = compareOp switch
                {
                    "==" => same,
                    "!=" => !same,
                    _ => throw new NotSupportedException($"格子只支持 == / != 比较，收到 {compareOp}"),
                };
                return (gridResult, $"{leftWhy} {compareOp} {rightWhy} → {gridResult}");
            }
            // Python bool 是 int 的子类；None/字符串也允许等值比较。
            object numericLeft = left is bool leftFlag ? (leftFlag ? 1 : 0) : left;
            object numericRight = right is bool rightFlag ? (rightFlag ? 1 : 0) : right;
            if (compareOp is "==" or "!=")
            {
                bool same = Equals(numericLeft, numericRight);
                return (compareOp == "==" ? same : !same, $"{leftWhy} {compareOp} {rightWhy}");
            }
            if (numericLeft is not int leftNumber || numericRight is not int rightNumber)
            {
                return (null, $"比较 {leftWhy} {compareOp} {rightWhy} 不是整数，无法比较");
            }
            bool result = compareOp switch
            {
                ">=" => leftNumber >= rightNumber,
                ">" => leftNumber > rightNumber,
                "<=" => leftNumber <= rightNumber,
                "<" => leftNumber < rightNumber,
                "==" => leftNumber == rightNumber,
                "!=" => leftNumber != rightNumber,
                _ => throw new NotSupportedException($"不支持的比较运算符 {compareOp}"),
            };
            return (result, $"{leftWhy} {compareOp} {rightWhy} → {result}");
        }
        if (node["runtime"] is JsonValue runtimeNode && runtimeNode.TryGetValue<string>(out string? flag2))
        {
            if (flag2 != "map_is_clear_mode") return (null, $"未知的运行期标志 {flag2}");
            if (host.Config.MapIsClearMode is not { } clearMode)
            {
                return (null, "map_is_clear_mode 未知：Campaign_UseClearMode 已开，"
                              + "但上游那批 MAP_HAS_* 覆盖还没在 C# 侧对齐");
            }
            return (clearMode, $"runtime.map_is_clear_mode = {clearMode}");
        }
        if (node["not"] is { } inner)
        {
            var (value, why) = EvaluateExpr(inner, host, state, env, plan, depth);
            if (value is null) return (null, why);
            return (!Truthy(value), $"not({why})");
        }
        foreach (var (operatorName, isAnd) in new[] { ("and", true), ("or", false) })
        {
            if (node[operatorName] is not JsonArray items || items.Count == 0) continue;
            var parts = new List<string>();
            object? lastValue = null;
            foreach (var item in items)
            {
                var (value, why) = EvaluateExpr(item, host, state, env, plan, depth);
                if (value is null) return (null, why);
                bool truth = Truthy(value);
                lastValue = value;
                parts.Add($"{why}={truth}");
                // 短路：与 Python 的 `and`/`or` 一致
                if (isAnd && !truth) return (value, $"{operatorName}({string.Join(", ", parts)})");
                if (!isAnd && truth) return (value, $"{operatorName}({string.Join(", ", parts)})");
            }
            return (lastValue, $"{operatorName}({string.Join(", ", parts)})");
        }
        return (null, "值表达式的形态不在白名单里");
    }

    /// <summary>Python 真值：None、空字符串、零和空格子集合为假。</summary>
    private static bool Truthy(object? value) => value switch
    {
        null => false,
        _ when ReferenceEquals(value, PythonNone) => false,   // Python None 为假
        bool flag => flag,
        // Python 的真假：整数 0 为假
        int number => number != 0,
        string text => text.Length != 0,
        CampaignGridSet set => !set.IsEmpty,
        _ => true,
    };

    /// <summary>
    /// Python `None` 的表示。求值失败用 `null` 返回，**不能**用它表示 `None`
    /// （否则 `ignore = None` 会被当成"求值失败"而阻塞）。
    /// </summary>
    private static readonly object PythonNone = new();

    /// <summary>当前计划返回合同只支持 bool/None；新增标量必须先扩展整个返回链。</summary>
    private static bool? HookReturn(object? value) => value switch
    {
        null => null,
        _ when ReferenceEquals(value, PythonNone) => null,
        bool flag => flag,
        _ => throw new NotSupportedException($"钩子返回类型 {value.GetType().Name} 未迁移；拒绝压缩为真值"),
    };

    private static string Describe(object? value) => value switch
    {
        null => "null",
        _ when ReferenceEquals(value, PythonNone) => "None",
        bool flag => flag ? "真" : "假",
        CampaignGridSet set => $"{set.Count} 格",
        _ => value.ToString() ?? "?",
    };

    /// <summary>
    /// 把步骤实参里的**局部变量引用**（`{"__local__": "boss", "__index__": 0}`）替换成具体的
    /// `{"__grid__": [x, y]}` —— 后者是既有解码器认的形式，这样原语侧一行都不用改。
    /// 变量没绑过、下标越界、或局部不是格子集合（当实参用不了）都抛 <see cref="NotSupportedException"/>。
    /// </summary>
    private static CampaignPlanStep SubstituteLocals(CampaignPlanStep step, Dictionary<string, object?> env)
    {
        if (step.Args is null) return step;
        bool changed = false;

        JsonArray Coordinates(CampaignGrid grid)
        {
            if (!CampaignLocations.TryParse(grid.Location, out int x, out int y))
                throw new NotSupportedException($"格子 {grid.Location} 解析不出坐标");
            return new JsonArray(x, y);
        }

        JsonNode? Encode(object? bound) => bound switch
        {
            null => null,
            _ when ReferenceEquals(bound, PythonNone) => null,
            CampaignGrid grid => new JsonObject { ["__grid__"] = Coordinates(grid) },
            CampaignGridSet set => new JsonObject
            {
                ["__grids__"] = new JsonArray(set.Grids.Select(grid => (JsonNode)Coordinates(grid)).ToArray()),
            },
            bool value => JsonValue.Create(value),
            int value => JsonValue.Create(value),
            string value => JsonValue.Create(value),
            JsonNode value => value.DeepClone(),
            _ => throw new NotSupportedException($"局部参数类型 {bound.GetType().Name} 未支持"),
        };

        JsonNode? Replace(JsonNode? node)
        {
            if (node is not JsonObject payload
                || (payload["__local__"] ?? payload["__local_grids__"] ?? payload["__param__"]) is not JsonValue nameNode
                || !nameNode.TryGetValue<string>(out string? name))
            {
                return node switch
                {
                    JsonArray values => new JsonArray(values.Select(Replace).Select(value => value?.DeepClone()).ToArray()),
                    JsonObject values => new JsonObject(values.Select(pair =>
                        KeyValuePair.Create(pair.Key, Replace(pair.Value)?.DeepClone()))),
                    _ => node,
                };
            }
            if (!env.TryGetValue(name, out var bound))
            {
                throw new NotSupportedException($"局部变量 {name} 没有绑定值");
            }
            if (payload.ContainsKey("__index__"))
            {
                if (bound is not CampaignGridSet set || payload["__index__"] is not JsonValue indexNode
                    || !indexNode.TryGetValue<int>(out int index))
                    throw new NotSupportedException($"局部变量 {name} 不是可索引的格子集合");
                int normalized = index < 0 ? set.Count + index : index;
                if (normalized < 0 || normalized >= set.Count)
                    throw new NotSupportedException($"局部变量 {name} 的下标 {index} 越界（共 {set.Count} 格）");
                bound = set[normalized];
            }
            changed = true;
            return Encode(bound);
        }

        var positional = step.Args.Positional.Select(Replace).ToArray();
        var keyword = step.Args.Keyword.ToDictionary(pair => pair.Key, pair => Replace(pair.Value));
        if (!changed) return step;
        return new CampaignPlanStep
        {
            Kind = step.Kind,
            Op = step.Op,
            Args = new CampaignPlanStepArgs { Positional = positional, Keyword = keyword },
            Target = step.Target,
            Test = step.Test,
            Body = step.Body,
            OrElse = step.OrElse,
        };
    }

    /// <summary>跨钩子调用的最大递归深度（上游存在 `self.battle_0()` 这种调用同关卡其它钩子的写法）。</summary>
    private const int MaxCallDepth = 64;

    private static Dictionary<string, object?> BindArguments(CampaignPlanBattle battle,
                                                              CampaignPlanStepArgs? supplied,
                                                              ICampaignPrimitiveHost host)
    {
        object Decode(JsonNode? node)
        {
            if (node is null) return PythonNone;
            if (node is JsonValue value)
            {
                if (value.TryGetValue<bool>(out bool flag)) return flag;
                if (value.TryGetValue<int>(out int number)) return number;
                if (value.TryGetValue<string>(out string? text)) return text!;
            }
            if (node is JsonObject gridPayload && gridPayload.ContainsKey("__grid__"))
            {
                string location = GridLocationOf(gridPayload);
                return host.Grids.FirstOrDefault(grid => grid.Location == location)
                    ?? throw new NotSupportedException($"参数格子 {location} 不在当前地图状态中");
            }
            return node.DeepClone();
        }
        var values = battle.Parameters.ToDictionary(pair => pair.Key,
            pair => (object?)Decode(pair.Value), StringComparer.Ordinal);
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        var positional = supplied?.Positional ?? [];
        if (positional.Count > battle.ParameterOrder.Count)
            throw new NotSupportedException($"{battle.Method} 位置参数数量超过签名，或导出缺少 parameter_order");
        for (int index = 0; index < positional.Count; index++)
        {
            string name = battle.ParameterOrder[index];
            assigned.Add(name);
            values[name] = Decode(positional[index]);
        }
        foreach (var (name, value) in supplied?.Keyword ?? new Dictionary<string, JsonNode?>())
        {
            if (!battle.Parameters.ContainsKey(name) || battle.PositionalOnlyParameters.Contains(name))
                throw new NotSupportedException($"{battle.Method} 不接受关键字参数 {name}");
            if (!assigned.Add(name)) throw new NotSupportedException($"{battle.Method} 参数 {name} 重复赋值");
            values[name] = Decode(value);
        }
        foreach (string required in battle.RequiredParameters)
            if (!assigned.Contains(required)) throw new NotSupportedException($"{battle.Method} 缺少必需参数 {required}");
        return values;
    }

    private static object? Invoke(CampaignPlan plan, CampaignPlanStep step, ICampaignPrimitiveHost host,
                                  Dictionary<string, object?> state, int depth)
    {
        var nested = plan.Header.Battles.FirstOrDefault(item => item.Method == step.Op);
        if (nested is not null)
        {
            if (depth >= MaxCallDepth) throw new NotSupportedException($"跨钩子调用超过 {MaxCallDepth} 层");
            var inner = Run(plan, nested, host, depth + 1, BindArguments(nested, step.Args, host), state);
            if (inner.Signal is { } signal)
                throw new CampaignControlFlowSignal(signal, inner.BlockedReason ?? signal);
            if (inner.BlockedReason is { } blocked)
                throw new NotSupportedException($"跨钩子 {step.Op} 被阻塞：{blocked}");
            return inner.ReturnValue;
        }
        if (!CampaignPrimitiveRegistry.TryGet(step.Op, out var primitive))
            throw new NotSupportedException($"原语 {step.Op} 未实现");
        if (UnevaluatedArguments(step) is { } argument)
            throw new NotSupportedException($"实参未求值：{argument}");
        CampaignPrimitiveRegistry.ValidateArguments(step);
        return primitive.Execute(host, step);
    }

    public static CampaignHookExecution Run(CampaignPlan plan, CampaignPlanBattle battle,
                                            ICampaignPrimitiveHost host) =>
        Run(plan, battle, host, depth: 0);

    /// <summary>
    /// 带**关卡实例状态**执行钩子（跨钩子共享同一份：`self._is_D9 = True` 之后别的钩子读得到）。
    /// 关卡循环用这个重载，状态初值来自计划里的 `initial_state`。
    /// </summary>
    public static CampaignHookExecution Run(CampaignPlan plan, CampaignPlanBattle battle,
                                            ICampaignPrimitiveHost host,
                                            Dictionary<string, object?> state) =>
        Run(plan, battle, host, depth: 0, env: null, state: state);

    private static CampaignHookExecution Run(CampaignPlan plan, CampaignPlanBattle battle,
                                             ICampaignPrimitiveHost host, int depth,
                                             Dictionary<string, object?>? env = null,
                                             Dictionary<string, object?>? state = null)
    {
        int actionsBefore = host is RecordingCampaignHost recorder ? recorder.Actions.Count : 0;
        try
        {
            return RunCore(plan, battle, host, depth, env, state);
        }
        catch (CampaignControlFlowSignal signal)
        {
            if (signal.Kind == "CampaignEnd")
            {
                host.RequestCampaignEnd(signal.Message);
                return Result(plan, battle, null, null, ["控制流信号 CampaignEnd"], host, actionsBefore, []);
            }
            return Result(plan, battle, null, signal.Message, [$"控制流信号 {signal.Kind}"],
                          host, actionsBefore, []) with { Signal = signal.Kind };
        }
        catch (NotSupportedException error)
        {
            return Result(plan, battle, null, error.Message, [error.Message], host, actionsBefore, []);
        }
    }

    internal static Dictionary<string, object?> CreateInitialState(CampaignPlan plan)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, node) in plan.Header.InitialState)
        {
            if (node is null) result[name] = PythonNone;
            else if (node is JsonValue value)
            {
                if (value.TryGetValue<bool>(out bool flag)) result[name] = flag;
                else if (value.TryGetValue<int>(out int number)) result[name] = number;
                else if (value.TryGetValue<string>(out string? text)) result[name] = text;
                else throw new NotSupportedException($"实例属性 {name} 的初值类型未支持");
            }
            else throw new NotSupportedException($"实例属性 {name} 的初值类型未支持");
        }
        return result;
    }

    private static CampaignHookExecution RunCore(CampaignPlan plan, CampaignPlanBattle battle,
                                             ICampaignPrimitiveHost host, int depth,
                                             Dictionary<string, object?>? env,
                                             Dictionary<string, object?>? state)
    {
        // **局部变量环境**：上游 `boss = self.map.select(is_boss=True)` 这类"观察"的绑定，
        // 后续 `if boss:` / `check_accessibility(boss[0], …)` 都从它取值。分支体共享同一份（Python 作用域）。
        env ??= BindArguments(battle, null, host);
        // **关卡实例属性**（跨钩子存在，如 `self._is_D9`）：由关卡循环创建、初值来自计划的
        // `initial_state`；单钩子诊断使用同一套初值转换。
        state ??= CreateInitialState(plan);
        var stepLog = new List<string>();
        var actions = new List<string>();
        int actionsBefore = host is RecordingCampaignHost recording ? recording.Actions.Count : 0;

        // **tier C 守卫**：`plan_complete=false` 表示该钩子含静态无法表达的部分（`unparsed` 里给原因），
        // 此时 `steps` 恒为空。不检查就会把"什么都没做"当成钩子执行完毕——那是**静默失败**。
        // 与 `BattlePlanRunner` 同一口径：拒绝执行并如实报原因。
        if (!battle.PlanComplete)
        {
            string reason = battle.Unparsed.Count > 0
                ? string.Join("、", battle.Unparsed)
                : "plan_complete=false";
            stepLog.Add($"{battle.Method}: 计划不完整（{reason}），拒绝执行");
            return Result(plan, battle, null, $"计划不完整：{reason}", stepLog, host, actionsBefore, actions);
        }

        foreach (var originalStep in battle.Steps)
        {
            if (host.EndRequested)
                return Result(plan, battle, null, null, stepLog, host, actionsBefore, actions);
            var step = SubstituteLocals(originalStep, env);
            // `branch`：条件分支。条件要么看**局部变量**（`if boss:`），要么调一次原语
            // （`if not self.check_accessibility(boss[0], fleet='boss')`）。分支体是步骤序列，可再嵌套。
            if (step.Kind == "branch")
            {
                var (taken, why) = EvaluateBranch(step, host, env, state, plan, depth);
                if (taken is null)
                {
                    stepLog.Add($"{step.Op}: {why}");
                    return Result(plan, battle, null, why, stepLog, host, actionsBefore, actions);
                }
                stepLog.Add($"branch：条件为{(taken == true ? "真" : "假")}（{why}）");
                var branchSteps = taken == true ? step.Body : step.OrElse;
                if (branchSteps.Count == 0) continue;
                var innerBattle = new CampaignPlanBattle
                {
                    Method = battle.Method,
                    Calls = battle.Calls,
                    PlanComplete = true,
                    StatementCount = battle.StatementCount,
                    Parameters = battle.Parameters,
                    Steps = branchSteps,
                };
                var branchRun = Run(plan, innerBattle, host, depth, env, state);
                stepLog.AddRange(branchRun.StepLog);
                if (branchRun.BlockedReason is not null)
                {
                    return Result(plan, battle, null, branchRun.BlockedReason, stepLog, host, actionsBefore, actions)
                        with { Signal = branchRun.Signal };
                }
                if (branchRun.DidReturn || host.EndRequested)
                {
                    return Result(plan, battle, branchRun.ReturnValue, null, stepLog, host, actionsBefore, actions);
                }
                continue;
            }

            // `local_set`：把**值表达式**绑到局部名（`ignore = SelectedGrids([A2])` / `boss = boss[0]` /
            // `ignore = None`）。与 `state_set` 的区别是作用域：局部名只在本次钩子执行里有效。
            if (step.Kind == "local_set")
            {
                if (step.Target is not { Length: > 0 } localName)
                {
                    return Result(plan, battle, null, "local_set 缺少绑定名", stepLog, host, actionsBefore, actions);
                }
                var (localValue, localWhy) = EvaluateExpr(step.Expr, host, state, env, plan, depth);
                if (localValue is null)
                {
                    return Result(plan, battle, null, localWhy, stepLog, host, actionsBefore, actions);
                }
                env[localName] = localValue;
                stepLog.Add($"local_set：{localName} = {Describe(localValue)}（{localWhy}）");
                continue;
            }

            // `state_set`：上游 `self.<属性> = …` —— 关卡实例属性（跨钩子存在，与局部变量不同）。
            if (step.Kind == "state_set")
            {
                if (step.Name is not { Length: > 0 } stateName)
                {
                    return Result(plan, battle, null, "state_set 缺少属性名", stepLog, host, actionsBefore, actions);
                }
                var (stateValue, stateWhy) = EvaluateExpr(step.Expr, host, state, env, plan, depth);
                if (stateValue is null)
                {
                    return Result(plan, battle, null, stateWhy, stepLog, host, actionsBefore, actions);
                }
                state[stateName] = stateValue;
                stepLog.Add($"state_set：self.{stateName} = {Describe(stateValue)}（{stateWhy}）");
                continue;
            }

            // `map_set`：上游 `for grid in self.map: grid.<flag> = <字面量>` —— 整图设一个布尔标志。
            // 逐个格子走宿主的 `SetGridFlag`（写模型 + 通知上游），**没有设备动作**。
            if (step.Kind == "map_set")
            {
                if (step.Flag is not { Length: > 0 } flag) 
                    return Result(plan, battle, null, "map_set 缺少 flag", stepLog, host, actionsBefore, actions);
                bool value = step.Value?.GetValue<bool>() ?? true;
                // **先取快照再写**：宿主的 `SetGridFlag` 会替换同一个列表里的元素，
                // 直接 foreach 枚举 `host.Grids` 会抛"Collection was modified"（实测被抓出来）。
                var targets = host.Grids.ToList();
                foreach (var grid in targets) host.SetGridFlag(grid, flag, value);
                stepLog.Add($"map_set：{targets.Count} 格的 {flag} = {value}");
                continue;
            }

            // `log`：上游 `logger.info(...)` 这类纯日志调用。**没有引擎副作用**（不改地图状态、
            // 不发设备动作），但计划里保留它并写进步骤日志——不静默丢，也不假装它做了什么。
            if (step.Kind == "log")
            {
                stepLog.Add($"log: {step.Text}");
                continue;
            }

            // `raise <Signal>()`：上游用异常做控制流。
            //   * `CampaignEnd` → 请求结束本关（**不发设备动作**），循环在检查点收尾；
            //   * `MapEnemyMoved` → 抛控制流信号，由 `execute_a_battle` 那层按 battle_count 判定重试。
            if (step.Kind == "raise")
            {
                string signal = step.Signal ?? "";
                if (signal == "CampaignEnd")
                {
                    host.Log("raise CampaignEnd：结束本关（控制流信号，不发设备动作）");
                    host.RequestCampaignEnd("CampaignEnd");
                    return Result(plan, battle, null, null, stepLog, host, actionsBefore, actions);
                }
                if (signal == "MapEnemyMoved")
                {
                    stepLog.Add("raise MapEnemyMoved");
                    throw new CampaignControlFlowSignal("MapEnemyMoved", "raise MapEnemyMoved");
                }
                stepLog.Add($"raise {signal}：未知信号，停止执行");
                return Result(plan, battle, null, $"未知的 raise 信号 {signal}", stepLog, host, actionsBefore, actions);
            }

            // `return <字面量>`：上游不少钩子以 `return True` 收尾（`self.X(); return True`）。
            // 这是**返回值**不是调用，所以没有 op；按字面量返回。
            if (step.Kind == "return")
            {
                bool? literal = null;
                if (step.Value is not null)
                {
                    if (step.Value is not JsonValue value || !value.TryGetValue<bool>(out bool flag))
                        throw new NotSupportedException("return 字面量只支持 bool/None，标量返回尚未迁移");
                    literal = flag;
                }
                stepLog.Add($"return {(literal is null ? "None" : literal.Value ? "True" : "False")}");
                return Result(plan, battle, literal, null, stepLog, host, actionsBefore, actions);
            }

            // `assign`：把一次调用的结果绑定成局部变量。
            //   * `map.select(**flags)` → 绑定一个**格子集合**（后续 `boss[0]` 会用到）
            //   * 其它原语 → 绑定它的布尔结果（`if <name>:` 直接看真假）
            if (step.Kind == "assign" && step.Target is { Length: > 0 } bind)
            {
                if (step.Op == "map.select")
                {
                    var selected = CampaignPrimitives.MapSelect(host, step);
                    env[bind] = selected;
                    stepLog.Add($"map.select → {selected.Count} 格（绑定 {bind}）");
                    continue;
                }
                object? assignedValue;
                try
                {
                    assignedValue = Invoke(plan, step, host, state, depth);
                }
                catch (NotSupportedException error)
                {
                    stepLog.Add($"{step.Op}: {error.Message}");
                    return Result(plan, battle, null, error.Message, stepLog, host, actionsBefore, actions);
                }
                env[bind] = assignedValue ?? PythonNone;
                stepLog.Add($"{step.Op}: {Describe(assignedValue ?? PythonNone)}（绑定 {bind}）");
                continue;
            }

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
                    CampaignPrimitiveRegistry.ValidateArguments(delegated.Step);
                    object? returned = basePrimitive.Execute(host, delegated.Step);
                    stepLog.Add($"{step.Op} → 父类实现 {delegated.Step.Op}：已执行" +
                                  (delegated.Note is null ? "" : $"（{delegated.Note}）"));
                    return Result(plan, battle, HookReturn(returned), null, stepLog, host, actionsBefore, actions);
                }
                catch (NotSupportedException error)
                {
                    stepLog.Add($"{step.Op}: {error.Message}");
                    return Result(plan, battle, null, error.Message, stepLog, host, actionsBefore, actions);
                }
            }

            if (step.Kind is not ("call" or "conditional" or "terminal"))
                return Result(plan, battle, null, $"未知步骤类型 {step.Kind}", stepLog, host, actionsBefore, actions);

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

            object? executed;
            try
            {
                executed = Invoke(plan, step, host, state, depth);
            }
            catch (NotSupportedException error)
            {
                stepLog.Add($"{step.Op}: {error.Message}");
                return Result(plan, battle, null, error.Message, stepLog, host, actionsBefore, actions);
            }

            stepLog.Add($"{step.Op}: {Describe(executed ?? PythonNone)}（{Role(role)}）");

            if (role == CampaignStepRole.Attempt && Truthy(executed))
            {
                return Result(plan, battle, true, null, stepLog, host, actionsBefore, actions);
            }
            if (role == CampaignStepRole.Fallback)
            {
                return Result(plan, battle, HookReturn(executed), null, stepLog, host, actionsBefore, actions);
            }
        }

        // 没有 terminal 兜底且所有尝试都没成功：上游会落到方法末尾（返回 None，视为假）
        return Result(plan, battle, null, null, stepLog, host, actionsBefore, actions, didReturn: false);
    }

    private static CampaignHookExecution Result(CampaignPlan plan, CampaignPlanBattle battle, bool? returned,
                                                string? blocked, List<string> stepLog, ICampaignPrimitiveHost host,
                                                int actionsBefore, List<string> actions, bool didReturn = true)
    {
        if (host is RecordingCampaignHost recording)
        {
            actions.AddRange(recording.Actions.Skip(actionsBefore));
        }
        var logs = host is RecordingCampaignHost recorder ? recorder.Logs.ToList() : [];
        return new CampaignHookExecution(plan.Chapter, plan.Level, battle.Method, returned, blocked,
                                         stepLog, actions, logs) { DidReturn = didReturn };
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
    Func<ICampaignPrimitiveHost, CampaignPlanStep, object?> Execute);

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
                    if (!battle.Parameters.TryGetValue(name, out var fallback)
                        || battle.RequiredParameters.Contains(name, StringComparer.Ordinal))
                    {
                        return new CampaignSuperDelegate(null,
                            $"super 委托实参 {name} 没有字面量默认值，无法还原（{step.Op}）");
                    }
                    positional.Add(fallback?.DeepClone());
                    note = $"实参 {name} 用签名默认值 {fallback?.ToJsonString() ?? "null"}";
                    continue;
                }
                positional.Add(value?.DeepClone());
            }
            var keyword = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            foreach (var (key, value) in args.Keyword)
            {
                if (ParameterName(value) is { } name)
                {
                    if (!battle.Parameters.TryGetValue(name, out var fallback)
                        || battle.RequiredParameters.Contains(name, StringComparer.Ordinal))
                    {
                        return new CampaignSuperDelegate(null,
                            $"super 委托关键字实参 {name} 没有字面量默认值，无法还原（{step.Op}）");
                    }
                    keyword[key] = fallback?.DeepClone();
                    note = $"关键字实参 {name} 用签名默认值 {fallback?.ToJsonString() ?? "null"}";
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
            (host, step) => CampaignPrimitives.ClearEnemy(host, DecodeOptions(host, step))),
        ["battle_default"] = new CampaignPrimitive(
            "battle_default", "默认战斗：clear_enemy 成功即真（上游 CampaignBase.battle_default）", false,
            (host, _) => CampaignPrimitives.BattleDefault(host)),
        ["clear_all_mystery"] = new CampaignPrimitive(
            "clear_all_mystery", "捡完所有神秘格子，恒返回假（上游 Map.clear_all_mystery）", false,
            // 上游这里把 kwargs 转发给 `select_grids`：关卡会传 `ignore=SelectedGrids([...])`
            // 表示"这些格子别动"。不接的话会**静默忽略** ignore（比阻塞更糟）。
            (host, step) => CampaignPrimitives.ClearAllMystery(
                host, DecodeOptions(host, step))),
        ["clear_filter_enemy"] = new CampaignPrimitive(
            "clear_filter_enemy", "按过滤串选敌人并清掉（上游 Map.clear_filter_enemy）", NeedsArguments: true,
            (host, step) => CampaignPrimitives.ClearFilterEnemy(host, DecodeFilter(step), DecodePreserve(step))),
        ["clear_any_enemy"] = new CampaignPrimitive(
            "clear_any_enemy", "敌人+塞壬+要塞一起选（上游 Map.clear_any_enemy）", false,
            (host, step) => CampaignPrimitives.ClearAnyEnemy(host, DecodeOptions(host, step))),
        ["clear_siren"] = new CampaignPrimitive(
            "clear_siren", "打塞壬/要塞，无配置直接返回假（上游 Map.clear_siren）", false,
            (host, step) => CampaignPrimitives.ClearSiren(host, DecodeOptions(host, step))),
        ["clear_boss"] = new CampaignPrimitive(
            "clear_boss", "找 boss 并清掉，找不到踩 may_boss（上游 Map.clear_boss）", false,
            (host, _) => CampaignPrimitives.ClearBoss(host)),
        ["clear_roadblocks"] = new CampaignPrimitive(
            "clear_roadblocks", "打掉路段里的路障（上游 Map.clear_roadblocks）", NeedsArguments: true,
            (host, step) => CampaignPrimitives.ClearRoadblocks(host, DecodeRoads(step), DecodeOptions(host, step))),
        ["clear_potential_roadblocks"] = new CampaignPrimitive(
            "clear_potential_roadblocks", "避免只剩一格空的路障（上游 Map.clear_potential_roadblocks）",
            NeedsArguments: true,
            (host, step) => CampaignPrimitives.ClearPotentialRoadblocks(host, DecodeRoads(step), DecodeOptions(host, step))),
        ["clear_first_roadblocks"] = new CampaignPrimitive(
            "clear_first_roadblocks", "保证每个路障块有一个已清格子（上游 Map.clear_first_roadblocks）",
            NeedsArguments: true,
            (host, step) => CampaignPrimitives.ClearFirstRoadblocks(host, DecodeRoads(step), DecodeOptions(host, step))),
        ["pick_up_ammo"] = new CampaignPrimitive(
            "pick_up_ammo", "捡弹药（上游 Map.pick_up_ammo）", false,
            (host, step) =>
            {
                var grid = DecodeGrid(host, step);
                bool noAmmo = grid is null && !host.Grids.Any(item => item.MayAmmo);
                CampaignPrimitives.PickUpAmmo(host, grid);
                return noAmmo ? false : null;
            }),
        ["pick_up_light_house"] = new CampaignPrimitive(
            "pick_up_light_house", "捡灯塔（上游关卡基类 helper，恒返回假）", NeedsArguments: true,
            (host, step) => CampaignPrimitives.PickUpLightHouse(host, RequireGrid(host, step))),
        ["pick_up_flare"] = new CampaignPrimitive(
            "pick_up_flare", "捡信号弹（上游关卡基类 helper，恒返回假）", NeedsArguments: true,
            (host, step) => CampaignPrimitives.PickUpFlare(host, RequireGrid(host, step))),
        ["capture_clear_boss"] = new CampaignPrimitive(
            "capture_clear_boss", "打 boss 后退（上游 Map.capture_clear_boss，deprecated）", false,
            (host, _) => { CampaignPrimitives.CaptureClearBoss(host); return null; }),
        ["fleet_2_push_forward"] = new CampaignPrimitive(
            "fleet_2_push_forward", "道中队推进到 weight 最低的可达海域（上游 Map.fleet_2_push_forward）", false,
            (host, _) => CampaignPrimitives.Fleet2PushForward(host)),
        ["fleet_2_protect"] = new CampaignPrimitive(
            "fleet_2_protect", "道中队游走清靠近的塞壬/敌人（上游 Map.fleet_2_protect）", false,
            (host, _) => CampaignPrimitives.Fleet2Protect(host)),
        ["clear_potential_boss"] = new CampaignPrimitive(
            "clear_potential_boss", "依次踩可达 may_boss 格子（上游 Map.clear_potential_boss）", false,
            (host, _) => CampaignPrimitives.ClearPotentialBoss(host)),
        ["check_accessibility"] = new CampaignPrimitive(
            "check_accessibility", "格子对指定舰队是否可达（上游 Fleet.check_accessibility）",
            NeedsArguments: true,
            (host, step) => CampaignPrimitives.CheckAccessibility(
                host, RequireGrid(host, step), OptionalFleet(step))),
        ["ensure_fleet"] = new CampaignPrimitive(
            "ensure_fleet", "切换舰队（上游 Fleet.fleet_ensure(index)）", NeedsArguments: true,
            (host, step) =>
            {
                int index = DecodeFleetIndex(step);
                CampaignPrimitives.RecordInvocation(host, "ensure_fleet");
                return host.EnsureFleet(index);
            }),
        ["fleet_ensure"] = new CampaignPrimitive(
            "fleet_ensure", "上游名别名 → ensure_fleet", NeedsArguments: true,
            (host, step) =>
            {
                int index = DecodeFleetIndex(step);
                CampaignPrimitives.RecordInvocation(host, "fleet_ensure");
                return host.EnsureFleet(index);
            }),
        ["fleet_at"] = new CampaignPrimitive(
            "fleet_at", "舰队是否在该格（上游 Fleet.fleet_at；纯状态判断）", NeedsArguments: true,
            (host, step) => CampaignPrimitives.FleetAt(host, RequireGrid(host, step), OptionalFleet(step))),
        ["goto"] = new CampaignPrimitive(
            "goto", "走到指定格（上游 Fleet.goto；移动本身由宿主执行）", NeedsArguments: true,
            (host, step) =>
            {
                var grid = RequireGrid(host, step);
                string expected = OptionalExpected(step);
                CampaignPrimitives.RecordInvocation(host, "goto");
                host.Goto(grid, expected);
                return null;
            }),
        ["clear_chosen_enemy"] = new CampaignPrimitive(
            "clear_chosen_enemy", "打指定格子（上游 Map.clear_chosen_enemy 的动作入口）", NeedsArguments: true,
            (host, step) => CampaignPrimitives.ClearChosenEnemy(host, RequireGrid(host, step), OptionalExpected(step))),
        ["switch_to"] = new CampaignPrimitive(
            "switch_to", "上游 Fleet.switch_to() 是 pass（切舰队由前缀完成）", false,
            (host, step) => { CampaignPrimitives.SwitchTo(host, CampaignPrimitiveRegistry.SplitFleetPrefix(step.Op).Prefix); return null; }),
        ["clear_map_items"] = new CampaignPrimitive(
            "clear_map_items", "按 cost 升序清掉指定格子上的物资（上游关卡基类 helper）", NeedsArguments: true,
            (host, step) => { CampaignPrimitives.ClearMapItems(host, RequireGrids(host, step)); return null; }),
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
            (host, step) => { CampaignPrimitives.HandleBossAppearRefocus(host, DecodeSwipe(step)); return null; }),
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
    private static JsonArray? Sequence(JsonNode? node) =>
        node as JsonArray ?? (node as JsonObject)?["__tuple__"] as JsonArray;

    // 上游 Map/Fleet 的调用签名。未迁移的参数明确拒绝，不能静默执行默认行为。
    internal static void ValidateArguments(CampaignPlanStep step)
    {
        string op = IsFleetPrefixed(step.Op) ? SplitFleetPrefix(step.Op).Inner : step.Op;
        string[] names = op switch
        {
            "goto" => ["location", "expected", "step_optimize", "turning_optimize"],
            "clear_chosen_enemy" => ["grid", "expected"],
            "check_accessibility" or "fleet_at" => ["grid", "fleet"],
            "pick_up_ammo" or "pick_up_flare" or "pick_up_light_house" or "fleet_2_rescue" => ["grid"],
            "clear_filter_enemy" => ["string", "preserve"],
            "clear_mechanism" or "clear_map_items" => ["grids"],
            "fleet_2_step_on" => ["grids", "roadblocks"],
            "clear_roadblocks" or "clear_potential_roadblocks" or "clear_first_roadblocks" => ["roads"],
            "fleet_ensure" or "ensure_fleet" => ["index"],
            "handle_boss_appear_refocus" => ["preset"],
            _ => [],
        };
        var args = step.Args;
        if (args is null) return;
        if (args.Positional.Count > names.Length)
            throw new NotSupportedException($"{op} 位置参数超过上游签名");
        bool selection = op is "clear_enemy" or "clear_any_enemy" or "clear_siren" or "clear_all_mystery"
            or "clear_roadblocks" or "clear_potential_roadblocks" or "clear_first_roadblocks";
        string[] options = ["nearby", "is_accessible", "scale", "genre", "strongest", "weakest", "sort", "ignore"];
        foreach (var name in args.Keyword.Keys)
        {
            int index = Array.IndexOf(names, name);
            if (index < 0 && !(selection && options.Contains(name)))
                throw new NotSupportedException($"{op} 参数 {name} 未迁移或不在上游签名中");
            if (index >= 0 && index < args.Positional.Count)
                throw new NotSupportedException($"{op} 参数 {name} 重复赋值");
        }
        if (op == "goto" && (Argument(step, 2, "step_optimize") is not null
                            || Argument(step, 3, "turning_optimize") is not null))
            throw new NotSupportedException("goto 的显式路径优化参数尚未迁移到宿主接口");
    }

    private static JsonNode? Argument(CampaignPlanStep step, int index, string name)
    {
        if (step.Args?.Keyword.TryGetValue(name, out var value) == true) return value;
        return step.Args?.Positional is { } positional && index < positional.Count ? positional[index] : null;
    }

    private static CampaignTargetOptions DecodeOptions(ICampaignPrimitiveHost host, CampaignPlanStep step)
    {
        var options = new CampaignTargetOptions();
        if (step.Args is not { } args) return options;
        options = options with { Ignore = OptionalIgnore(host, step) };
        if (args.Keyword.TryGetValue("nearby", out var nearby) && nearby is not null)
            options = options with { Nearby = nearby.GetValue<bool>() };
        if (args.Keyword.TryGetValue("is_accessible", out var accessible) && accessible is not null)
            options = options with { IsAccessible = accessible.GetValue<bool>() };
        if (args.Keyword.TryGetValue("scale", out var scaleNode) && Sequence(scaleNode) is { } scaleArray)
        {
            options = options with
            {
                Scale = scaleArray.OfType<JsonValue>().Select(value => value.GetValue<int>()).ToArray(),
                // 上游：元组为并集，列表按优先级取到即止。__tuple__ 保留这个运行语义。
                ScaleInOrder = scaleNode is JsonArray,
            };
        }
        if (args.Keyword.TryGetValue("genre", out var genreNode) && Sequence(genreNode) is { } genreArray)
        {
            options = options with
            {
                Genre = genreArray.OfType<JsonValue>().Select(value => value.GetValue<string>()).ToArray(),
                GenreInOrder = genreNode is JsonArray,
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
        if (args.Keyword.TryGetValue("sort", out var sortNode) && Sequence(sortNode) is { } sortArray)
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
    /// <summary>
    /// 解 `fleet_ensure(index)` / `ensure_fleet(index)` 的舰队序号：关键字 `index` 优先，
    /// 其次位置实参；都拿不到就报错（**实参是 `<expr>` 时执行器早一步就挡住了**，不会走到这里）。
    /// </summary>
    private static int DecodeFleetIndex(CampaignPlanStep step)
    {
        if (Argument(step, 0, "index") is JsonValue value && value.TryGetValue<int>(out int index))
        {
            return index;
        }
        throw new NotSupportedException($"{step.Op} 的舰队序号解不出来（需要 index 整数实参）");
    }

    /// <summary>取 `goto(grid, expected='…')` 的 `expected` 关键字（没有就空串）。</summary>
    private static string OptionalExpected(CampaignPlanStep step)
    {
        var node = Argument(step, 1, "expected");
        if (node is null) return "";
        return node is JsonValue value && value.TryGetValue<string>(out string? text) ? text : "";
    }

    /// <summary>取步骤里声明的 `fleet` 关键字实参（`check_accessibility(grid, fleet=…)` 用）。</summary>
    private static string? OptionalFleet(CampaignPlanStep step)
    {
        var node = Argument(step, 1, "fleet");
        if (node is null) return null;
        return node switch
        {
            JsonValue value when value.TryGetValue<string>(out string? text) => text,
            JsonValue value when value.TryGetValue<int>(out int number) => number.ToString(),
            _ => node.ToJsonString().Trim('"'),
        };
    }

    private static CampaignGrid? DecodeGrid(ICampaignPrimitiveHost host, CampaignPlanStep step)
    {
        string op = IsFleetPrefixed(step.Op) ? SplitFleetPrefix(step.Op).Inner : step.Op;
        foreach (var value in new[] { Argument(step, 0, op == "goto" ? "location" : "grid") })
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
        var node = Argument(step, 0, "preset");
        if (node is null) return null;
        if (Sequence(node) is not { } pair || pair.Count != 2
            || pair[0] is null || pair[1] is null)
        {
            throw new NotSupportedException($"preset 实参形状不支持（{step.Op}）：{node.ToJsonString()}");
        }
        return (pair[0]!.GetValue<int>(), pair[1]!.GetValue<int>());
    }

    private static IReadOnlyList<CampaignGrid>? DecodeGrids(ICampaignPrimitiveHost host, CampaignPlanStep step)
    {
        foreach (var value in new[] { Argument(step, 0, "grids") })
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
    /// <summary>
    /// 解 `clear_all_mystery(ignore=…)` 这个**关键字**实参（`{"__grids__": [[x, y], …]}`）。
    /// 上游把 kwargs 原样转发给 `select_grids`，关卡用 `ignore` 表示"这些格子别动"。
    /// </summary>
    private static CampaignGridSet? OptionalIgnore(ICampaignPrimitiveHost host, CampaignPlanStep step)
    {
        if (step.Args?.Keyword.TryGetValue("ignore", out var node) != true || node is null) return null;
        // 复用 `DecodeGrids`：它读的是位置实参，这里把关键字实参包成一个临时步骤（只有一处实参）。
        var synthetic = new CampaignPlanStep
        {
            Kind = step.Kind,
            Op = step.Op,
            Args = new CampaignPlanStepArgs { Positional = [node] },
        };
        var grids = DecodeGrids(host, synthetic);
        return grids is null
            ? throw new NotSupportedException($"{step.Op} 的 ignore 不是格子集合")
            : new CampaignGridSet(grids);
    }

    private static IReadOnlyList<CampaignGrid>? OptionalGrids(ICampaignPrimitiveHost host, CampaignPlanStep step)
    {
        if (Argument(step, 0, "grids") is null) return null;
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
        if (Argument(step, 0, "string") is JsonValue json
            && json.TryGetValue(out string? text) && text != "<expr>") return text;
        throw new NotSupportedException(
            $"clear_filter_enemy 的过滤串在导出里是未求值表达式（{step.Op}）——需要导出器求值后才能执行");
    }

    private static int DecodePreserve(CampaignPlanStep step)
    {
        if (Argument(step, 1, "preserve") is JsonValue value && value.TryGetValue(out int preserve))
        {
            return preserve;
        }
        return 0;
    }
}
