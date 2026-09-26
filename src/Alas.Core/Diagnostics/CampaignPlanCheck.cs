using Alas.Campaign;

namespace Alas.Core.Diagnostics;

/// <summary>
/// R5 第一步的只读入口：`Alas.Server r5-plan`。
///
/// 它只读**静态导出**（`data/campaign/&lt;章节&gt;/&lt;关卡&gt;.json`），把"关卡计划"摊开给人看：
/// 每个覆写钩子调用了哪些原语、这段覆写能否被静态表达、以及整章的可表达率。
///
/// 定位：这条命令**不执行关卡、不导入游戏代码、不连设备**，也不改变生产路径
/// （生产战役仍走上游 `CampaignRun.load_campaign()` + 原生 `Campaign.run()`）。
/// 它的用途是把"重写上游引擎"的输入侧先用产品代码读通，并与
/// `tools/diagnostics/r5_upstream_audit.py` 的统计口径对拍。
///
/// 用法：
///   Alas.Server r5-plan                                  # 全部章节概览
///   Alas.Server r5-plan campaign_main                    # 一章明细
///   Alas.Server r5-plan campaign_main --level campaign_1_1   # 单个关卡的钩子计划
/// </summary>
internal static class CampaignPlanCheck
{
    public static int Run(string dataDir, string? chapter, string? level, bool dryRunAll = false)
    {
        string campaignDir = Path.Combine(dataDir, "campaign");
        if (!Directory.Exists(campaignDir))
        {
            return Fail($"找不到导出目录：{campaignDir}（先运行 tools/export_upstream_data.py）");
        }

        var chapters = CampaignPlanReader.Chapters(dataDir);
        if (chapters.Count == 0)
        {
            return Fail($"{campaignDir} 下没有章节目录");
        }

        if (dryRunAll)
        {
            return DryRunAll(dataDir, chapters);
        }

        if (string.IsNullOrEmpty(chapter))
        {
            Console.WriteLine($"[导出   ] {campaignDir}（{chapters.Count} 章）");
            Console.WriteLine($"[口径   ] plan_complete = 该钩子能完整表达为静态调用序列");
            Console.WriteLine();
            Console.WriteLine($"{"章节",-26}{"关卡",-6}{"钩子",-6}{"可表达",-8}{"未表达",-8}可表达率");
            int levels = 0, battles = 0, complete = 0, traceMatch = 0, traceDiff = 0, traceEmpty = 0;
            int surfaceSteps = 0, surfaceHooks = 0, surfaceConforming = 0, surfaceImplementedSteps = 0;
            int surfaceSetup = 0, surfaceAttempts = 0, surfaceFallbacks = 0, surfaceDelegates = 0;
            var surfaceOps = new HashSet<string>(StringComparer.Ordinal);
            var allFailures = new List<string>();
            foreach (string name in chapters)
            {
                var chapterFailures = new List<string>();
                var plans = CampaignPlanReader.LoadChapter(dataDir, name, chapterFailures);
                allFailures.AddRange(chapterFailures);
                var summary = CampaignPlanReader.Summarize(name, plans);
                levels += summary.Levels;
                battles += summary.Battles;
                complete += summary.Complete;
                traceMatch += summary.TraceMatch;
                traceDiff += summary.TraceDiff;
                traceEmpty += summary.TraceEmpty;
                var chapterSurface = CampaignPlanExecutor.Summarize(name, plans);
                surfaceSteps += chapterSurface.Steps;
                surfaceHooks += chapterSurface.Hooks;
                surfaceConforming += chapterSurface.ConformingHooks;
                surfaceImplementedSteps += chapterSurface.ImplementedSteps;
                surfaceSetup += chapterSurface.Setup;
                surfaceAttempts += chapterSurface.Attempts;
                surfaceFallbacks += chapterSurface.Fallbacks;
                surfaceDelegates += chapterSurface.Delegates;
                surfaceOps.UnionWith(chapterSurface.Ops.Keys);
                Console.WriteLine($"{summary.Chapter,-26}{summary.Levels,-6}{summary.Battles,-6}" +
                                  $"{summary.Complete,-8}{summary.Battles - summary.Complete,-8}{summary.CompleteRate:P1}");
            }
            Console.WriteLine();
            Console.WriteLine($"[合计   ] {chapters.Count} 章 / {levels} 关卡导出 / {battles} 个钩子，" +
                              $"可表达 {complete}（{(battles == 0 ? 1 : (double)complete / battles):P1}）");
            Console.WriteLine($"[轨迹对拍] 与 calls 一致 {traceMatch} / 不一致 {traceDiff} / " +
                              $"steps 为空 {traceEmpty}（合计 {traceMatch + traceDiff + traceEmpty}）");
            Console.WriteLine($"[执行面 ] {surfaceHooks} 个钩子（形状符合契约 {surfaceConforming}），" +
                              $"{surfaceSteps} 步涉及 {surfaceOps.Count} 个原语、已实现 " +
                              $"{surfaceOps.Count(CampaignPrimitiveRegistry.IsImplemented)} 个；" +
                              $"步覆盖 {surfaceImplementedSteps}/{surfaceSteps}" +
                              $"（{(surfaceSteps == 0 ? 0 : (double)surfaceImplementedSteps / surfaceSteps):P1}）");
            Console.WriteLine($"[角色   ] 前置 {surfaceSetup} / 尝试 {surfaceAttempts} / " +
                              $"兜底 {surfaceFallbacks} / 委托 {surfaceDelegates}");
            if (allFailures.Count > 0)
            {
                Console.WriteLine($"[警告   ] {allFailures.Count} 个关卡导出读不出，前 3 条：");
                foreach (string failure in allFailures.Take(3))
                {
                    Console.WriteLine($"          {failure}");
                }
            }
            return 0;
        }

        if (!chapters.Contains(chapter, StringComparer.Ordinal))
        {
            return Fail($"没有这个章节：{chapter}（可用章节见 r5-plan 概览）");
        }

        var failures = new List<string>();
        var chapterPlans = CampaignPlanReader.LoadChapter(dataDir, chapter, failures);
        var chapterSummary = CampaignPlanReader.Summarize(chapter, chapterPlans);

        if (!string.IsNullOrEmpty(level))
        {
            var plan = chapterPlans.FirstOrDefault(item => item.Level == level);
            if (plan is null)
            {
                return Fail($"章节 {chapter} 里没有关卡 {level}");
            }
            Console.WriteLine($"[关卡   ] {plan.Chapter}/{plan.Level}  类 {plan.Header.Class}  " +
                              $"基类 {string.Join(", ", plan.Header.Bases)}");
            Console.WriteLine($"[来源   ] {plan.Source}   名称 {plan.Name}（{plan.NameSource}）");
            Console.WriteLine($"[完整性 ] 类级 plan_complete = {plan.Header.PlanComplete}，" +
                              $"钩子 {plan.Header.Battles.Count} 个，Config 键 {plan.Config.Count} 个");
            Console.WriteLine();
            var traces = CampaignPlanInterpreter.Interpret(plan);
            Console.WriteLine($"[轨迹   ] {traces.Count} 个钩子，" +
                              $"与 calls 一致 {traces.Count(trace => trace.MatchesCalls && trace.Steps.Count > 0)}，" +
                              $"steps 为空 {traces.Count(trace => trace.Steps.Count == 0)}");
            foreach (var trace in traces)
            {
                var battle = plan.Header.Battles.First(item => item.Method == trace.Method);
                var execution = CampaignPlanExecutor.DryRun(plan, battle);
                var shape = CampaignPlanExecutor.Validate(battle);
                string state = battle.PlanComplete ? "可表达" : "未表达";
                string mark = trace.Steps.Count == 0 ? "—" : trace.MatchesCalls ? "✓" : "✗";
                string contract = battle.Steps.Count == 0 ? "" : shape.Conforms ? "，形状符合契约" : $"，形状 {shape.Pattern}（交错）";
                string calls = battle.Calls.Count == 0 ? "（无原语调用）" : string.Join(" → ", battle.Calls);
                string counts = trace.Steps.Count == 0
                    ? ""
                    : $"  前置 {execution.Setup} / 尝试 {execution.Attempts} / 兜底 {(execution.HasFallback ? 1 : 0)} / " +
                      $"委托 {execution.Delegated}";
                Console.WriteLine($"  {mark} {battle.Method,-22}{state,-8}语句 {battle.StatementCount,-4}{calls}{counts}{contract}");
                if (battle.Unparsed.Count > 0)
                {
                    Console.WriteLine($"  {"",-2}{"",-22}{"",-8}未表达原因：{string.Join(", ", battle.Unparsed)}");
                }
                foreach (var step in battle.Steps)
                {
                    string arguments = FormatArguments(step.Args);
                    Console.WriteLine($"      {step.Kind,-16}{step.Op}{arguments}{StatusNote(plan, step)}");
                }
            }
            return 0;
        }

        Console.WriteLine($"[章节   ] {chapterPlans.Count} 个关卡导出，{chapterSummary.Battles} 个钩子，" +
                          $"可表达 {chapterSummary.Complete}（{chapterSummary.CompleteRate:P1}）");
        if (failures.Count > 0)
        {
            Console.WriteLine($"[警告   ] {failures.Count} 个文件读不出：{string.Join(", ", failures.Take(5))}");
        }
        Console.WriteLine();
        Console.WriteLine($"[计划步 ] {chapterSummary.Steps} 步 / {chapterSummary.Ops.Count} 个原语 / " +
                          $"{chapterSummary.Kinds.Count} 种步骤类型");
        Console.WriteLine($"[轨迹对拍] 与 calls 一致 {chapterSummary.TraceMatch} / " +
                          $"不一致 {chapterSummary.TraceDiff} / steps 为空 {chapterSummary.TraceEmpty} / " +
                          $"含未求值实参 {chapterSummary.UnresolvedArguments}");
        var surface = CampaignPlanExecutor.Summarize(chapter, chapterPlans);
        Console.WriteLine($"[执行面 ] {surface.Hooks} 个钩子（形状符合契约 {surface.ConformingHooks}），" +
                          $"涉及原语 {surface.Ops.Count} 个、已实现 {surface.ImplementedOps} 个；" +
                          $"步覆盖 {surface.ImplementedSteps}/{surface.Steps}（{surface.StepCoverage:P1}）");
        Console.WriteLine($"[角色   ] 前置 {surface.Setup} / 尝试 {surface.Attempts} / " +
                          $"兜底 {surface.Fallbacks} / 委托 {surface.Delegates}");
        Console.WriteLine("[步骤类型]");
        foreach (var (kind, count) in chapterSummary.Kinds.OrderByDescending(pair => pair.Value))
        {
            Console.WriteLine($"  {kind,-20}{count}");
        }
        Console.WriteLine();
        Console.WriteLine("[原语 Top 12]（计划的执行面）");
        foreach (var (op, count) in chapterSummary.Ops.OrderByDescending(pair => pair.Value).Take(12))
        {
            Console.WriteLine($"  {op,-30}{count}");
        }
        Console.WriteLine();
        Console.WriteLine("[未表达原因]");
        foreach (var (reason, count) in chapterSummary.Unparsed.OrderByDescending(pair => pair.Value).Take(8))
        {
            Console.WriteLine($"  {reason,-24}{count}");
        }
        Console.WriteLine();
        Console.WriteLine("[调用序列 Top 10]（去重后按名字排序，与 Python 审计口径一致）");
        foreach (var (sequence, count) in chapterSummary.Sequences.OrderByDescending(pair => pair.Value).Take(10))
        {
            Console.WriteLine($"  {count,-6}{(sequence.Length == 0 ? "（无原语调用）" : sequence)}");
        }
        Console.WriteLine();
        Console.WriteLine("[钩子 Top 10]");
        foreach (var (method, count) in chapterSummary.Methods.OrderByDescending(pair => pair.Value).Take(10))
        {
            Console.WriteLine($"  {method,-24}{count}");
        }
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"[错误   ] {message}");
        return 1;
    }

    /// <summary>
    /// 全库干跑：用**导出的真实地图**（`map.map_data` 令牌）构造地图状态，逐个钩子跑一遍
    /// <see cref="CampaignHookRunner"/>，统计"能跑完（有返回值）"与"被阻塞（按原因）"。
    ///
    /// 口径说明：这里的识别结果（哪个格子真有敌人/boss）不在导出里，因此**只做控制流与实参解码的
    /// 干跑**——能跑完不等于真机能打通，被阻塞则说明引擎侧确实缺东西（列出来就是要补的）。
    /// </summary>
    private static int DryRunAll(string dataDir, IReadOnlyList<string> chapters)
    {
        int hooks = 0, completed = 0, blocked = 0, noMap = 0, returnedTrue = 0;
        var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
        var samples = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in chapters)
        {
            foreach (var plan in CampaignPlanReader.LoadChapter(dataDir, name))
            {
                var grids = CampaignGridTokens.FromText(plan.Map?.MapData?.GetValue<string>());
                if (grids.Count == 0) noMap++;
                foreach (var battle in plan.Header.Battles)
                {
                    hooks++;
                    var host = new RecordingCampaignHost(grids, new CampaignRuntimeConfig())
                    {
                        BouncingRoutes = plan.Map?.BouncingEnemyData ?? [],
                        Fleet1Location = grids.Count > 0 ? grids[0].Location : "",
                        Fleet2Location = grids.Count > 0 ? grids[0].Location : "",
                    };
                    var execution = CampaignHookRunner.Run(plan, battle, host);
                    if (execution.Completed)
                    {
                        completed++;
                        if (execution.ReturnValue == true) returnedTrue++;
                        continue;
                    }
                    blocked++;
                    string reason = Normalize(execution.BlockedReason ?? "未注明");
                    reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
                    samples.TryAdd(reason, $"{plan.Chapter}/{plan.Level}::{battle.Method}");
                }
            }
        }

        Console.WriteLine($"[干跑全库] 钩子 {hooks} 个：跑完 {completed}（返回真 {returnedTrue}）/ 被阻塞 {blocked}");
        Console.WriteLine($"[地图状态] {chapters.Count} 章中 {noMap} 个关卡导出没有可用 map_data");
        Console.WriteLine();
        Console.WriteLine("[阻塞原因 Top 10]");
        foreach (var (reason, count) in reasons.OrderByDescending(pair => pair.Value).Take(10))
        {
            Console.WriteLine($"  {count,-6}{reason}");
            Console.WriteLine($"        {samples[reason]}");
        }
        return 0;
    }

    /// <summary>把阻塞原因里的具体名字抹掉，便于聚合（保留可判别的类别）。</summary>
    private static string Normalize(string reason)
    {
        int index = reason.IndexOf('：');
        string head = index > 0 ? reason[..index] : reason;
        return head
            .Replace("实参未求值", "实参未求值")
            .Trim();
    }

    /// <summary>步骤的执行侧状态标记（干跑判定，不执行任何东西）。</summary>
    private static string StatusNote(Alas.Campaign.CampaignPlan plan, CampaignPlanStep step)
    {
        if (step.Kind == "super_delegate" || step.Op.StartsWith("super().", StringComparison.Ordinal))
            return $"    [不可执行：{CampaignPrimitiveRegistry.ResolveSuperDelegate(step).Reason}]";
        var nested = plan.Header.Battles.FirstOrDefault(item =>
            item.Method == CampaignPrimitiveRegistry.InstanceMethodFor(step.Op));
        if (nested is not null) return nested.PlanComplete ? "    [实例覆写]" : "    [不可执行：覆写计划不完整]";
        return CampaignPrimitiveRegistry.IsImplemented(step.Op) ? "    [可执行]" : "    [原语未实现]";
    }

    /// <summary>把步骤实参排成一行；`"&lt;expr&gt;"` 是导出器未求值的表达式占位。</summary>
    private static string FormatArguments(CampaignPlanStepArgs? args)
    {
        if (args is null) return "";
        var parts = new List<string>();
        foreach (var value in args.Positional)
        {
            parts.Add(value?.ToJsonString() ?? "null");
        }
        foreach (var (name, value) in args.Keyword)
        {
            parts.Add($"{name}={value?.ToJsonString() ?? "null"}");
        }
        return parts.Count == 0 ? "" : "(" + string.Join(", ", parts) + ")";
    }
}
