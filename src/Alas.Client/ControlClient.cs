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
public sealed class ControlClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly bool _ownsHttp;
    private string? _token;

    /// <summary>Creates an owned transport with automatic redirects disabled.</summary>
    public ControlClient(Uri endpoint)
    {
        _endpoint = ValidateEndpoint(endpoint);
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        _ownsHttp = true;
    }

    /// <summary>Borrow a transport configured without automatic redirects or retries.</summary>
    public ControlClient(HttpClient http, Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(http);
        _endpoint = ValidateEndpoint(endpoint);
        _http = http;
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
        if (string.IsNullOrWhiteSpace(state.Token) || state.Queue is null || state.Active is null ||
            string.IsNullOrWhiteSpace(state.Active.Status) || state.LiveTasks is null || state.RecentLogs is null || state.Runs is null)
            throw new ControlProtocolException("服务状态缺少必需字段");
        Volatile.Write(ref _token, state.Token);
        return state;
    }

    public Task<JsonObject> GetReportAsync(string stamp, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stamp);
        return ReadReportAsync(stamp, cancellationToken);
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

    private async Task<T> SendAsync<T>(HttpRequestMessage request, HttpStatusCode expected,
                                     JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
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
}

public sealed class ControlApiException(HttpStatusCode statusCode, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

public sealed class ControlProtocolException(string message, Exception? inner = null) : Exception(message, inner);
