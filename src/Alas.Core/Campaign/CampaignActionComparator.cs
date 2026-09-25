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
    public static CampaignActionDiff Compare(CampaignActionTrace upstream, CampaignActionTrace csharp)
    {
        var primitives = upstream.Primitives.Union(csharp.Primitives, StringComparer.Ordinal)
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

        var upstreamSet = upstream.Primitives.ToHashSet(StringComparer.Ordinal);
        var csharpSet = csharp.Primitives.ToHashSet(StringComparer.Ordinal);
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
}
