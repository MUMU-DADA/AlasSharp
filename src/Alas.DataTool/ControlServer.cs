using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Runtime;
using Alas.Tasks;

namespace Alas.DataTool;

/// <summary>本机控制入口；任务语义仍由 QueueExecution 和各 ITaskRunner 决定。</summary>
internal sealed class ControlServer
{
    private readonly string _root;
    private readonly string _repo;
    private readonly string _data;
    private readonly string _tools;
    private readonly string _artifacts;
    private readonly string _control;
    private readonly int _port;
    private readonly string _token = RandomNumberGenerator.GetHexString(32);
    private readonly object _gate = new();
    private Task? _worker;
    private string? _mode;
    private string? _startedAt;
    private string? _finishedAt;
    private string? _error;
    private string? _stopPath;
    private string? _runDirectory;
    private bool _stopRequested;

    public ControlServer(string root, string repo, string data, string tools,
                         string? artifacts, string? workspace, int port)
    {
        _root = Path.GetFullPath(root);
        _repo = Path.GetFullPath(repo);
        _data = Path.GetFullPath(data);
        _tools = Path.GetFullPath(tools);
        _control = Path.GetFullPath(workspace ?? Path.Combine(_root, ".runtime", "control"));
        _artifacts = Path.GetFullPath(artifacts ?? Path.Combine(_control, "runs"));
        _port = port;
        Directory.CreateDirectory(_control);
        Directory.CreateDirectory(_artifacts);
    }

