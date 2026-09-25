using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Alas.Runtime;

namespace Alas.Tasks;

/// <summary>
/// 读账号配置里的开关值（`kind = "config_get"`，只读）。
///
/// 用途（R4 的"配置"面 + 执行环授权前的证据）：判"某个周期任务能不能无人值守跑"时，
/// 第一步是把**决定它会不会花资源的开关**报出来并**留成工件** ——
/// 授权那一刻的开关状态，事后要能查得到。
///
/// 数据来自宿主 op `config_get`（按点分路径读，不写死任何键名）。
/// **缺失 ≠ false**：配置里没显式设置会走上游默认值，本任务如实分成三类报出
/// （true / false / missing），不把缺失当成 false。
///
/// 输入（`Input`）：`{ "keys": ["Dorm.BuyFurniture.Enable"], "instance": "alas" }`；
/// 不指定实例时读取 `alas`。
/// 结论：读到配置即 `Succeeded`（哪怕某些键缺失——缺失是**信息**，不是失败）；
/// 配置读不出来 → `Failed`（环境问题）。
/// </summary>
public sealed class ConfigGetTask : ITaskRunner
{
    private static readonly HashSet<string> InputFields = new(StringComparer.Ordinal)
    {
        "keys", "instance",
    };

    public string Kind => "config_get";

    public IReadOnlyList<string> Preconditions(TaskRequest request, TaskContext context)
    {
        var problems = new List<string>();
        if (request.Input is not null)
            foreach (var field in request.Input.Select(pair => pair.Key))
                if (!InputFields.Contains(field))
                    problems.Add($"未知配置读取字段: input.{field}");

        if (request.Input?["keys"] is not JsonArray keys || keys.Count == 0)
        {
            problems.Add("input.keys 为空：至少要给一个点分路径（如 Dorm.BuyFurniture.Enable），"
                         + "否则这一步没有任何信息量");
            return problems;
        }

        for (int index = 0; index < keys.Count; index++)
            if (keys[index] is not JsonValue value
                || !value.TryGetValue<string>(out var key)
                || string.IsNullOrWhiteSpace(key))
                problems.Add($"input.keys[{index}] 必须是非空字符串");
        if (request.Input?.ContainsKey("instance") == true)
        {
            if (request.Input["instance"] is not JsonValue value
                || !value.TryGetValue<string>(out var instance)
                || string.IsNullOrWhiteSpace(instance))
                problems.Add("input.instance 必须是非空实例名");
            else
            {
                try
                {
                    if (ConfigWorkspace.ValidateName(instance) != instance)
                        problems.Add("input.instance 必须使用规范实例名");
                }
                catch (ArgumentException) { problems.Add("input.instance 实例名无效"); }
            }
        }
        return problems;
    }

    public TaskResult Run(TaskRequest request, TaskContext context, CancellationToken token)
    {
        var result = new TaskResult { Id = request.Id, Kind = Kind };
        var keys = (request.Input?["keys"] as JsonArray)?
            .Select(k => k!.GetValue<string>()).ToList() ?? new List<string>();
        try
        {
            var read = context.Session.Vision.CallTyped<ConfigGetResult>(
                "config_get", new { keys, instance = request.Input?["instance"]?.GetValue<string>() ?? "alas" });
            if (read.Error is not null)
            {
                result.Outcome = TaskOutcome.Failed;
                result.ErrorKind = RuntimeErrorKind.Internal;
                result.Error = read.Error;
                return result;
            }
            var values = new JsonObject();
            var missing = new JsonArray();
            var on = new JsonArray();
            var off = new JsonArray();
            foreach (var pair in read.Values ?? new Dictionary<string, JsonNode?>())
            {
                values[pair.Key] = pair.Value?.DeepClone();
                if (pair.Value is null)
                {
                    missing.Add(pair.Key);
                    continue;
                }
                // 只对布尔值分 on/off —— 数值开关（如 BuyAmount）不在这里做"大于 0"的判断，
                // 那是各域自己的语义，通用层不解释业务字段。
                if (pair.Value.GetValueKind() == System.Text.Json.JsonValueKind.True) on.Add(pair.Key);
                else if (pair.Value.GetValueKind() == System.Text.Json.JsonValueKind.False) off.Add(pair.Key);
            }
            result.Evidence = new JsonObject
            {
                ["instance"] = read.Instance,
                ["config_source"] = read.ConfigSource,
                ["checked"] = read.Checked,
                ["values"] = values,
                ["true_keys"] = on,
                ["false_keys"] = off,
                ["missing_keys"] = missing,
                ["note"] = "missing 表示配置里没显式设置（缺省走上游默认值），不等于 false/0",
            };
            result.Outcome = TaskOutcome.Succeeded;
        }
        catch (OperationCanceledException)
        {
            result.Outcome = TaskOutcome.Skipped;
            result.ErrorKind = RuntimeErrorKind.Cancelled;
            result.Error = "调用方取消";
        }
        catch (Exception error)
        {
            var wrapped = RuntimeErrors.Wrap(error, "读账号配置失败");
            result.Outcome = TaskOutcome.Failed;
            result.ErrorKind = wrapped.Kind;
            result.Error = wrapped.Message;
        }
        return result;
    }
}

/// <summary>宿主 `config_get` 的返回。</summary>
public sealed class ConfigGetResult
{
    [JsonPropertyName("instance")] public string? Instance { get; set; }
    [JsonPropertyName("config_source")] public string? ConfigSource { get; set; }
    [JsonPropertyName("values")] public Dictionary<string, JsonNode?>? Values { get; set; }
    [JsonPropertyName("missing")] public List<string>? Missing { get; set; }
    [JsonPropertyName("checked")] public int? Checked { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}
