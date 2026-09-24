using System.Text.Json.Nodes;
using Alas.Runtime;
using Alas.Vision;

namespace Alas.Tasks;

/// <summary>
/// 账号状态域（R2 第二个垂直切片）：把"现在是什么状态"变成一条可调度的**只读**任务。
///
/// 为什么它是只读的：批量任务开跑前必须先知道"在不在图里、停在哪一页、是哪份配置"，
/// 而这些判断**不需要点任何东西**。只读带来两个好处：
///   1. dry-run 里也能跑（不会碰游戏），因此可以在没有设备时用存盘帧验收；
///   2. 它天然是后续所有域的"前置条件探针"，而不是某一关的专用逻辑。
///
/// 输入（`Input`，全部可选）：
/// <code>
/// { "capture": false,              // true = 让设备现抓一帧（需要真跑会话）
///   "screenshot": "path/to.png" }  // 用存盘帧（离线/复盘）
/// </code>
/// 判据全部来自上游：页面用 `Page.check_button`，在图内用 `handler/IN_MAP` 的颜色比对；
/// 本域只负责"取一次状态、按通用结论报出去、留下证据"，不新增判据。
/// </summary>
public sealed class AccountStateTask : ITaskRunner
{
    public string Kind => "account_state";

    public IReadOnlyList<string> Preconditions(TaskRequest request, TaskContext context)
    {
        var problems = new List<string>();
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
                       && captureNode.TryGetValue<bool>(out var captureChecked) && captureChecked;
        string? screenshot = request.Input?["screenshot"] is JsonValue screenshotNode
                             && screenshotNode.TryGetValue<string>(out var screenshotChecked)
            ? screenshotChecked : null;
        if (capture && screenshot is not null)
            problems.Add("input.capture 与 input.screenshot 只能二选一");
        // 只有"现抓一帧"才需要设备；用存盘帧时连设备都不需要。
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

        AccountStateResult state;
        try
        {
            state = context.Session.Vision.AccountState(capture, screenshot);
        }
        catch (Exception error)
        {
            var wrapped = RuntimeErrors.Wrap(error, "读取账号状态失败");
            result.Outcome = TaskOutcome.Failed;
            result.ErrorKind = wrapped.Kind;
            result.Error = wrapped.Message;
            return result;
        }

        if (state.Error is not null || state.Frame?.Available != true)
        {
            result.Outcome = TaskOutcome.Failed;
            // 抓帧失败是设备问题；"宿主还没有画面"是调用方没给帧 —— 两者恢复路径不同。
            result.ErrorKind = state.Error?.Contains("设备抓帧失败") == true
                ? RuntimeErrorKind.DeviceUnavailable : RuntimeErrorKind.Internal;
            result.Error = state.Error ?? "宿主没有可用的当前画面";
            result.Evidence = Evidence(state, capture, screenshot);
            return result;
        }

        var faults = (state.PageErrors ?? new List<string>()).ToList();
        if (state.InMapError is not null) faults.Add(state.InMapError);
        if (state.ConfigError is not null) faults.Add(state.ConfigError);
        if (faults.Count > 0)
        {
            result.Outcome = TaskOutcome.Failed;
            result.ErrorKind = RuntimeErrorKind.UpstreamError;
            result.Error = "账号状态读取不完整: " + string.Join("; ", faults);
            result.Evidence = Evidence(state, capture, screenshot);
            return result;
        }

        result.Outcome = TaskOutcome.Succeeded;
        result.Evidence = Evidence(state, capture, screenshot);
        return result;
    }

    /// <summary>证据里保留**判据数值**（页面集合、in_map 及其相似度、配置要点），不只留一个布尔。</summary>
    private static JsonObject Evidence(AccountStateResult state, bool capture, string? screenshot)
    {
        double? tolerance = null;
        if (state.InMapEvidence is not null &&
            state.InMapEvidence.TryGetValue("tolerance", out var tol) &&
            tol.ValueKind == System.Text.Json.JsonValueKind.Number)
            tolerance = tol.GetDouble();
        return new JsonObject
        {
            ["server"] = state.Server,
            ["pages"] = new JsonArray((state.Pages ?? new List<string>())
                .Select(p => (JsonNode)JsonValue.Create(p)!).ToArray()),
            ["page_errors"] = new JsonArray((state.PageErrors ?? new List<string>())
                .Select(p => (JsonNode)JsonValue.Create(p)!).ToArray()),
            ["in_map"] = state.InMap,
            ["in_map_error"] = state.InMapError,
            ["in_map_tolerance"] = tolerance is null ? null : JsonValue.Create(Math.Round(tolerance.Value, 2)),
            ["frame"] = state.Frame is null ? null : new JsonObject
            {
                ["available"] = state.Frame.Available,
                ["path"] = state.Frame.Path,
                ["shape"] = state.Frame.Shape is null
                    ? null
                    : new JsonArray(state.Frame.Shape.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()),
            },
            ["source"] = screenshot is not null ? $"file:{screenshot}" : capture ? "device_capture" : "host_frame",
            ["campaign"] = state.Campaign is null ? null : JsonNode.Parse(
                System.Text.Json.JsonSerializer.Serialize(state.Campaign)),
            ["config"] = state.Config is null ? null : JsonNode.Parse(
                System.Text.Json.JsonSerializer.Serialize(state.Config)),
            ["config_name"] = state.ConfigName,
            ["config_error"] = state.ConfigError,
        };
    }
}
