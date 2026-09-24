using System.Text.Json.Nodes;

namespace Alas.UI.TaskEditor;

/// <summary>The host supplies transport; this UI never runs device or task logic itself.</summary>
public interface ITaskEditorBackend
{
    Task<JsonObject> SaveAsync(string instance, string revision,
        IReadOnlyList<TaskFieldChange> changes, CancellationToken cancellationToken);
    Task RunAsync(string instance, string task, CancellationToken cancellationToken);
    Task<ScriptValidation> ValidateScriptAsync(string instance, string task, string script,
        CancellationToken cancellationToken);
}

public sealed record TaskFieldChange(string Path, JsonNode? Value);
public sealed record ScriptDiagnostic(string Message, int? Line = null, int? Column = null,
    string Severity = "error");
public sealed record ScriptValidation(bool Valid, IReadOnlyList<ScriptDiagnostic> Diagnostics, string Summary = "");

/// <summary>A transport adapter maps a revision conflict to this exception, without retrying the write.</summary>
public sealed class TaskEditorConflictException(JsonObject currentConfig)
    : Exception("配置已在其他位置修改。请检查冲突并选择要保留的值。")
{
    public JsonObject CurrentConfig { get; } = (JsonObject)currentConfig.DeepClone();
}

public enum TaskFieldKind { Text, Number, Boolean, Select, MultiSelect, Multiline, DateTime, Password, Json, Yaml, Lua, Storage }

public sealed record TaskFieldOption(JsonNode? Value, string Label)
{
    public override string ToString() => Label;
}
