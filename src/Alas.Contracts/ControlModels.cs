using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Alas.Contracts;

public static class ControlProtocol
{
    public const int MaxRequestBodyBytes = 1024 * 1024;
    public const string TokenHeader = "X-Alas-Token";
    public const string EventsContract = "control-state/1";

    public static bool IsEventCursor(string? value) => value is { Length: >= 34 and <= 53 }
        && value[32] == ':' && value.Take(32).All(char.IsAsciiHexDigit)
        && value.Skip(33).All(char.IsAsciiDigit)
        && long.TryParse(value.AsSpan(33), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out long revision) && revision > 0;
}

/// <summary>Every event replaces the entire snapshot; never append its recent_logs as deltas.</summary>
public sealed record ControlStateUpdate(string Cursor, bool Reset, ControlState State);

// These are transport envelopes only. Queue inputs, reports and task evidence are
// preserved verbatim; interpreting them remains the runtime's responsibility.
public sealed record ControlState
{
    public required string Token { get; init; }
    public required JsonObject Queue { get; init; }
    public required ControlActivity Active { get; init; }
    public JsonObject? Report { get; init; }
    public required JsonArray LiveTasks { get; init; }
    public required JsonArray RecentLogs { get; init; }
    public required JsonObject Runs { get; init; }
}

public sealed record ControlActivity
{
    // "completed" is worker completion, never proof of a successful game task.
    public required string Status { get; init; }
    public string? Mode { get; init; }
    public string? StartedAt { get; init; }
    public string? FinishedAt { get; init; }
    public bool StopRequested { get; init; }
    public string? Error { get; init; }
    public string? RunDirectory { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<ControlRunMode>))]
public enum ControlRunMode
{
    [JsonStringEnumMemberName("dry_run")] DryRun,
    [JsonStringEnumMemberName("read_only")] ReadOnly,
    [JsonStringEnumMemberName("actions")] Actions,
}

public sealed record ControlQueueRequest
{
    public required JsonObject Queue { get; init; }
}

public sealed record ControlRunRequest
{
    public required JsonObject Queue { get; init; }
    public ControlRunMode Mode { get; init; } = ControlRunMode.DryRun;
    public bool ConfirmActions { get; init; }
    public string? Serial { get; init; }
    public double MaxSeconds { get; init; } = 1500;
    public int MaxRounds { get; init; } = 20;
    public bool Resume { get; init; }
    public bool ContinueOnError { get; init; }
}

public sealed record ControlAcknowledgement
{
    public required bool Ok { get; init; }
}

public sealed record ControlError
{
    public required string Error { get; init; }
}

public sealed record InstanceSummary
{
    public required string Instance { get; init; }
    public required string Revision { get; init; }
    public string? Serial { get; init; }
    public string? Server { get; init; }
}

public sealed record InstanceListResponse
{
    public required IReadOnlyList<InstanceSummary> Instances { get; init; }
}

public sealed record ConfigResponse
{
    public required string Instance { get; init; }
    public required string Revision { get; init; }
    public required JsonObject Values { get; init; }
}

public sealed record SchemaResponse
{
    public required JsonObject Menu { get; init; }
    public required JsonObject Args { get; init; }
    public required JsonObject Translations { get; init; }
}

public sealed record ConfigChange
{
    public required string Path { get; init; }
    public required JsonNode? Value { get; init; }
}

public sealed record ConfigPatchRequest
{
    public required string Instance { get; init; }
    public string? Revision { get; init; }
    public required IReadOnlyList<ConfigChange> Changes { get; init; }
}

public sealed record InstanceCreateRequest
{
    public required string Instance { get; init; }
    public string? Source { get; init; }
    public string? ImportFile { get; init; }
}

public sealed record InstanceDeleteRequest
{
    public required string Instance { get; init; }
    public required string Revision { get; init; }
}

public sealed record StatisticsRequest
{
    public required string Instance { get; init; }
    public required string Category { get; init; }
    public int Days { get; init; } = 7;
    public string? Month { get; init; }
    public string Period { get; init; } = "month";
}

public sealed record MeowfficerRequest
{
    public required string Instance { get; init; }
    public int Limit { get; init; } = 100;
}

// Explicit generated metadata also works when WASM trimming disables reflection.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ControlState))]
[JsonSerializable(typeof(ControlQueueRequest))]
[JsonSerializable(typeof(ControlRunRequest))]
[JsonSerializable(typeof(ControlAcknowledgement))]
[JsonSerializable(typeof(ControlError))]
[JsonSerializable(typeof(InstanceSummary))]
[JsonSerializable(typeof(InstanceListResponse))]
[JsonSerializable(typeof(ConfigResponse))]
[JsonSerializable(typeof(SchemaResponse))]
[JsonSerializable(typeof(ConfigPatchRequest))]
[JsonSerializable(typeof(ConfigChange))]
[JsonSerializable(typeof(InstanceCreateRequest))]
[JsonSerializable(typeof(InstanceDeleteRequest))]
[JsonSerializable(typeof(StatisticsRequest))]
[JsonSerializable(typeof(MeowfficerRequest))]
[JsonSerializable(typeof(IReadOnlyList<InstanceSummary>))]
[JsonSerializable(typeof(IReadOnlyList<ConfigChange>))]
[JsonSerializable(typeof(JsonObject))]
public partial class ControlJsonContext : JsonSerializerContext;
