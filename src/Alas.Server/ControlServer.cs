using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private const int BodyLimit = 1024 * 1024;
    private readonly ControlWorkspace _workspace;
    private readonly string _tools;
    private readonly int _port;
    private readonly string _token = RandomNumberGenerator.GetHexString(32);

    public ControlServer(string root, string repo, string data, string tools,
                         string? artifacts, string? workspace, int port)
    {
        if (port is < 1 or > 65535) throw new ArgumentException("port 必须在 1–65535 之间");
        _tools = Path.GetFullPath(tools);
        _port = port;
        _workspace = new ControlWorkspace(root, repo, data, tools, artifacts, workspace);
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
        app.Run(Handle);
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

    private async Task Handle(HttpContext context)
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
            if (HttpMethods.IsGet(request.Method) && path == "/")
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
                var state = _workspace.State();
                state["token"] = _token;
                await Reply(context, 200, state);
                return;
            }
            if (HttpMethods.IsGet(request.Method) && path == "/api/report")
            {
                var report = _workspace.Report(request.Query["stamp"].ToString());
                await Reply(context, report is null ? 404 : 200, report ?? Error("找不到运行报告"));
                return;
            }
            if (HttpMethods.IsPost(request.Method) && path is "/api/queue" or "/api/run" or "/api/stop")
            {
                if (request.Headers["X-Alas-Token"] != _token)
                {
                    await Reply(context, 403, Error("请求令牌无效"));
                    return;
                }
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
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (IOException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (BadHttpRequestException error) { await Reply(context, error.StatusCode, Error("无效的 HTTP 请求")); }
        catch (ControlWorkspaceUnavailableException error) { await Reply(context, 409, Error(error.Message)); }
        catch (ArgumentException error) { await Reply(context, 400, Error(error.Message)); }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException)
        {
            await Reply(context, 400, Error($"请求字段无效: {error.Message}"));
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            await Reply(context, 500, Error("控制服务内部错误"));
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
