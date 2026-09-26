using System.Text.RegularExpressions;

namespace Alas.Campaign;

/// <summary>上游一次出击的观测（从运行日志里解析出来）。</summary>
public sealed record UpstreamBattleObservation(
    int BattleCount,
    string HeaderHook,
    string? UsingFunction,
    bool NoCombatExecuted);

/// <summary>上游一次运行的观测结果（日志里的关键信号）。</summary>
public sealed record UpstreamRunObservation(
    IReadOnlyList<UpstreamBattleObservation> Rounds,
    bool CampaignEnd,
    bool BattleFunctionExhausted)
{
    public IReadOnlyList<string> UsedFunctions =>
        Rounds.Where(round => round.UsingFunction is not null)
              .Select(round => round.UsingFunction!)
              .ToArray();
}

/// <summary>
/// 上游运行日志的解析：只认上游自己打的信号，便于把"上游实际做了什么"与 C# 影子决策对齐。
///
/// 认的信号（都在 `module/campaign/campaign_base.py` 里）：
/// <list type="bullet">
///   <item><c>logger.hr(f'{FUNCTION_NAME_BASE}{self.battle_count}', level=2)</c> → 规则行
///         <c>──── BATTLE_0 ────</c> 与随后的 <c>BATTLE_0</c> 大写行 → **本次出击的 battle_count**；</item>
///   <item><c>logger.info(f'Using function: {func}')</c> → **实际调用的钩子名**；</item>
///   <item><c>ScriptError, No combat executed.</c> → 这次出击没打成；</item>
///   <item><c>Campaign end</c> / <c>Battle function exhausted.</c> → 关卡结束的两种方式。</item>
/// </list>
/// 解析不出来的一律跳过（不猜），因此日志格式变化时表现为"轮次变少"而不是错误结论。
/// </summary>
public static class UpstreamLogParser
{
    private static readonly Regex HeaderRule = new(@"^\s*[─-]{3,}\s*(BATTLE_\d+)\s*[─-]{3,}\s*$", RegexOptions.Compiled);
    private static readonly Regex HeaderPlain = new(@"^\s*(?:INFO|\S+)?\s*.*?\b(BATTLE_\d+)\b\s*$", RegexOptions.Compiled);
    private static readonly Regex UsingFunction = new(@"Using function:\s*([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

    public static UpstreamRunObservation Parse(string text)
    {
        var rounds = new List<UpstreamBattleObservation>();
        bool campaignEnd = false, exhausted = false;
        int? currentBattle = null;
        string? currentHeader = null;
        bool currentFromRule = false;
        string? pendingFunction = null;
        bool noCombat = false;

        void Flush()
        {
            if (currentBattle is null) return;
            rounds.Add(new UpstreamBattleObservation(currentBattle.Value, currentHeader ?? $"BATTLE_{currentBattle}",
                                                     pendingFunction, noCombat));
        }

        foreach (string raw in text.Replace("\r", "").Split('\n'))
        {
            string line = raw.TrimEnd();
            var rule = HeaderRule.Match(line);
            var plain = rule.Success ? null : HeaderPlain.Match(line);
            string? header = rule.Success ? rule.Groups[1].Value : plain is { Success: true } ? plain.Groups[1].Value : null;
            if (header is not null && int.TryParse(header["BATTLE_".Length..], out int battleCount))
            {
                // `logger.hr(title, level=2)` 会打**两行**：先规则行（一串 ─），再 INFO 行（同名大写）。
                // 只有规则行开新一轮；紧跟其后的同名 INFO 行属于同一轮（否则每轮会被算两次——实测踩到）。
                bool duplicateInfoLine = !rule.Success && currentFromRule
                                         && currentBattle == battleCount && currentHeader == header;
                if (!duplicateInfoLine)
                {
                    Flush();
                    currentBattle = battleCount;
                    currentHeader = header;
                    currentFromRule = rule.Success;
                    pendingFunction = null;
                    noCombat = false;
                }
                continue;
            }

            var used = UsingFunction.Match(line);
            if (used.Success)
            {
                pendingFunction = used.Groups[1].Value;
                continue;
            }
            if (line.Contains("No combat executed", StringComparison.Ordinal)) noCombat = true;
            if (line.Contains("Campaign end", StringComparison.OrdinalIgnoreCase)) campaignEnd = true;
            if (line.Contains("Battle function exhausted", StringComparison.Ordinal)) exhausted = true;
        }
        Flush();

        // 没打成的那一轮把标记带进观测
        if (noCombat && rounds.Count > 0)
        {
            rounds[^1] = rounds[^1] with { NoCombatExecuted = true };
        }
        return new UpstreamRunObservation(rounds, campaignEnd, exhausted);
    }
}

/// <summary>影子比对的一行：某一轮 battle_count 下，C# 会选什么钩子 vs 上游实际选了什么。</summary>
public sealed record ShadowComparisonRow(
    int BattleCount,
    string Expected,
    string? Actual,
    string Verdict,
    string? Note);

/// <summary>影子比对结果。</summary>
public sealed record ShadowComparison(
    string Chapter,
    string Level,
    IReadOnlyList<ShadowComparisonRow> Rows,
    int Matched,
    int Mismatched,
    int Skipped)
{
    public bool Clean => Matched > 0 && Mismatched == 0 && Skipped == 0;
}

/// <summary>
/// **影子模式**的核心：C# 只**算**不执行——对"上游这次运行实际发生的每一轮出击"，算出 C# 引擎
/// 在同一 battle_count 下会选哪个钩子，然后与上游日志里的 <c>Using function: …</c> 逐步比对。
///
/// 这是"域级开关注入生产路径之前"的前置验证：离线干跑只能证明引擎自洽，影子比对才能证明
/// **决策与上游一致**。差异按行报出，不掩盖。
/// </summary>
public static class CampaignShadow
{
    /// <summary>非默认变体（上游 `battle_function` 的另两个 @Config.when 分支）。</summary>
    private static readonly HashSet<string> VariantFunctions = new(StringComparer.Ordinal)
    {
        "clear_all", "battle_with_poor_map_data",
    };

