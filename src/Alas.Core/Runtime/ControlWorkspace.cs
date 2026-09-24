using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Tasks;

namespace Alas.Runtime;

/// <summary>控制工作区的单队列生命周期；不依赖 HTTP 或任何 UI 框架。</summary>
public sealed class ControlWorkspace
{
    private readonly string _root;
    private readonly string _repo;
    private readonly string _data;
    private readonly string _tools;
    private readonly string _artifacts;
    private readonly string _control;
    private readonly object _gate = new();
    private Task? _worker;
    private SessionLog? _log;
    private CancellationTokenSource? _stopSignal;
    private string? _mode;
    private string? _startedAt;
    private string? _finishedAt;
    private string? _error;
    private string? _stopPath;
    private string? _runDirectory;
    private bool _stopRequested;
    private bool _shuttingDown;

    public ControlWorkspace(string root, string repo, string data, string tools,
                         string? artifacts, string? workspace)
    {
        _root = Path.GetFullPath(root);
        _repo = Path.GetFullPath(repo);
        _data = Path.GetFullPath(data);
        _tools = Path.GetFullPath(tools);
        _control = Path.GetFullPath(workspace ?? Path.Combine(_root, ".runtime", "control"));
        _artifacts = Path.GetFullPath(artifacts ?? Path.Combine(_control, "runs"));
        Directory.CreateDirectory(_control);
        Directory.CreateDirectory(_artifacts);
    }

    public JsonObject State()
    {
        string? runDirectory;
        string? mode;
        string? started;
        string? finished;
        string? error;
        bool running;
        bool stopRequested;
        SessionLog? log;
        lock (_gate)
        {
            runDirectory = _runDirectory;
            running = _worker is { IsCompleted: false };
            mode = _mode;
            started = _startedAt;
            finished = _finishedAt;
            error = _error;
            stopRequested = _stopRequested;
            log = _log;
        }
        var active = new JsonObject
        {
            ["status"] = running ? "running" : started is null ? "idle" : error is null ? "completed" : "failed",
            ["mode"] = mode,
            ["started_at"] = started,
            ["finished_at"] = finished,
            ["stop_requested"] = stopRequested,
            ["error"] = error,
            ["run_directory"] = runDirectory,
        };
        JsonObject? report = runDirectory is not null && RunReport.IsRunDirectory(runDirectory)
            ? RunReport.Build(runDirectory).ToJson() : null;
        return new JsonObject
        {
            ["queue"] = LoadQueue(),
            ["active"] = active,
            ["report"] = report,
            ["live_tasks"] = LiveTasks(runDirectory),
            ["recent_logs"] = log is null ? RecentLogs(runDirectory)
                : new JsonArray(log.Recent(80).Select(entry => (JsonNode)entry.ToJson()).ToArray()),
            ["runs"] = RunReport.Summarize(_artifacts, 20),
        };
    }

