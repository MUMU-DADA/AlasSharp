using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Alas.Campaign;

namespace Alas.Core.Diagnostics;

/// <summary>
/// R5 原语级动作轨迹：`Alas.Server r5-actions --log &lt;上游运行日志&gt; [--chapter &lt;章&gt; --level &lt;关&gt;]`。
///
/// 把上游日志里的动作行映射回我们的原语名，回答两件事：
/// ① 这次真机运行**实际走了哪些原语、打了哪些格子**；
/// ② 其中哪些是 C# 侧**还没实现**的（真机口径的缺口，离线干跑发现不了）。
/// 给了关卡时还会对照该关计划的算子集合，指出"计划里有、这次运行没走到"的部分。
///
/// 只做日志解析与对照：不连设备、不改变任何运行状态。
/// </summary>
internal static class CampaignActionCheck
{
    public static int Run(string dataDir, string? chapter, string? level, string? logPath, bool asJson)
    {
        if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath))
        {
            return Fail("用法：r5-actions --log <上游运行日志> [--chapter <章> --level <关>]");
        }
        var trace = UpstreamActionParser.Parse(File.ReadAllText(logPath));
        var implemented = trace.Primitives.Where(CampaignPrimitiveRegistry.IsImplemented).ToArray();
        var missing = trace.Primitives.Where(name => !CampaignPrimitiveRegistry.IsImplemented(name)).ToArray();

        HashSet<string>? planned = null;
        if (!string.IsNullOrEmpty(chapter) && !string.IsNullOrEmpty(level))
        {
            try
            {
                var plan = CampaignPlanReader.Read(dataDir, chapter, level);
                planned = plan.Header.Battles
                    .SelectMany(battle => battle.Steps)
                    .Select(step => step.Op)
                    .Where(op => !op.StartsWith("super().", StringComparison.Ordinal))
                    .ToHashSet(StringComparer.Ordinal);
            }
            catch (Exception error) when (error is JsonException or IOException)
            {
                return Fail($"读不出关卡计划 {chapter}/{level}：{error.Message}");
            }
        }

        if (asJson)
        {
            Console.WriteLine(new JsonObject
            {
                ["actions"] = new JsonArray(trace.Actions.Select(action => (JsonNode)new JsonObject
                {
                    ["primitive"] = action.Primitive,
                    ["target"] = action.Target,
                }).ToArray()),
                ["primitives"] = new JsonArray(trace.Primitives.Select(name => (JsonNode)name!).ToArray()),
                ["implemented"] = new JsonArray(implemented.Select(name => (JsonNode)name!).ToArray()),
                ["missing"] = new JsonArray(missing.Select(name => (JsonNode)name!).ToArray()),
                ["unknown_markers"] = new JsonArray(trace.UnknownMarkers.Select(line => (JsonNode)line!).ToArray()),
            }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return missing.Length == 0 ? 0 : 1;
        }

        Console.WriteLine($"[动作轨迹] 共 {trace.Count} 次原语级动作，涉及 {trace.Primitives.Count} 个原语");
        foreach (var action in trace.Actions)
        {
            Console.WriteLine($"  {action.Primitive}{(action.Target is null ? "" : $"({action.Target})")}");
        }
        Console.WriteLine();
        Console.WriteLine($"[C# 覆盖] 已实现 {implemented.Length} / 未实现 {missing.Length}" +
                          (missing.Length == 0 ? "" : $"：{string.Join(", ", missing)}"));
        if (planned is not null)
        {
            var notExercised = planned.Where(op => !trace.Primitives.Contains(op, StringComparer.Ordinal)).ToArray();
            Console.WriteLine($"[计划对照] 该关计划算子 {planned.Count} 个，本次运行没走到的 {notExercised.Length} 个" +
                              (notExercised.Length == 0 ? "" : $"：{string.Join(", ", notExercised)}"));
        }
        if (trace.UnknownMarkers.Count > 0)
        {
            Console.WriteLine($"[认不出的动作行] {trace.UnknownMarkers.Count} 条（前 3 条）：");
            foreach (string line in trace.UnknownMarkers.Take(3))
            {
                Console.WriteLine($"  {line}");
            }
        }
        return missing.Length == 0 ? 0 : 1;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"[错误   ] {message}");
        return 1;
    }
}
