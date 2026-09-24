using System.Text.Json;
using Alas.Contracts;
using Alas.Runtime;

namespace Alas.UI.Desktop;

internal sealed partial class DirectCoreBackend
{
    private DeploySettingsWorkspace DeployOrThrow() => _deploy ?? throw new InvalidOperationException("部署设置不可用");

    public Task<DeploySettingsResponse> ReadDeploySettingsAsync(string language = "zh-CN", CancellationToken cancellationToken = default)
        => Task.Run(() => DeployOrThrow().Read(language).Deserialize(ControlJsonContext.Default.DeploySettingsResponse)!, cancellationToken);

    public Task<DeploySettingsPatchResponse> PatchDeploySettingsAsync(DeploySettingsPatchRequest request, CancellationToken cancellationToken = default)
        => Task.Run(() => DeployOrThrow().Patch(request.Values).Deserialize(ControlJsonContext.Default.DeploySettingsPatchResponse)!, cancellationToken);

    public Task<StartupRunResponse> ReadStartupRunAsync(string instance, CancellationToken cancellationToken = default)
        => Task.Run(() => DeployOrThrow().ReadStartup(instance).Deserialize(ControlJsonContext.Default.StartupRunResponse)!, cancellationToken);

    public Task<StartupRunResponse> SetStartupRunAsync(StartupRunRequest request, CancellationToken cancellationToken = default)
        => Task.Run(() => DeployOrThrow().SetStartup(request.Instance, request.Enabled).Deserialize(ControlJsonContext.Default.StartupRunResponse)!, cancellationToken);
}
