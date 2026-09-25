namespace Alas.Campaign;

/// <summary>
/// 计划里一步的**执行角色**。契约由导出数据实测得出（见下），不是猜测：
/// 全库 2795 个带计划的钩子里，步骤类型序列一律符合 <c>k* c* t? s*</c>——
/// 零到多个 <c>call</c> → 零到多个 <c>conditional</c> → 零或一个 <c>terminal</c> → 零到多个 <c>super_delegate</c>。
/// </summary>
public enum CampaignStepRole
{
    /// <summary>`call`：无条件执行（通常是前置动作，如选舰队、移动）。</summary>
    Setup,

    /// <summary>`conditional`：按顺序尝试；某一步成功则本钩子就此结束（短路返回真）。</summary>
    Attempt,

    /// <summary>`terminal`：前面的尝试都没成功时的兜底调用（可缺省）。</summary>
    Fallback,

    /// <summary>`super_delegate`：委托父类实现，不属本层执行面。</summary>
    Delegate,
}

/// <summary>干跑里的一步：执行角色 + 原语 + 是否已实现 + 实参摘要。</summary>
public sealed record CampaignExecutionStep(
    CampaignStepRole Role,
    string Op,
    bool Implemented,
    string Note);

/// <summary>一个钩子的干跑轨迹（按计划顺序）。</summary>
public sealed record CampaignExecutionTrace(
    string Chapter,
    string Level,
    string Method,
    IReadOnlyList<CampaignExecutionStep> Steps)
{
    public int Setup => Steps.Count(step => step.Role == CampaignStepRole.Setup);
    public int Attempts => Steps.Count(step => step.Role == CampaignStepRole.Attempt);
    public bool HasFallback => Steps.Any(step => step.Role == CampaignStepRole.Fallback);
    public int Delegated => Steps.Count(step => step.Role == CampaignStepRole.Delegate);
    public int Unimplemented => Steps.Count(step => !step.Implemented && step.Role != CampaignStepRole.Delegate);
}

/// <summary>
/// 计划形状：用于校验"步骤类型序列符合契约"。不符合即为导出器或上游结构变化，
/// C# 侧不猜语义，只报告。
/// </summary>
public sealed record CampaignPlanShape(
    bool Conforms,
    string Pattern,
    int Setup,
    int Attempts,
    bool HasFallback,
    int Delegates,
    IReadOnlyList<string> Violations);

/// <summary>
/// 原语注册表：C# 引擎已实现的原语集合。
///
/// 目前**为空**——P2 的逐域迁移按域往这里加实现，每个原语都要带对拍夹具与真实路径证据。
/// 这里刻意不做按关卡/按原语名的特例分支：注册表只回答"这个原语实现了没有"。
/// </summary>
public static class CampaignPrimitiveRegistry
{
    private static readonly HashSet<string> Implemented = new(StringComparer.Ordinal);

    /// <summary>已实现的原语名（排序返回，便于输出与对拍）。</summary>
    public static IReadOnlyList<string> ImplementedOps =>
        Implemented.OrderBy(name => name, StringComparer.Ordinal).ToArray();

    public static bool IsImplemented(string op) => Implemented.Contains(op);
}

/// <summary>
/// 关卡计划的**干跑执行器**：按计划契约判定每一步的角色与可执行性，但**不执行任何东西**——
/// 不调用上游、不连设备、不碰游戏。
///
/// 执行语义（实测契约，见 <see cref="CampaignStepRole"/>）：
/// <list type="number">
///   <item>先执行全部 <c>call</c>（无条件前置）；</item>
///   <item>再按顺序尝试 <c>conditional</c>，任一步成功即短路结束（钩子返回真）；</item>
///   <item>都没成功时执行 <c>terminal</c> 兜底（若有）；</item>
///   <item><c>super_delegate</c> 交给父类实现。</item>
/// </list>
/// </summary>
public static class CampaignPlanExecutor
{
    /// <summary>校验一个钩子的步骤类型序列是否符合契约。</summary>
    public static CampaignPlanShape Validate(CampaignPlanBattle battle)
    {
        var roles = new List<CampaignStepRole>();
        var violations = new List<string>();
        bool seenAttempt = false, seenFallback = false, seenDelegate = false;
        int setup = 0, attempts = 0, delegates = 0;
        bool hasFallback = false;
        foreach (var step in battle.Steps)
        {
            switch (step.Kind)
            {
                case "call":
                    if (seenAttempt || seenFallback || seenDelegate)
                        violations.Add($"call 出现在 conditional/terminal 之后：{step.Op}");
                    roles.Add(CampaignStepRole.Setup);
                    setup++;
                    break;
                case "conditional":
                    if (seenFallback || seenDelegate)
                        violations.Add($"conditional 出现在 terminal 之后：{step.Op}");
                    seenAttempt = true;
                    roles.Add(CampaignStepRole.Attempt);
                    attempts++;
                    break;
                case "terminal":
                    if (seenFallback) violations.Add($"重复的 terminal：{step.Op}");
                    if (seenDelegate) violations.Add($"terminal 出现在 super_delegate 之后：{step.Op}");
                    seenFallback = true;
                    hasFallback = true;
                    roles.Add(CampaignStepRole.Fallback);
                    break;
                case "super_delegate":
                    seenDelegate = true;
                    roles.Add(CampaignStepRole.Delegate);
                    delegates++;
                    break;
                default:
                    violations.Add($"未知步骤类型 {step.Kind}");
                    break;
            }
        }
        string pattern = string.Concat(roles.Select(role => role switch
        {
            CampaignStepRole.Setup => "k",
            CampaignStepRole.Attempt => "c",
            CampaignStepRole.Fallback => "t",
            _ => "s",
        }));
        return new CampaignPlanShape(violations.Count == 0, pattern, setup, attempts, hasFallback,
                                     delegates, violations);
    }

