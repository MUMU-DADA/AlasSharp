using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Alas.Engine.Tasks;

namespace Alas.Engine.Runtime;

/// <summary>Reads Engine artifacts. It never executes tasks or infers sortie success.</summary>
public sealed class RunReport
{
    private readonly JsonObject _report;
    private RunReport(JsonObject report) => _report = report;
    public JsonObject ToJson() => _report.DeepClone().AsObject();

    public static RunReport Build(string directory)
    {
        string root = Path.GetFullPath(directory);
        var findings = new JsonArray();
        var items = new JsonArray();
        var report = new JsonObject
        {
            ["directory"] = root, ["stamp"] = Path.GetFileName(root),
            ["queue_outcome"] = "unknown", ["dry_run"] = false,
            ["stopped_early"] = false, ["stop_reason"] = null,
            ["has_failures"] = false, ["evidence_complete"] = false,
            ["items"] = items, ["findings"] = findings,
        };
        void Finding(string code, string detail, string? artifact = null)
            => findings.Add(new JsonObject { ["code"] = code, ["detail"] = detail, ["artifact"] = artifact });
        try
        {
            var requests = JsonSerializer.Deserialize<TaskRequest[]>(ArtifactReader.ReadAllText(Path.Combine(root, "queue.json")), TaskQueue.Json)
                ?? throw new InvalidDataException("队列输入不是任务数组");
            TaskQueue.ValidateRequests(requests);
            var snapshot = JsonSerializer.Deserialize<QueueSnapshot>(ArtifactReader.ReadAllText(Path.Combine(root, "run.json")), TaskQueue.Json)
                ?? throw new InvalidDataException("缺少 Engine 运行快照");
            if (snapshot.Contract != "engine-queue/1" ||
                !Regex.IsMatch(snapshot.Attempt, @"\Aattempt-[0-9a-f]{32}\z") ||
                snapshot.Tasks.Any(task => task is null) || snapshot.Tasks.Count > requests.Length ||
                (snapshot.Complete && snapshot.Tasks.Count != requests.Length))
                throw new InvalidDataException("运行快照身份或任务数量不一致");
            int succeeded = 0, failed = 0, skipped = 0, dryRun = 0;
            for (int i = 0; i < snapshot.Tasks.Count; i++)
            {
                var result = snapshot.Tasks[i];
                var request = requests[i];
                if (result.Id != request.Id || result.Kind != request.Kind || !Enum.IsDefined(result.Outcome) ||
                    (result.Outcome == TaskOutcome.Succeeded && (snapshot.DryRun || result.Error is not null || result.FailureFrames is { Length: > 0 })) ||
                    (result.Outcome == TaskOutcome.DryRun && !snapshot.DryRun) ||
                    !Regex.IsMatch(request.Id, @"\A[a-zA-Z0-9_-]{1,64}\z"))
                    throw new InvalidDataException("快照中的任务身份或结论不一致");
                string taskRoot = Path.Combine(root, snapshot.Attempt, $"{i:D4}-{request.Id}");
                string taskPath = Path.Combine(taskRoot, "task.json");
                var item = JsonSerializer.SerializeToNode(result, TaskQueue.Json)!.AsObject();
                item["level"] = "task";
                item["input"] = request.Input?.DeepClone();
                item["required"] = request.Required;
                item["artifact"] = taskPath;
                items.Add(item);
                try
                {
                    var stored = JsonNode.Parse(ArtifactReader.ReadAllText(taskPath));
                    if (!JsonNode.DeepEquals(stored, JsonSerializer.SerializeToNode(result, TaskQueue.Json)))
                        throw new InvalidDataException("任务工件与运行快照不同");
                    var input = JsonNode.Parse(ArtifactReader.ReadAllText(Path.Combine(taskRoot, "request.json")));
                    if (!JsonNode.DeepEquals(input, JsonSerializer.SerializeToNode(request, TaskQueue.Json)))
                        throw new InvalidDataException("任务工件输入与队列不同");
                    if (JsonNode.Parse(ArtifactReader.ReadAllText(Path.Combine(taskRoot, "actions.json"))) is not JsonArray)
                        throw new InvalidDataException("动作工件不是数组");
                    foreach (string frame in result.FailureFrames ?? [])
                        if (!File.Exists(frame)) Finding("missing_artifact", "登记的失败帧不存在", frame);
                    if (result.Reason == "previously_completed")
                    {
                        string source = result.Evidence?["sourceArtifact"]?.GetValue<string>() ?? "";
                        if (!Regex.IsMatch(source, @"\Aattempt-[0-9a-f]{32}/[0-9]{4,}-[a-zA-Z0-9_-]{1,64}/task\.json\z"))
                            throw new InvalidDataException("已完成任务缺少有效来源");
                        string sourcePath = Path.Combine(root, source);
                        var bytes = File.ReadAllBytes(sourcePath);
                        string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
                        if (hash != result.Evidence?["sourceSha256"]?.GetValue<string>())
                            throw new InvalidDataException("已完成任务的来源哈希不同");
                    }
                }
                catch (Exception error) when (IsArtifactError(error))
                { Finding(error is FileNotFoundException or DirectoryNotFoundException ? "missing_artifact" : "unreadable_artifact", error.Message, taskPath); }
                switch (result.Outcome)
                {
                    case TaskOutcome.Succeeded: succeeded++; break;
                    case TaskOutcome.DryRun: dryRun++; break;
                    case TaskOutcome.Skipped: skipped++; break;
                    default: failed++; break;
                }
            }
            if (snapshot.Failed != snapshot.Tasks.Select((result, index) => TaskQueue.IsFailure(requests[index], result)).Any(value => value))
                throw new InvalidDataException("队列失败标记与任务结论不一致");
            report["dry_run"] = snapshot.DryRun;
            report["queue_outcome"] = !snapshot.Complete ? "running" : snapshot.Failed ? "failed" :
                snapshot.StopReason is not null ? "cancelled" : snapshot.DryRun ? "dry_run" : "succeeded";
            report["stopped_early"] = snapshot.StopReason is not null;
            report["stop_reason"] = snapshot.StopReason;
            report["has_failures"] = snapshot.Failed || failed > 0;
            report["totals"] = new JsonObject
            {
                ["tasks"] = snapshot.Tasks.Count, ["tasks_requested"] = requests.Length,
                ["tasks_succeeded"] = succeeded, ["tasks_failed"] = failed,
                ["tasks_skipped"] = skipped, ["tasks_dry_run"] = dryRun,
            };
            report["evidence_complete"] = snapshot.Complete && findings.Count == 0;
        }
        catch (Exception error) when (IsArtifactError(error))
        { Finding(error is FileNotFoundException or DirectoryNotFoundException ? "missing_artifact" : "unreadable_artifact", error.Message); }
        return new RunReport(report);
    }