    public int Run()
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        listener.Start();
        Console.WriteLine($"[控制界面] http://127.0.0.1:{_port}/");
        ConsoleCancelEventHandler cancel = (_, e) =>
        {
            e.Cancel = true;
            RequestStop();
            listener.Stop();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try { context = listener.GetContext(); }
                catch (HttpListenerException) when (!listener.IsListening) { break; }
                catch (ObjectDisposedException) { break; }
                _ = Task.Run(() => Handle(context));
            }
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            Task? worker;
            lock (_gate) worker = _worker;
            worker?.GetAwaiter().GetResult();
        }
        return 0;
    }

    private void Handle(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            string path = request.Url?.AbsolutePath ?? "";
            if (request.Url?.Host != "127.0.0.1")
            {
                Reply(context, 403, new JsonObject { ["error"] = "仅允许本机访问" });
                return;
            }
            if (request.HttpMethod == "GET" && path == "/")
            {
                string page = Path.Combine(_tools, "control_ui.html");
                if (!File.Exists(page))
                {
                    Reply(context, 404, new JsonObject { ["error"] = "控制界面文件不存在" });
                    return;
                }
                byte[] body = File.ReadAllBytes(page);
                context.Response.ContentType = "text/html; charset=utf-8";
                context.Response.Headers["Content-Security-Policy"] =
                    "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'";
                context.Response.Headers["Cache-Control"] = "no-store";
                context.Response.ContentLength64 = body.Length;
                context.Response.OutputStream.Write(body);
                return;
            }
            if (request.HttpMethod == "GET" && path == "/api/state")
            {
                Reply(context, 200, State());
                return;
            }
            if (request.HttpMethod == "GET" && path == "/api/report")
            {
                string? stamp = request.QueryString["stamp"];
                if (string.IsNullOrWhiteSpace(stamp) ||
                    stamp.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
                    throw new ArgumentException("运行标识无效");
                string directory = Path.Combine(_artifacts, stamp);
                if (!RunReport.IsRunDirectory(directory))
                {
                    Reply(context, 404, new JsonObject { ["error"] = "找不到运行报告" });
                    return;
                }
                Reply(context, 200, RunReport.Build(directory).ToJson());
                return;
            }
            if (request.HttpMethod == "POST" && path is "/api/queue" or "/api/run" or "/api/stop")
            {
                if (request.Headers["X-Alas-Token"] != _token)
                {
                    Reply(context, 403, new JsonObject { ["error"] = "请求令牌无效" });
                    return;
                }
                if (path == "/api/stop")
                {
                    if (!RequestStop())
                    {
                        Reply(context, 409, new JsonObject { ["error"] = "当前没有运行中的队列" });
                        return;
                    }
                    Reply(context, 200, new JsonObject { ["ok"] = true });
                    return;
                }
                var body = ReadBody(request);
                if (path == "/api/queue")
                {
                    var queue = RequireQueue(body);
                    SaveQueue(queue);
                    Reply(context, 200, new JsonObject { ["ok"] = true });
                    return;
                }
                StartRun(body);
                Reply(context, 202, new JsonObject { ["ok"] = true });
                return;
            }
            Reply(context, 404, new JsonObject { ["error"] = "未知接口" });
        }
        catch (ArgumentException error)
        {
            Reply(context, 400, new JsonObject { ["error"] = error.Message });
        }
        catch (RunAlreadyActiveException error)
        {
            Reply(context, 409, new JsonObject { ["error"] = error.Message });
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException)
        {
            Reply(context, 400, new JsonObject { ["error"] = $"请求字段无效: {error.Message}" });
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            Reply(context, 500, new JsonObject { ["error"] = "控制服务内部错误" });
        }
        finally { context.Response.Close(); }
    }

    private JsonObject State()
    {
        string? runDirectory;
        string? mode;
        string? started;
        string? finished;
        string? error;
        bool running;
        bool stopRequested;
        lock (_gate)
        {
            runDirectory = _runDirectory;
            running = _worker is { IsCompleted: false };
            mode = _mode;
            started = _startedAt;
            finished = _finishedAt;
            error = _error;
            stopRequested = _stopRequested;
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
            ["token"] = _token,
            ["queue"] = LoadQueue(),
            ["active"] = active,
            ["report"] = report,
            ["live_tasks"] = LiveTasks(runDirectory),
            ["recent_logs"] = RecentLogs(runDirectory),
            ["runs"] = RunReport.Summarize(_artifacts, 20),
        };
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

    private static JsonObject ReadBody(HttpListenerRequest request)
    {
        if (request.ContentLength64 < 0 || request.ContentLength64 > 1024 * 1024)
            throw new ArgumentException("请求体必须小于 1 MiB");
        using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
        try
        {
            return JsonNode.Parse(reader.ReadToEnd()) as JsonObject
                ?? throw new ArgumentException("请求体必须是 JSON 对象");
        }
        catch (JsonException error)
        {
            throw new ArgumentException($"请求体不是合法 JSON: {error.Message}", error);
        }
    }

    private static JsonObject RequireQueue(JsonObject body)
    {
        if (body["queue"] is not JsonObject queue)
            throw new ArgumentException("缺少 queue 对象");
        TaskQueueFile.Parse(queue.ToJsonString());
        return queue;
    }

    private void SaveQueue(JsonObject queue)
    {
        lock (_gate)
        {
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

    private void StartRun(JsonObject body)
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
            if (_worker is { IsCompleted: false })
                throw new RunAlreadyActiveException();
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
            _worker = Task.Run(() =>
            {
                try
                {
                    QueueExecution.RunFile(requestPath, options, stopOnFailure: stopOnFailure,
                        resume: resume, stopFile: _stopPath, onSessionStarted: directory =>
                        {
                            lock (_gate) _runDirectory = directory;
                        });
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
                    }
                }
            });
        }
    }

    private bool RequestStop()
    {
        lock (_gate)
        {
            if (_worker is not { IsCompleted: false } || _stopPath is null) return false;
            File.WriteAllText(_stopPath, "stop");
            _stopRequested = true;
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
                if (JsonNode.Parse(File.ReadAllText(path)) is JsonObject task)
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
            foreach (string line in File.ReadLines(path).TakeLast(80))
                if (JsonNode.Parse(line) is JsonObject entry) logs.Add(entry);
        }
        catch (IOException) { }
        catch (JsonException) { }
        return logs;
    }

    private static void Reply(HttpListenerContext context, int status, JsonObject payload)
    {
        if (!context.Response.OutputStream.CanWrite) return;
        byte[] body = Encoding.UTF8.GetBytes(payload.ToJsonString());
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.ContentLength64 = body.Length;
        context.Response.OutputStream.Write(body);
    }

    private sealed class RunAlreadyActiveException()
        : Exception("已有队列正在运行");
}
