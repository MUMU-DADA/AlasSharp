using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Campaign;
using RulePlan = Alas.Campaign.CampaignPlan;

namespace Alas.Core.Diagnostics;

/// <summary>
/// R5 宿主调用翻译核对：`Alas.Server r5-calls [--chapter &lt;章&gt; --level &lt;关&gt;] [--json]`。
///
/// 打印"计划步骤 → 宿主调用（上游方法名 + 参数引用形式）"的翻译结果：
/// 不给关卡时用**全库所有计划步骤**做覆盖检查；给了关卡就只看这一关。
/// 用途：接设备之前先把"C# 到底会怎么调上游"变成可核对的事实，而不是等真机跑起来才发现参数对不上。
///
/// 只读：不连设备、不改变任何运行状态。
/// </summary>
internal static class CampaignCallCheck
{
    public static int Run(string dataDir, string? chapter, string? level, bool asJson)
    {
        var plans = new List<RulePlan>();
        try
        {
            if (!string.IsNullOrEmpty(chapter) && !string.IsNullOrEmpty(level))
            {
                plans.Add(CampaignPlanReader.Read(dataDir, chapter, level));
            }
            else
            {
                foreach (string name in CampaignPlanReader.Chapters(dataDir))
                {
                    plans.AddRange(CampaignPlanReader.LoadChapter(dataDir, name));
                }
            }
        }
        catch (Exception error) when (error is JsonException or IOException)
        {
            return Fail(error.Message);
        }

        var unsupported = new Dictionary<string, (int Count, string Reason)>(StringComparer.Ordinal);
        // 实参里含"只能运行期解析"的引用（局部变量 / 钩子参数）的调用：**不算不支持**，
        // 单独统计并在 JSON 里报出（执行器调用前会替换成具体值），别让"翻译的时机在运行期"看起来像缺口。
        var runtime = new Dictionary<string, (int Count, string Reason)>(StringComparer.Ordinal);
        var methods = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var rows = new JsonArray();
        int steps = 0, translated = 0, structural = 0;
        foreach (var plan in plans)
        {
            foreach (var battle in plan.Header.Battles)
            {
                foreach (var step in battle.Steps)
                {
                    steps++;
                    // **结构步骤**（`branch` / `return`）不是调用，也没有上游方法可调：
                    // 它们由执行器处理，翻译器管不着。显式分开统计——不然它们会以空 op 混进"已翻译"，
                    // 又是一次"看起来能跑"的假绿。
                    if (step.Kind is "branch" or "return")
                    {
                        structural++;
                        continue;
                    }
                    var call = CampaignCallTranslator.Translate(step);
                    if (call.Unsupported is { } reason)
                    {
                        var current = unsupported.GetValueOrDefault(step.Op);
                        unsupported[step.Op] = (current.Count + 1, reason);
                        continue;
                    }
                    translated++;
                    if (call.RuntimeOnly is { } runtimeReason)
                    {
                        var currentRuntime = runtime.GetValueOrDefault(step.Op);
                        runtime[step.Op] = (currentRuntime.Count + 1, runtimeReason);
                    }
                    methods[call.Method] = methods.GetValueOrDefault(call.Method) + 1;
                    if (rows.Count < 400)
                    {
                        rows.Add(new JsonObject
                        {
                            ["chapter"] = plan.Chapter,
                            ["level"] = plan.Level,
                            ["hook"] = battle.Method,
                            ["op"] = call.Op,
                            ["method"] = call.Method,
                            ["fleet_prefix"] = call.FleetPrefix,
                            ["args"] = new JsonArray(call.Args.Select(arg => arg?.DeepClone()).ToArray()),
                            ["kwargs"] = new JsonObject(call.KeywordArgs.Select(pair =>
                                new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value?.DeepClone())).ToArray()),
                        });
                    }
                }
            }
        }

        if (asJson)
        {
            Console.WriteLine(new JsonObject
            {
                ["steps"] = steps,
                ["translated"] = translated,
                ["structural"] = structural,
                ["unsupported"] = new JsonArray(unsupported.Select(pair => (JsonNode)new JsonObject
                {
                    ["op"] = pair.Key,
                    ["count"] = pair.Value.Count,
                    ["reason"] = pair.Value.Reason,
                }).ToArray()),
                ["runtime_only"] = new JsonArray(runtime.Select(pair => (JsonNode)new JsonObject
                {
                    ["op"] = pair.Key,
                    ["count"] = pair.Value.Count,
                    ["reason"] = pair.Value.Reason,
                }).ToArray()),
                ["methods"] = new JsonObject(methods.Select(pair =>
                    new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value)).ToArray()),
                ["sample"] = rows,
            }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            Console.WriteLine($"[翻译   ] 步骤 {steps} 个：可翻译 {translated}，" +
                              $"不支持 {steps - translated}");
            Console.WriteLine("[上游方法分布]");
            foreach (var (method, count) in methods)
            {
                Console.WriteLine($"  {count,-6}{method}");
            }
            if (unsupported.Count > 0)
            {
                Console.WriteLine("[不支持的原语]");
                foreach (var (op, detail) in unsupported)
                {
                    Console.WriteLine($"  {op,-28}{detail.Count} 次：{detail.Reason}");
                }
            }
        }
        return unsupported.Count == 0 ? 0 : 1;
    }

    /// <summary>参数是 `object?`：直接 `JsonValue.Create(object)` 会在序列化时报类型错，按实际类型构造。</summary>
    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        string text => JsonValue.Create(text),
        int number => JsonValue.Create(number),
        double number => JsonValue.Create(number),
        bool flag => JsonValue.Create(flag),
        _ => JsonValue.Create(value.ToString()),
    };

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"[错误   ] {message}");
        return 1;
    }
}
