using System.Text.Json.Nodes;

namespace Alas.Campaign;

/// <summary>原语执行时读到的运行时配置（只含已移植分支需要的字段，逐项对应上游 config）。</summary>
public sealed record CampaignRuntimeConfig(
    string? EnemyPriority = null,
    bool MapClearAllThisTime = false,
    bool MapHasSiren = false,
    bool MapHasFortress = false,
    bool Fleet2 = false,
    bool MapHasMovableNormalEnemy = false);

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

    /// <summary>清掉选中格子上的敌人（上游 <c>clear_chosen_enemy</c>）：返回是否真的打了。</summary>
    bool ClearChosenEnemy(CampaignGrid grid, string expected);

    /// <summary>捡走选中格子上的神秘物资（上游 <c>clear_chosen_mystery</c>）。</summary>
    bool ClearChosenMystery(CampaignGrid grid);

    void Log(string message);
}

/// <summary>干跑宿主：记录原语要做的动作，但**什么都不执行**（不连设备、不点屏幕）。</summary>
public sealed class RecordingCampaignHost : ICampaignPrimitiveHost
{
    public RecordingCampaignHost(IEnumerable<CampaignGrid> grids, CampaignRuntimeConfig? config = null)
    {
        Grids = grids.ToArray();
        Config = config ?? new CampaignRuntimeConfig();
    }

    public IReadOnlyList<CampaignGrid> Grids { get; }

    public CampaignRuntimeConfig Config { get; }

    /// <summary>干跑记录到的动作（按调用顺序）。</summary>
    public List<string> Actions { get; } = [];

    /// <summary>干跑记录到的日志（原语的判断依据，便于人工核对）。</summary>
    public List<string> Logs { get; } = [];

    public bool ClearChosenEnemy(CampaignGrid grid, string expected)
    {
        Actions.Add($"clear_chosen_enemy({grid.Location}, expected={expected})");
        return true;
    }

    public bool ClearChosenMystery(CampaignGrid grid)
    {
        Actions.Add($"clear_chosen_mystery({grid.Location})");
        return true;
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
/// 未移植分支一律抛 <see cref="NotSupportedException"/> 或经 <c>Unsupported</c> 报出，不猜语义。
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
        return host.ClearChosenEnemy(decision.Target, "enemy");
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
        var decision = CampaignTargetSelector.SelectFilterEnemyTarget(
            new CampaignGridSet(host.Grids), filter, preserve,
            host.Config.EnemyPriority, host.Config.MapHasMovableNormalEnemy);
        if (decision.Unsupported is not null)
            throw new NotSupportedException($"clear_filter_enemy 走到未移植分支：{decision.Branch}——{decision.Unsupported}");
        if (decision.Target is null)
        {
            host.Log($"clear_filter_enemy：无目标（{decision.Branch}）");
            return false;
        }
        host.Log($"clear_filter_enemy：选中 {decision.Target.Location}（{decision.Target.FilterKey}，{decision.Branch}）");
        return host.ClearChosenEnemy(decision.Target, "enemy");
    }
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
    public static CampaignHookExecution Run(CampaignPlan plan, CampaignPlanBattle battle, ICampaignPrimitiveHost host)
    {
        var stepLog = new List<string>();
        var actions = new List<string>();
        int actionsBefore = host is RecordingCampaignHost recording ? recording.Actions.Count : 0;

        foreach (var step in battle.Steps)
        {
            if (step.Kind == "super_delegate")
            {
                stepLog.Add($"{step.Op}: 委托父类实现，本层未执行");
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

/// <summary>
/// 原语注册表：C# 引擎已实现的原语。P2 按域往里加，每加一个都要带对拍夹具与真实路径证据。
/// 这里刻意不做"按关卡/按编号"的特例分支——注册表只回答"这个原语实现了没有、怎么执行"。
/// </summary>
public static class CampaignPrimitiveRegistry
{
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
    };

    /// <summary>已实现的原语名（排序返回，便于输出与对拍）。</summary>
    public static IReadOnlyList<string> ImplementedOps =>
        Table.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray();

    public static bool IsImplemented(string op) => Table.ContainsKey(op);

    public static bool TryGet(string op, out CampaignPrimitive primitive) => Table.TryGetValue(op, out primitive!);

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
        return options;
    }

    private static string DecodeFilter(CampaignPlanStep step)
    {
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
