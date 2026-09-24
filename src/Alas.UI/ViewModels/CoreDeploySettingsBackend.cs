using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Alas.Contracts;
using Alas.UI.DeploySettings;
using Alas.UI.Settings;

namespace Alas.UI.ViewModels;

/// <summary>Translate the Core deployment schema; desktop remains an in-process capability call.</summary>
public sealed class CoreDeploySettingsBackend(IAlasUiBackend backend) : ISettingsBackend
{
    private Dictionary<string, string> _types = new(StringComparer.Ordinal);
    public async Task<SettingsSnapshot?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!backend.IsConnected)
            return new SettingsSnapshot([], DisconnectedSettingsBackend.Notice);
        var response = await backend.ReadDeploySettingsAsync(cancellationToken: cancellationToken);
        var groups = response.Groups.Select(node =>
        {
            var group = node!.AsObject();
            var fields = group["fields"]!.AsArray().Select(node =>
            {
                var field = node!.AsObject();
                string kind = Text(field["type"]);
                return new SettingsField(Text(field["key"]), Text(field["label"]), Text(field["value"]),
                    !response.Demo, kind, field["options"]?.AsArray().Select(Text).ToArray(),
                    Help: Regex.Replace(Text(field["help"]), "<[^>]*>", ""), IsInteger: kind == "int");
            }).ToArray();
            return new SettingsGroup(Text(group["label"]), fields, Text(group["key"]));
        }).ToArray();
        _types = groups.SelectMany(group => group.Fields).ToDictionary(field => field.Key, field => field.Kind, StringComparer.Ordinal);
        return new SettingsSnapshot(groups);
    }

    public async Task SaveAsync(SettingsChange change, CancellationToken cancellationToken = default)
    {
        if (!backend.IsConnected) throw new DeployTransportException(DisconnectedSettingsBackend.Notice, retryable: true);
        try
        {
            if (!_types.TryGetValue(change.Key, out var kind)) throw new ArgumentException("部署字段尚未加载");
            // Boolean fields require a JSON boolean; text (including numeric-looking text) stays text.
            JsonNode? value = kind == "bool" ? JsonValue.Create(bool.Parse(change.Value)) : JsonValue.Create(change.Value);
            await backend.PatchDeploySettingsAsync(new DeploySettingsPatchRequest
            {
                Values = new JsonObject { [change.Key] = value },
            }, cancellationToken);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            bool retryable = failure is IOException or TimeoutException ||
                failure is HttpRequestException http && (http.StatusCode is null || (int)http.StatusCode >= 500);
            throw new DeployTransportException(failure.Message, retryable);
        }
    }

    private static string Text(JsonNode? value) => value is null ? string.Empty :
        value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString();
}
