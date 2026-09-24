using System.Text.Json.Nodes;
using Alas.Contracts;

namespace Alas.UI.ViewModels;

/// <summary>
/// Shared UI capabilities. Desktop calls Alas.Core in-process; browser and
/// remote hosts may provide a network adapter. Pages never depend on HTTP.
/// </summary>
public interface IAlasControlBackend
{
    Task<JsonObject> ReadStateAsync(CancellationToken cancellationToken = default);
    Task<JsonObject?> ReadReportAsync(string stamp, CancellationToken cancellationToken = default);
    Task<SchemaResponse> ReadSchemaAsync(string language = "zh-CN", CancellationToken cancellationToken = default);
    Task<ConfigResponse> ReadConfigAsync(string instance, CancellationToken cancellationToken = default);
    Task<ConfigResponse> PatchConfigAsync(ConfigPatchRequest request, CancellationToken cancellationToken = default);
    Task<ConfigResponse> CreateInstanceAsync(InstanceCreateRequest request, CancellationToken cancellationToken = default);
    Task DeleteInstanceAsync(InstanceDeleteRequest request, CancellationToken cancellationToken = default);
    Task<InstanceImportSource> ImportInstanceAsync(InstanceImportRequest request, CancellationToken cancellationToken = default);
    Task<InstanceImportListResponse> ReadInstanceImportsAsync(CancellationToken cancellationToken = default);
    Task SaveQueueAsync(JsonObject queue, CancellationToken cancellationToken = default);
    Task StartRunAsync(ControlRunRequest request, CancellationToken cancellationToken = default);
    Task StartTaskAsync(InstanceTaskRunRequest request, CancellationToken cancellationToken = default);
    Task StartSchedulerAsync(InstanceSchedulerRunRequest request, CancellationToken cancellationToken = default);
    Task<bool> RequestStopAsync(CancellationToken cancellationToken = default);
    Task<JsonObject> ReadStatisticsAsync(StatisticsRequest request, CancellationToken cancellationToken = default);
    Task<JsonObject> RefreshStatisticsLootAsync(string instance, CancellationToken cancellationToken = default);
    Task<JsonObject> ReadMeowfficerAsync(MeowfficerRequest request, CancellationToken cancellationToken = default);
    Task<JsonObject> ClearMeowfficerAsync(string instance, CancellationToken cancellationToken = default);
    Task<JsonObject> ValidateShopStrategyAsync(string script, CancellationToken cancellationToken = default);
}

public interface IAlasUiBackend : IRefreshableInstanceSource, IAlasControlBackend, IDisposable
{
}
