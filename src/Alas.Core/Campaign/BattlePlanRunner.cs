using System.Text.Json;
using Alas.Core;

namespace Alas.Campaign;

/// <summary>
/// 引擎算子调用。关卡规则表里的每一步最终都会变成一次 <see cref="EngineCall"/>。
///
/// <see cref="Op"/> 用的是上游的方法名（`battle_default` / `clear_siren` /
/// `fleet_boss.clear_boss` …），这样规则表与引擎契约天然同名，不需要额外的映射表。
/// </summary>
public sealed record EngineCall(
    string Op,
    string Kind,
    IReadOnlyDictionary<string, JsonElement> PositionalAndKeyword);

/// <summary>
/// 战斗引擎接口。
///
/// 为什么不给 120 个方法逐个写签名：契约（见 .bench/engine_contract.json）本来就有 120 个方法，
/// 且 tier B 关卡还会用到词表外的算子。这里用「按名字派发」保持开放，
/// 等某个域真正实现时再为它加类型化封装——过早铺 120 个空方法只会制造维护负担。
/// </summary>
public interface IBattleEngine
{
    /// <summary>执行一个算子，返回它的布尔结果（多数算子的语义是"这一阶段是否已完成"）。</summary>
    bool Invoke(EngineCall call);
}

/// <summary>记录调用序列的测试替身：用于验证规则解释器，不执行任何真实操作。</summary>
public sealed class RecordingEngine : IBattleEngine
{
    public List<EngineCall> Calls { get; } = new();

    /// <summary>返回 true 的算子名；为空表示所有算子都返回 false。</summary>
    public HashSet<string> TrueOps { get; init; } = new();

    /// <summary>只在前 N 次调用返回 true（用于验证提前结束语义）。</summary>
    public int TrueUntilCallIndex { get; init; } = int.MaxValue;

    public bool Invoke(EngineCall call)
    {
        Calls.Add(call);
        if (Calls.Count - 1 >= TrueUntilCallIndex) return false;
        return TrueOps.Contains(call.Op);
    }
}

public sealed record StepOutcome(string Method, string Op, bool Result, bool Terminated);

public sealed class BattleRunResult
{
    public List<StepOutcome> Steps { get; } = new();
    public List<string> MethodsRun { get; } = new();

    /// <summary>没有显式 return、走到方法末尾的阶段（Python 语义：返回 None）。</summary>
    public List<string> ImplicitReturns { get; } = new();

    public string? StoppedAt { get; set; }
    public bool Completed { get; set; }
}

/// <summary>
/// 关卡规则解释器：把 S0 冻结出来的 JSON 战斗计划，按上游生成器模板的语义执行。
///
/// 上游模板（见 dev_tools/map_extractor.py）只会生成这几种语句：
///     if self.X(args): return True      → conditional：算子返回真则**本阶段立即结束**
///     if not self.X(args): ...          → conditional_negated
///     return self.Y(args)               → terminal：本阶段以它的返回值结束
///     self.X(args)                      → call：执行后继续
///     var = self.X(args)                → assign：执行后继续（变量由引擎侧保存）
/// 解释器严格按这个语义走，因此 C# 侧不需要为每个关卡写代码。
/// </summary>
public static class BattlePlanRunner
{
    /// <summary>执行一个关卡的全部战斗阶段（battle_0、battle_1、… 直到 boss 阶段）。</summary>
    public static BattleRunResult Run(CampaignIr ir, IBattleEngine engine)
    {
        var result = new BattleRunResult();
        var battles = ir.Campaign.Battles
            .Where(b => b.Method.StartsWith("battle_", StringComparison.Ordinal))
            .OrderBy(b => BattleIndex(b.Method))
            .ToList();

        foreach (var battle in battles)
        {
            result.MethodsRun.Add(battle.Method);
            if (!battle.PlanComplete)
            {
                // tier C：计划不完整，解释器拒绝执行，避免"残缺计划当完整计划用"
                result.StoppedAt = battle.Method;
                return result;
            }

            bool terminated = false;
            foreach (var step in battle.Steps)
            {
                var call = new EngineCall(step.Op, step.Kind, Flatten(step.Args));
                bool value = engine.Invoke(call);
                result.Steps.Add(new StepOutcome(battle.Method, step.Op, value,
                    step.Kind is "terminal" || step.Kind == "super_delegate"));

                switch (step.Kind)
                {
                    case "terminal":
                        terminated = true;
                        break;
                    case "conditional":
                        if (value) terminated = true;
                        break;
                    case "conditional_negated":
                        if (!value) terminated = true;
                        break;
                    case "super_delegate":
                        // 纯委托给父类实现：C# 侧表现为调用虚方法的基类版本，本阶段结束
                        terminated = true;
                        break;
                    case "call":
                    case "assign":
                        break;
                    default:
                        throw new InvalidOperationException($"未知步骤类型: {step.Kind}");
                }

                if (terminated) break;
            }

            if (!terminated)
            {
                // 走到方法末尾而没有显式 return —— Python 语义是返回 None（假值），
                // 这是**合法的上游写法**，不是计划异常。实测 event_20200716_en/a1.py：
                //     def battle_4(self):
                //         self.fleet_boss.capture_clear_boss()
                // 早期版本把它当异常提前返回，导致 18 个关卡 completed=False（已修）。
                result.ImplicitReturns.Add(battle.Method);
            }

            if (ir.Campaign.BossBattle is int boss && BattleIndex(battle.Method) >= boss)
            {
                result.StoppedAt = battle.Method;
                result.Completed = true;
                return result;
            }
        }

        result.Completed = true;
        return result;
    }

    public static int BattleIndex(string method)
        => method.StartsWith("battle_", StringComparison.Ordinal)
           && int.TryParse(method.AsSpan(7), out int n) ? n : -1;

    /// <summary>把 IR 里的 args 拍平成一层的 JsonElement 字典（位置参数用 p0/p1…，关键字参数用原名）。</summary>
    private static IReadOnlyDictionary<string, JsonElement> Flatten(JsonElement? args)
    {
        var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (args is not { ValueKind: JsonValueKind.Object } obj) return dict;

        if (obj.TryGetProperty("positional", out var pos) && pos.ValueKind == JsonValueKind.Array)
        {
            int i = 0;
            foreach (var item in pos.EnumerateArray())
                dict[$"p{i++}"] = item.Clone();
        }
        if (obj.TryGetProperty("keyword", out var kw) && kw.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in kw.EnumerateObject())
                dict[prop.Name] = prop.Value.Clone();
        }
        return dict;
    }
}
