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

// Explicit generated metadata also works when WASM trimming disables reflection.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ControlState))]
[JsonSerializable(typeof(ControlQueueRequest))]
[JsonSerializable(typeof(ControlRunRequest))]
[JsonSerializable(typeof(ControlAcknowledgement))]
[JsonSerializable(typeof(ControlError))]
[JsonSerializable(typeof(JsonObject))]
public partial class ControlJsonContext : JsonSerializerContext;
