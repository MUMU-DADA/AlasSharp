using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tasks;

public sealed record TaskQueueOptions(string Artifacts, bool DryRun = false, bool ContinueOnFailure = false, string? ResumeDirectory = null);
public sealed record TaskQueueResult(string Directory, IReadOnlyList<TaskResult> Tasks, bool Failed);

/// <summary>New task composition. A single lazy session, typed runners and evidence-bound resumption.</summary>
public sealed class TaskQueue
{
    public static JsonSerializerOptions Json { get; } = CreateJson();
    private readonly IReadOnlyDictionary<string, ITaskRunner> _runners;
    public TaskQueue(IEnumerable<ITaskRunner>? runners = null)
        => _runners = (runners ?? [new ObserveTask(), new NavigateTask(), new DataKeyTask()]).ToDictionary(r => r.Kind, StringComparer.Ordinal);
    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }
    public static async Task<TaskRequest[]> ReadAsync(string file, CancellationToken token = default)
        => JsonSerializer.Deserialize<TaskRequest[]>(await File.ReadAllTextAsync(file, token), Json)
            ?? throw new InvalidDataException("Task queue must be an array");
    public async Task<TaskQueueResult> RunAsync(IReadOnlyList<TaskRequest> requests, EngineSessionOptions sessionOptions,
        TaskQueueOptions options, CancellationToken token = default)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var request in requests)
        {
            if (request is null || request.Id is null || !Regex.IsMatch(request.Id, @"\A[a-zA-Z0-9_-]{1,64}\z") || !seen.Add(request.Id))
                throw new ArgumentException("Task ids must be unique portable identifiers");
            if (string.IsNullOrWhiteSpace(request.Kind) || !double.IsFinite(request.TimeoutSeconds) || request.TimeoutSeconds <= 0 || request.TimeoutSeconds > int.MaxValue / 1000.0)
                throw new ArgumentException("Task kind or timeout is invalid");
            if (request.DependsOn?.Any(id => id == request.Id || !seen.Contains(id)) == true)
                throw new ArgumentException("Dependencies must refer to earlier tasks");
        }
        // Snapshot mutable JSON inputs before execution and hash the session identity, without storing private paths/serial in state.
        string queueJson = JsonSerializer.Serialize(requests, Json);
        requests = JsonSerializer.Deserialize<TaskRequest[]>(queueJson, Json)!;
        string fingerprint = Hash(Encoding.UTF8.GetBytes(queueJson + JsonSerializer.Serialize(sessionOptions, Json)));
        if (options.ResumeDirectory is not null && options.DryRun) throw new ArgumentException("Dry run cannot mutate resume state");
        string directory = options.ResumeDirectory is null
            ? Path.Combine(Path.GetFullPath(options.Artifacts), Guid.NewGuid().ToString("N")) : Path.GetFullPath(options.ResumeDirectory);
        if (options.ResumeDirectory is null) Directory.CreateDirectory(directory);
        using var exclusive = new FileStream(Path.Combine(directory, "queue.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var completed = new Dictionary<string, CompletedTask>(StringComparer.Ordinal);
        if (options.ResumeDirectory is not null)
        {
            var envelope = JsonSerializer.Deserialize<StateEnvelope>(await File.ReadAllTextAsync(Path.Combine(directory, "state.json"), token), Json)
                ?? throw new InvalidDataException("Resume state is missing");
            var state = envelope.State;
            if (Hash(JsonSerializer.SerializeToUtf8Bytes(state, Json)) != envelope.Sha256)
                throw new InvalidDataException("Resume state checksum changed");
            if (state.Fingerprint != fingerprint) throw new InvalidDataException("Resume queue or session identity changed");
            if (await File.ReadAllTextAsync(Path.Combine(directory, "queue.json"), token) != queueJson)
                throw new InvalidDataException("Stored queue changed");
            foreach (var pair in state.Completed)
            {
                var expected = requests.FirstOrDefault(r => r.Id == pair.Key);
                if (expected is null || pair.Value is null || !Regex.IsMatch(pair.Value.Artifact, @"\Aattempt-[0-9a-f]{32}/[0-9]{4,}-[a-zA-Z0-9_-]{1,64}/task\.json\z"))
                    throw new InvalidDataException("Invalid completed task reference");
                byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(directory, pair.Value.Artifact), token);
                var result = JsonSerializer.Deserialize<TaskResult>(bytes, Json);
                if (Hash(bytes) != pair.Value.Sha256 || result?.Id != pair.Key || result.Kind != expected.Kind ||
                    result.Outcome != TaskOutcome.Succeeded || result.Error is not null || result.FailureFrames is { Length: > 0 })
                    throw new InvalidDataException("Resume completion evidence is missing or changed");
                string taskDirectory = Path.GetDirectoryName(Path.Combine(directory, pair.Value.Artifact))!;
                var actualEvidence = await EvidenceHashesAsync(taskDirectory, token);
                if (pair.Value.Files.Count != actualEvidence.Count || pair.Value.Files.Any(p => !actualEvidence.TryGetValue(p.Key, out var hash) || hash != p.Value))
                    throw new InvalidDataException("Resume task artifacts are missing or changed");
                if (await File.ReadAllTextAsync(Path.Combine(taskDirectory, "request.json"), token) != JsonSerializer.Serialize(expected, Json))
                    throw new InvalidDataException("Resume task request changed");
                if (expected.DependsOn?.Any(id => !state.Completed.ContainsKey(id)) == true)
                    throw new InvalidDataException("Resume completion dependencies are missing");
                completed.Add(pair.Key, pair.Value);
            }
        }
        else await File.WriteAllTextAsync(Path.Combine(directory, "queue.json"), queueJson);
        var capabilities = new TaskCapabilities(sessionOptions.AllowActions, sessionOptions.ModelDirectory is not null);
        string attempt = "attempt-" + Guid.NewGuid().ToString("N");
        var results = new List<TaskResult>();
        var satisfied = new HashSet<string>(completed.Keys, StringComparer.Ordinal);
        EngineSession? session = null;
        bool stop = false, failed = false;
        try
        {
            for (int i = 0; i < requests.Count; i++)
            {
                var request = requests[i];
                string relative = $"{attempt}/{i:D4}-{request.Id}";
                string artifact = Path.Combine(directory, relative);
                Directory.CreateDirectory(artifact);
                await WriteAsync(Path.Combine(artifact, "request.json"), request);
                TaskResult result;
                bool executed = false;
                var elapsed = Stopwatch.StartNew();
                if (completed.TryGetValue(request.Id, out var prior))
                    result = new(request.Id, request.Kind, TaskOutcome.Skipped, "previously_completed",
                        new JsonObject { ["sourceArtifact"] = prior.Artifact, ["sourceSha256"] = prior.Sha256 });
                else if (token.IsCancellationRequested || stop)
                    result = new(request.Id, request.Kind, TaskOutcome.Skipped, token.IsCancellationRequested ? "cancelled_before_task" : "previous_failure");
                else if (request.DependsOn?.Any(id => !satisfied.Contains(id)) == true)
                    result = new(request.Id, request.Kind, TaskOutcome.Skipped, "dependency_not_completed");
                else if (!_runners.TryGetValue(request.Kind, out var runner))
                    result = new(request.Id, request.Kind, TaskOutcome.Refused, "unsupported_task_kind");
                else
                {
                    try
                    {
                        runner.Validate(request.Input);
                        var missing = runner.Preconditions(request, capabilities);
                        if (missing.Count > 0)
                            result = new(request.Id, request.Kind, TaskOutcome.Skipped, "preconditions_unmet",
                                new JsonObject { ["unmet"] = new JsonArray(missing.Select(s => JsonValue.Create(s)).ToArray()) });
                        else if (options.DryRun) result = new(request.Id, request.Kind, TaskOutcome.DryRun, "validated_without_device");
                        else if (runner.RequiresActions && !capabilities.AllowActions)
                            result = new(request.Id, request.Kind, TaskOutcome.Refused, "actions_disabled");
                        else
                        {
                            session ??= new EngineSession(sessionOptions);
                            var context = session.BeginTask(TimeSpan.FromSeconds(request.TimeoutSeconds));
                            executed = true;
                            using var deadline = new CancellationTokenSource(context.Timeout);
                            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
                            try { result = await runner.RunAsync(request, context, limit.Token); limit.Token.ThrowIfCancellationRequested(); }
                            catch (OperationCanceledException error) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
                            { throw new TimeoutException("Task deadline expired", error); }
                            if (result.Id != request.Id || result.Kind != request.Kind || !Enum.IsDefined(result.Outcome))
                                throw new InvalidDataException("Task runner returned inconsistent identity or outcome");
                        }
                    }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        result = new(request.Id, request.Kind, executed ? TaskOutcome.Failed : TaskOutcome.Refused,
                            error is OperationCanceledException ? "cancelled_during_task" : error.GetType().Name, Error: error.ToString());
                    }
                }
                if (executed && session is not null)
                {
                    var evidence = await session.SaveEvidenceAsync(artifact, result.Outcome == TaskOutcome.Failed);
                    var details = result.Evidence?.DeepClone().AsObject() ?? new JsonObject();
                    details["boundary"] = JsonSerializer.SerializeToNode(evidence, Json);
                    result = result with { Evidence = details,
                        FailureFrames = result.Outcome == TaskOutcome.Failed && evidence.Image is not null ? [evidence.Image] : [] };
                }
                else await File.WriteAllTextAsync(Path.Combine(artifact, "actions.json"), "[]");
                result = result with { ElapsedSeconds = elapsed.Elapsed.TotalSeconds };
                string resultFile = Path.Combine(artifact, "task.json");
                await WriteAsync(resultFile, result);
                results.Add(result);
                if (result.Outcome == TaskOutcome.Succeeded)
                {
                    satisfied.Add(request.Id);
                    completed[request.Id] = new(relative + "/task.json", Hash(await File.ReadAllBytesAsync(resultFile)), await EvidenceHashesAsync(artifact));
                }
                else if (options.DryRun && result.Outcome == TaskOutcome.DryRun) satisfied.Add(request.Id);
                bool taskFailed = result.Outcome is TaskOutcome.Failed or TaskOutcome.Refused ||
                    result.Outcome == TaskOutcome.Skipped && request.Required && result.Reason != "previously_completed";
                failed |= taskFailed;
                stop |= taskFailed && !options.ContinueOnFailure;
                if (!options.DryRun) await SaveStateAsync(directory, fingerprint, completed);
            }
            if (!options.DryRun && requests.Count == 0) await SaveStateAsync(directory, fingerprint, completed);
        }
        finally { if (session is not null) await session.DisposeAsync(); }
        var summary = new TaskQueueResult(directory, results, failed);
        await WriteAsync(Path.Combine(directory, attempt, "summary.json"), summary);
        return summary;
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static async Task<Dictionary<string, string>> EvidenceHashesAsync(string directory, CancellationToken token = default)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Task evidence must be regular files");
            hashes.Add(Path.GetFileName(file), Hash(await File.ReadAllBytesAsync(file, token)));
        }
        if (!hashes.ContainsKey("request.json") || !hashes.ContainsKey("task.json") || !hashes.ContainsKey("actions.json"))
            throw new InvalidDataException("Task evidence is incomplete");
        return hashes;
    }
    private static Task SaveStateAsync(string directory, string fingerprint, Dictionary<string, CompletedTask> completed)
    {
        var state = new QueueState(fingerprint, completed);
        return WriteAsync(Path.Combine(directory, "state.json"), new StateEnvelope(state, Hash(JsonSerializer.SerializeToUtf8Bytes(state, Json))));
    }
    private static async Task WriteAsync<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, Json));
        File.Move(temporary, path, overwrite: true);
    }
    private sealed record CompletedTask(string Artifact, string Sha256, Dictionary<string, string> Files);
    private sealed record QueueState(string Fingerprint, Dictionary<string, CompletedTask> Completed);
    private sealed record StateEnvelope(QueueState State, string Sha256);
}