    /// <summary>干跑一个钩子。</summary>
    public static CampaignExecutionTrace DryRun(CampaignPlan plan, CampaignPlanBattle battle)
    {
        var steps = new List<CampaignExecutionStep>(battle.Steps.Count);
        foreach (var step in battle.Steps)
        {
            // 角色按**步骤类型**判定（不是按位置）：交错的 call/conditional 也能正确执行。
            var role = step.Kind switch
            {
                "conditional" => CampaignStepRole.Attempt,
                "terminal" => CampaignStepRole.Fallback,
                "super_delegate" => CampaignStepRole.Delegate,
                _ => CampaignStepRole.Setup,
            };
            steps.Add(new CampaignExecutionStep(role, step.Op, CampaignPrimitiveRegistry.IsImplemented(step.Op),
                                                Describe(step)));
        }
        return new CampaignExecutionTrace(plan.Chapter, plan.Level, battle.Method, steps);
    }

    /// <summary>干跑一个关卡的全部钩子。</summary>
    public static IReadOnlyList<CampaignExecutionTrace> DryRun(CampaignPlan plan) =>
        plan.Header.Battles.Select(battle => DryRun(plan, battle)).ToArray();

    /// <summary>统计一章的执行面：出现的原语、已实现数、各角色步数、形状符合契约的钩子数。</summary>
    public static CampaignExecutionSurface Summarize(string chapter, IEnumerable<CampaignPlan> plans)
    {
        var ops = new Dictionary<string, int>(StringComparer.Ordinal);
        int steps = 0, setup = 0, attempts = 0, fallbacks = 0, delegates = 0, conforming = 0, hooks = 0;
        foreach (var plan in plans)
        {
            foreach (var battle in plan.Header.Battles)
            {
                hooks++;
                var shape = Validate(battle);
                if (shape.Conforms) conforming++;
                foreach (var trace in new[] { DryRun(plan, battle) })
                {
                    foreach (var step in trace.Steps)
                    {
                        steps++;
                        switch (step.Role)
                        {
                            case CampaignStepRole.Setup: setup++; break;
                            case CampaignStepRole.Attempt: attempts++; break;
                            case CampaignStepRole.Fallback: fallbacks++; break;
                            default: delegates++; break;
                        }
                        if (step.Role != CampaignStepRole.Delegate)
                            ops[step.Op] = ops.GetValueOrDefault(step.Op) + 1;
                    }
                }
            }
        }
        return new CampaignExecutionSurface(chapter, hooks, conforming, steps, setup, attempts, fallbacks,
                                            delegates, ops);
    }

    private static string Describe(CampaignPlanStep step)
    {
        var parts = new List<string>();
        foreach (var value in step.Args?.Positional ?? [])
        {
            parts.Add(value?.ToJsonString() ?? "null");
        }
        if (step.Args is { } args)
        {
            foreach (var (name, value) in args.Keyword)
            {
                parts.Add($"{name}={value?.ToJsonString() ?? "null"}");
            }
        }
        return parts.Count == 0 ? "" : string.Join(", ", parts);
    }
}

/// <summary>一章的执行面快照。</summary>
public sealed record CampaignExecutionSurface(
    string Chapter,
    int Hooks,
    int ConformingHooks,
    int Steps,
    int Setup,
    int Attempts,
    int Fallbacks,
    int Delegates,
    IReadOnlyDictionary<string, int> Ops)
{
    /// <summary>已实现的原语数（注册表交集的规模）。</summary>
    public int ImplementedOps => Ops.Keys.Count(CampaignPrimitiveRegistry.IsImplemented);
}
