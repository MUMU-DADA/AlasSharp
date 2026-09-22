using System.Text.Json;
using Alas.Campaign;
using Alas.Core;

namespace Alas.DataTool;

/// <summary>
/// S3 地基验收：关卡规则解释器。
///
/// 校验思路是**两个独立推导互相对拍**：
///   A. 解释器实际执行的算子序列（C# 侧运行时产生）
///   B. 导出器从上游源码 AST 归一出来的步骤序列（S0 冻结的 JSON）
/// 两者一致，才说明规则表没有在「归一 → 执行」这条链路上丢步、错序或错参。
///
/// 三种场景：
///   1. 所有算子返回 false → 应当执行完整计划，顺序与计划逐项一致
///   2. 所有条件算子返回 true → 每个阶段应停在第一个条件算子处（上游模板语义）
///   3. tier C（计划不完整）→ 解释器必须**拒绝执行**，不能拿残缺计划当真
/// </summary>
internal static class CampaignCheck
{
    public static int Run(UpstreamData.Catalog catalog, string? target)
    {
        if (target is not null) return Show(catalog, target);

        int checkedChapters = 0, planSteps = 0;
        int orderMismatch = 0, earlyStopMismatch = 0, tierCRefused = 0, tierCLeaked = 0;
        var samples = new List<string>();

        foreach (var entry in catalog.Campaign.Chapters)
        {
            var ir = catalog.LoadCampaign(entry);
            var battles = ir.Campaign.Battles
                .Where(b => b.Method.StartsWith("battle_", StringComparison.Ordinal))
                .OrderBy(b => BattlePlanRunner.BattleIndex(b.Method))
                .ToList();
            if (battles.Count == 0) continue;

            // ---- 场景 3：计划不完整的关卡必须被拒绝
            if (!entry.PlanComplete)
            {
                var rec = new RecordingEngine();
                var r = BattlePlanRunner.Run(ir, rec);
                if (!r.Completed && r.StoppedAt is not null) tierCRefused++;
                else
                {
                    tierCLeaked++;
                    if (samples.Count < 8)
                        samples.Add($"tier C 未被拒绝: {entry.Source}（completed={r.Completed}）");
                }
                continue;
            }

            checkedChapters++;
            planSteps += battles.Sum(b => b.Steps.Count);

            // ---- 场景 1：全部返回 false → 完整执行，顺序逐项一致
            var engineA = new RecordingEngine();
            var resultA = BattlePlanRunner.Run(ir, engineA);
            var expectedOps = battles.SelectMany(b => b.Steps).Select(s => s.Op).ToList();
            var actualOps = resultA.Steps.Select(s => s.Op).ToList();
            if (!resultA.Completed || !expectedOps.SequenceEqual(actualOps, StringComparer.Ordinal))
            {
                orderMismatch++;
                if (samples.Count < 8)
                {
                    int at = FirstDiff(expectedOps, actualOps);
                    samples.Add($"{entry.Source}: 顺序/步数不一致（第 {at} 步："
                                + $"计划={(at < expectedOps.Count ? expectedOps[at] : "<无>")} "
                                + $"执行={(at < actualOps.Count ? actualOps[at] : "<无>")}，"
                                + $"completed={resultA.Completed}）");
                }
            }

            // ---- 场景 2：所有条件算子返回 true → 每个阶段停在第一个条件算子
            var trueOps = battles.SelectMany(b => b.Steps)
                .Where(s => s.Kind is "conditional" or "conditional_negated")
                .Select(s => s.Op).ToHashSet(StringComparer.Ordinal);
            var engineB = new RecordingEngine { TrueOps = trueOps };
            var resultB = BattlePlanRunner.Run(ir, engineB);
            var expectedEarly = ExpectedEarlyStop(battles);
            var actualEarly = resultB.Steps.Select(s => s.Op).ToList();
            if (!expectedEarly.SequenceEqual(actualEarly, StringComparer.Ordinal))
            {
                earlyStopMismatch++;
                if (samples.Count < 8)
                {
                    int at = FirstDiff(expectedEarly, actualEarly);
                    samples.Add($"{entry.Source}: 提前结束语义不一致（第 {at} 步："
                                + $"应为={(at < expectedEarly.Count ? expectedEarly[at] : "<无>")} "
                                + $"实为={(at < actualEarly.Count ? actualEarly[at] : "<无>")}）");
                }
            }
        }

        Console.WriteLine($"可完整计划的关卡 : {checkedChapters}（tier A + B）");
        Console.WriteLine($"计划步骤总数     : {planSteps}");
        Console.WriteLine($"tier C 拒绝执行  : {tierCRefused}，漏放行 {tierCLeaked}");
        Console.WriteLine($"[完整执行顺序] 不一致 {orderMismatch}");
        Console.WriteLine($"[提前结束语义] 不一致 {earlyStopMismatch}");
        foreach (var s in samples) Console.WriteLine($"   {s}");

        int problems = orderMismatch + earlyStopMismatch + tierCLeaked;
        Console.WriteLine();
        Console.WriteLine(problems == 0 ? "结果: OK" : $"结果: FAIL（{problems} 处）");
        return problems == 0 ? 0 : 1;
    }

    /// <summary>
    /// 上游模板语义：条件算子为真则本阶段立即结束。
    /// 因此在「所有条件算子都返回真」的场景下，每个阶段应执行到第一个条件算子（含）为止。
    /// </summary>
    private static List<string> ExpectedEarlyStop(List<CampaignBattle> battles)
    {
        var ops = new List<string>();
        foreach (var battle in battles)
        {
            foreach (var step in battle.Steps)
            {
                if (step.Kind == "super_delegate") break;   // 纯委托，不产生引擎调用
                ops.Add(step.Op);
                if (step.Kind is "conditional" or "conditional_negated") break;
                if (step.Kind == "terminal") break;
            }
        }
        return ops;
    }

    private static int FirstDiff(List<string> a, List<string> b)
    {
        int n = Math.Min(a.Count, b.Count);
        for (int i = 0; i < n; i++)
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return i;
        return n;
    }

    private static int Show(UpstreamData.Catalog catalog, string key)
    {
        var entry = catalog.Campaign.Chapters.FirstOrDefault(c =>
                        string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase))
                    ?? catalog.Campaign.Chapters.FirstOrDefault(c =>
                        c.Source.Contains(key, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            Console.Error.WriteLine($"找不到关卡: {key}");
            return 2;
        }

        var ir = catalog.LoadCampaign(entry);
        Console.WriteLine($"源文件 : {entry.Source}");
        Console.WriteLine($"关卡名 : {ir.Name}   分级: {entry.Tier}   计划完整: {entry.PlanComplete}");
        Console.WriteLine($"boss 阶段: {ir.Campaign.BossBattle}");
        Console.WriteLine();

        var engine = new RecordingEngine();
        var result = BattlePlanRunner.Run(ir, engine);
        Console.WriteLine("解释器执行轨迹（所有算子返回 false 的情形）：");
        foreach (var s in result.Steps)
            Console.WriteLine($"  {s.Method,-10} {s.Op,-24} {(s.Terminated ? "(阶段结束)" : "")}");
        Console.WriteLine($"→ 完成={result.Completed} 停在={result.StoppedAt} 共 {result.Steps.Count} 步");
        return 0;
    }
}
