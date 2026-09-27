using System.Text.Json;
using Alas.Contracts;
using Alas.UI.ConfigManager;

namespace Alas.UI.ViewModels;

public sealed class EngineConfigInstancesBackend(IAlasUiBackend backend) : IConfigInstancesBackend
{
    public async Task<IReadOnlyList<ConfigInstanceInfo>> ListInstancesAsync(CancellationToken cancellationToken = default)
    {
        var result = await backend.ReadInstancesAsync(cancellationToken);
        return result.Instances.Select(item => new ConfigInstanceInfo(item.Instance,
            ServerLabel(item.Server), item.Serial ?? "", item.Status)).ToArray();
    }

    private static string ServerLabel(string? server)
    {
        if (string.IsNullOrEmpty(server) || server == "disabled") return "";
        return server switch
        {
            "cn" => "中国大陆",
            "en" => "国际服",
            "jp" => "日本",
            "tw" => "中国台湾",
            _ => server,
        };
    }

    public async Task<ConfigContent> ReadConfigAsync(string instance, CancellationToken cancellationToken = default)
    {
        var result = await backend.ReadConfigAsync(instance, cancellationToken);
        return new ConfigContent(result.Instance, result.Revision,
            result.Values.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public async Task<string> CreateInstanceAsync(string name, string? source, string? importFile,
        CancellationToken cancellationToken = default)
    {
        var result = await backend.CreateInstanceAsync(new InstanceCreateRequest
            { Instance = name, Source = source, ImportFile = importFile }, cancellationToken);
        backend.Refresh();
        return result.Instance;
    }

    public async Task ImportConfigAsync(string name, string content, CancellationToken cancellationToken = default)
        => _ = await backend.ImportInstanceAsync(new InstanceImportRequest { Name = name, Content = content }, cancellationToken);

    public async Task<IReadOnlyList<ConfigImportSource>> ListImportsAsync(CancellationToken cancellationToken = default)
        => (await backend.ReadInstanceImportsAsync(cancellationToken)).Sources
            .Select(item => new ConfigImportSource(item.Name, item.ModifiedAt)).ToArray();

    public async Task DeleteInstanceAsync(string instance, string revision, CancellationToken cancellationToken = default)
    {
        await backend.DeleteInstanceAsync(new InstanceDeleteRequest { Instance = instance, Revision = revision }, cancellationToken);
        backend.Refresh();
    }
}
