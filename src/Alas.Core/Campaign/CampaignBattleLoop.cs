namespace Alas.Campaign;

/// <summary>
/// 关卡循环的结果类别（逐项对应上游语义）：
/// <list type="bullet">
///   <item><c>Ended</c>：收到 <c>CampaignEnd</c>（上游 <c>run()</c> 捕获后 <c>return True</c>）；</item>
///   <item><c>Exhausted</c>：20 轮打完仍没有结束（上游记 <c>Battle function exhausted.</c>）；</item>
///   <item><c>ScriptError</c>：某次出击没打成且 <c>Error_HandleError=False</c>（上游 <c>raise ScriptError</c>）；</item>
///   <item><c>Blocked</c>：撞到未实现的原语 / 未求值实参 / 未迁移的控制流——如实停下，不假装跑完。</item>
/// </list>
/// </summary>
public enum CampaignLevelOutcome
{
    Ended,
    Exhausted,
    ScriptError,
    Blocked,
}

/// <summary>一轮出击的记录：选了哪个钩子、结果、以及被阻塞的原因。</summary>
public sealed record CampaignBattleRound(
    int Index,
    int BattleCount,
    string Hook,
    bool? Result,
    string? BlockedReason);

/// <summary>关卡循环的完整结果。</summary>
public sealed record CampaignLevelRun(
    string Chapter,
    string Level,
    CampaignLevelOutcome Outcome,
    string? Detail,
    IReadOnlyList<CampaignBattleRound> Rounds)
{
    public bool Succeeded => Outcome == CampaignLevelOutcome.Ended;
}

/// <summary>
/// 关卡循环的 C# 移植，逐条对应上游 <c>module/campaign/campaign_base.py</c>：
/// <list type="bullet">
///   <item><c>run()</c>：最多 20 轮「出击」，捕获 <c>CampaignEnd</c> 即返回成功，否则记
///         <c>Battle function exhausted.</c> 并按 <c>Error_HandleError</c> 决定撤退还是抛 <c>ScriptError</c>；</item>
///   <item><c>execute_a_battle()</c>：最多 10 次尝试，捕获 <c>MapEnemyMoved</c>——期间 <c>battle_count</c> 增长就算成功，
///         否则重试；最终没打成时记 <c>No combat executed.</c> 并按 <c>Error_HandleError</c> 处理；</item>
///   <item><c>battle_function()</c>：按配置三选一（见 <see cref="BattleFunction"/>）。</item>
/// </list>
///
/// **未移植/待真机确认的部分（如实标注）**：
/// ① 标准流程里 <c>CampaignEnd</c> 由 <c>MapOperation.withdraw()</c> 在检测到已回到章节页时抛出
/// （`module/map/map_operation.py:410`）；干跑宿主把 <c>Withdraw()</c> 视作"本关结束请求"，
/// 这是**近似**，真机上要确认结束时机；
/// ② `fleet_2_break_siren_caught` / `brute_clear_boss` / `clear_bouncing_enemy` 等原语尚未实现——
/// 走到它们时按 <c>Blocked</c> 停下，不猜。
/// </summary>
public static class CampaignBattleLoop
{
    /// <summary>上游 <c>run()</c> 的轮数上限。</summary>
    public const int MaxRounds = 20;

    /// <summary>上游 <c>execute_a_battle()</c> 的重试上限。</summary>
    public const int MaxAttempts = 10;

    /// <summary>上游按 <c>battle_count</c> 回看钩子的窗口（<c>for extra_battle in range(10)</c>）。</summary>
    public const int HookLookback = 10;

    public static CampaignLevelRun Run(CampaignPlan plan, ICampaignPrimitiveHost host)
    {
        var rounds = new List<CampaignBattleRound>();
        for (int round = 0; round < MaxRounds; round++)
        {
            int battleCountBefore = host.BattleCount;
            var attempt = ExecuteABattle(plan, host, round);
            rounds.Add(attempt);

            if (attempt.BlockedReason is not null)
            {
                return new CampaignLevelRun(plan.Chapter, plan.Level, CampaignLevelOutcome.Blocked,
                                            attempt.BlockedReason, rounds);
            }

            // 上游：withdraw() 检测到已回到章节页时 raise CampaignEnd，run() 捕获后 return True。
            if (host.EndRequested)
            {
                host.Log($"Campaign end（{host.EndReason ?? "未注明"}）");
                return new CampaignLevelRun(plan.Chapter, plan.Level, CampaignLevelOutcome.Ended,
                                            host.EndReason, rounds);
            }

            if (attempt.Result != true)
            {
                host.Log($"ScriptError, No combat executed.（battle_count {battleCountBefore} → {host.BattleCount}）");
                if (host.Config.ErrorHandleError)
                {
                    host.Log("ScriptError, No combat executed, Withdrawing");
                    host.Withdraw();
                    if (host.EndRequested)
                    {
                        return new CampaignLevelRun(plan.Chapter, plan.Level, CampaignLevelOutcome.Ended,
                                                    host.EndReason, rounds);
                    }
                    continue;
                }
                return new CampaignLevelRun(plan.Chapter, plan.Level, CampaignLevelOutcome.ScriptError,
                                            "No combat executed.", rounds);
            }
        }

        host.Log("Battle function exhausted.");
        if (host.Config.ErrorHandleError)
        {
            host.Log("ScriptError, Battle function exhausted, Withdrawing");
            host.Withdraw();
            return new CampaignLevelRun(plan.Chapter, plan.Level, CampaignLevelOutcome.Ended,
                                        host.EndReason ?? "Battle function exhausted", rounds);
        }
        return new CampaignLevelRun(plan.Chapter, plan.Level, CampaignLevelOutcome.Exhausted,
                                    "Battle function exhausted.", rounds);
    }

