using System.Text.RegularExpressions;

namespace Alas.Campaign;

/// <summary>上游运行日志里识别出的一次原语级动作。</summary>
public sealed record CampaignAction(string Primitive, string? Target, string Raw);

/// <summary>原语级动作轨迹：本次上游运行实际用过哪些原语、打了哪些格子。</summary>
public sealed record CampaignActionTrace(
    IReadOnlyList<CampaignAction> Actions,
    IReadOnlyList<string> Primitives,
    IReadOnlyList<string> UnknownMarkers)
{
    public int Count => Actions.Count;
}

/// <summary>
/// 从上游运行日志里抽**原语级动作轨迹**：把上游自己打的日志行映射回我们的原语名
/// （`<<< CLEAR ENEMY >>>` → <c>clear_enemy</c>、`Clear enemy: F1` → <c>clear_chosen_enemy(F1)</c>、
/// `Pick up ammo: B2` → <c>pick_up_ammo(B2)</c> 等）。
///
/// 用途：① 说明"这次真机运行到底走了哪些原语"；② 与 C# 侧已实现原语对照，暴露"真机用了但我们没实现"
/// 的原语（这类缺口离线干跑发现不了）；③ 为原语动作层对拍准备输入。
///
/// 只认列在表里的上游日志行；认不出的行**原样收进 <see cref="CampaignActionTrace.UnknownMarkers"/>
/// 供人工核对**，不猜、不编造动作。
/// </summary>
public static class UpstreamActionParser
{
    private sealed record Rule(Regex Pattern, string Primitive, int TargetGroup = 0);

    private static readonly Rule[] Rules =
    [
        // 表头（`logger.hr(...)` → `<<< X >>>`）
        new(new Regex(@"<<<\s*CLEAR FILTER ENEMY\s*>>>", RegexOptions.Compiled), "clear_filter_enemy"),
        new(new Regex(@"<<<\s*CLEAR FIRST ROADBLOCK\s*>>>", RegexOptions.Compiled), "clear_first_roadblocks"),
        new(new Regex(@"<<<\s*AVOID POTENTIAL ROADBLOCK\s*>>>", RegexOptions.Compiled), "clear_potential_roadblocks"),
        new(new Regex(@"<<<\s*CLEAR ROADBLOCK\s*>>>", RegexOptions.Compiled), "clear_roadblocks"),
        new(new Regex(@"<<<\s*CLEAR ALL MYSTERY\s*>>>", RegexOptions.Compiled), "clear_all_mystery"),
        new(new Regex(@"<<<\s*CLEAR BOUNCING ENEMY\s*>>>", RegexOptions.Compiled), "clear_bouncing_enemy"),
        new(new Regex(@"<<<\s*CLEAR POTENTIAL BOSS\s*>>>", RegexOptions.Compiled), "clear_potential_boss"),
        new(new Regex(@"<<<\s*CLEAR SIREN\s*>>>", RegexOptions.Compiled), "clear_siren"),
        new(new Regex(@"<<<\s*CLEAR BOSS\s*>>>", RegexOptions.Compiled), "clear_boss"),
        new(new Regex(@"<<<\s*CLEAR ENEMY\s*>>>", RegexOptions.Compiled), "clear_enemy"),
        // 动作行
        new(new Regex(@"Brute clear BOSS roadblocks", RegexOptions.Compiled), "brute_clear_boss"),
        new(new Regex(@"Brute clear roadblocks between fleets", RegexOptions.Compiled), "brute_fleet_meet"),
        new(new Regex(@"Brute clear BOSS\b", RegexOptions.Compiled), "brute_clear_boss"),
        new(new Regex(@"Break siren caught, fleet_2:\s*(\S+)", RegexOptions.Compiled), "fleet_2_break_siren_caught", 1),
        new(new Regex(@"Clear bouncing enemy:\s*(.+)$", RegexOptions.Compiled), "clear_bouncing_enemy", 1),
        new(new Regex(@"Clear mechanism:\s*(\S+)", RegexOptions.Compiled), "clear_mechanism", 1),
        new(new Regex(@"Mechanism all cleared", RegexOptions.Compiled), "clear_mechanism"),
        new(new Regex(@"Pick up ammo:\s*(\S+)", RegexOptions.Compiled), "pick_up_ammo", 1),
        new(new Regex(@"Map has no ammo", RegexOptions.Compiled), "pick_up_ammo"),
        new(new Regex(@"Pick up light house on\s*(\S+)", RegexOptions.Compiled), "pick_up_light_house", 1),
        new(new Regex(@"Pick up flares on\s*(\S+)", RegexOptions.Compiled), "pick_up_flare", 1),
        new(new Regex(@"Fleet_2 push forward", RegexOptions.Compiled), "fleet_2_push_forward"),
        new(new Regex(@"Fleet_2 rescue", RegexOptions.Compiled), "fleet_2_rescue"),
        new(new Regex(@"Fleet_2 step on\s*(\S+)", RegexOptions.Compiled), "fleet_2_step_on", 1),
        new(new Regex(@"Fleet 2 step on got roadblocks", RegexOptions.Compiled), "fleet_2_step_on"),
        new(new Regex(@"Enemy roadblock:\s*(.+)$", RegexOptions.Compiled), "brute_clear_boss", 1),
        new(new Regex(@"Clear enemy:\s*(\S+)", RegexOptions.Compiled), "clear_chosen_enemy", 1),
        // 舰队位置标记：上游 `logger.attr_align('Fleet_1', location)` → `[Fleet_1: C1]`。
        // 它在**每次出击前后**打一次，串起来就是这一局的走位序列（路线层对照用）。
        new(new Regex(@"\[Fleet_1:\s*([A-Z]+\d+)\]", RegexOptions.Compiled), "fleet_1_position", 1),
        new(new Regex(@"\[Fleet_2:\s*([A-Z]+\d+)\]", RegexOptions.Compiled), "fleet_2_position", 1),
        new(new Regex(@"No battle executed", RegexOptions.Compiled), "battle_default"),
        new(new Regex(@"Grand Capture detected, Withdrawing", RegexOptions.Compiled), "capture_clear_boss"),
        new(new Regex(@"Failed to clear bouncing enemy", RegexOptions.Compiled), "clear_bouncing_enemy"),
    ];

