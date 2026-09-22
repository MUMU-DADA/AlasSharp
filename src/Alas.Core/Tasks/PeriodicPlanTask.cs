using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Alas.Runtime;

namespace Alas.Tasks;

/// <summary>
/// 周期任务动作半边的**勘察**任务（`kind = "periodic_plan"`，只读）。
///
/// 用途：在决定"要不要真跑某个周期任务"之前，先看清它会去跑哪个类、构造什么 ——
/// 上游把任务名到执行者的映射写在 `alas.py` 的同名方法里，宿主 op 用 AST 读它
/// （不 import、不实例化、不碰设备）。
///
/// 为什么要有任务这一层：报告/前端/批量调度都只认 `ITaskRunner`；
/// 而且"跑之前先看清"这件事本身就该是可调度、可留证据的一步。
///
/// 输入（`Input`）：`{ "tasks": ["commission", "research"] }` —— 不给则用一小撮常见任务名。
/// 结论：**每个任务名都查到了就 `Succeeded`**；有查不到的 → `Failed`（名字写错是调用方问题，
/// 不该悄悄跳过）；宿主读不到 alas.py → `Failed`。
/// </summary>
public sealed class PeriodicPlanTask : ITaskRunner
{
    private static readonly string[] DefaultTasks = { "commission", "research", "dorm", "reward" };

    public string Kind => "periodic_plan";

    public IReadOnlyList<string> Preconditions(TaskRequest request, TaskContext context)
    {
        var names = Requested(request);
        var problems = new List<string>();
        if (names.Count == 0)
            problems.Add("input.tasks 为空：至少要给一个上游任务名（如 commission），"
                         + "否则这一步没有任何信息量");
        return problems;
    }

    public TaskResult Run(TaskRequest request, TaskContext context, CancellationToken token)
    {
        var result = new TaskResult { Id = request.Id, Kind = Kind };
        var names = Requested(request);
        var plans = new JsonArray();
        var missing = new List<string>();
        try
        {
            foreach (var name in names)
            {
                token.ThrowIfCancellationRequested();
                var plan = context.Session.Vision.CallTyped<PeriodicPlanResult>(
                    "periodic_plan", new { task = name });
                if (plan.Found != true) missing.Add(name);
                plans.Add(new JsonObject
                {
                    ["task"] = name,
                    ["found"] = plan.Found,
                    ["lineno"] = plan.LineNumber,
                    ["imports"] = new JsonArray((plan.Imports ?? new List<string>())
                        .Select(i => (JsonNode)JsonValue.Create(i)!).ToArray()),
                    ["calls"] = new JsonArray((plan.Calls ?? new List<string>())
                        .Select(c => (JsonNode)JsonValue.Create(c)!).ToArray()),
                    ["calls_run"] = plan.CallsRun,
                    ["error"] = plan.Error,
                });
            }
            result.Evidence = new JsonObject
            {
                ["count"] = names.Count,
                ["missing"] = new JsonArray(missing
                    .Select(m => (JsonNode)JsonValue.Create(m)!).ToArray()),
                ["plans"] = plans,
            };
            if (missing.Count > 0)
            {
                // 名字写错是调用方的问题：明确失败并列出是哪些，不悄悄跳过
                result.Outcome = TaskOutcome.Failed;
                result.ErrorKind = RuntimeErrorKind.Internal;
                result.Error = "这些任务名在上游 alas.py 里找不到：" + string.Join(", ", missing);
            }
            else
            {
                result.Outcome = TaskOutcome.Succeeded;
            }
        }
        catch (OperationCanceledException)
        {
            result.Outcome = TaskOutcome.Skipped;
            result.ErrorKind = RuntimeErrorKind.Cancelled;
            result.Error = "调用方取消";
        }
        catch (Exception error)
        {
            var wrapped = RuntimeErrors.Wrap(error, "周期任务勘察失败");
            result.Outcome = TaskOutcome.Failed;
            result.ErrorKind = wrapped.Kind;
            result.Error = wrapped.Message;
        }
        return result;
    }

    private static List<string> Requested(TaskRequest request)
    {
        if (request.Input?["tasks"] is JsonArray array)
            return array.Select(n => n?.GetValue<string>() ?? "")
                .Where(n => n.Length > 0).ToList();
        return DefaultTasks.ToList();
    }
}

/// <summary>宿主 `periodic_plan` 的返回：这个任务名会去跑什么。</summary>
public sealed class PeriodicPlanResult
{
    [JsonPropertyName("task")] public string? Task { get; set; }
    [JsonPropertyName("found")] public bool? Found { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("lineno")] public int? LineNumber { get; set; }
    [JsonPropertyName("imports")] public List<string>? Imports { get; set; }
    [JsonPropertyName("calls")] public List<string>? Calls { get; set; }
    [JsonPropertyName("calls_run")] public bool? CallsRun { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}
