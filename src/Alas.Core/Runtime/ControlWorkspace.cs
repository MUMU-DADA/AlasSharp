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
    private readonly AlasSession.EngineFactory? _engineFactory;
    private readonly object _gate = new();
    private Task? _worker;
    private SessionLog? _log;
    private CancellationTokenSource? _stopSignal;
    private string? _mode;
    private string? _kind;
    private string? _instance;
    private string? _startedAt;
    private string? _finishedAt;
    private string? _error;
    private string? _stopPath;
    private string? _runDirectory;
    private bool _stopRequested;
    private bool _shuttingDown;
    private AlasSession? _session;

    public ControlWorkspace(string root, string repo, string data, string tools,
                         string? artifacts, string? workspace, AlasSession.EngineFactory? engineFactory = null)
    {
        _root = Path.GetFullPath(root);
        _engineFactory = engineFactory;
        _repo = Path.GetFullPath(repo);
        _data = Path.GetFullPath(data);
        _tools = Path.GetFullPath(tools);
        _control = Path.GetFullPath(workspace ?? Path.Combine(_root, ".runtime", "control"));
        _artifacts = Path.GetFullPath(artifacts ?? Path.Combine(_control, "runs"));
        Directory.CreateDirectory(_control);
        Directory.CreateDirectory(_artifacts);
    }

    public JsonObject State(string? selectedInstance = null)
    {
        string? runDirectory;
        string? mode;
        string? kind;
        string? instance;
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
            kind = _kind;
            instance = _instance;
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
            ["kind"] = kind,
            ["instance"] = instance,
            ["scheduler"] = kind == "scheduler_run" ? ReadSchedulerState(runDirectory) : null,
            ["started_at"] = started,
            ["finished_at"] = finished,
            ["stop_requested"] = stopRequested,
            ["error"] = error,
            ["run_directory"] = runDirectory,
        };
        JsonObject? report = runDirectory is not null && RunReport.IsRunDirectory(runDirectory)
            ? RunReport.Build(runDirectory).ToJson() : null;
        var state = new JsonObject
        {
            ["queue"] = LoadQueue(),
            ["active"] = active,
            ["report"] = report,
            ["live_tasks"] = LiveTasks(runDirectory),
            ["recent_logs"] = log is null ? RecentLogs(runDirectory)
                : new JsonArray(log.Recent(80).Select(entry => (JsonNode)entry.ToJson()).ToArray()),
            ["runs"] = RunReport.Summarize(_artifacts, 20),
        };
        if (selectedInstance is not null)
        {
            // This read works before the first run and while another instance owns the host.
            var config = new ConfigWorkspace(_repo).Get(selectedInstance);
            var overview = InstanceOverview.FromConfig(config, DateTime.Now);
            overview["status"] = InstanceOverview.Status(config.Instance, active, report);
            state["overview"] = overview;
        }
        return state;
    }

    public IReadOnlyList<ConfigInstance> Instances(ConfigWorkspace configs)
    {
        var state = State();
        var active = state["active"]!.AsObject();
        return configs.List().Select(item => item with
        {
            Status = InstanceOverview.Status(item.Instance, active, state["report"] as JsonObject),
            CurrentTask = active["instance"]?.GetValue<string>() == item.Instance &&
                          active["status"]?.GetValue<string>() == "running" &&
                          active["scheduler"]?["phase"]?.GetValue<string>() == "running"
                ? active["scheduler"]?["task"]?.GetValue<string>() : null,
        }).ToArray();
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
            ScreenshotBackend = body["screenshot_backend"]?.GetValue<string>() ?? "scrcpy",
            ControlBackend = body["control_backend"]?.GetValue<string>() ?? "MaaTouch",
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
            var tasks = queue["tasks"]!.AsArray();
            _kind = tasks.Count == 1 ? tasks[0]?["kind"]?.GetValue<string>() : "queue";
            _instance = tasks.Count == 1 ? tasks[0]?["input"]?["instance"]?.GetValue<string>() : null;
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
                    AlasSession session;
                    lock (_gate)
                    {
                        _session ??= AlasSession.Start(HostSessionOptions(options), _engineFactory, log: log);
                        session = _session;
                    }
                    QueueExecution.RunFile(requestPath, options, stopOnFailure: stopOnFailure,
                        resume: resume, stopFile: _stopPath, token: stopSignal.Token, onSessionStarted: directory =>
                        {
                            lock (_gate) _runDirectory = directory;
                        }, log: log, sharedSession: session);
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

    /// <summary>
    /// Submit a selected instance/task through its native periodic or tool domain.
    /// UI and HTTP callers supply intent only; upstream owns command resolution
    /// and native dispatch, and the normal queue owns evidence and shutdown.
    /// </summary>
    public void StartTask(JsonObject body)
    {
        if (body["confirm_actions"]?.GetValue<bool>() != true)
            throw new ArgumentException("动作运行需要明确授权");
        string instance = body["instance"]?.GetValue<string>() ?? "";
        string task = body["task"]?.GetValue<string>() ?? "";
        var configs = new ConfigWorkspace(_repo);
        var snapshot = configs.Get(instance);
        var schema = configs.Schema();
        if (schema.Args[task] is not JsonObject definition)
            throw new ArgumentException("当前任务不在上游配置目录中");
        string? command = definition["Scheduler"]?["Command"]?["value"]?.GetValue<string>();
        string kind = "periodic_run";
        if (string.IsNullOrWhiteSpace(command))
        {
            var plan = ReadHostJson("tool_plan", new JsonObject { ["task"] = task });
            if (plan["found"]?.GetValue<bool>() != true)
                throw new ArgumentException(plan["reason"]?.GetValue<string>() ?? "当前上游未注册此独立工具");
            command = task;
            kind = "tool_run";
        }
        StartInstanceRun(snapshot, kind, new JsonObject
        {
            ["instance"] = snapshot.Instance, ["task"] = command,
            ["allow_actions"] = true, ["confirm"] = command,
        });
    }

    public void StartScheduler(JsonObject body)
    {
        if (body["confirm_actions"]?.GetValue<bool>() != true)
            throw new ArgumentException("调度器运行需要明确动作授权");
        var snapshot = new ConfigWorkspace(_repo).Get(body["instance"]?.GetValue<string>() ?? "");
        StartInstanceRun(snapshot, "scheduler_run", new JsonObject
        {
            ["instance"] = snapshot.Instance, ["allow_actions"] = true, ["confirm"] = snapshot.Instance,
        });
    }

    private void StartInstanceRun(ConfigSnapshot snapshot, string kind, JsonObject input)
    {
        string? serial = snapshot.Values["Alas"]?["Emulator"]?["Serial"]?.GetValue<string>();
        if (kind != "tool_run" && string.IsNullOrWhiteSpace(serial))
            throw new ArgumentException("实例没有配置设备串号，不能执行任务");
        StartRun(new JsonObject
        {
            ["mode"] = "actions", ["confirm_actions"] = true, ["serial"] = serial,
            ["screenshot_backend"] = snapshot.Values["Alas"]?["Emulator"]?["ScreenshotMethod"]?.DeepClone(),
            ["control_backend"] = snapshot.Values["Alas"]?["Emulator"]?["ControlMethod"]?.DeepClone(),
            ["queue"] = new JsonObject
            {
                ["tasks"] = new JsonArray(new JsonObject
                {
                    ["id"] = "single-task", ["kind"] = kind, ["required"] = true,
                    ["input"] = input,
                }),
            },
        });
    }

    private static JsonObject? ReadSchedulerState(string? runDirectory)
    {
        if (runDirectory is null || !Directory.Exists(runDirectory)) return null;
        try
        {
            string? directory = Directory.EnumerateDirectories(runDirectory, "scheduler-*")
                .OrderByDescending(Directory.GetLastWriteTimeUtc).FirstOrDefault();
            if (directory is null) return null;
            string state = Path.Combine(directory, "state.json");
            var snapshot = File.Exists(state) ? JsonNode.Parse(ArtifactReader.ReadAllText(state)) as JsonObject : null;
            if (snapshot is null) return null;
            snapshot["stream"] = Path.GetFileName(runDirectory) + "/" + Path.GetFileName(directory);
            string logs = Path.Combine(directory, "logs.json");
            try
            {
                if (File.Exists(logs) && JsonNode.Parse(ArtifactReader.ReadAllText(logs)) is JsonObject tail
                    && tail["instance"]?.GetValue<string>() == snapshot["instance"]?.GetValue<string>())
                    snapshot["logs"] = tail;
            }
            catch (IOException) { }
            catch (JsonException) { }
            return snapshot;
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }

    /// <summary>Serialized upstream service call sharing the process-local Python host.</summary>
    public JsonObject ReadHostJson(string operation, JsonObject arguments)
    {
        lock (_gate)
        {
            if (_shuttingDown) throw new ControlWorkspaceUnavailableException("控制服务正在关闭");
            if (_worker is { IsCompleted: false })
                throw new ControlWorkspaceUnavailableException("队列正在运行，暂不接受其他宿主调用");
            _session ??= AlasSession.Start(ApiSessionOptions(), _engineFactory);
            _log = _session.Log;
            return _session.Vision.CallTyped<JsonObject>(operation, arguments);
        }
    }

    private SessionOptions ApiSessionOptions() => new()
    {
        RepoDirectory = _repo,
        ToolsDirectory = _tools,
        DataDirectory = _data,
        DryRun = true,
        MaxSeconds = 1,
        MaxRounds = 1,
    };

    private static SessionOptions HostSessionOptions(SessionOptions options) => new()
    {
        RepoDirectory = options.RepoDirectory,
        ToolsDirectory = options.ToolsDirectory,
        DataDirectory = options.DataDirectory,
        Serial = options.Serial,
        ScreenshotBackend = options.ScreenshotBackend,
        ControlBackend = options.ControlBackend,
        DryRun = options.DryRun,
        ReadOnlyDevice = options.ReadOnlyDevice,
        AllowActions = options.AllowActions,
        MaxSeconds = options.MaxSeconds,
        MaxRounds = options.MaxRounds,
        RepeatUntilCleared = options.RepeatUntilCleared,
        ClearAll = options.ClearAll,
        Fleet1 = options.Fleet1,
        Fleet2 = options.Fleet2,
        SubmarineFleet = options.SubmarineFleet,
        // The queue preparation below owns the per-run artifact directory.
        ArtifactsDirectory = null,
    };

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
            return ShutdownAsync(_worker ?? Task.CompletedTask);
        }
    }

    private async Task ShutdownAsync(Task worker)
    {
        await worker.ConfigureAwait(false);
        lock (_gate)
        {
            _session?.Dispose();
            _session = null;
        }
    }
}

public sealed class ControlWorkspaceUnavailableException(string message) : Exception(message);
