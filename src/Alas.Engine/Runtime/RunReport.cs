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
                    if (JsonNode.Parse(ArtifactReader.ReadAllText(Path.Combine(taskRoot, "actions.json"))) is not JsonArray actions)
                        throw new InvalidDataException("动作工件不是数组");
                    ValidateBoundary(taskRoot, result, actions.Count);
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

    private static void ValidateBoundary(string taskRoot, TaskResult result, int actionAttempts)
    {
        var boundary = result.Evidence?["boundary"]?.Deserialize<EngineSession.JsonObjectEvidence>(TaskQueue.Json);
        string[] frames = result.FailureFrames ?? [];
        if (boundary is null)
        {
            if (frames.Length != 0) throw new InvalidDataException("登记的失败帧缺少任务边界证据");
            return;
        }
        if (boundary.ActionAttempts != actionAttempts)
            throw new InvalidDataException("任务边界动作次数与动作工件不同");
        if (boundary.MapPreparationFile is { } preparationFile)
        {
            if (preparationFile != "map-preparation.json") throw new InvalidDataException("地图准备工件必须位于当前任务目录");
            string preparationPath = Path.Combine(taskRoot, preparationFile);
            if ((File.GetAttributes(preparationPath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("地图准备工件必须是普通文件");
            var preparation = JsonSerializer.Deserialize<CampaignPreparationEvidence>(ArtifactReader.ReadAllText(preparationPath), TaskQueue.Json)
                ?? throw new InvalidDataException("缺少地图准备记录");
            if (preparation.Info is { } info && (info.FrameSequence <= 0 || !double.IsFinite(info.ClearPercentage) ||
                    info.ClearPercentage is < 0 or >= 1.4) ||
                preparation.DoubleBook is { } book && (book.FrameSequence <= 0 || book.Clicks is < 0 or > 4 ||
                    !book.Available && (book.Enabled != false || book.Clicks != 0)))
                throw new InvalidDataException("地图准备记录无效");
        }
        if (boundary.EmotionFile is { } emotionFile)
        {
            if (emotionFile != "emotion.json") throw new InvalidDataException("心情工件必须位于当前任务目录");
            string emotionPath = Path.Combine(taskRoot, emotionFile);
            if ((File.GetAttributes(emotionPath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("心情工件必须是普通文件");
            var events = JsonSerializer.Deserialize<EmotionEvent[]>(ArtifactReader.ReadAllText(emotionPath), TaskQueue.Json);
            if (events is null || events.Length == 0 || events.Any(e => e is null || e.Values.IsDefault || e.Values.Length != 2 ||
                    e.Operation is not ("entry" or "wait" or "reduce") ||
                    (e.Operation == "entry" ? e.Fleet is not null : e.Fleet is not (1 or 2)) ||
                    (e.Operation == "reduce" && e.BattleSequence is null or <= 0)))
                throw new InvalidDataException("心情工件缺少有效记录或战斗帧");
        }
        if (boundary.Image is not { } image)
        {
            if (boundary.Sha256 is not null || boundary.FrameSequence is not null || frames.Length != 0)
                throw new InvalidDataException("无截图的任务边界含有图像证据");
            return;
        }
        // EngineSession stores a file name relative to this task, never to the
        // process working directory. Do not let an artifact redirect the reader.
        if (image is not ("frame.png" or "failure.png"))
            throw new InvalidDataException("任务边界图像必须是任务目录内的截图文件");
        if (boundary.FrameSequence is null or <= 0 || boundary.Sha256 is null ||
            !Regex.IsMatch(boundary.Sha256, @"\A[0-9a-f]{64}\z"))
            throw new InvalidDataException("任务边界缺少有效帧编号或图像哈希");
        if (result.Outcome == TaskOutcome.Failed ? !frames.SequenceEqual(new[] { image }) : frames.Length != 0)
            throw new InvalidDataException("失败帧登记与任务边界图像不同");
        string path = Path.Combine(taskRoot, image);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("任务边界图像必须是普通文件");
        string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
        if (hash != boundary.Sha256) throw new InvalidDataException("任务边界图像哈希不同");
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
