using System.Text.Json.Nodes;
using Alas.Campaign;

namespace Alas.Core.Diagnostics;

/// <summary>
/// R5 原语动作层对照：`Alas.Server r5-diff --log &lt;上游日志&gt; --chapter &lt;章&gt; --level &lt;关&gt; [--frame|--detection]`。
///
/// 一边是**上游实际动作**（从运行日志解析），一边是 **C# 干跑动作**（同一关卡上跑关卡循环，动作只被记录）；
/// 逐原语比对集合与**目标格子**——即"上游打了哪个格子，C# 会不会打同一个"。
///
/// 诚实边界（结论里也会打印）：
/// <list type="bullet">
///   <item>**不比重复次数**：干跑状态在一次运行内不刷新，同一目标会被反复选中，次数差异不是引擎错误；</item>
///   <item>差异可能来自**状态来源不同**（上游现场识别 vs C# 的声明地图/某张帧），所以结论必须带状态来源；</item>
///   <item>本命令**不连设备**：它只是把两份都来自磁盘/干跑的证据摆在一起。</item>
/// </list>
/// </summary>
internal static class CampaignDiffCheck
{
    public static int Run(string dataDir, string repoDir, string toolsDir, string? logPath,
                          CampaignDryRunHelper.Options options, bool asJson)
    {
        if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath))
        {
            return Fail("用法：r5-diff --log <上游运行日志> (--chapter <章> --level <关> | --chapter-module <模块名>) " +
                        "[--frame <地图帧> | --detection <识别.json>] [--json]");
        }
        if (!CampaignDryRunHelper.TryExecute(dataDir, repoDir, toolsDir, options, out var dryRun, out string? error))
        {
            return Fail(error ?? "干跑失败");
        }

        var upstream = UpstreamActionParser.Parse(File.ReadAllText(logPath));
        var csharp = CampaignDryRunHelper.ToTrace(dryRun!);
        var diff = CampaignActionComparator.Compare(upstream, csharp);

        if (asJson)
        {
            Console.WriteLine(new JsonObject
            {
                ["level"] = $"{dryRun!.Plan.Chapter}/{dryRun.Plan.Level}",
                ["detection"] = dryRun.DetectionSource,
                ["same_primitives"] = diff.SamePrimitives,
                ["any_target_matched"] = diff.AnyTargetMatched,
                ["only_upstream"] = new JsonArray(diff.OnlyUpstream.Select(name => (JsonNode)name!).ToArray()),
                ["only_csharp"] = new JsonArray(diff.OnlyCSharp.Select(name => (JsonNode)name!).ToArray()),
                ["target_mismatches"] = new JsonArray(diff.TargetMismatches.Select(text => (JsonNode)text!).ToArray()),
                ["entries"] = new JsonArray(diff.Entries.Select(entry => (JsonNode)new JsonObject
                {
                    ["primitive"] = entry.Primitive,
                    ["upstream_count"] = entry.UpstreamCount,
                    ["csharp_count"] = entry.CSharpCount,
                    ["upstream_targets"] = new JsonArray(entry.UpstreamTargets.Select(t => (JsonNode)t!).ToArray()),
                    ["csharp_targets"] = new JsonArray(entry.CSharpTargets.Select(t => (JsonNode)t!).ToArray()),
                    ["targets_intersect"] = entry.TargetsIntersect,
                }).ToArray()),
            }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return diff.TargetMismatches.Count == 0 ? 0 : 1;
        }

        Console.WriteLine($"[动作对照] {dryRun!.Plan.Chapter}/{dryRun.Plan.Level}");
        Console.WriteLine($"[状态来源] 上游=现场识别（来自日志）；C#={dryRun.DetectionSource ?? "声明地图（未叠加识别）"}");
        Console.WriteLine($"{"原语",-32}{"上游",-6}{"C#",-6}{"上游目标",-18}{"C# 目标",-18}目标一致");
        foreach (var entry in diff.Entries)
        {
            Console.WriteLine($"{entry.Primitive,-32}{entry.UpstreamCount,-6}{entry.CSharpCount,-6}" +
                              $"{Join(entry.UpstreamTargets),-18}{Join(entry.CSharpTargets),-18}" +
                              (entry.UpstreamTargets.Count == 0 || entry.CSharpTargets.Count == 0
                                  ? "—" : entry.TargetsIntersect ? "是" : "**否**"));
        }
        Console.WriteLine();
        if (diff.OnlyUpstream.Count > 0) Console.WriteLine($"[只有上游用到] {string.Join(", ", diff.OnlyUpstream)}");
        if (diff.OnlyCSharp.Count > 0) Console.WriteLine($"[只有 C# 用到] {string.Join(", ", diff.OnlyCSharp)}");
        foreach (string mismatch in diff.TargetMismatches) Console.WriteLine($"[目标不一致] {mismatch}");
        Console.WriteLine($"[结论   ] 原语集合{(diff.SamePrimitives ? "相同" : "不同")}；" +
                          $"目标格子{(diff.AnyTargetMatched ? "至少一个原语打到同一格（正面证据）" : "没有交集")}");
        Console.WriteLine("[口径   ] 不比重复次数（干跑状态不刷新，同一目标会被反复选中）；" +
                          "差异也可能是状态来源不同造成的。");
        return diff.TargetMismatches.Count == 0 ? 0 : 1;
    }

    private static string Join(IReadOnlyList<string> values) =>
        values.Count == 0 ? "—" : string.Join("/", values);

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"[错误   ] {message}");
        return 1;
    }
}
