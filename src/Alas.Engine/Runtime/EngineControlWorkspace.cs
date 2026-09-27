using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Rules;
using Alas.Engine.Tasks;

namespace Alas.Engine.Runtime;

/// <summary>In-process control surface backed only by the new Engine task graph.</summary>
public sealed class EngineControlWorkspace
{
    private readonly string _repo;
    private readonly string _control;
    private readonly string _artifacts;
    private readonly ConfigWorkspace _configs;
    private readonly object _gate = new();
    private Task? _worker;
    private string? _mode, _kind, _instance, _startedAt, _finishedAt, _error, _runDirectory;
    private Dictionary<string, TaskActivity> _activities = new(StringComparer.Ordinal);
    private volatile bool _stopRequested;
    private bool _shuttingDown;

    public EngineControlWorkspace(string root, string repo, string data, string tools,
        string? artifacts, string? workspace)
    {
        _ = data;
        _ = tools;
        _repo = Path.GetFullPath(repo);
        _control = Path.GetFullPath(workspace ?? Path.Combine(Path.GetFullPath(root), ".runtime", "control"));
        _artifacts = Path.GetFullPath(artifacts ?? Path.Combine(_control, "runs"));
        Directory.CreateDirectory(_control);
        Directory.CreateDirectory(_artifacts);
        _configs = new ConfigWorkspace(_repo);
    }

    public JsonObject State(string? selectedInstance = null)
    {
        string? runDirectory, mode, kind, instance, started, finished, error;
        bool stopRequested, running;
        lock (_gate)
        {
            runDirectory = _runDirectory;
            running = _worker is { IsCompleted: false };
            mode = _mode; kind = _kind; instance = _instance;
            started = _startedAt; finished = _finishedAt; error = _error;
            stopRequested = _stopRequested;
        }
        JsonObject queue = LoadQueue();
        Dictionary<string, TaskActivity> activities;
        lock (_gate) activities = new(_activities, StringComparer.Ordinal);
        string status = running ? "running" : started is null ? "idle" : error is null ? "completed" : "failed";
        JsonObject? report = runDirectory is not null && RunReport.IsRunDirectory(runDirectory)
            ? RunReport.Build(runDirectory).ToJson() : null;
        var active = new JsonObject
        {
            ["status"] = status,
            ["mode"] = mode, ["kind"] = kind, ["instance"] = instance,
            // The control plane exposes the Engine queue as a typed activity.
            // Keep the legacy scheduler slot empty so clients cannot mistake
            // configuration task names for an upstream scheduler process.
            ["engine"] = EngineActivity(queue, status, kind, activities), ["scheduler"] = null,
            ["started_at"] = started, ["finished_at"] = finished,
            ["stop_requested"] = stopRequested, ["error"] = error, ["run_directory"] = runDirectory,
        };
        var state = new JsonObject
        {
            ["queue"] = queue, ["active"] = active, ["report"] = report,
            ["live_tasks"] = report?["items"]?.DeepClone() ?? new JsonArray(), ["recent_logs"] = new JsonArray(),
            ["runs"] = RunReport.Summarize(_artifacts, 20),
        };
        if (selectedInstance is not null)
        {
            var config = _configs.Get(selectedInstance);
            var overview = InstanceOverview.FromConfig(config, DateTime.Now);
            overview["status"] = InstanceOverview.Status(config.Instance, active, report);
            state["overview"] = overview;
        }
        return state;
    }