    private static bool IsArtifactError(Exception error)
        => error is IOException or InvalidDataException or JsonException or InvalidOperationException or ArgumentException or UnauthorizedAccessException;

    public static bool IsRunDirectory(string directory) => File.Exists(Path.Combine(directory, "run.json"));

    public static string? LatestRun(string artifactsRoot) => Directories(artifactsRoot).FirstOrDefault();

    // Engine run ids are random: lexical order is not chronological order.
    private static IEnumerable<string> Directories(string root) => Directory.Exists(root)
        ? Directory.EnumerateDirectories(root).Where(IsRunDirectory)
            .OrderByDescending(path => File.GetLastWriteTimeUtc(Path.Combine(path, "run.json")))
            .ThenBy(path => path, StringComparer.Ordinal)
        : [];

    public static JsonObject Summarize(string artifactsRoot, int limit)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        var runs = new JsonArray();
        foreach (string path in Directories(artifactsRoot).Take(limit))
        {
            var report = Build(path).ToJson();
            report.Remove("items");
            runs.Add(report);
        }
        return new JsonObject
        {
            ["artifacts_root"] = Path.GetFullPath(artifactsRoot), ["exists"] = Directory.Exists(artifactsRoot),
            ["returned"] = runs.Count, ["runs"] = runs,
        };
    }
}
