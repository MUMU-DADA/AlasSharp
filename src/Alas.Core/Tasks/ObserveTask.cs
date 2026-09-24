using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.MapDetection;
using Alas.Runtime;
using Alas.Vision;

namespace Alas.Tasks;

/// <summary>
/// 常驻会话的只读观测：复用已配置的设备与宿主抓帧、判定页面，并可选调用上游地图识别。
/// 本任务只汇总上游返回；不点击、不导航，也不维护页面或地图识别规则。
/// </summary>
public sealed class ObserveTask : ITaskRunner
{
    public string Kind => "observe";

    public IReadOnlyList<string> Preconditions(TaskRequest request, TaskContext context)
    {
        var problems = new List<string>();
        ReadInput(request, problems);
        if (context.Session.DeviceConfigureCount != 1)
            problems.Add("observe 需要已配置设备的会话（dry-run 不配置设备）");
        return problems;
    }

    public TaskResult Run(TaskRequest request, TaskContext context, CancellationToken token)
    {
        var problems = new List<string>();
        var (tickSeconds, seconds, mapMode, chapter) = ReadInput(request, problems);
        if (problems.Count > 0) throw new ArgumentException(string.Join("; ", problems));

        var result = new TaskResult { Id = request.Id, Kind = Kind };
        var captureTimes = new List<double>();
        var pageTimes = new List<double>();
        var tickGaps = new List<double>();
        var mapTimes = new List<double>();
        var pageHits = new Dictionary<string, int>(StringComparer.Ordinal);
        var pageErrors = new HashSet<string>(StringComparer.Ordinal);
        var lastPages = new List<string>();
        var errorDetails = new JsonArray();
        var elapsed = new Stopwatch();
        string? captureMethod = null, lastMapReason = null, firstError = null;
        List<int>? captureShape = null;
        int ticks = 0, captureAttempts = 0, captureFailures = 0, pageFailures = 0;
        int mapAttempts = 0, mapHits = 0, mapErrors = 0;
        int? lastGridCount = null;
        int? pagesTick = null;
        RuntimeErrorKind firstErrorKind = RuntimeErrorKind.None;
        var evidence = new JsonObject
        {
            ["tick_seconds"] = tickSeconds,
            ["requested_seconds"] = seconds,
            ["map_mode"] = mapMode,
            ["chapter"] = chapter,
            ["read_only"] = true,
        };

        void RecordError(string operation, int tick, string message, RuntimeErrorKind kind,
                         Exception? exception = null)
        {
            var trace = exception?.ToString().Split('\n').TakeLast(12)
                .Select(line => line.TrimEnd('\r')).ToArray() ?? Array.Empty<string>();
            errorDetails.Add(new JsonObject
            {
                ["operation"] = operation,
                ["tick"] = tick,
                ["error_kind"] = RuntimeErrors.Name(kind),
                ["error"] = message,
                ["traceback_tail"] = Strings(trace),
            });
            if (firstError is not null) return;
            firstErrorKind = kind;
            firstError = trace.Length == 0 ? message : message + "\n" + string.Join("\n", trace);
        }

        TaskResult Finish(bool cancelled = false)
        {
            evidence["ticks"] = ticks;
            evidence["errors"] = errorDetails.Count;
            evidence["error_details"] = errorDetails;
            evidence["elapsed_seconds"] = Round(elapsed.Elapsed.TotalSeconds);
            evidence["cancelled"] = cancelled;
            evidence["pages"] = Strings(lastPages);
            evidence["pages_tick"] = pagesTick;
            evidence["page_hits"] = JsonSerializer.SerializeToNode(pageHits);
            evidence["page_errors"] = Strings(pageErrors.OrderBy(e => e, StringComparer.Ordinal));
            // 预热结果单独放在 warmup，以下统计只计算正式 tick，避免失败数大于尝试数。
            evidence["capture"] = new JsonObject
            {
                ["attempts"] = captureAttempts,
                ["succeeded"] = captureTimes.Count,
                ["failed"] = captureFailures,
                ["method"] = captureMethod,
                ["shape"] = JsonSerializer.SerializeToNode(captureShape),
                ["timing_ms"] = Stats(captureTimes),
            };
            evidence["page_detection"] = new JsonObject
            {
                ["attempts"] = pageTimes.Count,
                ["failed"] = pageFailures,
                ["skipped_capture_failed"] = captureFailures,
                ["timing_ms"] = Stats(pageTimes),
            };
            evidence["tick_interval_ms"] = Stats(tickGaps);
            if (mapMode is not null)
                evidence["map"] = new JsonObject
                {
                    ["mode"] = mapMode,
                    ["chapter"] = chapter,
                    ["attempts"] = mapAttempts,
                    ["detected_hits"] = mapHits,
                    ["errors"] = mapErrors,
                    ["skipped_capture_failed"] = captureFailures,
                    ["last_grid_count"] = lastGridCount,
                    ["last_reason"] = lastMapReason,
                    ["timing_ms"] = Stats(mapTimes),
                };

            result.Outcome = errorDetails.Count > 0 ? TaskOutcome.Failed
                : cancelled ? TaskOutcome.Skipped : TaskOutcome.Succeeded;
            result.ErrorKind = errorDetails.Count > 0 ? firstErrorKind
                : cancelled ? RuntimeErrorKind.Cancelled : RuntimeErrorKind.None;
            result.Error = firstError ?? (cancelled ? "调用方取消：在观测 tick 边界停止" : null);
            result.StopReason = cancelled ? "cancelled" : null;
            result.Evidence = evidence;
            return result;
        }

        if (token.IsCancellationRequested) return Finish(cancelled: true);

        // 正式计时前预热一次设备后端；没有拿到首帧就没有可观测的画面。
        var warmupWatch = Stopwatch.StartNew();
        try
        {
            var warmup = context.Session.Vision.CaptureViaEngine(raw: true);
            warmupWatch.Stop();
            evidence["warmup"] = CaptureEvidence(warmup, warmupWatch.Elapsed.TotalMilliseconds);
            captureMethod = warmup.Method;
            captureShape = warmup.Shape;
            if (warmup.Error is not null)
            {
                RecordError("warmup_capture", 0, $"首次抓帧失败: {warmup.Error}",
                            RuntimeErrorKind.DeviceUnavailable);
                return Finish();
            }
        }
        catch (Exception error)
        {
            warmupWatch.Stop();
            evidence["warmup"] = new JsonObject
            {
                ["ok"] = false,
                ["elapsed_ms"] = Round(warmupWatch.Elapsed.TotalMilliseconds),
                ["error"] = error.Message,
            };
            RecordError("warmup_capture", 0, $"首次抓帧失败: {error.Message}",
                        RuntimeErrors.Classify(error), error);
            return Finish();
        }

        elapsed.Start();
        double previousTick = 0;
        while (ticks == 0 || elapsed.Elapsed.TotalSeconds < seconds)
        {
            if (token.IsCancellationRequested) return Finish(cancelled: true);
            var tickWatch = Stopwatch.StartNew();
            int currentTick = ticks + 1;
            bool capturedFreshFrame = false;
            captureAttempts++;
            try
            {
                var captured = context.Session.Vision.CaptureViaEngine(raw: true);
                captureMethod = captured.Method ?? captureMethod;
                captureShape = captured.Shape ?? captureShape;
                if (captured.Error is null)
                {
                    captureTimes.Add(captured.CaptureMs);
                    capturedFreshFrame = true;
                }
                else
                {
                    captureFailures++;
                    RecordError("device_capture_set", currentTick, captured.Error,
                                RuntimeErrorKind.DeviceUnavailable);
                }
            }
            catch (Exception error)
            {
                captureFailures++;
                RecordError("device_capture_set", currentTick, error.Message,
                            RuntimeErrors.Classify(error), error);
            }

            // 抓帧失败后宿主可能仍持有旧帧，必须跳过本 tick 的全部视觉判定。
            if (capturedFreshFrame)
            {
                var pageWatch = Stopwatch.StartNew();
                try
                {
                    var current = context.Session.Vision.PageCurrent();
                    lastPages = current.Hit ?? new List<string>();
                    pagesTick = currentTick;
                    foreach (string page in lastPages)
                        pageHits[page] = pageHits.GetValueOrDefault(page) + 1;
                    if (current.Errors is { Count: > 0 })
                    {
                        pageFailures++;
                        foreach (string error in current.Errors) pageErrors.Add(error);
                        RecordError("page_current", currentTick, string.Join("; ", current.Errors),
                                    RuntimeErrorKind.UpstreamError);
                    }
                }
                catch (Exception error)
                {
                    pageFailures++;
                    pageErrors.Add($"{error.GetType().Name}: {error.Message}");
                    RecordError("page_current", currentTick, error.Message,
                                RuntimeErrors.Classify(error), error);
                }
                finally
                {
                    pageWatch.Stop();
                    pageTimes.Add(pageWatch.Elapsed.TotalMilliseconds);
                }

                if (mapMode is not null)
                {
                    mapAttempts++;
                    var mapWatch = Stopwatch.StartNew();
                    try
                    {
                        var detected = context.Session.Vision.CallTyped<MapDetectResult>(
                            "map_detect", new { mode = mapMode, chapter });
                        var mapError = detected.ExecutionError;
                        lastMapReason = mapError ?? detected.Reason;
                        lastGridCount = mapError is null || detected.GridFlagsError is not null
                            ? detected.GridCount : null;
                        if (mapError is not null)
                        {
                            mapErrors++;
                            RecordError("map_detect", currentTick, mapError,
                                        RuntimeErrorKind.UpstreamError);
                        }
                        else if (detected.Detected)
                        {
                            mapHits++;
                            lastGridCount = detected.GridCount;
                        }
                    }
                    catch (Exception error)
                    {
                        mapErrors++;
                        lastMapReason = $"{error.GetType().Name}: {error.Message}";
                        lastGridCount = null;
                        RecordError("map_detect", currentTick, error.Message,
                                    RuntimeErrors.Classify(error), error);
                    }
                    finally
                    {
                        mapWatch.Stop();
                        mapTimes.Add(mapWatch.Elapsed.TotalMilliseconds);
                    }
                }
            }

            ticks++;
            double now = elapsed.Elapsed.TotalSeconds;
            tickGaps.Add((now - previousTick) * 1000);
            previousTick = now;
            tickWatch.Stop();

            // 取消只在完成一个 tick 后生效，不中断已进入宿主的抓帧或识别调用。
            if (token.IsCancellationRequested) return Finish(cancelled: true);
            double wait = Math.Min(seconds - elapsed.Elapsed.TotalSeconds,
                                   tickSeconds - tickWatch.Elapsed.TotalSeconds);
            double waitUntil = elapsed.Elapsed.TotalSeconds + Math.Max(0, wait);
            // WaitOne 接受整毫秒；向上取整避免最后不足 1 ms 的余量触发密集补采样。
            while (wait > 0)
            {
                if (token.WaitHandle.WaitOne((int)Math.Min(int.MaxValue, Math.Ceiling(wait * 1000))))
                    return Finish(cancelled: true);
                wait = waitUntil - elapsed.Elapsed.TotalSeconds;
            }
        }
        elapsed.Stop();
        return Finish();
    }

