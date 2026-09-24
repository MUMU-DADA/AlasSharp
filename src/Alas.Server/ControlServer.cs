using System.Net;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Contracts;
using Alas.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Alas.Server;

/// <summary>Kestrel 传输层；队列生命周期与全部执行语义由 Alas.Core/Runtime 管理。</summary>
public sealed class ControlServer
{
    private const int BodyLimit = ControlProtocol.MaxRequestBodyBytes;
    private readonly ControlWorkspace _workspace;
    private readonly ConfigWorkspace _config;
    private readonly string _tools;
    private readonly StaticUiFiles? _ui;
    private readonly int _port;
    private readonly string _token = RandomNumberGenerator.GetHexString(32);

    public ControlServer(string root, string repo, string data, string tools,
                         string? artifacts, string? workspace, int port, string? uiRoot = null)
    {
        if (port is < 1 or > 65535) throw new ArgumentException("port 必须在 1–65535 之间");
        _tools = Path.GetFullPath(tools);
        _port = port;
        _ui = uiRoot is null ? null : new StaticUiFiles(uiRoot);
        _workspace = new ControlWorkspace(root, repo, data, tools, artifacts, workspace);
        _config = new ConfigWorkspace(repo);
    }

    public int Run() => RunAsync().GetAwaiter().GetResult();

