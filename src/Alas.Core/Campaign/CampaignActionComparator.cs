namespace Alas.Campaign;

/// <summary>一个原语在两边的用量与目标对照。</summary>
public sealed record CampaignActionDiffEntry(
    string Primitive,
    int UpstreamCount,
    int CSharpCount,
    IReadOnlyList<string> UpstreamTargets,
    IReadOnlyList<string> CSharpTargets)
{
    public bool TargetsIntersect => UpstreamTargets.Intersect(CSharpTargets, StringComparer.Ordinal).Any();
}

/// <summary>上游动作轨迹 vs C# 干跑动作轨迹的对照结果。</summary>
public sealed record CampaignActionDiff(
    IReadOnlyList<CampaignActionDiffEntry> Entries,
    IReadOnlyList<string> OnlyUpstream,
    IReadOnlyList<string> OnlyCSharp,
    IReadOnlyList<string> TargetMismatches)
{
    /// <summary>至少有一个共同原语的目标集合相交——"选的是同一批格子"的正面证据。</summary>
    public bool AnyTargetMatched => Entries.Any(entry => entry.TargetsIntersect);

    public bool SamePrimitives => OnlyUpstream.Count == 0 && OnlyCSharp.Count == 0;
}

/// <summary>
/// 原语动作层对照：把**上游实际动作**（从运行日志解析）与 **C# 干跑动作**（录制宿主记录）逐原语比对。
///
/// 比什么、不比什么（都写在结论里，避免误读）：
/// <list type="bullet">
///   <item>比**原语集合**与**目标格子**——"上游打了 D2，C# 也打 D2"这类一致性/不一致是实质性证据；</item>
///   <item>**不比重复次数**：干跑状态在一次运行内不刷新（识别结果不会变），所以同一个目标会被反复选中；
///         次数差异只能当提示，不能当引擎错误；</item>
///   <item>差异可能来自**状态来源不同**（上游是现场识别，C# 可能是声明地图或某张帧），结论里会写明状态来源。</item>
/// </list>
/// </summary>
public static class CampaignActionComparator
{
    /// <summary>
    /// **纯日志标记**：只在上游日志里存在、不是引擎原语（舰队位置标记已由路线层对照消费）。
    /// 不排除的话，它们会以"只有上游用到"的形式污染原语对照结论。
    /// </summary>
    private static readonly HashSet<string> LogOnlyMarkers = new(StringComparer.Ordinal)
    {
        "fleet_1_position", "fleet_2_position",
    };

    public static CampaignActionDiff Compare(CampaignActionTrace upstream, CampaignActionTrace csharp)
    {
        var upstreamPrimitives = upstream.Primitives.Where(name => !LogOnlyMarkers.Contains(name)).ToArray();
        var csharpPrimitives = csharp.Primitives.Where(name => !LogOnlyMarkers.Contains(name)).ToArray();
        var primitives = upstreamPrimitives.Union(csharpPrimitives, StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal);
        var entries = new List<CampaignActionDiffEntry>();
        foreach (string primitive in primitives)
        {
            var upstreamActions = upstream.Actions.Where(action => action.Primitive == primitive).ToArray();
            var csharpActions = csharp.Actions.Where(action => action.Primitive == primitive).ToArray();
            entries.Add(new CampaignActionDiffEntry(
                primitive,
                upstreamActions.Length,
                csharpActions.Length,
                Targets(upstreamActions),
                Targets(csharpActions)));
        }

        var upstreamSet = upstreamPrimitives.ToHashSet(StringComparer.Ordinal);
        var csharpSet = csharpPrimitives.ToHashSet(StringComparer.Ordinal);
        var mismatches = entries
            .Where(entry => entry.UpstreamTargets.Count > 0 && entry.CSharpTargets.Count > 0
                            && !entry.TargetsIntersect)
            .Select(entry => $"{entry.Primitive}：上游打 {string.Join("/", entry.UpstreamTargets)}，" +
                             $"C# 打 {string.Join("/", entry.CSharpTargets)}")
            .ToArray();

        return new CampaignActionDiff(
            entries,
            upstreamSet.Except(csharpSet).OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            csharpSet.Except(upstreamSet).OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            mismatches);
    }

    private static IReadOnlyList<string> Targets(IEnumerable<CampaignAction> actions) =>
        actions.Select(action => action.Target)
               .Where(target => !string.IsNullOrEmpty(target))
               .Select(target => target!)
               .Distinct(StringComparer.Ordinal)
               .OrderBy(target => target, StringComparer.Ordinal)
               .ToArray();

    /// <summary>路线层对照：上游的走位序列 vs C# 的攻击/走位序列（都按**出现顺序**去掉连续重复）。</summary>
    public sealed record CampaignRouteComparison(
        IReadOnlyList<string> UpstreamRoute,
        IReadOnlyList<string> CSharpRoute,
        IReadOnlyList<string> Common,
        bool SameOrder);

    /// <summary>
    /// 比"走的路"：上游用日志里的 `[Fleet_1: X]` 位置标记串成序列；C# 侧用**动作目标**的首次出现顺序
    /// （`clear_chosen_enemy(X)` 在下游本身就包含 `goto(X)`，所以它的目标就是"走到了哪"）。
    ///
    /// <see cref="CampaignRouteComparison.SameOrder"/>：两边共同格子在各自序列里的**相对顺序**是否一致
    /// （不是要求逐格相同——干跑状态不刷新，C# 会在同一格反复出手）。
    /// </summary>
    public static CampaignRouteComparison CompareRoute(CampaignActionTrace upstream, CampaignActionTrace csharp,
                                                       string fleetPrefix = "fleet_1_position")
    {
        var upstreamRoute = Distinct(upstream.Actions
            .Where(action => action.Primitive == fleetPrefix)
            .Select(action => action.Target));
        var csharpRoute = Distinct(csharp.Actions
            .Where(action => action.Primitive is "clear_chosen_enemy" or "goto" && action.Target is not null)
            .Select(action => action.Target));
        var common = upstreamRoute.Intersect(csharpRoute, StringComparer.Ordinal).ToArray();
        var upstreamOrder = upstreamRoute.ToList();
        var csharpOrder = csharpRoute.ToList();
        bool sameOrder = common.Length < 2
            || common.Select(value => upstreamOrder.IndexOf(value))
                     .SequenceEqual(common.Select(value => csharpOrder.IndexOf(value)));
        return new CampaignRouteComparison(upstreamRoute, csharpRoute, common, sameOrder);
    }

    /// <summary>按出现顺序去掉**连续重复**（上游每个出击点会打两次位置标记）。</summary>
    private static IReadOnlyList<string> Distinct(IEnumerable<string?> values)
    {
        var result = new List<string>();
        foreach (string? value in values)
        {
            if (string.IsNullOrEmpty(value)) continue;
            if (result.Count > 0 && result[^1] == value) continue;
            result.Add(value);
        }
        return result;
    }
}
