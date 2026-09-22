using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Alas.Navigation;
using Alas.Runtime;
using Alas.Vision;

namespace Alas.Tasks;

/// <summary>
/// 导航域（R1 收尾）：把 `alashub goto` 那条"自己驱动宿主"的 CLI 分支搬成运行时里的任务。
///
/// 为什么要搬：CLI 里直接驱动宿主违反"编排只在 `Alas.Core/Runtime`"；而且其它任务域都需要
/// "先到某页"这个前置能力，做成任务才能被队列复用（`{"kind":"navigate","input":{"to":"page_campaign"}}`）。
///
/// **导航逻辑一行都不重写**：直接包 <see cref="PageNavigator"/>（它已经在跑真机：运行时向上游要
/// 页面图、变体择优、未建模画面按返回自救）。本类只做三件事：翻译输入、把设备 I/O 接到宿主、
/// 把结果写进任务结论与证据。
///
/// 输入（`Input`）：`{ "to": "page_campaign", "max_hops": 8 }` —— `to` 用**上游页面名**，不写坐标。
/// 结论：到目标页 → `Succeeded`；没到 → `Failed`（证据里带跳数与最终页面集合）；
/// 目标页不在上游页面图里 → **前置条件不满足 → skipped**（"没跑"不是"跑失败"）。
/// </summary>
public sealed class NavigateTask : ITaskRunner
{
    public string Kind => "navigate";

    public IReadOnlyList<string> Preconditions(TaskRequest request, TaskContext context)
    {
        var problems = new List<string>();
        string? target = request.Input?["to"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(target))
        {
            problems.Add("input.to 为空：导航任务必须给出目标页面名（上游页面名，如 page_campaign）");
            return problems;
        }
        try
        {
            var graph = PageGraph.Load(context.Session.Vision);
            if (!graph.HasPage(target))
                problems.Add($"目标页不在上游页面图里: {target}"
                             + $"（图里共 {graph.NodeCount} 节点 / {graph.EdgeCount} 条边）");
        }
        catch (Exception error)
        {
            problems.Add($"取不到上游页面图: {error.GetType().Name}: {error.Message}");
        }
        return problems;
    }

    public TaskResult Run(TaskRequest request, TaskContext context, CancellationToken token)
    {
        string target = request.Input?["to"]?.GetValue<string>() ?? "";
        int maxHops = 8;
        if (request.Input?["max_hops"] is JsonNode hops
            && hops.GetValueKind() == JsonValueKind.Number)
            maxHops = (int)hops.GetValue<double>();

        var result = new TaskResult { Id = request.Id, Kind = Kind };
        try
        {
            var graph = PageGraph.Load(context.Session.Vision);
            var navigator = new PageNavigator(context.Session.Vision,
                new HostNavigationDevice(context.Session.Vision), graph)
            {
                MaxHops = maxHops,
            };
            var navigation = navigator.Goto(target);
            result.Evidence = Evidence(navigation, graph);
            result.Outcome = navigation.Success ? TaskOutcome.Succeeded : TaskOutcome.Failed;
            if (!navigation.Success)
            {
                // 没到目标页**不是上游报错**：可能是页面图里这条路走不通、或画面未建模。
                result.ErrorKind = RuntimeErrorKind.None;
                result.Error = $"未到达 {target}: {navigation.Failure ?? "未知原因"}";
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
            var wrapped = RuntimeErrors.Wrap(error, $"导航到 {target} 失败");
            result.Outcome = TaskOutcome.Failed;
            // 设备/宿主类错误照实分类（没设备时就是这一种），其余归内部错误。
            result.ErrorKind = wrapped.Kind;
            result.Error = wrapped.Message;
        }
        return result;
    }

    private static JsonObject Evidence(NavigationResult navigation, PageGraph graph)
    {
        var hops = new JsonArray();
        foreach (var hop in navigation.Hops)
            hops.Add(JsonNode.Parse(JsonSerializer.Serialize(hop)));
        return new JsonObject
        {
            ["target"] = navigation.Target,
            ["success"] = navigation.Success,
            ["final_pages"] = new JsonArray(navigation.FinalPages
                .Select(p => (JsonNode)JsonValue.Create(p)!).ToArray()),
            ["hops"] = hops,
            ["failure"] = navigation.Failure,
            ["graph_nodes"] = graph.NodeCount,
            ["graph_edges"] = graph.EdgeCount,
        };
    }
}

/// <summary>
/// 把导航器的设备 I/O 接到**宿主**（而不是另起一套 adb 层）：
/// 抓帧与点击都走 `IVisionEngine`，与项目"设备 I/O 走宿主"的边界一致。
/// </summary>
internal sealed class HostNavigationDevice : INavigationDevice
{
    private readonly IVisionEngine _vision;

    public HostNavigationDevice(IVisionEngine vision) => _vision = vision;

    /// <summary>抓帧后**像素留在宿主里**，C# 只拿到形状等元信息。</summary>
    public void Capture() => _vision.CaptureViaEngine(raw: true);

    public void Click(int x, int y)
        => _vision.CallTyped<DeviceClickResult>("device_click", new { x, y });

    public void Back() => _vision.CallTyped<DeviceClickResult>("device_back", new { });
}

/// <summary>`device_click` / `device_back` 的返回（宿主只回耗时，动作本身由宿主执行）。</summary>
public sealed class DeviceClickResult
{
    [JsonPropertyName("ms")] public double? Milliseconds { get; set; }
    [JsonPropertyName("click_error")] public string? ClickError { get; set; }
}
