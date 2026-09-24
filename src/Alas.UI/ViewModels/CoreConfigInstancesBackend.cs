using System.Text.Json;
using Alas.Contracts;
using Alas.UI.ConfigManager;

namespace Alas.UI.ViewModels;

public sealed class CoreConfigInstancesBackend(IAlasUiBackend backend) : IConfigInstancesBackend
{
    public async Task<IReadOnlyList<ConfigInstanceInfo>> ListInstancesAsync(CancellationToken cancellationToken = default)
    {
        var result = await backend.ReadInstancesAsync(cancellationToken);
        var translations = result.Instances.Count == 0 ? null :
            (await backend.ReadSchemaAsync(cancellationToken: cancellationToken)).Translations;
        return result.Instances.Select(item => new ConfigInstanceInfo(item.Instance,
            ServerLabel(item.Server, translations), item.Serial ?? "", item.Status)).ToArray();
    }

    private static string ServerLabel(string? server, System.Text.Json.Nodes.JsonObject? translations)
    {
        if (string.IsNullOrEmpty(server) || server == "disabled") return "";
        string key = $"Emulator.ServerName.{server}";
        System.Text.Json.Nodes.JsonNode? value = translations?[key];
        if (value is null)
        {
            value = translations;
            foreach (string part in key.Split('.')) value = (value as System.Text.Json.Nodes.JsonObject)?[part];
        }
        return value is System.Text.Json.Nodes.JsonValue text && text.TryGetValue<string>(out var label)
            ? label : server;
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