    public JsonObject? Report(string? stamp)
    {
        if (string.IsNullOrWhiteSpace(stamp) ||
            stamp.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ArgumentException("运行标识无效");
        string directory = Path.Combine(_artifacts, stamp);
        return RunReport.IsRunDirectory(directory) ? RunReport.Build(directory).ToJson() : null;
    }

    private JsonObject LoadQueue()
    {
        lock (_gate)
        {
            string path = Path.Combine(_control, "queue.json");
            if (!File.Exists(path))
                return new JsonObject
                {
                    ["tasks"] = new JsonArray(new JsonObject
                    {
                        ["id"] = "catalog",
                        ["kind"] = "task_catalog",
                        ["input"] = new JsonObject(),
                    }),
                };
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new ArgumentException("保存的队列不是 JSON 对象");
        }
    }

    private static JsonObject RequireQueue(JsonObject body)
    {
        if (body["queue"] is not JsonObject queue)
            throw new ArgumentException("缺少 queue 对象");
        TaskQueueFile.Parse(queue.ToJsonString());
        return queue;
    }

    public void SaveQueueRequest(JsonObject body) => SaveQueue(RequireQueue(body));

    private void SaveQueue(JsonObject queue)
    {
        lock (_gate)
        {
            if (_shuttingDown) throw new ControlWorkspaceUnavailableException("控制服务正在关闭");
            string path = Path.Combine(_control, "queue.json");
            string temporary = Path.Combine(_control, $"queue-{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporary, queue.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    public void StartRun(JsonObject body)
    {
        var queue = RequireQueue(body);
        string mode = body["mode"]?.GetValue<string>() ?? "dry_run";
        if (mode is not ("dry_run" or "read_only" or "actions"))
            throw new ArgumentException("mode 必须是 dry_run、read_only 或 actions");
        if (mode == "actions" && body["confirm_actions"]?.GetValue<bool>() != true)
            throw new ArgumentException("动作运行需要明确授权");
        double maxSeconds = body["max_seconds"]?.GetValue<double>() ?? 1500;
        int maxRounds = body["max_rounds"]?.GetValue<int>() ?? 20;
        if (!double.IsFinite(maxSeconds) || maxSeconds <= 0 || maxRounds <= 0)
            throw new ArgumentException("运行上限必须是正数");
        var options = new SessionOptions
        {
            RepoDirectory = _repo,
            ToolsDirectory = _tools,
            DataDirectory = _data,
            ArtifactsDirectory = _artifacts,
            Serial = body["serial"]?.GetValue<string>()?.Trim() is { Length: > 0 } serial ? serial : null,
            DryRun = mode == "dry_run",
            ReadOnlyDevice = mode == "read_only",
            AllowActions = mode == "actions",
            MaxSeconds = maxSeconds,
            MaxRounds = maxRounds,
        };
        options.Validate();
        bool resume = body["resume"]?.GetValue<bool>() ?? false;
        bool stopOnFailure = !(body["continue_on_error"]?.GetValue<bool>() ?? false);
        lock (_gate)
        {
            if (_shuttingDown) throw new ControlWorkspaceUnavailableException("控制服务正在关闭");
            if (_worker is { IsCompleted: false })
                throw new ControlWorkspaceUnavailableException("已有队列正在运行");
            SaveQueue(queue);
            string id = Guid.NewGuid().ToString("N");
            string requestPath = Path.Combine(_control, $"request-{id}.json");
            File.WriteAllText(requestPath, queue.ToJsonString());
            _stopPath = Path.Combine(_control, $"stop-{id}.request");
            _runDirectory = null;
            _mode = mode;
            _startedAt = DateTimeOffset.Now.ToString("O");
            _finishedAt = null;
            _error = null;
            _stopRequested = false;
            var log = new SessionLog();
            _log = log;
            var stopSignal = new CancellationTokenSource();
            _stopSignal = stopSignal;
            _worker = Task.Run(() =>
            {
                try
                {
                    QueueExecution.RunFile(requestPath, options, stopOnFailure: stopOnFailure,
                        resume: resume, stopFile: _stopPath, token: stopSignal.Token, onSessionStarted: directory =>
                        {
                            lock (_gate) _runDirectory = directory;
                        }, log: log);
                }
                catch (Exception error)
                {
                    lock (_gate) _error = error.Message;
                }
                finally
                {
                    lock (_gate)
                    {
                        _finishedAt = DateTimeOffset.Now.ToString("O");
                        _stopSignal = null;
                        stopSignal.Dispose();
                    }
                }
            });
        }
    }

    public bool RequestStop()
    {
        lock (_gate)
        {
            if (_worker is not { IsCompleted: false } || _stopPath is null || _stopSignal is null) return false;
            // Cancellation is consumed at the same runtime boundaries as a stop file.
            // Failure to persist the optional marker must never bypass shutdown's wait.
            _stopSignal.Cancel();
            _stopRequested = true;
            try { File.WriteAllText(_stopPath, "stop"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine("停止标记无法写入，已通过内存信号请求边界停止；仍将等待工件落盘。");
            }
            return true;
        }
    }

    private static JsonArray LiveTasks(string? directory)
    {
        var tasks = new JsonArray();
        if (directory is null || !Directory.Exists(directory)) return tasks;
        foreach (string path in Directory.GetFiles(directory, "task-*.json")
                     .OrderBy(File.GetLastWriteTimeUtc))
        {
            try
            {
                if (JsonNode.Parse(ArtifactReader.ReadAllText(path)) is JsonObject task)
                    tasks.Add(task);
            }
            catch (IOException) { }
            catch (JsonException) { }
        }
        return tasks;
    }

    private static JsonArray RecentLogs(string? directory)
    {
        var logs = new JsonArray();
        if (directory is null) return logs;
        string path = Path.Combine(directory, "session-log.jsonl");
        if (!File.Exists(path)) return logs;
        try
        {
            foreach (string line in ArtifactReader.ReadLines(path).TakeLast(80))
                if (JsonNode.Parse(line) is JsonObject entry) logs.Add(entry);
        }
        catch (IOException) { }
        catch (JsonException) { }
        return logs;
    }

    /// <summary>原子关闭接单并请求任务边界停止；调用方必须等待返回的任务完成再退出。</summary>
    public Task BeginShutdown()
    {
        lock (_gate)
        {
            _shuttingDown = true;
            RequestStop();
            return _worker ?? Task.CompletedTask;
        }
    }
}

public sealed class ControlWorkspaceUnavailableException(string message) : Exception(message);
