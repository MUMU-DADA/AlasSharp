using System.Text.Json.Nodes;
using Alas.Runtime;

namespace Alas.Tasks;

/// <summary>一条队列跑完的结果。任务列表就是证据链：谁跑了、谁被跳过、为什么。</summary>
public sealed class QueueResult
{
    public List<TaskResult> Tasks { get; } = new();
    /// <summary>`succeeded` / `dry_run` / `partial` / `failed` / `cancelled`。</summary>
    public string Outcome { get; set; } = "failed";
    public bool StoppedEarly { get; set; }
    public string? StopReason { get; set; }
    public double ElapsedSeconds { get; set; }
    public string? IndexPath { get; set; }
    public string? StatePath { get; set; }
    public bool DryRun { get; set; }

    public bool Succeeded => Outcome is "succeeded" or "dry_run";
    public int FailedCount => Tasks.Count(t => t.Failed);
    public int SkippedCount => Tasks.Count(t => t.Outcome == TaskOutcome.Skipped);
}

/// <summary>
/// 任务队列（R2 通用调度）：按顺序跑多个**业务域任务**，复用同一个常驻会话。
///
/// 它负责四件通用的事，业务域一概不重复实现：
///   1. **前置条件**：不满足就记 `skipped`（`required` 的任务则算失败并停下）——
///      "没跑"与"跑失败"必须分开，否则报告会撒谎；
///   2. **跨任务复位**：每个任务开始前记一条边界日志，任务自己负责把游戏状态复位到
///      可开始状态（例如战役域由上游 `prepare_campaign_navigation` 处理上一局残留）；
///   3. **失败即停 + 取消**：与批次同样的边界语义（不打断正在执行的上游调用）；
///   4. **证据与断点**：每个任务一份工件，队列一份 index.json，并逐任务写 `state.json`
///      供 `--resume` 跳过已成功的任务（长队列被中断后不必从头再来）。
/// </summary>
public sealed class TaskQueue
{
    private readonly AlasSession _session;
    private readonly Dictionary<string, ITaskRunner> _runners = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _artifactNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>一个任务失败后是否停掉整条队列。默认停（与批次同一理由）。</summary>
    public bool StopOnFailure { get; set; } = true;

    /// <summary>断点续跑：这些任务 id 视为已完成，直接跳过（并如实记 skipped+原因）。</summary>
    public HashSet<string> ResumeCompleted { get; } = new(StringComparer.Ordinal);

    public TaskQueue(AlasSession session) => _session = session;

    public TaskQueue Register(ITaskRunner runner)
    {
        _runners[runner.Kind] = runner;
        return this;
    }

    public IReadOnlyCollection<string> Kinds => _runners.Keys;