    private static (double TickSeconds, double Seconds, string? MapMode, string? Chapter) ReadInput(
        TaskRequest request, List<string> problems)
    {
        double tick = Number(request, "tick_seconds", 0.5, problems);
        double seconds = Number(request, "seconds", 20, problems);
        string? map = null, chapter = null;
        if (request.Input is not null)
            foreach (string field in request.Input.Select(pair => pair.Key))
                if (field is not ("seconds" or "tick_seconds" or "map" or "chapter"))
                    problems.Add($"未知观测输入字段: input.{field}");
        if (request.Input?["map"] is JsonNode mapNode)
        {
            if (mapNode is JsonValue value && value.TryGetValue<string>(out var text)) map = text;
            else problems.Add("map 必须是字符串或 null");
        }
        if (!double.IsFinite(tick) || tick < 0)
            problems.Add("tick_seconds 必须是大于等于 0 的有限数值");
        if (!double.IsFinite(seconds) || seconds <= 0)
            problems.Add("seconds 必须是大于 0 的有限数值");
        if (map is not null && map is not ("main" or "os"))
            problems.Add("map 只支持上游通用模式 main 或 os");
        if (request.Input?["chapter"] is JsonNode chapterNode)
        {
            if (chapterNode is JsonValue value && value.TryGetValue<string>(out var text))
            {
                var parts = text.Split('.');
                if (parts.Length == 3 && parts[0] == "campaign"
                    && parts.All(p => p.Length > 0 && (char.IsAsciiLetter(p[0]) || p[0] == '_')
                                      && p.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')))
                    chapter = text;
                else problems.Add("chapter 必须是完整的 campaign 模块名");
            }
            else problems.Add("chapter 必须是字符串或 null");
            if (map is null) problems.Add("chapter 需要同时指定 map 模式");
        }
        return (tick, seconds, map, chapter);
    }

    private static double Number(TaskRequest request, string key, double defaultValue,
                                 List<string> problems)
    {
        if (request.Input?.ContainsKey(key) != true) return defaultValue;
        var node = request.Input[key];
        if (node?.GetValueKind() == JsonValueKind.Number)
        {
            try { return node.Deserialize<double>(); }
            catch (JsonException) { }
        }
        problems.Add($"{key} 必须是数值（不能是字符串、布尔、容器或 null）");
        return defaultValue;
    }

    private static JsonObject CaptureEvidence(DeviceCaptureResult capture, double elapsedMs)
        => new()
        {
            ["ok"] = capture.Error is null,
            ["capture_ms"] = Round(capture.CaptureMs),
            ["elapsed_ms"] = Round(elapsedMs),
            ["method"] = capture.Method,
            ["raw"] = capture.Raw,
            ["shape"] = JsonSerializer.SerializeToNode(capture.Shape),
            ["error"] = capture.Error,
        };

    private static JsonObject Stats(List<double> values)
    {
        var stats = new JsonObject { ["count"] = values.Count };
        if (values.Count == 0) return stats;
        var sorted = values.OrderBy(v => v).ToList();
        stats["median"] = Round(sorted[sorted.Count / 2]);
        stats["p25"] = Round(sorted[sorted.Count / 4]);
        stats["p75"] = Round(sorted[3 * sorted.Count / 4]);
        stats["max"] = Round(sorted[^1]);
        return stats;
    }

    private static JsonArray Strings(IEnumerable<string> values)
        => new(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());

    private static double Round(double value) => Math.Round(value, 3);
}