    /// <summary>上游 <c>execute_a_battle()</c>：最多 10 次尝试，<c>MapEnemyMoved</c> 时按 battle_count 判定。</summary>
    private static CampaignBattleRound ExecuteABattle(CampaignPlan plan, ICampaignPrimitiveHost host, int round)
    {
        int battleCountBefore = host.BattleCount;

        // 变体 1/2 是固定原语序列（fleet_2_break_siren_caught / brute_clear_boss / clear_bouncing_enemy …），
        // 这些原语尚未实现——明确阻塞，绝不按默认策略（钩子选择）静默跑错。
        string variant = BattleFunctionVariant(host.Config);
        if (variant != "default_hooks")
        {
            return new CampaignBattleRound(round, battleCountBefore, "battle_function", null,
                $"battle_function 变体 {variant} 尚未迁移（需要 fleet_2_break_siren_caught 等原语）");
        }

        string hook = SelectHook(plan, host.BattleCount);
        host.Log($"{hook}（第 {round + 1} 轮，battle_count={host.BattleCount}）");

        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var battle = plan.Header.Battles.FirstOrDefault(item => item.Method == hook);
            if (battle is null)
            {
                // 上游 `hasattr(self, 'battle_default')` 会命中**基类** CampaignBase 的实现
                // （`module/campaign/campaign_base.py`），关卡自身没有这个方法也照样可用。
                if (hook == "battle_default")
                {
                    bool defaultResult = CampaignPrimitives.BattleDefault(host);
                    return new CampaignBattleRound(round, battleCountBefore, hook, defaultResult, null);
                }
                if (hook == "battle_boss")
                {
                    return new CampaignBattleRound(round, battleCountBefore, hook, null,
                        "基类 battle_boss 需要 brute_clear_boss（尚未迁移）");
                }
                return new CampaignBattleRound(round, battleCountBefore, hook, false,
                                               $"关卡导出与基类都没有钩子 {hook}");
            }

            var execution = CampaignHookRunner.Run(plan, battle, host);
            if (execution.BlockedReason is null)
            {
                return new CampaignBattleRound(round, battleCountBefore, hook, execution.ReturnValue, null);
            }
            // 上游：MapEnemyMoved 被 execute_a_battle 捕获；battle_count 增长即视为打过了，否则重试。
            if (execution.BlockedReason.Contains("MapEnemyMoved", StringComparison.Ordinal))
            {
                if (host.BattleCount > battleCountBefore)
                {
                    return new CampaignBattleRound(round, battleCountBefore, hook, true, null);
                }
                host.Log("MapEnemyMoved：battle_count 未增长，重试");
                continue;
            }
            return new CampaignBattleRound(round, battleCountBefore, hook, null, execution.BlockedReason);
        }

        return new CampaignBattleRound(round, battleCountBefore, hook, false,
                                       "MapEnemyMoved 重试超过 10 次");
    }

    /// <summary>
    /// 上游默认 <c>battle_function()</c>（`MAP_CLEAR_ALL_THIS_TIME=False, POOR_MAP_DATA=False`）：
    /// 从 <c>battle_{battle_count}</c> 往回找最多 10 个已定义的钩子，找到就用它，否则用 <c>battle_default</c>。
    /// </summary>
    public static string SelectHook(CampaignPlan plan, int battleCount)
    {
        for (int extra = 0; extra < HookLookback; extra++)
        {
            int index = battleCount - extra;
            if (index < 0) break;
            string candidate = $"battle_{index}";
            if (plan.Header.Battles.Any(item => item.Method == candidate)) return candidate;
        }
        return "battle_default";
    }

    /// <summary>
    /// 上游 <c>battle_function()</c> 的三个 <c>@Config.when</c> 变体。
    /// 变体 3（默认）走关卡覆写钩子（有静态计划）；变体 1/2 是固定原语序列，
    /// 其中 <c>fleet_2_break_siren_caught</c> / <c>brute_clear_boss</c> / <c>clear_bouncing_enemy</c>
    /// 尚未实现 → 按 <c>Blocked</c> 如实停下。
    /// </summary>
    public static string BattleFunctionVariant(CampaignRuntimeConfig config) =>
        config.MapClearAllThisTime ? "clear_all"
        : config.PoorMapData ? "battle_with_poor_map_data"
        : "default_hooks";
}