    public QueueResult Run(IReadOnlyList<TaskRequest> requests, CancellationToken token = default)
    {
        if (requests.Count == 0) throw new ArgumentException("队列至少要有一个任务");
        var queue = new QueueResult { DryRun = _session.Options.DryRun };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        _session.Log.Info("queue", "开始任务队列", new Dictionary<string, object?>
        {
            ["tasks"] = requests.Count,
            ["kinds"] = string.Join(",", requests.Select(r => r.Kind).Distinct()),
            ["dry_run"] = _session.Options.DryRun,
            ["artifacts"] = _session.RunDirectory,
        });

        foreach (var request in requests)
        {
            var result = new TaskResult { Id = request.Id, Kind = request.Kind };
            queue.Tasks.Add(result);

            if (token.IsCancellationRequested)
            {
                result.Outcome = TaskOutcome.Skipped;
                result.ErrorKind = RuntimeErrorKind.Cancelled;
                result.Error = "调用方取消：未开始这个任务";
                result.ArtifactPath = WriteTaskArtifact(request, result);
                LogTask(result);
                SkipRemaining(queue, requests, "调用方取消：未开始这个任务",
                              RuntimeErrorKind.Cancelled);
                queue.Outcome = "cancelled";
                queue.StoppedEarly = true;
                queue.StopReason = "cancelled";
                break;
            }

            // ---- 队列级：请求本身是否可用
            string? invalid = Validate(request);
            if (invalid is not null)
            {
                Fail(result, RuntimeErrorKind.Internal, invalid);
            }
            else if (ResumeCompleted.Contains(request.Id))
            {
                result.Outcome = TaskOutcome.Skipped;
                result.Error = "断点续跑：本次运行之前已完成";
                result.StopReason = "resumed";
                _session.Log.Info("queue", "按断点跳过已完成任务",
                                  new Dictionary<string, object?> { ["task"] = request.Id });
            }
            else
            {
                // ---- 域级：前置条件（"没跑"与"跑失败"分开记）
                var runner = _runners[request.Kind];
                IReadOnlyList<string>? unmet = null;
                try { unmet = runner.Preconditions(request, new TaskContext(_session)); }
                catch (Exception error)
                {
                    var wrapped = RuntimeErrors.Wrap(error, $"任务 {request.Id} 前置校验异常");
                    Fail(result, wrapped.Kind, wrapped.Message + "\n" +
                         string.Join("\n", error.ToString().Split('\n').TakeLast(12)));
                    result.StopReason = "precondition_error";
                }
                if (unmet is { Count: > 0 })
                {
                    result.UnmetPreconditions.AddRange(unmet);
                    result.Outcome = request.Required ? TaskOutcome.Failed : TaskOutcome.Skipped;
                    result.ErrorKind = RuntimeErrorKind.None;
                    result.Error = "前置条件不满足: " + string.Join("; ", unmet);
                    result.StopReason = "precondition";
                }
                else if (unmet is not null)
                {
                    RunTask(runner, request, result, token);
                }
            }

            result.ArtifactPath = WriteTaskArtifact(request, result);
            WriteState(queue, requests);
            LogTask(result);

            if (result.ErrorKind == RuntimeErrorKind.Cancelled)
            {
                queue.StoppedEarly = true;
                queue.StopReason = "cancelled";
                queue.Outcome = "cancelled";
                SkipRemaining(queue, requests, "调用方取消：未开始这个任务",
                              RuntimeErrorKind.Cancelled);
                break;
            }

            if (result.Failed && StopOnFailure)
            {
                queue.StoppedEarly = true;
                queue.StopReason = $"任务失败: {request.Id}（{result.Error ?? result.OutcomeName}）";
                SkipRemaining(queue, requests, $"前序任务 {request.Id} 失败，按失败即停跳过",
                              result.ErrorKind);
                break;
            }
        }

        watch.Stop();
        queue.ElapsedSeconds = Math.Round(watch.Elapsed.TotalSeconds, 1);
        Aggregate(queue);
        queue.StatePath = StatePath();
        queue.IndexPath = WriteIndex(queue, requests);
        _session.Log.Info("queue", "任务队列结束", new Dictionary<string, object?>
        {
            ["outcome"] = queue.Outcome,
            ["tasks"] = queue.Tasks.Count,
            ["failed"] = queue.FailedCount,
            ["skipped"] = queue.SkippedCount,
            ["elapsed_s"] = queue.ElapsedSeconds,
        });
        return queue;
    }

