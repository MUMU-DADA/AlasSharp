using System.Text.Json;
using Alas.Contracts;
using Alas.Engine.Runtime;

namespace Alas.UI.Desktop;

internal sealed partial class DirectEngineBackend
{
    private EngineSettingsWorkspace EngineSettingsOrThrow() => _settings ?? throw new InvalidOperationException("Engine 设置不可用");

    public Task<EngineSettingsResponse> ReadEngineSettingsAsync(string language = "zh-CN", CancellationToken cancellationToken = default)
        => Task.Run(() => EngineSettingsOrThrow().Read(language).Deserialize(ControlJsonContext.Default.EngineSettingsResponse)!, cancellationToken);

    public Task<EngineSettingsPatchResponse> PatchEngineSettingsAsync(EngineSettingsPatchRequest request, CancellationToken cancellationToken = default)
        => Task.Run(() => EngineSettingsOrThrow().Patch(request.Values).Deserialize(ControlJsonContext.Default.EngineSettingsPatchResponse)!, cancellationToken);

    public Task<StartupRunResponse> ReadStartupRunAsync(string instance, CancellationToken cancellationToken = default)
        => Task.Run(() => EngineSettingsOrThrow().ReadStartup(instance).Deserialize(ControlJsonContext.Default.StartupRunResponse)!, cancellationToken);

    public Task<StartupRunResponse> SetStartupRunAsync(StartupRunRequest request, CancellationToken cancellationToken = default)
        => Task.Run(() => EngineSettingsOrThrow().SetStartup(request.Instance, request.Enabled).Deserialize(ControlJsonContext.Default.StartupRunResponse)!, cancellationToken);
}
