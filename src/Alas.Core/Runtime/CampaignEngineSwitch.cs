namespace Alas.Runtime;

/// <summary>一个域的执行后端。</summary>
public enum CampaignEngineMode
{
    /// <summary>完全走上游（不记录影子比对）——最保守。</summary>
    Upstream,

    /// <summary>走上游 + **只算不执行**地记录 C# 决策（默认值：不改变任何动作，只多一份观测）。</summary>
    Shadow,

    /// <summary>由 C# 执行该域——**需要二次闸门**（见下），否则拒绝并退回影子模式。</summary>
    CSharp,
}

/// <summary>
/// 一个域的开关状态。<paramref name="DependsOn"/> 表示"本域只在依赖域切成 csharp 后才会被调用"——
/// 实测：C# 的寻路与原语**没有独立的调用点**，它们只会在 C# 关卡循环内部被用到（见文档 P3 接线分析）。
/// </summary>
public sealed record CampaignEngineDomain(string Name, CampaignEngineMode Mode, bool Wired,
                                          string Note, string? DependsOn = null);

/// <summary>
/// 域级执行开关（P2「回退能力」的代码骨架）。
///
/// 设计约束（与文档里的"切换与回退设计"一致）：
/// <list type="bullet">
///   <item>**默认不改行为**：`loop` 域默认 <see cref="CampaignEngineMode.Shadow"/>——仍然跑上游，
///         只是多记录一份 C# 影子决策；</item>
///   <item>**域之间有依赖**：实测 C# 生产路径**没有任何寻路/原语调用点**（它们只服务离线对拍），
///         所以 `path` / `primitives` 只有在 `loop` 切成 `csharp` 后才会被调用。`loop` 没切时，
///         这两个域**即便设成 csharp 也不生效**，快照里会改写成 upstream 并说明原因——
///         把"依赖关系"写成显式字段，而不是让使用者以为可以独立切换；</item>
///   <item>**`csharp` 是双钥匙**：既要 `ALAS_ENGINE_&lt;域&gt;=csharp`，又要
///         `ALAS_ENGINE_ALLOW_CSHARP=1`（表示"真实路径证据已备齐、由人明确放行"）。
///         缺第二把钥匙时**拒绝**并退回影子模式，把拒绝原因记进日志——绝不在没有证据时悄悄切换；</item>
///   <item>取值非法一律**退回默认**并报出原因，不猜。</item>
/// </list>
///
/// 环境变量：`ALAS_ENGINE_LOOP` / `ALAS_ENGINE_PATH` / `ALAS_ENGINE_PRIMITIVES`，
/// 取值 `upstream` / `shadow` / `csharp`（大小写不敏感）。
/// </summary>
public static class CampaignEngineSwitch
{
    public const string AllowCSharpVariable = "ALAS_ENGINE_ALLOW_CSHARP";

    /// <summary>域 → 环境变量名、是否已接进生产路径、依赖域。</summary>
    private static readonly (string Domain, string Variable, bool Wired, string? DependsOn, string Note)[] Domains =
    [
        ("loop", "ALAS_ENGINE_LOOP", true, null,
            "关卡循环：生产路径仍走上游 Campaign.run()；shadow = 额外记录 C# 决策，csharp = 由 C# 执行（需闸门）"),
        ("path", "ALAS_ENGINE_PATH", false, "loop",
            "寻路：**没有独立调用点**，只会在 C# 关卡循环内部被调用；loop 未切 csharp 时本域不生效"),
        ("primitives", "ALAS_ENGINE_PRIMITIVES", false, "loop",
            "原语执行：同上——C# 原语只在 C# 关卡循环内部被调用；loop 未切 csharp 时本域不生效"),
    ];

    /// <summary>本次进程的开关快照（每个域一条，含拒绝/依赖说明）。</summary>
    public static IReadOnlyList<CampaignEngineDomain> Snapshot(Func<string, string?>? readVariable = null)
    {
        readVariable ??= Environment.GetEnvironmentVariable;
        bool allowCSharp = readVariable(AllowCSharpVariable) == "1";
        var result = new List<CampaignEngineDomain>();
        CampaignEngineMode loopMode = CampaignEngineMode.Shadow;

        foreach (var (domain, variable, wired, dependsOn, note) in Domains)
        {
            string? raw = readVariable(variable);
            CampaignEngineMode mode;
            string resolvedNote = note;
            if (string.IsNullOrWhiteSpace(raw))
            {
                // 默认：循环域记影子（不改变动作），其它域保持"走上游"的表述
                mode = domain == "loop" ? CampaignEngineMode.Shadow : CampaignEngineMode.Upstream;
            }
            else
            {
                switch (raw.Trim().ToLowerInvariant())
                {
                    case "upstream":
                        mode = CampaignEngineMode.Upstream;
                        break;
                    case "shadow":
                        mode = CampaignEngineMode.Shadow;
                        break;
                    case "csharp":
                        if (allowCSharp)
                        {
                            mode = CampaignEngineMode.CSharp;
                            resolvedNote += $"；已由 {AllowCSharpVariable}=1 放行";
                        }
                        else
                        {
                            mode = CampaignEngineMode.Shadow;
                            resolvedNote = $"拒绝 csharp：缺少 {AllowCSharpVariable}=1" +
                                           "（没有真实路径证据不许切换）→ 退回影子模式";
                        }
                        break;
                    default:
                        mode = domain == "loop" ? CampaignEngineMode.Shadow : CampaignEngineMode.Upstream;
                        resolvedNote = $"取值非法（{raw.Trim()}）→ 退回默认；合法值：upstream / shadow / csharp";
                        break;
                }
            }

            // 依赖域没切成 csharp：本域即便设成 csharp 也不生效（没有调用点），改写为 upstream 并说明。
            if (dependsOn == "loop" && mode == CampaignEngineMode.CSharp && loopMode != CampaignEngineMode.CSharp)
            {
                mode = CampaignEngineMode.Upstream;
                resolvedNote = "依赖 loop 域：loop 未切到 csharp，本域**不生效**（C# 实现没有独立调用点）";
            }
            else if (!wired && mode == CampaignEngineMode.CSharp)
            {
                // 取值层面允许（人明确放行了），但如实说明：当前没有任何调用点消费它。
                resolvedNote += "；（尚未接线：当前没有调用点消费这个取值，改它不会改变行为）";
            }

            if (domain == "loop") loopMode = mode;
            result.Add(new CampaignEngineDomain(domain, mode, wired, resolvedNote, dependsOn));
        }
        return result;
    }

    /// <summary>循环域的当前模式（生产路径用这个决定"要不要记录影子比对"）。</summary>
    public static CampaignEngineMode LoopMode(Func<string, string?>? readVariable = null) =>
        Snapshot(readVariable).First(domain => domain.Name == "loop").Mode;
}
