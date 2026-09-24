using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Alas.Contracts;

namespace Alas.Client;

/// <summary>
/// Shared HTTP client with no UI, device or Python dependencies. The caller owns
/// an injected HttpClient and must disable redirects/retries on that transport.
/// A cancelled request never sends a stop command or retries a write.
/// </summary>
public sealed partial class ControlClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly bool _ownsHttp;
    private readonly TimeSpan _requestTimeout;
    private string? _token;

    /// <summary>Creates an owned transport with automatic redirects disabled.</summary>
    public ControlClient(Uri endpoint, TimeSpan? requestTimeout = null)
    {
        _endpoint = ValidateEndpoint(endpoint);
        _requestTimeout = ValidateTimeout(requestTimeout);
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        _ownsHttp = true;
    }

    /// <summary>Borrow a transport configured without automatic redirects or retries.</summary>
    public ControlClient(HttpClient http, Uri endpoint, TimeSpan? requestTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        _endpoint = ValidateEndpoint(endpoint);
        _requestTimeout = ValidateTimeout(requestTimeout);
        _http = http;
    }

    private static TimeSpan ValidateTimeout(TimeSpan? timeout)
    {
        var value = timeout ?? TimeSpan.FromSeconds(30);
        if (value <= TimeSpan.Zero || value.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(timeout), "请求期限必须为正的有限时长");
        return value;
    }

    private static Uri ValidateEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https") ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
            endpoint.AbsolutePath != "/")
            throw new ArgumentException("控制服务地址必须是无用户信息、路径、查询或片段的 HTTP(S) 地址", nameof(endpoint));
        return endpoint;
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    /// <summary>Fetch a snapshot and acquire this service instance's write token.</summary>
    public async Task<ControlState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_endpoint, "api/state"));
        var state = await SendAsync(request, HttpStatusCode.OK, ControlJsonContext.Default.ControlState, cancellationToken)
            .ConfigureAwait(false);
        AcceptState(state);
        return state;
    }

    private void AcceptState(ControlState state)
    {
        if (string.IsNullOrWhiteSpace(state.Token) || state.Queue is null || state.Active is null ||
            string.IsNullOrWhiteSpace(state.Active.Status) || state.LiveTasks is null || state.RecentLogs is null || state.Runs is null)
            throw new ControlProtocolException("服务状态缺少必需字段");
        Volatile.Write(ref _token, state.Token);
    }

    public Task<JsonObject> GetReportAsync(string stamp, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stamp);
        return ReadReportAsync(stamp, cancellationToken);
    }

    public async Task<InstanceListResponse> ListInstancesAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_endpoint, "api/instances"));
        return await SendAsync(request, HttpStatusCode.OK, ControlJsonContext.Default.InstanceListResponse, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<SchemaResponse> GetSchemaAsync(string language = "zh-CN", CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri(_endpoint, "api/schema?language=" + Uri.EscapeDataString(language)));
        return await SendAsync(request, HttpStatusCode.OK, ControlJsonContext.Default.SchemaResponse, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Browser/remote transport for the statistics report. The server-side
    /// handler invokes Alas.Core directly; this method only crosses a process
    /// boundary when the caller is a web or remote client.
    /// </summary>
    public async Task<JsonObject> GetStatisticsReportAsync(StatisticsRequest request,
                                                     CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = "instance=" + Uri.EscapeDataString(request.Instance) +
            "&category=" + Uri.EscapeDataString(request.Category) +
            "&days=" + request.Days.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            "&month=" + Uri.EscapeDataString(request.Month ?? "") +
            "&period=" + Uri.EscapeDataString(request.Period);
        using var message = new HttpRequestMessage(HttpMethod.Get,
            new Uri(_endpoint, "api/statistics/report?" + query));
        return await SendAsync(message, HttpStatusCode.OK, ControlJsonContext.Default.JsonObject, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<JsonObject> RefreshStatisticsLootAsync(string instance,
                                                        CancellationToken cancellationToken = default)
        => WriteReadJsonAsync("api/statistics/refresh-loot", new JsonObject { ["instance"] = instance },
            HttpMethod.Post, HttpStatusCode.OK, cancellationToken);

    public async Task<JsonObject> GetMeowfficerReportAsync(MeowfficerRequest request,
                                                     CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string path = "api/meowfficer/report?instance=" + Uri.EscapeDataString(request.Instance) +
            "&limit=" + request.Limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var message = new HttpRequestMessage(HttpMethod.Get, new Uri(_endpoint, path));
        return await SendAsync(message, HttpStatusCode.OK, ControlJsonContext.Default.JsonObject, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<JsonObject> ClearMeowfficerReportAsync(string instance,
                                                       CancellationToken cancellationToken = default)
        => WriteReadJsonAsync("api/meowfficer/clear", new JsonObject { ["instance"] = instance },
            HttpMethod.Post, HttpStatusCode.OK, cancellationToken);

    /// <summary>Validate a restricted upstream shop strategy without executing it.</summary>
    public Task<JsonObject> ValidateShopStrategyAsync(string script,
                                                      CancellationToken cancellationToken = default)
        => WriteReadJsonAsync("api/tasks/validate-script", new JsonObject { ["script"] = script },
            HttpMethod.Post, HttpStatusCode.OK, cancellationToken);

    public async Task<ConfigResponse> GetConfigAsync(string instance, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instance);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri(_endpoint, "api/config/" + Uri.EscapeDataString(instance)));
        return await SendAsync(request, HttpStatusCode.OK, ControlJsonContext.Default.ConfigResponse, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<ConfigResponse> PatchConfigAsync(ConfigPatchRequest request, CancellationToken cancellationToken = default)
        => WriteReadAsync("api/config/" + Uri.EscapeDataString(request.Instance), request,
            ControlJsonContext.Default.ConfigPatchRequest, ControlJsonContext.Default.ConfigResponse,
            HttpMethod.Patch, HttpStatusCode.OK, cancellationToken);

    public Task<ConfigResponse> CreateInstanceAsync(InstanceCreateRequest request, CancellationToken cancellationToken = default)
        => WriteReadAsync("api/instances", request, ControlJsonContext.Default.InstanceCreateRequest,
            ControlJsonContext.Default.ConfigResponse, HttpMethod.Post, HttpStatusCode.Created, cancellationToken);

    public Task DeleteInstanceAsync(InstanceDeleteRequest request, CancellationToken cancellationToken = default)
        => WriteAsync("api/instances/" + Uri.EscapeDataString(request.Instance), request,
            ControlJsonContext.Default.InstanceDeleteRequest, HttpMethod.Delete, HttpStatusCode.OK, cancellationToken);

    public Task<InstanceImportSource> ImportInstanceAsync(InstanceImportRequest request, CancellationToken cancellationToken = default)
        => WriteReadAsync("api/instances/import", request, ControlJsonContext.Default.InstanceImportRequest,
            ControlJsonContext.Default.InstanceImportSource, HttpMethod.Post, HttpStatusCode.OK, cancellationToken);

    public async Task<InstanceImportListResponse> GetInstanceImportsAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_endpoint, "api/instances/importable"));
        return await SendAsync(request, HttpStatusCode.OK, ControlJsonContext.Default.InstanceImportListResponse, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<JsonObject> ReadReportAsync(string stamp, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri(_endpoint, "api/report?stamp=" + Uri.EscapeDataString(stamp)));
        return await SendAsync(request, HttpStatusCode.OK, ControlJsonContext.Default.JsonObject, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task SaveQueueAsync(JsonObject queue, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queue);
        return WriteAsync("api/queue", new ControlQueueRequest { Queue = queue },
            ControlJsonContext.Default.ControlQueueRequest, HttpStatusCode.OK, cancellationToken);
    }

    /// <summary>Only acknowledges acceptance. Observe state/report for actual outcomes.</summary>
    public Task StartRunAsync(ControlRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return WriteAsync("api/run", request, ControlJsonContext.Default.ControlRunRequest,
            HttpStatusCode.Accepted, cancellationToken);
    }

    public Task RequestStopAsync(CancellationToken cancellationToken = default) =>
        WriteAsync("api/stop", new JsonObject(), ControlJsonContext.Default.JsonObject,
            HttpStatusCode.OK, cancellationToken);

    public Task StartTaskAsync(InstanceTaskRunRequest request, CancellationToken cancellationToken = default)
        => WriteAsync("api/tasks/run", request, ControlJsonContext.Default.InstanceTaskRunRequest,
            HttpStatusCode.Accepted, cancellationToken);

    private async Task WriteAsync<T>(string path, T body, JsonTypeInfo<T> type,
                                    HttpStatusCode expected, CancellationToken cancellationToken)
    {
        string token = Volatile.Read(ref _token)
            ?? throw new InvalidOperationException("请先读取服务状态以取得当前服务的写令牌");
        // ByteArrayContent supplies Content-Length on desktop and WASM. JsonContent
        // would use chunked transfer on desktop, which this local API rejects.
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(body, type);
        if (bytes.Length > ControlProtocol.MaxRequestBodyBytes)
            throw new ArgumentException("请求体不能超过 1 MiB", nameof(body));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_endpoint, path));
        request.Headers.Add(ControlProtocol.TokenHeader, token);
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        var response = await SendAsync(request, expected, ControlJsonContext.Default.ControlAcknowledgement, cancellationToken)
            .ConfigureAwait(false);
        if (!response.Ok) throw new ControlProtocolException("服务未确认操作已接受");
    }

    private async Task<TResponse> WriteReadAsync<TRequest, TResponse>(
        string path, TRequest body, JsonTypeInfo<TRequest> requestType, JsonTypeInfo<TResponse> responseType,
        HttpMethod method, HttpStatusCode expected, CancellationToken cancellationToken)
    {
        string token = Volatile.Read(ref _token)
            ?? throw new InvalidOperationException("请先读取服务状态以取得当前服务的写令牌");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(body, requestType);
        if (bytes.Length > ControlProtocol.MaxRequestBodyBytes)
            throw new ArgumentException("请求体不能超过 1 MiB", nameof(body));
        using var request = new HttpRequestMessage(method, new Uri(_endpoint, path));
        request.Headers.Add(ControlProtocol.TokenHeader, token);
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return await SendAsync(request, expected, responseType, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonObject> WriteReadJsonAsync(string path, JsonObject body, HttpMethod method,
                                                      HttpStatusCode expected, CancellationToken cancellationToken)
    {
        string token = Volatile.Read(ref _token)
            ?? throw new InvalidOperationException("请先读取服务状态以取得当前服务的写令牌");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(body, ControlJsonContext.Default.JsonObject);
        if (bytes.Length > ControlProtocol.MaxRequestBodyBytes)
            throw new ArgumentException("请求体不能超过 1 MiB", nameof(body));
        using var request = new HttpRequestMessage(method, new Uri(_endpoint, path));
        request.Headers.Add(ControlProtocol.TokenHeader, token);
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return await SendAsync(request, expected, ControlJsonContext.Default.JsonObject, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task WriteAsync<T>(string path, T body, JsonTypeInfo<T> type,
                                     HttpMethod method, HttpStatusCode expected, CancellationToken cancellationToken)
    {
        string token = Volatile.Read(ref _token)
            ?? throw new InvalidOperationException("请先读取服务状态以取得当前服务的写令牌");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(body, type);
        if (bytes.Length > ControlProtocol.MaxRequestBodyBytes)
            throw new ArgumentException("请求体不能超过 1 MiB", nameof(body));
        using var request = new HttpRequestMessage(method, new Uri(_endpoint, path));
        request.Headers.Add(ControlProtocol.TokenHeader, token);
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        _ = await SendAsync(request, expected, ControlJsonContext.Default.JsonObject, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage request, HttpStatusCode expected,
                                     JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        // HttpClient.Timeout ends after headers with ResponseHeadersRead. Keep a
        // deadline across the JSON body as well; an ambiguous write is never retried.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_requestTimeout);
        cancellationToken = deadline.Token;
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        await RequireStatusAsync(response, expected, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentType?.MediaType != "application/json")
            throw new ControlProtocolException("控制服务响应不是 JSON");
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync(stream, type, cancellationToken).ConfigureAwait(false)
                ?? throw new ControlProtocolException("控制服务响应为空");
        }
        catch (JsonException error)
        {
            throw new ControlProtocolException("控制服务 JSON 响应不符合合同", error);
        }
    }

    private static async Task RequireStatusAsync(HttpResponseMessage response, HttpStatusCode expected,
                                                CancellationToken cancellationToken)
    {
        if (response.StatusCode != expected)
        {
            string message = $"控制服务返回 HTTP {(int)response.StatusCode}";
            try
            {
                if (response.Content.Headers.ContentType?.MediaType == "application/json")
                {
                    await using var errorStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    var error = await JsonSerializer.DeserializeAsync(errorStream, ControlJsonContext.Default.ControlError, cancellationToken)
                        .ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(error?.Error)) message = error.Error;
                }
            }
            catch (JsonException) { } // Preserve HTTP status even for a broken error envelope.
            throw new ControlApiException(response.StatusCode, message);
        }
    }
}

public sealed class ControlApiException(HttpStatusCode statusCode, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

public sealed class ControlProtocolException(string message, Exception? inner = null) : Exception(message, inner);
