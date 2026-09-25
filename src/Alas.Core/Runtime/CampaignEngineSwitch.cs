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

/// <summary>一个域的开关状态。</summary>
public sealed record CampaignEngineDomain(string Name, CampaignEngineMode Mode, bool Wired, string Note);

/// <summary>
/// 域级执行开关（P2「回退能力」的代码骨架）。
///
/// 设计约束（与文档里的"切换与回退设计"一致）：
/// <list type="bullet">
///   <item>**默认不改行为**：`loop` 域默认 <see cref="CampaignEngineMode.Shadow"/>——仍然跑上游，
///         只是多记录一份 C# 影子决策；另两个域（`path` / `primitives`）在生产路径上**根本没接线**，
///         所以它们的取值目前不会改变任何行为（<see cref="CampaignEngineDomain.Wired"/> 为 false 时如实标注）；</item>
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

    /// <summary>域 → 环境变量名。（`path` / `primitives` 尚未接进生产路径，保留给后续迁移。）</summary>
    private static readonly (string Domain, string Variable, bool Wired, string Note)[] Domains =
    [
        ("loop", "ALAS_ENGINE_LOOP", true,
            "关卡循环：生产路径仍走上游 Campaign.run()；shadow = 额外记录 C# 决策，csharp = 由 C# 执行（需闸门）"),
        ("path", "ALAS_ENGINE_PATH", false,
            "寻路：生产路径由上游 Fleet.find_path_initial 完成，C# 实现只用于离线对拍，尚未接线"),
        ("primitives", "ALAS_ENGINE_PRIMITIVES", false,
            "原语执行：生产路径由上游地图/战斗代码完成，C# 实现只用于离线干跑，尚未接线"),
    ];

    /// <summary>本次进程的开关快照（每个域一条，含拒绝原因）。</summary>
    public static IReadOnlyList<CampaignEngineDomain> Snapshot(Func<string, string?>? readVariable = null)
    {
        readVariable ??= Environment.GetEnvironmentVariable;
        bool allowCSharp = readVariable(AllowCSharpVariable) == "1";
        var result = new List<CampaignEngineDomain>();
        foreach (var (domain, variable, wired, note) in Domains)
        {
            string? raw = readVariable(variable);
            if (string.IsNullOrWhiteSpace(raw))
            {
                // 默认：循环域记影子（不改变动作），未接线的域保持"走上游"的表述
                result.Add(new CampaignEngineDomain(domain,
                    domain == "loop" ? CampaignEngineMode.Shadow : CampaignEngineMode.Upstream, wired, note));
                continue;
            }
            switch (raw.Trim().ToLowerInvariant())
            {
                case "upstream":
                    result.Add(new CampaignEngineDomain(domain, CampaignEngineMode.Upstream, wired, note));
                    break;
                case "shadow":
                    result.Add(new CampaignEngineDomain(domain, CampaignEngineMode.Shadow, wired, note));
                    break;
                case "csharp":
                    result.Add(allowCSharp
                        ? new CampaignEngineDomain(domain, CampaignEngineMode.CSharp, wired,
                            note + "；已由 " + AllowCSharpVariable + "=1 放行")
                        : new CampaignEngineDomain(domain, CampaignEngineMode.Shadow, wired,
                            $"拒绝 csharp：缺少 {AllowCSharpVariable}=1（没有真实路径证据不许切换）→ 退回影子模式"));
                    break;
                default:
                    result.Add(new CampaignEngineDomain(domain,
                        domain == "loop" ? CampaignEngineMode.Shadow : CampaignEngineMode.Upstream, wired,
                        $"取值非法（{raw.Trim()}）→ 退回默认；合法值：upstream / shadow / csharp"));
                    break;
            }
        }
        return result;
    }

    /// <summary>循环域的当前模式（生产路径用这个决定"要不要记录影子比对"）。</summary>
    public static CampaignEngineMode LoopMode(Func<string, string?>? readVariable = null) =>
        Snapshot(readVariable).First(domain => domain.Name == "loop").Mode;
}
