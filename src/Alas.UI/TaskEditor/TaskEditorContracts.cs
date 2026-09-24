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

public sealed record MeowfficerScoreReport(string Instance, string GeneratedAt, int Count,
    IReadOnlyList<MeowfficerCat> Cats);
public sealed record MeowfficerCat(string Cat, IReadOnlyList<string>? Tags = null, int? Level = null,
    bool Fixed = false, bool Maxed = false, string? Note = null, string? Source = null,
    int? PointsSpent = null, IReadOnlyList<MeowfficerTalent>? Talents = null,
    IReadOnlyList<MeowfficerRubric>? Rubrics = null, MeowfficerAdvice? Advice = null);
public sealed record MeowfficerTalent(string Name, int? Level = null, string? Kind = null, bool Inferred = false);
public sealed record MeowfficerRubric(string Label, string? Tier = null, double? Score = null,
    double? X = null, double? Y = null, string? XLabel = null, string? YLabel = null,
    IReadOnlyList<string>? XHits = null, IReadOnlyList<string>? YHits = null,
    IReadOnlyList<string>? Notes = null, string? Source = null, bool Primary = false);
public sealed record MeowfficerAdvice(string Verdict, string Headline, string Reason,
    string? CostText = null, IReadOnlyList<string>? Targets = null);

public interface IMeowfficerReportBackend
{
    Task<MeowfficerScoreReport?> LoadAsync(string instance, CancellationToken cancellationToken);
    Task ClearAsync(string instance, CancellationToken cancellationToken);
}

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
