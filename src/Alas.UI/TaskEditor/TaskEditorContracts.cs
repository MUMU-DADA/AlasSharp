using System.Text.Json.Nodes;

namespace Alas.UI.TaskEditor;

/// <summary>The host supplies transport; this UI never runs device or task logic itself.</summary>
public interface ITaskEditorBackend
{
    /// <summary>Persist one Engine queue entry; the Engine owns input validation.</summary>
    Task<JsonObject> SaveQueueAsync(string instance, string kind, JsonObject input,
        CancellationToken cancellationToken)
        => Task.FromException<JsonObject>(new NotSupportedException("此编辑器后端未接入 Engine 队列保存"));
    /// <summary>Submit the same queue entry for action execution.</summary>
    Task RunQueueAsync(string instance, string kind, JsonObject input,
        CancellationToken cancellationToken)
        => Task.FromException(new NotSupportedException("此编辑器后端未接入 Engine 队列运行"));
}

public sealed record MeowfficerScoreReport(string Instance, string GeneratedAt, int Count,
    IReadOnlyList<MeowfficerCat> Cats);
public sealed record MeowfficerCat(string Cat, IReadOnlyList<string>? Tags = null, int? Level = null,
    bool Fixed = false, bool Maxed = false, string? Note = null, string? Source = null,
    int? PointsSpent = null, IReadOnlyList<MeowfficerTalent>? Talents = null,
    IReadOnlyList<MeowfficerRubric>? Rubrics = null, MeowfficerAdvice? Advice = null, string? Primary = null);
public sealed record MeowfficerTalent(string Name, int? Level = null, string? Kind = null, bool Inferred = false);
public sealed record MeowfficerRubric(string Label, string? Tier = null, double? Score = null,
    double? X = null, double? Y = null, string? XLabel = null, string? YLabel = null,
    IReadOnlyList<string>? XHits = null, IReadOnlyList<string>? YHits = null,
    IReadOnlyList<string>? Notes = null, string? Source = null, bool Primary = false, string? Key = null);
public sealed record MeowfficerAdvice(string Verdict, string Headline, string Reason,
    string? CostText = null, IReadOnlyList<string>? Targets = null,
    string? Label = null, double? Score = null, string? Tier = null, double? Cost = null,
    bool? CostEstimated = null, int? PointsSpent = null);

public interface IMeowfficerReportBackend
{
    Task<MeowfficerScoreReport?> LoadAsync(string instance, CancellationToken cancellationToken);
    Task ClearAsync(string instance, CancellationToken cancellationToken);
}

public enum TaskFieldKind { Json }

public sealed record TaskFieldOption(JsonNode? Value, string Label)
{
    public override string ToString() => Label;
}
