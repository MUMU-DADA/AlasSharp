using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Alas.Contracts;
using Alas.UI.TaskEditor;

namespace Alas.UI.ViewModels;

/// <summary>
/// Adapts the shared task editor to the application capability boundary.
/// The adapter contains no transport code: desktop reaches Alas.Engine directly
/// through <see cref="IAlasControlBackend"/> and browser builds use its HTTP
/// implementation behind the same interface.
/// </summary>
public sealed class EngineTaskEditorBackend(IAlasControlBackend backend) : ITaskEditorBackend
{
    private readonly IAlasControlBackend _backend = backend ?? throw new ArgumentNullException(nameof(backend));

    public async Task<JsonObject> SaveQueueAsync(string instance, string kind, JsonObject input,
        CancellationToken cancellationToken)
    {
        JsonObject queue = Queue(instance, kind, input);
        await _backend.SaveQueueAsync(queue, cancellationToken).ConfigureAwait(false);
        return queue;
    }

    public Task RunQueueAsync(string instance, string kind, JsonObject input,
        CancellationToken cancellationToken)
        => _backend.StartRunAsync(new ControlRunRequest
        {
            Instance = instance,
            Mode = ControlRunMode.Actions,
            ConfirmActions = true,
            Queue = Queue(instance, kind, input),
        }, cancellationToken);

    private static JsonObject Queue(string instance, string kind, JsonObject input) => new()
    {
        ["tasks"] = new JsonArray(new JsonObject
        {
            ["id"] = kind, ["kind"] = kind, ["input"] = input.DeepClone(),
            ["required"] = true, ["instance"] = instance,
        })
    };

    public async Task<JsonObject> SaveAsync(string instance, string revision,
        IReadOnlyList<TaskFieldChange> changes, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instance);
        var response = await _backend.PatchConfigAsync(new ConfigPatchRequest
        {
            Instance = instance,
            Revision = revision,
            Changes = changes.Select(change => new ConfigChange
            {
                Path = change.Path,
                Value = change.Value?.DeepClone(),
            }).ToArray(),
        }, cancellationToken).ConfigureAwait(false);
        return new JsonObject
        {
            ["instance"] = response.Instance,
            ["revision"] = response.Revision,
            ["values"] = response.Values.DeepClone(),
        };
    }

    public Task RunAsync(string instance, string task, CancellationToken cancellationToken)
        => RunQueueAsync(instance, task, new JsonObject(), cancellationToken);

}

/// <summary>Maps the shared report view to the Engine capability boundary.</summary>
public sealed class EngineMeowfficerReportBackend(IAlasControlBackend backend) : IMeowfficerReportBackend
{
    private readonly IAlasControlBackend _backend = backend ?? throw new ArgumentNullException(nameof(backend));

    public async Task<MeowfficerScoreReport?> LoadAsync(string instance, CancellationToken cancellationToken)
    {
        JsonObject response = await _backend.ReadMeowfficerAsync(new MeowfficerRequest { Instance = instance }, cancellationToken)
            .ConfigureAwait(false);
        if (response["instance"]?.GetValue<string>() != instance || response["cats"] is not JsonArray)
            throw new InvalidOperationException("评分报告响应不符合所选实例合同");
        return response.Deserialize(TaskReportJsonContext.Default.MeowfficerScoreReport)
            ?? throw new InvalidOperationException("评分报告响应为空");
    }

    public async Task ClearAsync(string instance, CancellationToken cancellationToken)
        => _ = await _backend.ClearMeowfficerAsync(instance, cancellationToken).ConfigureAwait(false);

}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MeowfficerScoreReport))]
internal partial class TaskReportJsonContext : JsonSerializerContext;