    private void RunTask(ITaskRunner runner, TaskRequest request, TaskResult result,
                         CancellationToken token)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var context = new TaskContext(_session);
        // 跨任务复位边界：先**看一眼现在是什么画面**（只读），再让任务自己去把状态带到
        // 可开始的样子。快照是证据：没有它，"复位了没有"只能靠嘴说。
        result.BoundaryState = SnapshotBoundary(context);
        _session.Log.Info("queue", "任务开始（跨任务复位边界）",
                          new Dictionary<string, object?>
                          {
                              ["task"] = request.Id,
                              ["kind"] = request.Kind,
                              ["pages"] = result.BoundaryState?["pages"]?.ToJsonString(),
                              ["in_map"] = result.BoundaryState?["in_map"]?.ToJsonString(),
                          });
        try
        {
            var outcome = runner.Run(request, context, token);
            result.Outcome = outcome.Outcome;
            result.ErrorKind = outcome.ErrorKind;
            result.Error = outcome.Error;
            result.StopReason = outcome.StopReason;
            result.Evidence = outcome.Evidence;
        }
        catch (OperationCanceledException)
        {
            result.Outcome = TaskOutcome.Skipped;
            result.ErrorKind = RuntimeErrorKind.Cancelled;
            result.Error = "调用方取消";
        }
        catch (Exception error)
        {
            var wrapped = RuntimeErrors.Wrap(error, $"任务 {request.Id} 抛出异常");
            result.Outcome = TaskOutcome.Failed;
            result.ErrorKind = wrapped.Kind;
            result.Error = wrapped.Message;
        }
        watch.Stop();
        result.ElapsedSeconds = Math.Round(watch.Elapsed.TotalSeconds, 1);
    }

    /// <summary>
    /// 任务边界的只读状态快照。**拿不到画面不算失败**：dry-run 下宿主本来就可能还没有帧，
    /// 这时如实记 `available=false`；宿主报错也照记，不让它把任务本身带崩。
    /// </summary>
    private static JsonObject SnapshotBoundary(TaskContext context)
    {
        var snapshot = new JsonObject { ["available"] = false };
        try
        {
            var state = context.Session.Vision.AccountState();
            snapshot["available"] = state.Frame?.Available == true;
            snapshot["server"] = state.Server;
            snapshot["pages"] = state.Pages is null
                ? null
                : new JsonArray(state.Pages.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray());
            snapshot["in_map"] = state.InMap;
            if (state.Error is not null) snapshot["note"] = state.Error;
        }
        catch (Exception error)
        {
            snapshot["note"] = $"边界快照取不到: {error.GetType().Name}: {error.Message}";
        }
        return snapshot;
    }

    private string? Validate(TaskRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Id)) return "任务缺少 id";
        if (string.IsNullOrWhiteSpace(request.Kind)) return $"任务 {request.Id} 缺少 kind";
        if (!_runners.ContainsKey(request.Kind))
            return $"任务 {request.Id} 的 kind={request.Kind} 没有注册运行器"
                   + $"（已注册: {string.Join(",", _runners.Keys.OrderBy(k => k, StringComparer.Ordinal))}）";
        return null;
    }

    private static void Fail(TaskResult result, RuntimeErrorKind kind, string message)
    {
        result.Outcome = TaskOutcome.Failed;
        result.ErrorKind = kind;
        result.Error = message;
        result.StopReason = "invalid_request";
    }

    private void SkipRemaining(QueueResult queue, IReadOnlyList<TaskRequest> requests,
                               string reason, RuntimeErrorKind kind)
    {
        foreach (var rest in requests.Skip(queue.Tasks.Count))
        {
            var skipped = new TaskResult
            {
                Id = rest.Id,
                Kind = rest.Kind,
                Outcome = TaskOutcome.Skipped,
                ErrorKind = kind,
                Error = reason,
            };
            skipped.ArtifactPath = WriteTaskArtifact(rest, skipped);
            queue.Tasks.Add(skipped);
            LogTask(skipped);
        }
        WriteState(queue, requests);
    }

    private static void Aggregate(QueueResult queue)
    {
        if (queue.Outcome == "cancelled") return;      // 取消已经定过性，别被下面的规则覆盖
        if (queue.Tasks.Any(t => t.Failed))
        {
            queue.Outcome = "failed";
            return;
        }
        if (queue.Tasks.Count == 0)
        {
            queue.Outcome = "failed";
            return;
        }
        if (queue.Tasks.All(t => t.Outcome is TaskOutcome.Succeeded or TaskOutcome.DryRun))
        {
            // dry-run 与真跑混在一队里是正常的（只读任务在 dry-run 下也真的完成了）：
            // 只要每个任务都"按预期做完了"就算整队成功，有 dry-run 分量时标 dry_run。
            queue.Outcome = queue.Tasks.Any(t => t.Outcome == TaskOutcome.DryRun)
                ? "dry_run" : "succeeded";
            return;
        }
        if (queue.Tasks.All(t => t.Outcome is TaskOutcome.DryRun or TaskOutcome.Skipped))
        {
            queue.Outcome = queue.Tasks.Any(t => t.Outcome == TaskOutcome.DryRun) ? "dry_run" : "partial";
            return;
        }
        queue.Outcome = "partial";
    }

    private void LogTask(TaskResult result)
        => _session.Log.Add(result.Failed ? "ERROR" : "INFO", "task",
                            $"{result.Kind}:{result.Id} → {result.OutcomeName}",
                            new Dictionary<string, object?>
                            {
                                ["outcome"] = result.OutcomeName,
                                ["error_kind"] = RuntimeErrors.Name(result.ErrorKind),
                                ["error"] = result.Error,
                                ["elapsed_s"] = result.ElapsedSeconds,
                                ["preconditions"] = result.UnmetPreconditions.Count,
                            });

    private string? WriteTaskArtifact(TaskRequest request, TaskResult result)
    {
        if (_session.RunDirectory is null) return null;
        string stem = $"task-{Sanitize(request.Id)}";
        string name;
        for (int suffix = 0; ; suffix++)
        {
            name = suffix == 0 ? $"{stem}.json" : $"{stem}-{suffix + 1}.json";
            if (_artifactNames.Add(name)) break;
        }
        return _session.WriteArtifact(name, new JsonObject
        {
            ["id"] = request.Id,
            ["kind"] = request.Kind,
            ["required"] = request.Required,
            ["input"] = request.Input?.DeepClone(),
            ["outcome"] = result.OutcomeName,
            ["error_kind"] = RuntimeErrors.Name(result.ErrorKind),
            ["error"] = result.Error,
            ["stop_reason"] = result.StopReason,
            ["elapsed_s"] = result.ElapsedSeconds,
            ["boundary_state"] = result.BoundaryState?.DeepClone(),
            ["unmet_preconditions"] = new JsonArray(
                result.UnmetPreconditions.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray()),
            ["evidence"] = result.Evidence?.DeepClone(),
        });
    }

    private string? WriteIndex(QueueResult queue, IReadOnlyList<TaskRequest> requests)
    {
        if (_session.RunDirectory is null) return null;
        var tasks = new JsonArray();
        for (int index = 0; index < queue.Tasks.Count; index++)
        {
            var task = queue.Tasks[index];
            var request = requests[index];
            tasks.Add(new JsonObject
            {
                ["id"] = task.Id,
                ["kind"] = task.Kind,
                ["required"] = request.Required,
                ["input"] = request.Input?.DeepClone(),
                ["outcome"] = task.OutcomeName,
                ["error_kind"] = RuntimeErrors.Name(task.ErrorKind),
                ["error"] = task.Error,
                ["elapsed_s"] = task.ElapsedSeconds,
                ["artifact"] = task.ArtifactPath,
            });
        }
        return _session.WriteArtifact("queue.json", new JsonObject
        {
            ["outcome"] = queue.Outcome,
            ["dry_run"] = queue.DryRun,
            ["stopped_early"] = queue.StoppedEarly,
            ["stop_reason"] = queue.StopReason,
            ["elapsed_s"] = queue.ElapsedSeconds,
            ["host_start_count"] = _session.HostStartCount,
            ["device_configure_count"] = _session.DeviceConfigureCount,
            ["tasks"] = tasks,
        });
    }

    private string? StatePath()
        => _session.RunDirectory is null ? null : Path.Combine(_session.RunDirectory, "state.json");

    /// <summary>逐任务刷新断点文件：中断后 `--resume` 能跳过已成功的任务。</summary>
    private void WriteState(QueueResult queue, IReadOnlyList<TaskRequest> requests)
    {
        string? path = StatePath();
        if (path is null) return;
        var done = new JsonObject();
        // **先继承上一份断点**：`completed` 是**累积**语义（"到目前为止做完的"），
        // 若只写本次运行的切片，一次"全都跳过"的运行会把历史清空 —— 下一次 `--resume`
        // 就会重新执行那些任务。对战役域就是**再打一遍、再花一次石油**，而且不报错，
        // 只在日志里表现为"这次怎么又多打了几个图"（见 `docs/runtime.md` 的工件与断点）。
        for (int index = 0; index < requests.Count; index++)
        {
            var request = requests[index];
            var task = queue.Tasks.FirstOrDefault(t => t.Id == request.Id);
            string? outcome = task?.Outcome is TaskOutcome.Succeeded or TaskOutcome.DryRun
                ? task.OutcomeName
                : ResumeCompleted.Contains(request.Id) ? "carried_over" : null;
            if (outcome is null) continue;
            done[request.Id] = new JsonObject
            {
                ["identity"] = TaskQueueFile.ResumeIdentity(requests, index, _session.Options),
                ["outcome"] = outcome,
            };
        }
        File.WriteAllText(path, new JsonObject
        {
            ["completed"] = done,
            ["updated"] = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
        }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Sanitize(string text)
        => new(text.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
}
