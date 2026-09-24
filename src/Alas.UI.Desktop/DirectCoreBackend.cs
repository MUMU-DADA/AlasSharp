using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Contracts;
using Alas.Runtime;
using Alas.UI.ViewModels;
using RuntimeConfigChange = Alas.Runtime.ConfigChange;

namespace Alas.UI.Desktop;

/// <summary>
/// Desktop composition root for the real backend. It reads instances through
/// Alas.Core directly and keeps the control workspace in the same process; no
/// loopback HTTP hop is used for native desktop operation.
/// </summary>
internal sealed class DirectCoreBackend : IAlasUiBackend
{
    private readonly ConfigWorkspace? _configs;
    private readonly ControlWorkspace? _workspace;
    private readonly List<InstanceCardViewModel> _instances = [];
    private bool _disposed;
    private bool _connected;

    public DirectCoreBackend()
    {
        string? root = FindProjectRoot();
        string? repo = Environment.GetEnvironmentVariable("ALAS_REPO");
        if (string.IsNullOrWhiteSpace(repo))
            repo = root is null ? null : Path.Combine(root, ".runtime", "engine");
        if (root is null || repo is null || !Directory.Exists(repo)) return;

        try
        {
            _configs = new ConfigWorkspace(repo);
            _workspace = new ControlWorkspace(root, repo, Path.Combine(root, "data"),
                Path.Combine(root, "tools"), Path.Combine(root, ".runtime", "control", "runs"),
                Path.Combine(root, ".runtime", "control"));
            Refresh();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public event EventHandler? Changed;

    public bool IsConnected => _connected;

    public IReadOnlyList<InstanceCardViewModel> Instances => _instances;

    public Task<JsonObject> ReadStateAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => WorkspaceOrThrow().State(), cancellationToken);

    public Task<JsonObject> ReadInstanceStateAsync(string instance, CancellationToken cancellationToken = default)
        => Task.Run(() => WorkspaceOrThrow().State(instance), cancellationToken);

    public Task<JsonObject?> ReadReportAsync(string stamp, CancellationToken cancellationToken = default)
        => Task.Run(() => WorkspaceOrThrow().Report(stamp), cancellationToken);

    public Task<SchemaResponse> ReadSchemaAsync(string language = "zh-CN", CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            ConfigSchema schema = ConfigsOrThrow().Schema(language);
            return new SchemaResponse { Menu = schema.Menu, Args = schema.Args, Translations = schema.Translations };
        }, cancellationToken);

    public Task<ConfigResponse> ReadConfigAsync(string instance, CancellationToken cancellationToken = default)
        => Task.Run(() => ToResponse(ConfigsOrThrow().Get(instance)), cancellationToken);

