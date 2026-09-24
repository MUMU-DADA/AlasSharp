using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices.JavaScript;
using Alas.Client;
using Alas.Contracts;
using Alas.UI.ViewModels;

namespace Alas.UI.Browser;

/// <summary>
/// Browser/remote transport adapter. Only this adapter crosses HTTP; the
/// shared views consume IAlasControlBackend and remain unaware of transport.
/// The server endpoint is the same-origin Alas.Server listener by default.
/// </summary>
internal sealed class BrowserControlBackend : IAlasUiBackend
{
    private readonly ControlClient _client = new(CreateEndpoint());
    private readonly List<InstanceCardViewModel> _instances = [];
    private bool _disposed;
    private bool _connected;

    public event EventHandler? Changed;
    public bool IsConnected => _connected;
    public IReadOnlyList<InstanceCardViewModel> Instances => _instances;

    private static Uri CreateEndpoint()
    {
        try { return new Uri(BrowserLocation.Origin() + "/", UriKind.Absolute); }
        catch (Exception) { return new Uri("http://127.0.0.1:8765/"); }
    }

    public void Refresh() => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_disposed) return;
        try
        {
            var response = await _client.ListInstancesAsync().ConfigureAwait(false);
            _instances.Clear();
            foreach (var item in response.Instances)
                _instances.Add(InstanceCardViewModel.Create(item.Instance, "stopped", item.Server, item.Serial ?? string.Empty));
            _connected = true;
        }
        catch (Exception error) when (error is HttpRequestException or ControlApiException or ControlProtocolException)
        {
            _instances.Clear();
            _connected = false;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<JsonObject> ReadStateAsync(CancellationToken cancellationToken = default)
    {
        var state = await _client.GetStateAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToNode(state, ControlJsonContext.Default.ControlState)?.AsObject()
            ?? throw new ControlProtocolException("控制状态无法转换为共享快照");
    }

    public async Task<JsonObject?> ReadReportAsync(string stamp, CancellationToken cancellationToken = default)
    {
        try { return await _client.GetReportAsync(stamp, cancellationToken).ConfigureAwait(false); }
        catch (ControlApiException error) when (error.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    public Task<SchemaResponse> ReadSchemaAsync(string language = "zh-CN", CancellationToken cancellationToken = default)
        => _client.GetSchemaAsync(language, cancellationToken);

    public Task<ConfigResponse> ReadConfigAsync(string instance, CancellationToken cancellationToken = default)
        => _client.GetConfigAsync(instance, cancellationToken);

    public Task<ConfigResponse> PatchConfigAsync(ConfigPatchRequest request, CancellationToken cancellationToken = default)
        => _client.PatchConfigAsync(request, cancellationToken);

    public Task<ConfigResponse> CreateInstanceAsync(InstanceCreateRequest request, CancellationToken cancellationToken = default)
        => _client.CreateInstanceAsync(request, cancellationToken);

    public Task DeleteInstanceAsync(InstanceDeleteRequest request, CancellationToken cancellationToken = default)
        => _client.DeleteInstanceAsync(request, cancellationToken);

    public Task SaveQueueAsync(JsonObject queue, CancellationToken cancellationToken = default)
        => _client.SaveQueueAsync(queue, cancellationToken);

    public Task StartRunAsync(ControlRunRequest request, CancellationToken cancellationToken = default)
        => _client.StartRunAsync(request, cancellationToken);

    public Task StartTaskAsync(InstanceTaskRunRequest request, CancellationToken cancellationToken = default)
        => _client.StartTaskAsync(request, cancellationToken);

    public async Task<bool> RequestStopAsync(CancellationToken cancellationToken = default)
    {
        await _client.RequestStopAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public Task<JsonObject> ReadStatisticsAsync(StatisticsRequest request, CancellationToken cancellationToken = default)
        => _client.GetStatisticsReportAsync(request, cancellationToken);

    public Task<JsonObject> RefreshStatisticsLootAsync(string instance, CancellationToken cancellationToken = default)
        => _client.RefreshStatisticsLootAsync(instance, cancellationToken);

    public Task<JsonObject> ReadMeowfficerAsync(MeowfficerRequest request, CancellationToken cancellationToken = default)
        => _client.GetMeowfficerReportAsync(request, cancellationToken);

    public Task<JsonObject> ClearMeowfficerAsync(string instance, CancellationToken cancellationToken = default)
        => _client.ClearMeowfficerReportAsync(instance, cancellationToken);

    public Task<JsonObject> ValidateShopStrategyAsync(string script, CancellationToken cancellationToken = default)
        => _client.ValidateShopStrategyAsync(script, cancellationToken);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Dispose();
    }
}

internal static partial class BrowserLocation
{
    [JSImport("globalThis.location.origin", "globalThis")]
    internal static partial string Origin();
}
