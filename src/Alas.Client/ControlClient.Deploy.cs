using System.Net;
using Alas.Contracts;

namespace Alas.Client;

public sealed partial class ControlClient
{
    public async Task<DeploySettingsResponse> GetDeploySettingsAsync(string language = "zh-CN", CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_endpoint, "api/settings?language=" + Uri.EscapeDataString(language)));
        return await SendAsync(request, HttpStatusCode.OK, ControlJsonContext.Default.DeploySettingsResponse, cancellationToken).ConfigureAwait(false);
    }

    public Task<DeploySettingsPatchResponse> PatchDeploySettingsAsync(DeploySettingsPatchRequest request, CancellationToken cancellationToken = default)
        => WriteReadAsync("api/settings", request, ControlJsonContext.Default.DeploySettingsPatchRequest,
            ControlJsonContext.Default.DeploySettingsPatchResponse, HttpMethod.Patch, HttpStatusCode.OK, cancellationToken);

    public async Task<StartupRunResponse> GetStartupRunAsync(string instance, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_endpoint, "api/startup?instance=" + Uri.EscapeDataString(instance)));
        return await SendAsync(request, HttpStatusCode.OK, ControlJsonContext.Default.StartupRunResponse, cancellationToken).ConfigureAwait(false);
    }

    public Task<StartupRunResponse> SetStartupRunAsync(StartupRunRequest request, CancellationToken cancellationToken = default)
        => WriteReadAsync("api/startup", request, ControlJsonContext.Default.StartupRunRequest,
            ControlJsonContext.Default.StartupRunResponse, HttpMethod.Post, HttpStatusCode.OK, cancellationToken);
}