    /// <summary>本次比对声明的变体：默认变体（按 battle_count 选钩子）或另两个变体名。</summary>
    public const string DefaultVariant = "default_hooks";

    public static ShadowComparison Compare(CampaignPlan plan, UpstreamRunObservation observation,
                                           string declaredVariant = DefaultVariant)
    {
        var rows = new List<ShadowComparisonRow>();
        int matched = 0, mismatched = 0, skipped = 0;
        bool declaredIsVariant = VariantFunctions.Contains(declaredVariant);
        foreach (var round in observation.Rounds)
        {
            string expected = declaredIsVariant ? declaredVariant : CampaignBattleLoop.SelectHook(plan, round.BattleCount);
            string? actual = round.UsingFunction;
            if (actual is null)
            {
                rows.Add(new ShadowComparisonRow(round.BattleCount, expected, null, "跳过", "日志里没有 Using function"));
                skipped++;
                continue;
            }
            if (string.Equals(expected, actual, StringComparison.Ordinal))
            {
                rows.Add(new ShadowComparisonRow(round.BattleCount, expected, actual, "一致", null));
                matched++;
                continue;
            }
            string note;
            if (VariantFunctions.Contains(actual))
            {
                note = $"上游走了变体 {actual}，但本次比对声明的是 {declaredVariant}"
                     + $"（若该运行确实配置了它，请用 --variant {actual} 重跑）";
            }
            else if (declaredIsVariant)
            {
                note = $"本次比对声明变体 {declaredVariant}，但上游实际按 battle_count 选了钩子 {actual}";
            }
            else
            {
                bool inPlan = plan.Header.Battles.Any(item => item.Method == actual);
                note = inPlan ? "两边选的钩子不同" : "上游调的钩子在关卡导出里没有";
            }
            rows.Add(new ShadowComparisonRow(round.BattleCount, expected, actual, "不一致", note));
            mismatched++;
        }
        return new ShadowComparison(plan.Chapter, plan.Level, rows, matched, mismatched, skipped);
    }
}
