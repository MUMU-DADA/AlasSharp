using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Alas.Runtime;

namespace Alas.Tasks;

/// <summary>
/// Queue-owned navigation. Each leg uses upstream UI.ui_ensure(); this task owns
/// authorization, rounds, cancellation boundaries, and artifacts.
/// </summary>
public sealed class NavigateTask : ITaskRunner
{
    public string Kind => "navigate";

    public IReadOnlyList<string> Preconditions(TaskRequest request, TaskContext context)
    {
        var problems = new List<string>();
        var (target, rounds) = ReadInput(request, problems);
        if (ActionPrecondition(context) is string denied) problems.Add(denied);
        if (problems.Count > 0) return problems;
        try
        {
            var pages = context.Session.Vision.PageList().Pages;
            if (!pages.Any(page => page.Page == target && page.CheckButton is not null))
                problems.Add($"目标页不在上游可验证页面列表里: {target}");
            if (rounds > 1 && !pages.Any(page => page.Page == "page_main" && page.CheckButton is not null))
                problems.Add("rounds > 1 需要上游页面列表包含 page_main");
        }
        catch (Exception error)
        {
            problems.Add($"取不到上游页面列表: {error.GetType().Name}: {error.Message}");
        }
        return problems;
    }

    public TaskResult Run(TaskRequest request, TaskContext context, CancellationToken token)
    {
        var problems = new List<string>();
        var (target, rounds) = ReadInput(request, problems);
        if (ActionPrecondition(context) is string denied) problems.Add(denied);
        if (problems.Count > 0)
            return new TaskResult
            {
                Id = request.Id, Kind = Kind, Outcome = TaskOutcome.Skipped,
                StopReason = "precondition", Error = string.Join("; ", problems),
            };

        var result = new TaskResult { Id = request.Id, Kind = Kind };
        var roundEvidence = new JsonArray();
        var evidence = new JsonObject
        {
            ["target"] = target,
            ["rounds_requested"] = rounds,
            ["rounds_completed"] = 0,
            ["rounds"] = roundEvidence,
            ["final_page"] = null,
            ["success"] = false,
        };
        result.Evidence = evidence;
        JsonObject? currentRound = null;
        string phase = "to_target";
        int completed = 0;
        try
        {
            bool NavigateLeg(string destination, string leg)
            {
                phase = leg;
                token.ThrowIfCancellationRequested();
                var native = context.Session.Vision.CallTyped<NativeUiEnsureResult>(
                    "ui_ensure", new { destination, allow_actions = true });
                var legEvidence = new JsonObject
                {
                    ["destination"] = destination,
                    ["arrived"] = native.Arrived,
                    ["final_page"] = native.FinalPage,
                    // Upstream may click Home in ui_get_current_page before returning changed=false.
                    ["changed"] = native.Changed,
                    ["elapsed_ms"] = native.ElapsedMilliseconds,
                    ["error"] = native.Error,
                    ["error_kind"] = native.ErrorKind,
                };
                if (native.TracebackTail is { Count: > 0 })
                    legEvidence["traceback_tail"] = JsonSerializer.SerializeToNode(native.TracebackTail);
                currentRound![leg] = legEvidence;
                evidence["final_page"] = native.FinalPage;
                if (!native.Arrived || native.FinalPage != destination || native.Error is not null)
                {
                    result.Outcome = TaskOutcome.Failed;
                    result.ErrorKind = NativeErrorKind(native.ErrorKind);
                    result.Error = $"未到达 {destination}: {native.Error ?? "原生导航未确认到达"}";
                    evidence["failure"] = result.Error;
                    evidence["failure_phase"] = leg;
                    currentRound["failure"] = result.Error;
                    return false;
                }
                // Native navigation is indivisible; cancellation takes effect between legs.
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
            evidence["traceback_tail"] = JsonSerializer.SerializeToNode(trace);
            if (currentRound is not null) currentRound["failure"] = wrapped.Message;
        }
        return result;
    }

    private static (string Target, int Rounds) ReadInput(TaskRequest request, List<string> problems)
    {
        string target = "";
        if (request.Input?["to"] is JsonValue value && value.TryGetValue<string>(out var text)
            && !string.IsNullOrWhiteSpace(text))
            target = text;
        else problems.Add("input.to 必须是非空字符串（上游页面名）");
        if (request.Input?.ContainsKey("max_hops") == true)
            problems.Add("input.max_hops 已停用：上游 UI.ui_ensure 不提供逐跳上限");
        int rounds = PositiveInteger(request, "rounds", 1, problems);
        return (target, rounds);
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

    private static RuntimeErrorKind NativeErrorKind(string? kind)
        => kind switch
        {
            "ActionNotAllowed" => RuntimeErrorKind.ChapterRefused,
            "UnknownPage" => RuntimeErrorKind.ChapterRefused,
            "DestinationNotVisible" => RuntimeErrorKind.UpstreamError,
            "GameStuckError" => RuntimeErrorKind.Timeout,
            "GameNotRunningError" => RuntimeErrorKind.DeviceUnavailable,
            _ => RuntimeErrorKind.UpstreamError,
        };

    private static string? ActionPrecondition(TaskContext context)
    {
        if (context.Options.DryRun || !context.Options.AllowActions)
            return "navigate 会点击设备，需要 --run --allow-actions；只读会话不授予动作权限";
        if (context.Session.DeviceConfigureCount != 1)
            return "navigate 需要已配置的设备";
        return null;
    }
}

public sealed class NativeUiEnsureResult
{
    [JsonPropertyName("arrived")] public bool Arrived { get; set; }
    [JsonPropertyName("final_page")] public string? FinalPage { get; set; }
    [JsonPropertyName("changed")] public bool? Changed { get; set; }
    [JsonPropertyName("elapsed_ms")] public double? ElapsedMilliseconds { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("error_kind")] public string? ErrorKind { get; set; }
    [JsonPropertyName("traceback_tail")] public List<string>? TracebackTail { get; set; }
}
