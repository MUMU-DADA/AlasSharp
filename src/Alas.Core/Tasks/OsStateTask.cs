using System.Text.Json.Nodes;
using Alas.MapDetection;
using Alas.Runtime;

namespace Alas.Tasks;

/// <summary>
/// 大世界/海域域（R2 第三域）的第一刀：**只读状态探针**。
///
/// 为什么先做只读：大世界的动作流程（导航、选海域、出击）必须真机验收，而设备不在线时
/// 做出来也验不了；识别入口却是现成的 —— 宿主已经有 `map_detect`（`mode="os"`）这条
/// 产品路径（`alashub map --mode os`，5/5 通过）。所以先把它接成一条可调度的只读任务，
/// 动作流程等设备上线再加，避免造出无法验收的域。
///
/// 输入（`Input`）：
/// <code>
/// { "screenshot": "path/to/os_map.png",   // 用存盘帧（离线/复盘）
///   "capture": false }                     // 或让设备现抓一帧（需要真跑会话）
/// </code>
///
/// 结论口径：**探针跑通即 `Succeeded`** —— "没检测到"是有效状态（可能不在海域里），
/// 不是失败；只有宿主/设备调用出错才是 `Failed`。判据仍全部来自上游宿主。
/// </summary>
public sealed class OsStateTask : ITaskRunner
{
    public string Kind => "os_state";

    public IReadOnlyList<string> Preconditions(TaskRequest request, TaskContext context)
    {
        var problems = new List<string>();
        bool capture = request.Input?["capture"]?.GetValue<bool>() ?? false;
        string? screenshot = request.Input?["screenshot"]?.GetValue<string>();
        if (!capture && string.IsNullOrWhiteSpace(screenshot))
            problems.Add("需要 screenshot=<帧路径> 或 capture=true（二者之一）");
        if (capture && context.Options.DryRun)
            problems.Add("capture=true 需要真跑会话（dry-run 不碰设备）；离线请用 screenshot=<帧路径>");
        if (capture && string.IsNullOrWhiteSpace(context.Options.Serial))
            problems.Add("capture=true 需要指定设备 serial");
        if (!string.IsNullOrWhiteSpace(screenshot) && !File.Exists(screenshot))
            problems.Add($"screenshot 文件不存在: {screenshot}");
        return problems;
    }

    public TaskResult Run(TaskRequest request, TaskContext context, CancellationToken token)
    {
        bool capture = request.Input?["capture"]?.GetValue<bool>() ?? false;
        string? screenshot = request.Input?["screenshot"]?.GetValue<string>();
        var result = new TaskResult { Id = request.Id, Kind = Kind };
        var evidence = new JsonObject
        {
            ["mode"] = "os",
            ["source"] = screenshot is not null ? $"file:{screenshot}"
                       : capture ? "device_capture" : "host_frame",
        };

        try
        {
            if (screenshot is not null)
            {
                var loaded = context.Session.Vision.LoadScreenshot(screenshot);
                evidence["frame"] = JsonNode.Parse(
                    System.Text.Json.JsonSerializer.Serialize(loaded));
            }
            else if (capture)
            {
                var captured = context.Session.Vision.CaptureViaEngine(raw: true);
                evidence["frame"] = JsonNode.Parse(
                    System.Text.Json.JsonSerializer.Serialize(captured));
                if (captured.Error is not null)
                {
                    result.Outcome = TaskOutcome.Failed;
                    result.ErrorKind = RuntimeErrorKind.DeviceUnavailable;
                    result.Error = $"设备抓帧失败: {captured.Error}";
                    result.Evidence = evidence;
                    return result;
                }
            }

            // 与 `alashub map --mode os` 走的是同一个宿主 op（判据不在 C# 里重写）。
            var detection = context.Session.Vision.CallTyped<MapDetectResult>(
                "map_detect", new { mode = "os" });
            evidence["detected"] = detection.Detected;
            evidence["grid_count"] = detection.GridCount;
        }
        catch (Exception error)
        {
            var wrapped = RuntimeErrors.Wrap(error, "大世界状态探针失败");
            result.Outcome = TaskOutcome.Failed;
            result.ErrorKind = wrapped.Kind;
            result.Error = wrapped.Message;
            result.Evidence = evidence;
            return result;
        }

        result.Outcome = TaskOutcome.Succeeded;
        result.Evidence = evidence;
        return result;
    }
}