    /// <summary>认得出动作、但**不属于已迁移原语**的上游标记（用于说明"这次运行还走了什么"）。</summary>
    private static readonly Regex[] ContextMarkers =
    [
        new(@"\[Map_clear_percentage\]", RegexOptions.Compiled),
        new(@"MAP_CLEAR_ALL_THIS_TIME", RegexOptions.Compiled),
        new(@"Using function:", RegexOptions.Compiled),
    ];

    /// <summary>
    /// "像原语动作"的行特征：用来捞**表里没有**的动作行，供人工核对是否有新原语。
    /// 刻意收窄（要带动词与冒号/空格），避免把 `[Emotion fleet_2]`、`Hard satisfied: Fleet_1` 这类
    /// 属性行当成动作——实测这两类会把噪声算进"认不出的动作行"。
    /// </summary>
    private static readonly Regex[] ActionLikeMarkers =
    [
        new(@"Clear enemy:", RegexOptions.Compiled),
        new(@"Clear mechanism:", RegexOptions.Compiled),
        new(@"Clear bouncing enemy", RegexOptions.Compiled),
        new(@"Pick up (ammo|light house|flares)", RegexOptions.Compiled),
        new(@"Brute clear", RegexOptions.Compiled),
        new(@"Enemy roadblock:", RegexOptions.Compiled),
        new(@"Fleet_2 (push forward|rescue|step on)", RegexOptions.Compiled),
        new(@"Fleet_2 has no where to push", RegexOptions.Compiled),
        new(@"Fleet_2 pushed to", RegexOptions.Compiled),
        new(@"Mechanism all cleared", RegexOptions.Compiled),
        new(@"Failed to clear bouncing enemy", RegexOptions.Compiled),
        new(@"No battle executed", RegexOptions.Compiled),
        new(@"Grand Capture detected", RegexOptions.Compiled),
        new(@"BOSS not detected", RegexOptions.Compiled),
        new(@"Boss guessing", RegexOptions.Compiled),
    ];

    public static CampaignActionTrace Parse(string text)
    {
        var actions = new List<CampaignAction>();
        var unknown = new List<string>();
        foreach (string raw in text.Replace("\r", "").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            bool matched = false;
            foreach (var rule in Rules)
            {
                var match = rule.Pattern.Match(line);
                if (!match.Success) continue;
                string? target = rule.TargetGroup > 0 && match.Groups.Count > rule.TargetGroup
                    && match.Groups[rule.TargetGroup].Success
                    ? match.Groups[rule.TargetGroup].Value.Trim()
                    : null;
                actions.Add(new CampaignAction(rule.Primitive, target, line));
                matched = true;
                break;
            }
            if (matched) continue;
            if (ContextMarkers.Any(marker => marker.IsMatch(line))) continue;
            // 认不出的行不进轨迹；只收集**看起来像原语动作**的行（供人工核对是否有新原语）
            if (ActionLikeMarkers.Any(marker => marker.IsMatch(line)))
            {
                unknown.Add(line);
            }
        }
        var primitives = actions.Select(action => action.Primitive).Distinct(StringComparer.Ordinal).ToArray();
        return new CampaignActionTrace(actions, primitives, unknown);
    }
}
