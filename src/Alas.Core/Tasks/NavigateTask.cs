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
/// 输入（`Input`）：`{ "to": "page_campaign", "max_hops": 8, "rounds": 1 }` —— `to` 用**上游页面名**，不写坐标。
/// 结论：到目标页 → `Succeeded`；没到 → `Failed`（证据里带跳数与最终页面集合）；
/// 目标页不在上游页面图里 → **前置条件不满足 → skipped**（"没跑"不是"跑失败"）。
/// </summary>
public sealed class NavigateTask : ITaskRunner
{
    public string Kind => "navigate";

    public IReadOnlyList<string> Preconditions(TaskRequest request, TaskContext context)
    {
        var problems = new List<string>();
        var (target, _, rounds) = ReadInput(request, problems);
        if (ActionPrecondition(context) is string denied) problems.Add(denied);
        if (problems.Count > 0) return problems;
        try
        {
            var graph = PageGraph.Load(context.Session.Vision);
            if (!graph.HasPage(target))
                problems.Add($"目标页不在上游页面图里: {target}"
                             + $"（图里共 {graph.NodeCount} 节点 / {graph.EdgeCount} 条边）");
            if (rounds > 1 && !graph.HasPage("page_main"))
                problems.Add("rounds > 1 需要上游页面图包含 page_main（回合之间返回主界面）");
        }
        catch (Exception error)
        {
            problems.Add($"取不到上游页面图: {error.GetType().Name}: {error.Message}");
        }
        return problems;
    }

    public TaskResult Run(TaskRequest request, TaskContext context, CancellationToken token)
    {
        var problems = new List<string>();
        var (target, maxHops, rounds) = ReadInput(request, problems);
        if (ActionPrecondition(context) is string denied) problems.Add(denied);
        if (problems.Count > 0)
            return new TaskResult
            {
                Id = request.Id, Kind = Kind, Outcome = TaskOutcome.Skipped,
                StopReason = "precondition", Error = string.Join("; ", problems),
            };

        var result = new TaskResult { Id = request.Id, Kind = Kind };
        var roundEvidence = new JsonArray();
        var hops = new JsonArray();
        var evidence = new JsonObject
        {
            ["target"] = target,
            ["max_hops"] = maxHops,
            ["rounds_requested"] = rounds,
            ["rounds_completed"] = 0,
            ["rounds"] = roundEvidence,
            ["hops"] = hops,
            ["final_pages"] = new JsonArray(),
            ["success"] = false,
        };
        result.Evidence = evidence;
        JsonObject? currentRound = null;
        string phase = "load_graph";
        int completed = 0;
        try
        {
            var graph = PageGraph.Load(context.Session.Vision);
            evidence["graph_nodes"] = graph.NodeCount;
            evidence["graph_edges"] = graph.EdgeCount;
            var navigator = new PageNavigator(context.Session.Vision,
                new HostNavigationDevice(context.Session.Vision), graph)
            {
                MaxHops = maxHops,
            };

            bool NavigateLeg(string destination, string leg)
            {
                phase = leg;
                token.ThrowIfCancellationRequested();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var navigation = navigator.Goto(destination);
                watch.Stop();
                var legEvidence = Evidence(navigation, graph);
                legEvidence["elapsed_ms"] = Math.Round(watch.Elapsed.TotalMilliseconds, 3);
                currentRound![leg] = legEvidence;
                evidence["final_pages"] = legEvidence["final_pages"]!.DeepClone();
                foreach (var hop in legEvidence["hops"]!.AsArray()) hops.Add(hop!.DeepClone());
                if (!navigation.Success)
                {
                    result.Outcome = TaskOutcome.Failed;
                    result.Error = $"未到达 {destination}: {navigation.Failure ?? "未知原因"}";
                    evidence["failure"] = result.Error;
                    evidence["failure_phase"] = leg;
                    currentRound!["failure"] = result.Error;
                    currentRound!["failure_phase"] = leg;
                    return false;
                }
                // 已进入的导航完整结束后才响应取消，不中断一次宿主/页面导航调用。
                token.ThrowIfCancellationRequested();
                return true;
            }

            for (int round = 1; completed < rounds; round++)
            {
                token.ThrowIfCancellationRequested();
                currentRound = new JsonObject { ["round"] = round, ["success"] = false };
                roundEvidence.Add(currentRound);
                if (round > 1 && !NavigateLeg("page_main", "return_to_main")) return result;
                if (!NavigateLeg(target, "to_target")) return result;
                currentRound["success"] = true;
                completed++;
                evidence["rounds_completed"] = completed;
            }
            result.Outcome = TaskOutcome.Succeeded;
            evidence["success"] = true;
        }
        catch (OperationCanceledException)
        {
            result.Outcome = TaskOutcome.Skipped;
            result.ErrorKind = RuntimeErrorKind.Cancelled;
            result.StopReason = "cancelled";
            result.Error = "调用方取消：在导航边界停止";
            evidence["cancelled"] = true;
        }
        catch (Exception error)
        {
            var wrapped = RuntimeErrors.Wrap(error, $"导航到 {target} 失败");
            var trace = error.ToString().Split('\n').TakeLast(12)
                .Select(line => line.TrimEnd('\r')).ToArray();
            result.Outcome = TaskOutcome.Failed;
            result.ErrorKind = wrapped.Kind;
            result.Error = wrapped.Message + "\n" + string.Join("\n", trace);
            evidence["failure"] = wrapped.Message;
            evidence["failure_phase"] = phase;
            evidence["traceback_tail"] = new JsonArray(
                trace.Select(line => (JsonNode)JsonValue.Create(line)!).ToArray());
            if (currentRound is not null)
            {
                currentRound["failure"] = wrapped.Message;
                currentRound["failure_phase"] = phase;
            }
        }
        return result;
    }

