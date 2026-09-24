using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Alas.Contracts;
using Alas.UI.TaskEditor;

namespace Alas.UI.ViewModels;

/// <summary>
/// Adapts the shared task editor to the application capability boundary.
/// The adapter contains no transport code: desktop reaches Alas.Core directly
/// through <see cref="IAlasControlBackend"/> and browser builds use its HTTP
/// implementation behind the same interface.
/// </summary>
public sealed class CoreTaskEditorBackend(IAlasControlBackend backend) : ITaskEditorBackend
{
    private readonly IAlasControlBackend _backend = backend ?? throw new ArgumentNullException(nameof(backend));

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
        => _backend.StartTaskAsync(new InstanceTaskRunRequest
        {
            Instance = instance,
            Task = task,
            ConfirmActions = true,
        }, cancellationToken);

    public async Task<ScriptValidation> ValidateScriptAsync(string instance, string task, string script,
        CancellationToken cancellationToken)
    {
        JsonObject response = await _backend.ValidateShopStrategyAsync(script, cancellationToken)
            .ConfigureAwait(false);
        if (response["valid"] is not JsonValue validValue || !validValue.TryGetValue<bool>(out bool valid)
            || response["diagnostics"] is not JsonArray)
            throw new InvalidOperationException("脚本校验响应缺少 valid/diagnostics 合同");
        var diagnostics = new List<ScriptDiagnostic>();
        if (response["diagnostics"] is JsonArray items)
        {
            foreach (var node in items)
            {
                if (node is not JsonObject item || item["message"] is not JsonValue messageValue ||
                    !messageValue.TryGetValue<string>(out var message) ||
                    item["code"] is not JsonValue codeValue || !codeValue.TryGetValue<string>(out var code))
                    throw new InvalidOperationException("脚本诊断缺少 code/message 合同");
                diagnostics.Add(new ScriptDiagnostic(message,
                    item["line"]?.GetValue<int>(), item["column"]?.GetValue<int>(),
                    "error", code));
            }
        }
        string summary = valid ? "脚本校验通过" : "脚本校验失败";
        return new ScriptValidation(valid, diagnostics, summary);
    }


}

/// <summary>Maps the shared report view to the same Core capability boundary.</summary>
public sealed class CoreMeowfficerReportBackend(IAlasControlBackend backend) : IMeowfficerReportBackend
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
