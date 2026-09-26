using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Campaign;

namespace Alas.Core.Diagnostics;

/// <summary>Static, recursive call encoding audit. It never proves native method callability.</summary>
internal static class CampaignCallCheck
{
    private static readonly HashSet<string> StructuralKinds = new(StringComparer.Ordinal)
    { "branch", "return", "local_set", "state_set", "map_set", "raise", "log" };
    private static readonly HashSet<string> CallKinds = new(StringComparer.Ordinal)
    { "call", "conditional", "conditional_negated", "terminal", "assign", "super_delegate" };

    public static int Run(string dataDir, string? chapter, string? level, bool asJson)
    {
        var rows = new JsonArray();
        var unsupported = new JsonArray();
        var incomplete = new JsonArray();
        var unresolvedBindings = new JsonArray();
        var runtime = new JsonArray();
        var methods = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int steps = 0, structural = 0, conditionCalls = 0, calls = 0, staticEncoded = 0, runtimeResolved = 0;
        int plans = 0, hooks = 0;
        try
        {
            string campaignRoot = Path.Combine(dataDir, "campaign");
            if (!Directory.Exists(campaignRoot)) return Fail("缺少 campaign 导出目录");
            string[] files = !string.IsNullOrEmpty(chapter) && !string.IsNullOrEmpty(level)
                ? [Path.Combine(campaignRoot, chapter, level + ".json")]
                : Directory.GetFiles(campaignRoot, "*.json", SearchOption.AllDirectories)
                    .OrderBy(path => path, StringComparer.Ordinal).ToArray();
            if (files.Length == 0) return Fail("campaign 导出目录为空，不能完成调用审计");
            foreach (string file in files)
            {
                // Read the raw declaration so newly introduced nested expressions cannot
                // disappear through an older typed model before the audit sees them.
                var document = JsonNode.Parse(File.ReadAllText(file))?.AsObject()
                    ?? throw new JsonException("章节导出为空");
                var battles = document["campaign"]?["battles"]?.AsArray()
                    ?? throw new JsonException("章节导出缺少 campaign.battles");
                string location = Path.GetRelativePath(campaignRoot, file).Replace('\\', '/');
                var declared = battles.Select(b => b?["method"]?.GetValue<string>()
                    ?? throw new JsonException("钩子缺少 method")).ToHashSet(StringComparer.Ordinal);
                plans++;
                foreach (var node in battles)
                {
                    var battle = node!.AsObject();
                    string hook = battle["method"]!.GetValue<string>();
                    string origin = location + ":" + hook;
                    hooks++;
                    if (battle["plan_complete"]?.GetValue<bool>() != true)
                    {
                        incomplete.Add(new JsonObject { ["source"] = origin,
                            ["unparsed"] = battle["unparsed"]?.DeepClone() });
                    }
                    WalkSteps(battle["steps"]?.AsArray()
                        ?? throw new JsonException($"{origin} 缺少 steps"), origin + ".steps", declared);
                }
            }
            if (hooks == 0) return Fail("导出没有钩子，不能完成调用审计");
        }
        catch (Exception error) when (error is JsonException or IOException or InvalidOperationException or ArgumentException)
        {
            return Fail(error.Message);
        }

        bool complete = unsupported.Count == 0 && incomplete.Count == 0 && unresolvedBindings.Count == 0;
        var output = new JsonObject
        {
            ["scope"] = "recursive_static_encoding/1",
            ["plans"] = plans, ["hooks"] = hooks, ["steps"] = steps,
            ["structural"] = structural, ["condition_calls"] = conditionCalls, ["calls"] = calls,
            ["static_encoded"] = staticEncoded, ["runtime_resolved"] = runtimeResolved,
            ["translated"] = staticEncoded + runtimeResolved,
            ["unsupported"] = unsupported, ["incomplete_hooks"] = incomplete,
            ["unresolved_bindings"] = unresolvedBindings, ["runtime_only"] = runtime,
            ["dynamic_callability_verified"] = false,
            ["complete"] = complete,
            ["methods"] = new JsonObject(methods.Select(pair =>
                new KeyValuePair<string, JsonNode?>(pair.Key, JsonValue.Create(pair.Value)))),
            ["sample"] = rows,
        };
        if (asJson) Console.WriteLine(output.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        else
        {
            Console.WriteLine($"[静态编码] 章节 {plans} / 钩子 {hooks} / 步骤 {steps} / 条件调用 {conditionCalls}");
            Console.WriteLine($"[调用] 静态编码 {staticEncoded} / 需环境求值 {runtimeResolved} / 编码失败 {unsupported.Count}");
            Console.WriteLine($"[未完成] 不完整钩子 {incomplete.Count} / 待原生解析绑定 {unresolvedBindings.Count}");
            Console.WriteLine("[边界] 未验证动态方法可调用性；静态编码成功不代表可以运行或通关。");
        }
        return complete ? 0 : 1;

        void WalkSteps(JsonArray sequence, string path, HashSet<string> declared)
        {
            for (int index = 0; index < sequence.Count; index++)
            {
                var step = sequence[index]?.AsObject() ?? throw new JsonException($"{path}[{index}] 不是步骤对象");
                string source = $"{path}[{index}]";
                string kind = step["kind"]?.GetValue<string>() ?? throw new JsonException(source + " 缺少 kind");
                steps++;
                if (StructuralKinds.Contains(kind)) structural++;
                else if (CallKinds.Contains(kind)) AuditCall(step, source, declared, kind);
                else unsupported.Add(new JsonObject { ["source"] = source, ["reason"] = "未知步骤类型: " + kind });
                // Conditions can contain calls inside nested logical expressions.
                WalkExpression(step["test"], source + ".test", declared);
                WalkExpression(step["expr"], source + ".expr", declared);
                WalkExpression(step["value"], source + ".value", declared);
                if (step["body"] is JsonArray body) WalkSteps(body, source + ".body", declared);
                if (step["orelse"] is JsonArray otherwise) WalkSteps(otherwise, source + ".orelse", declared);
            }
        }

        void WalkExpression(JsonNode? node, string source, HashSet<string> declared)
        {
            if (node is JsonObject obj)
            {
                foreach (var (key, value) in obj)
                {
                    if (key == "call" && value is JsonObject call)
                    {
                        conditionCalls++;
                        AuditCall(call, source + ".call", declared, "call");
                    }
                    else WalkExpression(value, source + "." + key, declared);
                }
            }
            else if (node is JsonArray array)
                for (int index = 0; index < array.Count; index++) WalkExpression(array[index], $"{source}[{index}]", declared);
        }

        void AuditCall(JsonObject raw, string source, HashSet<string> declared, string kind)
        {
            calls++;
            string op = raw["op"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(op))
            {
                unsupported.Add(new JsonObject { ["source"] = source, ["reason"] = "调用缺少 op" });
                return;
            }
            var step = new CampaignPlanStep { Kind = kind, Op = op,
                Args = raw["args"]?.Deserialize<CampaignPlanStepArgs>() };
            var call = CampaignCallTranslator.Translate(step);
            if (call.Unsupported is { } reason)
            {
                unsupported.Add(new JsonObject { ["source"] = source, ["op"] = op, ["reason"] = reason });
                return;
            }
            if (call.RuntimeOnly is { } runtimeReason)
            {
                runtimeResolved++;
                runtime.Add(new JsonObject { ["source"] = source, ["op"] = op, ["reason"] = runtimeReason });
            }
            else staticEncoded++;
            var (prefix, inner) = CampaignPrimitiveRegistry.SplitFleetPrefix(op);
            string binding = op == "map.select" ? "executor"
                : CampaignPrimitiveRegistry.IsImplemented(op) ? "registered_primitive"
                : prefix != "super()" && declared.Contains(inner) ? "declared_hook"
                : "native_resolution_required";
            if (binding == "native_resolution_required")
                unresolvedBindings.Add(new JsonObject { ["source"] = source, ["op"] = op,
                    ["reason"] = "静态导出不足以确定该方法绑定；需原生继承/动态环境解析，不能据此声称可运行或不存在" });
            methods[call.Method] = methods.GetValueOrDefault(call.Method) + 1;
            rows.Add(new JsonObject
            {
                ["source"] = source, ["op"] = op, ["method"] = call.Method,
                ["binding"] = binding, ["encoding"] = call.RuntimeOnly is null ? "static" : "runtime",
                ["dynamic_callable"] = (JsonNode?)null, ["fleet_prefix"] = call.FleetPrefix,
                ["args"] = new JsonArray(call.Args.Select(arg => arg?.DeepClone()).ToArray()),
                ["kwargs"] = new JsonObject(call.KeywordArgs.Select(pair =>
                    new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value?.DeepClone()))),
            });
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"[错误] {message}");
        return 1;
    }
}
