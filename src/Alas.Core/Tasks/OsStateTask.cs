using System.Text.Json.Nodes;
using Alas.MapDetection;
using Alas.Runtime;

namespace Alas.Tasks;

/// <summary>
/// 大世界/海域域（R2 第三域）的第一刀：**只读状态探针**。
///
/// 为什么先做只读：大世界的动作流程（导航、选海域、出击）必须真机验收，而设备不在线时
/// 做出来也验不了；识别入口却是现成的 —— 宿主已有 `map_detect`（`mode="os"`）和
/// `globe_detect`。所以先把它们接成一条可调度的只读任务，
/// 动作流程等设备上线再加，避免造出无法验收的域。
///
/// 输入（`Input`）：
/// <code>
/// { "screenshot": "path/to/os_map.png",   // 用存盘帧（离线/复盘）
///   "capture": false,                     // 或让设备现抓一帧（需要真跑会话）
///   "detect": "map" }                     // map（默认）或 globe
/// </code>
///
/// 结论口径：**探针跑通即 `Succeeded`** —— map 的"没检测到"是有效状态；
/// globe 的上游加载或坐标往返出错、宿主/设备调用出错才是 `Failed`。
/// </summary>
public sealed class OsStateTask : ITaskRunner
{
    public string Kind => "os_state";

    public IReadOnlyList<string> Preconditions(TaskRequest request, TaskContext context)
    {
        var problems = new List<string>();
        if (request.Input?.ContainsKey("detect") == true
            && (request.Input["detect"] is not JsonValue detectValue
                || !detectValue.TryGetValue<string>(out var requestedDetect)
                || requestedDetect is not ("map" or "globe")))
            problems.Add("input.detect 必须是 map 或 globe");
        if (request.Input?.ContainsKey("capture") == true
            && (request.Input["capture"] is not JsonValue captureValue
                || !captureValue.TryGetValue<bool>(out _)))
            problems.Add("input.capture 必须是 JSON 布尔值");
        if (request.Input?.ContainsKey("screenshot") == true
            && (request.Input["screenshot"] is not JsonValue screenshotValue
                || !screenshotValue.TryGetValue<string>(out var path)
                || string.IsNullOrWhiteSpace(path)))
            problems.Add("input.screenshot 必须是非空帧路径字符串");
        bool capture = request.Input?["capture"] is JsonValue captureNode
                       && captureNode.TryGetValue<bool>(out var captureValueChecked)
                       && captureValueChecked;
        string? screenshot = request.Input?["screenshot"] is JsonValue screenshotNode
                             && screenshotNode.TryGetValue<string>(out var screenshotValueChecked)
            ? screenshotValueChecked : null;
        if (capture && screenshot is not null)
            problems.Add("input.capture 与 input.screenshot 只能二选一");
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
        string detect = request.Input?["detect"]?.GetValue<string>() ?? "map";
        var result = new TaskResult { Id = request.Id, Kind = Kind };
        var evidence = new JsonObject
        {
            ["mode"] = "os",
            ["detect"] = detect,
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

            if (detect == "globe")
            {
                var globe = context.Session.Vision.CallTyped<JsonObject>("globe_detect");
                evidence["globe"] = globe.DeepClone();
                evidence["globe_center"] = globe["center_loca"]?.DeepClone();
                evidence["log_lines"] = globe["log_lines"]?.DeepClone();
                if (globe["load"]?.GetValue<string>() != "ok"
                    || globe["roundtrip_error"] is not null)
                {
                    result.Outcome = TaskOutcome.Failed;
                    result.ErrorKind = RuntimeErrorKind.UpstreamError;
                    result.Error = globe["roundtrip_error"]?.GetValue<string>()
                                   ?? $"globe_detect 加载失败: {globe["load"]}";
                    result.Evidence = evidence;
                    return result;
                }
            }
            else
            {
                // 与 `alashub map --mode os` 走同一个上游判据。
                var detection = context.Session.Vision.CallTyped<MapDetectResult>(
                    "map_detect", new { mode = "os" });
                evidence["detected"] = detection.Detected;
                evidence["in_map"] = detection.InMap;
                evidence["backend"] = detection.Backend;
                evidence["construct_error"] = detection.ConstructError;
                evidence["load"] = detection.Load;
                evidence["predict"] = detection.Predict;
                evidence["grid_count"] = detection.GridCount;
                evidence["center_loca"] = detection.CenterLoca is null ? null
                    : System.Text.Json.JsonSerializer.SerializeToNode(detection.CenterLoca);
                evidence["reason"] = detection.Reason;
                if (detection.ExecutionError is { } mapError)
                {
                    result.Outcome = TaskOutcome.Failed;
                    result.ErrorKind = RuntimeErrorKind.UpstreamError;
                    result.Error = $"map_detect 执行失败: {mapError}";
                    result.Evidence = evidence;
                    return result;
                }
            }
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