    private static (string Target, int MaxHops, int Rounds) ReadInput(
        TaskRequest request, List<string> problems)
    {
        string target = "";
        if (request.Input?["to"] is JsonValue value && value.TryGetValue<string>(out var text)
            && !string.IsNullOrWhiteSpace(text))
            target = text;
        else problems.Add("input.to 必须是非空字符串（上游页面名）");
        int maxHops = PositiveInteger(request, "max_hops", 8, problems);
        int rounds = PositiveInteger(request, "rounds", 1, problems);
        return (target, maxHops, rounds);
    }

    private static int PositiveInteger(TaskRequest request, string key, int defaultValue,
                                       List<string> problems)
    {
        if (request.Input?.ContainsKey(key) != true) return defaultValue;
        var node = request.Input[key];
        if (node?.GetValueKind() == JsonValueKind.Number)
        {
            try
            {
                double number = node.Deserialize<double>();
                if (double.IsFinite(number) && number >= 1 && number <= int.MaxValue
                    && number == Math.Truncate(number)) return (int)number;
            }
            catch (JsonException) { }
        }
        problems.Add($"input.{key} 必须是 1 到 {int.MaxValue} 的整数数值");
        return defaultValue;
    }

    private static string? ActionPrecondition(TaskContext context)
    {
        if (context.Options.DryRun || !context.Options.AllowActions)
            return "navigate 会点击设备，需要 --run --allow-actions；只读会话不授予动作权限";
        if (context.Session.DeviceConfigureCount != 1)
            return "navigate 需要已配置的设备";
        return null;
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
    public void Capture()
    {
        var captured = _vision.CaptureViaEngine(raw: true);
        if (captured.Error is not null)
            throw new AlasRuntimeException(RuntimeErrorKind.DeviceUnavailable,
                                           $"设备抓帧失败: {captured.Error}");
    }

    public void Click(int x, int y)
        => CheckAction(_vision.CallTyped<DeviceClickResult>("device_click", new { x, y }), "点击");

    public void Back()
        => CheckAction(_vision.CallTyped<DeviceClickResult>("device_back", new { }), "返回");

    private static void CheckAction(DeviceClickResult result, string action)
    {
        string? error = result.ClickError ?? result.Error;
        if (error is not null || result.Ok == false)
            throw new AlasRuntimeException(RuntimeErrorKind.DeviceUnavailable,
                                           $"设备{action}失败: {error ?? "宿主返回 ok=false"}");
    }
}

/// <summary>`device_click` / `device_back` 的返回（宿主只回耗时，动作本身由宿主执行）。</summary>
public sealed class DeviceClickResult
{
    [JsonPropertyName("ms")] public double? Milliseconds { get; set; }
    [JsonPropertyName("click_error")] public string? ClickError { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("ok")] public bool? Ok { get; set; }
}