    public Task<ConfigResponse> PatchConfigAsync(ConfigPatchRequest request, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            ArgumentNullException.ThrowIfNull(request);
            var changes = request.Changes.Select(change => new RuntimeConfigChange(change.Path, change.Value?.DeepClone())).ToArray();
            return ToResponse(ConfigsOrThrow().Patch(request.Instance, request.Revision, changes));
        }, cancellationToken);

    public Task<ConfigResponse> CreateInstanceAsync(InstanceCreateRequest request, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            ArgumentNullException.ThrowIfNull(request);
            return ToResponse(ConfigsOrThrow().Create(request.Instance, request.Source, request.ImportFile));
        }, cancellationToken);

    public Task DeleteInstanceAsync(InstanceDeleteRequest request, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            ArgumentNullException.ThrowIfNull(request);
            ConfigsOrThrow().Delete(request.Instance, request.Revision);
        }, cancellationToken);

    public Task SaveQueueAsync(JsonObject queue, CancellationToken cancellationToken = default)
        => Task.Run(() => WorkspaceOrThrow().SaveQueueRequest(new JsonObject { ["queue"] = queue.DeepClone() }), cancellationToken);

    public Task StartRunAsync(ControlRunRequest request, CancellationToken cancellationToken = default)
        => Task.Run(() => WorkspaceOrThrow().StartRun(ToJson(request)), cancellationToken);

    public Task StartTaskAsync(InstanceTaskRunRequest request, CancellationToken cancellationToken = default)
        => Task.Run(() => WorkspaceOrThrow().StartTask(new JsonObject
        {
            ["instance"] = request.Instance, ["task"] = request.Task,
            ["confirm_actions"] = request.ConfirmActions,
        }), cancellationToken);

    public Task<bool> RequestStopAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => WorkspaceOrThrow().RequestStop(), cancellationToken);

    public Task StartSchedulerAsync(InstanceSchedulerRunRequest request, CancellationToken cancellationToken = default)
        => Task.Run(() => WorkspaceOrThrow().StartScheduler(new JsonObject
        {
            ["instance"] = request.Instance, ["confirm_actions"] = request.ConfirmActions,
        }), cancellationToken);

    public Task<JsonObject> ReadStatisticsAsync(StatisticsRequest request, CancellationToken cancellationToken = default)
        => ReadHostAsync("statistics_report", new JsonObject
        {
            ["instance"] = request.Instance,
            ["category"] = request.Category,
            ["days"] = request.Days,
            ["month"] = request.Month,
            ["period"] = request.Period,
        }, cancellationToken);

    public Task<JsonObject> RefreshStatisticsLootAsync(string instance, CancellationToken cancellationToken = default)
        => ReadHostAsync("statistics_refresh_loot", new JsonObject { ["instance"] = instance }, cancellationToken);

    public Task<JsonObject> ReadMeowfficerAsync(MeowfficerRequest request, CancellationToken cancellationToken = default)
        => ReadHostAsync("meowfficer_report", new JsonObject { ["instance"] = request.Instance, ["limit"] = request.Limit }, cancellationToken);

    public Task<JsonObject> ClearMeowfficerAsync(string instance, CancellationToken cancellationToken = default)
        => ReadHostAsync("meowfficer_clear", new JsonObject { ["instance"] = instance }, cancellationToken);

    public Task<JsonObject> ValidateShopStrategyAsync(string script, CancellationToken cancellationToken = default)
        => ReadHostAsync("shop_strategy_validate", new JsonObject { ["script"] = script }, cancellationToken);

    public Task<InstanceImportSource> ImportInstanceAsync(InstanceImportRequest request, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            var imported = ConfigsOrThrow().Import(request.Name, request.Content);
            return new InstanceImportSource { Name = imported.Name, ModifiedAt = imported.ModifiedAt };
        }, cancellationToken);

    public Task<InstanceImportListResponse> ReadInstanceImportsAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => new InstanceImportListResponse
        {
            Sources = ConfigsOrThrow().ListImports().Select(item => new InstanceImportSource
                { Name = item.Name, ModifiedAt = item.ModifiedAt }).ToArray(),
        }, cancellationToken);

    public void Refresh()
    {
        if (_disposed || _configs is null)
        {
            _connected = false;
            _instances.Clear();
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        try
        {
            var items = WorkspaceOrThrow().Instances(_configs);
            _instances.Clear();
            foreach (var item in items)
            {
                _instances.Add(InstanceCardViewModel.Create(
                    item.Instance,
                    item.Status,
                    item.Server,
                    item.Serial ?? string.Empty, item.CurrentTask));
            }
            _connected = true;
        }
        catch (IOException) { _connected = false; _instances.Clear(); }
        catch (UnauthorizedAccessException) { _connected = false; _instances.Clear(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _workspace?.BeginShutdown().GetAwaiter().GetResult(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) { }
    }

    private ConfigWorkspace ConfigsOrThrow() => _configs ?? throw new InvalidOperationException("Alas.Core 配置工作区不可用");
    private ControlWorkspace WorkspaceOrThrow() => _workspace ?? throw new InvalidOperationException("Alas.Core 控制工作区不可用");

    private Task<JsonObject> ReadHostAsync(string operation, JsonObject arguments, CancellationToken cancellationToken)
        => Task.Run(() => WorkspaceOrThrow().ReadHostJson(operation, arguments), cancellationToken);

    private static ConfigResponse ToResponse(ConfigSnapshot snapshot) => new()
    {
        Instance = snapshot.Instance,
        Revision = snapshot.Revision,
        Values = snapshot.Values,
    };

    private static JsonObject ToJson(ControlRunRequest request)
        => JsonSerializer.SerializeToNode(request, ControlJsonContext.Default.ControlRunRequest)?.AsObject()
           ?? throw new InvalidOperationException("运行请求无法序列化");

    private static string? FindProjectRoot()
    {
        for (string? current = AppContext.BaseDirectory; current is not null; current = Directory.GetParent(current)?.FullName)
        {
            if (Directory.Exists(Path.Combine(current, ".runtime", "engine")) &&
                Directory.Exists(Path.Combine(current, "tools"))) return current;
        }
        string working = Directory.GetCurrentDirectory();
        return Directory.Exists(Path.Combine(working, ".runtime", "engine")) ? working : null;
    }
}