    private static JsonObject? EngineActivity(JsonObject queue, string status, string? currentKind,
        IReadOnlyDictionary<string, TaskActivity> activities)
    {
        if (currentKind is null && status == "idle") return null;
        string? runningKind = activities.Values.FirstOrDefault(activity => activity.State == "running")?.Kind
            ?? (status == "running" ? currentKind : null);
        var tasks = new JsonArray();
        if (queue["tasks"] is JsonArray entries)
            foreach (var entry in entries.OfType<JsonObject>())
            {
                string? id = entry["id"]?.GetValue<string>();
                string? kind = entry["kind"]?.GetValue<string>();
                if (id is null || kind is null) continue;
                activities.TryGetValue(id, out var activity);
                tasks.Add(new JsonObject
                {
                    ["id"] = id, ["kind"] = kind,
                    ["state"] = activity?.State ?? (status == "running" ? "pending" :
                        status == "completed" ? "completed" : status == "failed" ? "failed" : "pending"),
                    ["reason"] = activity?.Reason
                });
            }
        return new JsonObject
        {
            ["contract"] = "engine-activity/1",
            ["source"] = "engine-queue",
            ["phase"] = status,
            ["task"] = runningKind ?? currentKind,
            ["tasks"] = tasks
        };
    }

    public IReadOnlyList<ConfigInstance> Instances(ConfigWorkspace configs)
    {
        var state = State();
        var active = state["active"]!.AsObject();
        return configs.List().Select(item => item with
        {
            Status = InstanceOverview.Status(item.Instance, active, state["report"] as JsonObject),
            CurrentTask = active["instance"]?.GetValue<string>() == item.Instance &&
                          active["status"]?.GetValue<string>() == "running"
                ? active["kind"]?.GetValue<string>() : null,
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
                return new JsonObject { ["tasks"] = new JsonArray(new JsonObject
                    { ["id"] = "observe", ["kind"] = "observe", ["input"] = new JsonObject() }) };
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new ArgumentException("保存的队列不是 JSON 对象");
        }
    }

    private static JsonObject RequireQueue(JsonObject body)
    {
        if (body["queue"] is not JsonObject queue || queue["tasks"] is not JsonArray tasks)
            throw new ArgumentException("缺少 queue.tasks 数组");
        var requests = JsonSerializer.Deserialize<TaskRequest[]>(tasks.ToJsonString(), TaskQueue.Json)
            ?? throw new ArgumentException("任务队列格式无效");
        TaskQueue.ValidateRequests(requests);
        return queue;
    }

    public void SaveQueueRequest(JsonObject body) => SaveQueue(RequireQueue(body));

    private void SaveQueue(JsonObject queue)
    {
        lock (_gate)
        {
            EnsureAccepting();
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
        JsonObject queue = RequireQueue(body);
        string mode = body["mode"]?.GetValue<string>() ?? "dry_run";
        if (mode is not ("dry_run" or "read_only" or "actions"))
            throw new ArgumentException("mode 必须是 dry_run、read_only 或 actions");
        if (mode == "actions" && body["confirm_actions"]?.GetValue<bool>() != true)
            throw new ArgumentException("动作运行需要明确授权");
        double maxSeconds = body["max_seconds"]?.GetValue<double>() ?? 1500;
        int maxRounds = body["max_rounds"]?.GetValue<int>() ?? 20;
        if (!double.IsFinite(maxSeconds) || maxSeconds <= 0 || maxRounds <= 0)
            throw new ArgumentException("运行上限必须是正数");
        if (maxRounds != 20)
            throw new EngineCapabilityUnavailableException("新引擎目前不接受 max_rounds；请由对应 C# 任务规则定义轮次上限");
        var tasks = JsonSerializer.Deserialize<TaskRequest[]>(queue["tasks"]!.ToJsonString(), TaskQueue.Json)!;
        var rawTasks = queue["tasks"]!.AsArray();
        for (int i = 0; i < tasks.Length; i++)
            if (rawTasks[i] is JsonObject item && !item.Any(p => p.Key.Equals("timeoutSeconds", StringComparison.OrdinalIgnoreCase)))
                tasks[i] = tasks[i] with { TimeoutSeconds = maxSeconds };
        TaskQueue.ValidateRequests(tasks);
        string? instance = body["instance"]?.GetValue<string>() ?? tasks.Select(task => task.Instance)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (tasks.Select(task => task.Instance).Append(instance).Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal).Skip(1).Any())
            throw new EngineCapabilityUnavailableException("一个队列只能连接一个实例；请拆分为多个 Engine 队列");
        string? serial = body["serial"]?.GetValue<string>();
        ConfigSnapshot? config = ResolveConfig(instance, serial);
        if (config is not null && serial is not null &&
            serial != config.Values["Alas"]?["Emulator"]?["Serial"]?.GetValue<string>())
            throw new ArgumentException("serial 与所选实例不一致");
        var session = BuildSession(config, serial, mode == "actions");
        bool dryRun = mode == "dry_run";
        bool continueOnFailure = body["continue_on_error"]?.GetValue<bool>() ?? false;
        bool resume = body["resume"]?.GetValue<bool>() ?? false;
        string? resumeDirectory = resume ? body["resume_directory"]?.GetValue<string>() : null;
        if (resume && string.IsNullOrWhiteSpace(resumeDirectory))
            throw new ArgumentException("resume_directory 是必需的");
        if (resume && dryRun) throw new ArgumentException("dry_run 不支持断点续跑");
        lock (_gate)
        {
            EnsureAccepting();
            if (_worker is { IsCompleted: false }) throw new EngineControlWorkspaceUnavailableException("已有任务队列正在运行");
            SaveQueue(queue);
            _mode = mode; _instance = config?.Instance ?? instance;
            _kind = tasks.Length == 1 ? tasks[0].Kind : "queue";
            _activities = tasks.ToDictionary(task => task.Id,
                task => new TaskActivity(task.Id, task.Kind, "pending"), StringComparer.Ordinal);
            _startedAt = DateTimeOffset.Now.ToString("O"); _finishedAt = null;
            _error = null; _runDirectory = null; _stopRequested = false;
            _worker = Task.Run(async () =>
            {
                try
                {
                    var result = await new TaskQueue().RunAsync(tasks, session,
                        new TaskQueueOptions(_artifacts, dryRun, continueOnFailure, resumeDirectory,
                            () => _stopRequested, directory => { lock (_gate) _runDirectory = directory; },
                            activity => { lock (_gate) _activities[activity.Id] = activity; }));
                    if (result.Failed) lock (_gate) _error = "一个或多个引擎任务未成功";
                }
                catch (Exception error) { lock (_gate) _error = error.Message; }
                finally { lock (_gate) _finishedAt = DateTimeOffset.Now.ToString("O"); }
            });
        }
    }

    private ConfigSnapshot? ResolveConfig(string? instance, string? serial)
    {
        if (instance is not null) return _configs.Get(instance);
        if (!string.IsNullOrWhiteSpace(serial))
        {
            var match = _configs.List().FirstOrDefault(item => item.Serial == serial);
            if (match is not null) return _configs.Get(match.Instance);
        }
        return _configs.List().Count == 1 ? _configs.Get(_configs.List()[0].Instance) : null;
    }

    private EngineSessionOptions BuildSession(ConfigSnapshot? config, string? serial, bool allowActions)
    {
        JsonObject? emulator = config?.Values["Alas"]?["Emulator"] as JsonObject;
        string actualSerial = serial ?? emulator?["Serial"]?.GetValue<string>() ?? "";
        if (allowActions && string.IsNullOrWhiteSpace(actualSerial))
            throw new ArgumentException("动作运行需要实例串号或 serial");
        string? package = emulator?["PackageName"]?.GetValue<string>();
        // ServerName selects an account region/shard; the package determines asset variants.
        GameServer server = GameServerRules.FromPackage(package ?? Environment.GetEnvironmentVariable("ALAS_SERVER") ?? "cn");
        string? models = Environment.GetEnvironmentVariable("ALAS_OCR_MODELS");
        return new EngineSessionOptions(
            Environment.GetEnvironmentVariable("ALAS_ADB") ?? "adb", actualSerial, server,
            Path.Combine(_repo, "assets"), Environment.GetEnvironmentVariable("ALAS_PYTHON") ?? "python",
            package, string.IsNullOrWhiteSpace(models) ? null : Path.GetFullPath(models), allowActions,
            config is null ? null : _repo, config?.Instance);
    }

    /// <summary>
    /// Start exactly one task through the same Engine queue used by the
    /// explicit <c>/api/run</c> endpoint.  The old Core accepted an upstream
    /// task name and then dispatched it to a Python host; that boundary is
    /// intentionally gone.  A task name here is therefore a registered
    /// <see cref="ITaskRunner.Kind"/> and the optional input is passed to that
    /// runner unchanged.
    /// </summary>
    public void StartTask(JsonObject body)
    {
        ArgumentNullException.ThrowIfNull(body);
        string instance = body["instance"]?.GetValue<string>()
            ?? throw new ArgumentException("单任务启动需要 instance");
        string kind = body["task"]?.GetValue<string>()
            ?? throw new ArgumentException("单任务启动需要 task");
        if (string.IsNullOrWhiteSpace(instance) || string.IsNullOrWhiteSpace(kind))
            throw new ArgumentException("instance 和 task 不能为空");
        if (!System.Text.RegularExpressions.Regex.IsMatch(kind, @"\A[a-zA-Z0-9_-]{1,64}\z"))
            throw new ArgumentException("task 必须是 Engine 任务标识");
        if (body["input"] is not null and not JsonObject)
            throw new ArgumentException("input 必须是 JSON 对象");
        double timeout = body["timeout_seconds"]?.GetValue<double>() ?? 1500;
        if (!double.IsFinite(timeout) || timeout <= 0 || timeout > int.MaxValue / 1000.0)
            throw new ArgumentException("timeout_seconds 必须是正数");
        bool confirm = body["confirm_actions"]?.GetValue<bool>() ?? false;
        var request = new JsonObject
        {
            ["instance"] = instance,
            ["mode"] = confirm ? "actions" : "read_only",
            ["confirm_actions"] = confirm,
            ["max_seconds"] = timeout,
            ["queue"] = new JsonObject
            {
                ["tasks"] = new JsonArray(new JsonObject
                {
                    ["id"] = kind,
                    ["kind"] = kind,
                    ["input"] = body["input"]?.DeepClone(),
                    ["required"] = true,
                    ["timeoutSeconds"] = timeout,
                    ["instance"] = instance,
                })
            }
        };
        StartRun(request);
    }

    // These are deliberately typed Engine capability seams.  They do not
    // accept an operation name or arbitrary arguments, so an old Python host
    // cannot be smuggled back into the product through a generic RPC method.
    public JsonObject ReadStatistics(EngineStatisticsRequest request)
        => throw new EngineCapabilityUnavailableException("Engine 统计域尚未实现");

    public JsonObject RefreshStatisticsLoot(string instance)
        => throw new EngineCapabilityUnavailableException("Engine 统计刷新域尚未实现");

    public JsonObject ReadMeowfficer(EngineMeowfficerRequest request)
        => throw new EngineCapabilityUnavailableException("Engine 指挥喵报告域尚未实现");

    public JsonObject ClearMeowfficer(string instance)
        => throw new EngineCapabilityUnavailableException("Engine 指挥喵清理域尚未实现");

    public JsonObject ValidateShopStrategy(string script)
        => throw new EngineCapabilityUnavailableException("Engine 商店策略校验域尚未实现");

    public bool RequestStop()
    {
        lock (_gate)
        {
            if (_worker is not { IsCompleted: false }) return false;
            _stopRequested = true;
            return true;
        }
    }

    public Task BeginShutdown()
    {
        Task worker;
        lock (_gate) { _shuttingDown = true; _stopRequested = true; worker = _worker ?? Task.CompletedTask; }
        return worker;
    }

    private void EnsureAccepting()
    {
        if (_shuttingDown) throw new EngineControlWorkspaceUnavailableException("控制服务正在关闭");
    }

}

public sealed class EngineControlWorkspaceUnavailableException(string message) : Exception(message);
public sealed class EngineCapabilityUnavailableException(string message) : Exception(message);
public sealed record EngineStatisticsRequest(string Instance, string Category, int Days = 7,
    string? Month = null, string Period = "month");
public sealed record EngineMeowfficerRequest(string Instance, int Limit = 100);