    public async Task<int> RunAsync(CancellationToken shutdown = default)
    {
        // No command-line/configuration URL binding: environment settings must not
        // expand this local-only API to a network listener before remote auth exists.
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, _port);
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = BodyLimit;
        });
        await using var app = builder.Build();
        await using var events = new ControlStateFeed(() => ReadState().ToJsonString(), app.Lifetime.ApplicationStopping);
        app.Run(context => Handle(context, events, app.Lifetime.ApplicationStopping));
        // The same gate rejects in-flight POSTs that finish reading after shutdown.
        using var stopping = app.Lifetime.ApplicationStopping.Register(() => _workspace.BeginShutdown());
        try
        {
            await app.StartAsync(shutdown);
            Console.WriteLine($"[控制界面] http://127.0.0.1:{_port}/");
            await app.WaitForShutdownAsync(shutdown);
        }
        finally
        {
            // Kestrel's HTTP shutdown deadline is not a deadline for an active sortie.
            // Never pass RequestAborted to the queue or abandon its final artifacts.
            await _workspace.BeginShutdown();
        }
        return 0;
    }

    private JsonObject ReadState()
    {
        var state = _workspace.State();
        state["token"] = _token;
        return state;
    }

    private async Task Handle(HttpContext context, ControlStateFeed events, CancellationToken stopping)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        try
        {
            var request = context.Request;
            string path = request.Path.Value ?? "";
            if (request.Host.Host != "127.0.0.1" || (request.Host.Port ?? 80) != _port ||
                context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address) ||
                !IsSameOrigin(request))
            {
                await Reply(context, 403, Error("仅允许本机同源访问"));
                return;
            }
            if (_ui is not null && !path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) &&
                path != "/api" && await _ui.TryServe(context)) return;
            if (_ui is null && HttpMethods.IsGet(request.Method) && path == "/")
            {
                string page = Path.Combine(_tools, "control_ui.html");
                if (!File.Exists(page))
                {
                    await Reply(context, 404, Error("控制界面文件不存在"));
                    return;
                }
                context.Response.ContentType = "text/html; charset=utf-8";
                context.Response.Headers.ContentSecurityPolicy =
                    "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; frame-ancestors 'none'";
                await context.Response.SendFileAsync(page, context.RequestAborted);
                return;
            }
            if (HttpMethods.IsGet(request.Method) && path == "/api/state")
            {
                await Reply(context, 200, ReadState());
                return;
            }
            if (HttpMethods.IsGet(request.Method) && path == "/api/events")
            {
                await StreamState(context, events, stopping);
                return;
            }
            if (HttpMethods.IsGet(request.Method) && path == "/api/report")
            {
                var report = _workspace.Report(request.Query["stamp"].ToString());
                await Reply(context, report is null ? 404 : 200, report ?? Error("找不到运行报告"));
                return;
            }
            if (HttpMethods.IsGet(request.Method) && path == "/api/instances")
            {
                await Reply(context, 200, Instances(_config.List()));
                return;
            }
            if (HttpMethods.IsGet(request.Method) && path == "/api/schema")
            {
                string language = request.Query["language"].ToString();
                var schema = _config.Schema(string.IsNullOrWhiteSpace(language) ? "zh-CN" : language);
                await Reply(context, 200, new JsonObject
                {
                    ["menu"] = schema.Menu,
                    ["args"] = schema.Args,
                    ["translations"] = schema.Translations,
                });
                return;
            }
            if (HttpMethods.IsGet(request.Method) && TryInstancePath(path, "config", out string? configInstance))
            {
                var config = _config.Get(configInstance);
                await Reply(context, 200, Config(config));
                return;
            }
            if (HttpMethods.IsGet(request.Method) && path == "/api/statistics/report")
            {
                string instance = RequiredQuery(request, "instance");
                string category = request.Query["category"].ToString();
                int days = ParseQueryInt(request, "days", 7, 1, 365);
                string month = request.Query["month"].ToString();
                string period = string.IsNullOrWhiteSpace(request.Query["period"]) ? "month" : request.Query["period"].ToString();
                var result = _workspace.ReadHostJson("statistics_report", new JsonObject
                {
                    ["instance"] = instance, ["category"] = category, ["days"] = days,
                    ["month"] = string.IsNullOrWhiteSpace(month) ? null : month, ["period"] = period,
                });
                await Reply(context, 200, result);
                return;
            }
            if (HttpMethods.IsPost(request.Method) && path == "/api/instances")
            {
                RequireToken(request);
                var body = await ReadBody(request);
                string instance = RequiredString(body, "instance");
                string? source = OptionalString(body, "source");
                string? importFile = OptionalString(body, "import_file");
                var created = _config.Create(instance, source, importFile);
                await Reply(context, 201, Config(created));
                return;
            }
            if (HttpMethods.IsPost(request.Method) && path == "/api/statistics/refresh-loot")
            {
                RequireToken(request);
                var body = await ReadBody(request);
                await Reply(context, 200, _workspace.ReadHostJson("statistics_refresh_loot",
                    new JsonObject { ["instance"] = RequiredString(body, "instance") }));
                return;
            }
            if (HttpMethods.IsGet(request.Method) && path == "/api/instances/importable")
            {
                await Reply(context, 200, new JsonObject
                {
                    ["sources"] = new JsonArray(_config.ListImports().Select(item => (JsonNode)new JsonObject
                    { ["name"] = item.Name, ["modified_at"] = item.ModifiedAt.ToString("O") }).ToArray()),
                });
                return;
            }
            if (HttpMethods.IsPost(request.Method) && path == "/api/instances/import")
            {
                RequireToken(request);
                var body = await ReadBody(request);
                var imported = _config.Import(RequiredString(body, "name"), RequiredString(body, "content"));
                await Reply(context, 200, new JsonObject
                { ["name"] = imported.Name, ["modified_at"] = imported.ModifiedAt.ToString("O") });
                return;
            }
            if (HttpMethods.IsGet(request.Method) && path == "/api/meowfficer/report")
            {
                string instance = RequiredQuery(request, "instance");
                int limit = ParseQueryInt(request, "limit", 100, 1, 500);
                await Reply(context, 200, _workspace.ReadHostJson("meowfficer_report",
                    new JsonObject { ["instance"] = instance, ["limit"] = limit }));
                return;
            }
            if (HttpMethods.IsPost(request.Method) && path == "/api/meowfficer/clear")
            {
                RequireToken(request);
                var body = await ReadBody(request);
                await Reply(context, 200, _workspace.ReadHostJson("meowfficer_clear",
                    new JsonObject { ["instance"] = RequiredString(body, "instance") }));
                return;
            }
            if (HttpMethods.IsPost(request.Method) && path == "/api/tasks/validate-script")
            {
                RequireToken(request);
                var body = await ReadBody(request);
                await Reply(context, 200, _workspace.ReadHostJson("shop_strategy_validate",
                    new JsonObject { ["script"] = body["script"]?.GetValue<string>()
                        ?? throw new ArgumentException("缺少 script 字符串") }));
                return;
            }
            if (HttpMethods.IsPost(request.Method) && path == "/api/tasks/run")
            {
                RequireToken(request);
                _workspace.StartTask(await ReadBody(request));
                await Reply(context, 202, new JsonObject { ["ok"] = true });
                return;
            }
            if (HttpMethods.IsPost(request.Method) && path == "/api/scheduler/start")
            {
                RequireToken(request);
                _workspace.StartScheduler(await ReadBody(request));
                await Reply(context, 202, new JsonObject { ["ok"] = true });
                return;
            }
            if (HttpMethods.IsPatch(request.Method) && TryInstancePath(path, "config", out configInstance))
            {
                RequireToken(request);
                var body = await ReadBody(request);
                string? revision = OptionalString(body, "revision");
                if (body["changes"] is not JsonArray changes || changes.Count is < 1 or > 200)
                    throw new ArgumentException("changes 必须包含 1 到 200 项");
                var parsed = changes.Select(ParseChange).ToArray();
                var updated = _config.Patch(configInstance, revision, parsed);
                await Reply(context, 200, Config(updated));
                return;
            }
            if (HttpMethods.IsDelete(request.Method) && TryInstancePath(path, "instances", out string? deleteInstance))
            {
                RequireToken(request);
                var body = await ReadBody(request);
                _config.Delete(deleteInstance, RequiredString(body, "revision"));
                await Reply(context, 200, new JsonObject { ["deleted"] = deleteInstance });
                return;
            }
            if (HttpMethods.IsPost(request.Method) && path is "/api/queue" or "/api/run" or "/api/stop")
            {
                RequireToken(request);
                if (path == "/api/stop")
                {
                    bool stopped = _workspace.RequestStop();
                    await Reply(context, stopped ? 200 : 409,
                        stopped ? new JsonObject { ["ok"] = true } : Error("当前没有运行中的队列"));
                    return;
                }
                var body = await ReadBody(request);
                if (path == "/api/queue") _workspace.SaveQueueRequest(body);
                else _workspace.StartRun(body);
                await Reply(context, path == "/api/queue" ? 200 : 202, new JsonObject { ["ok"] = true });
                return;
            }
            await Reply(context, 404, Error("未知接口"));
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested || stopping.IsCancellationRequested) { }
        catch (IOException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (BadHttpRequestException error) { await Reply(context, error.StatusCode, Error("无效的 HTTP 请求")); }
        catch (ControlWorkspaceUnavailableException error) { await Reply(context, 409, Error(error.Message)); }
        catch (ConfigWorkspaceException error)
        {
            int status = error.Code switch { "FORBIDDEN" => 403, "CONFLICT" => 409, "NOT_FOUND" => 404, _ => 400 };
            await Reply(context, status, Error(error.Message));
        }
        catch (ArgumentException error) { await Reply(context, 400, Error(error.Message)); }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException)
        {
            await Reply(context, 400, Error($"请求字段无效: {error.Message}"));
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            if (context.Response.HasStarted) { context.Abort(); return; }
            await Reply(context, 500, Error("控制服务内部错误"));
        }
    }

    private static async Task StreamState(HttpContext context, ControlStateFeed feed, CancellationToken stopping)
    {
        string cursor = context.Request.Headers["Last-Event-ID"].ToString();
        if (cursor.Length != 0 && !ControlProtocol.IsEventCursor(cursor))
            throw new ArgumentException("事件游标无效");
        using var subscription = feed.Subscribe();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping);
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers["X-Alas-Events"] = ControlProtocol.EventsContract;
        context.Response.Headers["X-Accel-Buffering"] = "no";
        bool first = true;
        while (!lifetime.IsCancellationRequested)
        {
            ControlStateFeed.Snapshot? snapshot = null;
            using (var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                heartbeat.CancelAfter(TimeSpan.FromSeconds(15));
                try { snapshot = await subscription.Pending.Reader.ReadAsync(heartbeat.Token); }
                catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { }
                catch (System.Threading.Channels.ChannelClosedException) when (stopping.IsCancellationRequested) { break; }
                catch (System.Threading.Channels.ChannelClosedException error)
                {
                    Console.Error.WriteLine($"控制状态流采样失败: {error.InnerException ?? error}");
                    context.Abort();
                    return;
                }
            }
            string frame = ": keep-alive\n\n";
            if (snapshot is not null)
            {
                string kind = first && cursor != snapshot.Cursor ? "reset" : "snapshot";
                frame = $"id: {snapshot.Cursor}\nevent: {kind}\ndata: {snapshot.Json}\n\n";
                cursor = snapshot.Cursor;
                first = false;
            }
            using var write = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            write.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await context.Response.WriteAsync(frame, write.Token);
                await context.Response.Body.FlushAsync(write.Token);
            }
            catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
            {
                context.Abort(); // Slow HTTP client: release its subscription, never stop the queue.
                return;
            }
        }
    }

    private bool IsSameOrigin(HttpRequest request)
    {
        // Non-browser clients may omit Origin; browser callers must match exactly.
        if (!request.Headers.TryGetValue("Origin", out var origin)) return true;
        return origin.Count == 1 && Uri.TryCreate(origin[0], UriKind.Absolute, out var uri)
            && uri.Scheme == "http" && uri.Host == "127.0.0.1" && uri.Port == _port
            && uri.UserInfo.Length == 0 && uri.AbsolutePath == "/"
            && uri.Query.Length == 0 && uri.Fragment.Length == 0;
    }

    private static async Task<JsonObject> ReadBody(HttpRequest request)
    {
        // Preserve the local API's explicit-length contract and bound allocations.
        if (request.ContentLength is not { } length || length > BodyLimit)
            throw new ArgumentException("请求体必须小于 1 MiB，并提供 Content-Length");
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        string text = await reader.ReadToEndAsync(request.HttpContext.RequestAborted);
        try
        {
            return JsonNode.Parse(text) as JsonObject ?? throw new ArgumentException("请求体必须是 JSON 对象");
        }
        catch (JsonException error) { throw new ArgumentException($"请求体不是合法 JSON: {error.Message}", error); }
    }

    private static JsonObject Error(string message) => new() { ["error"] = message };

    private void RequireToken(HttpRequest request)
    {
        if (request.Headers[ControlProtocol.TokenHeader] != _token)
            throw new ConfigWorkspaceException("FORBIDDEN", "请求令牌无效");
    }

    private static bool TryInstancePath(string path, string prefix, out string instance)
    {
        string marker = "/api/" + prefix + "/";
        if (path.StartsWith(marker, StringComparison.Ordinal) && path.Length > marker.Length)
        {
            instance = Uri.UnescapeDataString(path[marker.Length..]);
            return instance.Length > 0 && !instance.Contains('/');
        }
        instance = "";
        return false;
    }

    private static JsonObject Instances(IReadOnlyList<ConfigInstance> instances) => new()
    {
        ["instances"] = new JsonArray(instances.Select(item => new JsonObject
        {
            ["instance"] = item.Instance,
            ["revision"] = item.Revision,
            ["serial"] = item.Serial,
            ["server"] = item.Server,
        }).ToArray()),
    };

    private static JsonObject Config(ConfigSnapshot config) => new()
    {
        ["instance"] = config.Instance,
        ["revision"] = config.Revision,
        ["values"] = config.Values,
    };

    private static Alas.Runtime.ConfigChange ParseChange(JsonNode? node)
    {
        if (node is not JsonObject change) throw new ArgumentException("配置修改项必须是对象");
        string path = RequiredString(change, "path");
        return new Alas.Runtime.ConfigChange(path, change["value"]?.DeepClone());
    }

    private static string RequiredString(JsonObject body, string key)
    {
        string? value = body[key]?.GetValue<string>()?.Trim();
        return string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"缺少 {key}") : value;
    }

    private static string? OptionalString(JsonObject body, string key)
        => body[key] is null ? null : RequiredString(body, key);

    private static string RequiredQuery(HttpRequest request, string key)
    {
        string value = request.Query[key].ToString().Trim();
        return string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"缺少查询参数 {key}") : value;
    }

    private static int ParseQueryInt(HttpRequest request, string key, int fallback, int min, int max)
    {
        string text = request.Query[key].ToString();
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < min || value > max)
            throw new ArgumentException($"查询参数 {key} 超出范围");
        return value;
    }

    private static async Task Reply(HttpContext context, int status, JsonObject payload)
    {
        if (context.RequestAborted.IsCancellationRequested || context.Response.HasStarted) return;
        byte[] body = Encoding.UTF8.GetBytes(payload.ToJsonString());
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = body.Length;
        await context.Response.Body.WriteAsync(body, context.RequestAborted);
    }
}
