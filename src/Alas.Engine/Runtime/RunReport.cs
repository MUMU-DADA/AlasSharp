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
        if (boundary.SubmarineFile is { } submarineFile)
        {
            if (submarineFile != "submarine-location.json") throw new InvalidDataException("潜艇定位工件必须位于当前任务目录");
            string submarinePath = Path.Combine(taskRoot, submarineFile);
            if ((File.GetAttributes(submarinePath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("潜艇定位工件必须是普通文件");
            var stored = JsonNode.Parse(ArtifactReader.ReadAllText(submarinePath));
            var submarine = stored?.Deserialize<SubmarineLocationEvidence>(TaskQueue.Json);
            ValidateSubmarine(submarine);
            if (result.Evidence?["submarine"] is { } declared && !JsonNode.DeepEquals(declared, stored))
                throw new InvalidDataException("任务潜艇定位与独立工件不同");
        }
        else if (result.Evidence?["submarine"] is not null)
            throw new InvalidDataException("任务缺少潜艇定位工件");
        if (boundary.SubmarineCallsFile is { } callsFile)
        {
            if (callsFile != "submarine-calls.json") throw new InvalidDataException("潜艇呼叫工件必须位于当前任务目录");
            string callsPath = Path.Combine(taskRoot, callsFile);
            if ((File.GetAttributes(callsPath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("潜艇呼叫工件必须是普通文件");
            var calls = JsonSerializer.Deserialize<SubmarineCallEvidence[]>(ArtifactReader.ReadAllText(callsPath), TaskQueue.Json);
            if (calls is null || calls.Length == 0) throw new InvalidDataException("缺少潜艇呼叫记录");
            foreach (var call in calls) ValidateSubmarineCall(call);
        }
        if (boundary.SubmarineMovesFile is { } movesFile)
        {
            if (movesFile != "submarine-moves.json") throw new InvalidDataException("潜艇移动工件必须位于当前任务目录");
            string movesPath = Path.Combine(taskRoot, movesFile);
            if ((File.GetAttributes(movesPath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("潜艇移动工件必须是普通文件");
            var moves = JsonSerializer.Deserialize<SubmarineMoveEvidence[]>(ArtifactReader.ReadAllText(movesPath), TaskQueue.Json);
            if (moves is null || moves.Length == 0) throw new InvalidDataException("缺少潜艇移动记录");
            foreach (var move in moves) ValidateSubmarineMove(move);
        }
        if (boundary.WalkRecoveriesFile is { } walkFile)
        {
            if (walkFile != "walk-recoveries.json") throw new InvalidDataException("移动恢复工件必须位于当前任务目录");
            string walkPath = Path.Combine(taskRoot, walkFile);
            if ((File.GetAttributes(walkPath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("移动恢复工件必须是普通文件");
            var walks = JsonSerializer.Deserialize<WalkRecoveryEvidence[]>(ArtifactReader.ReadAllText(walkPath), TaskQueue.Json);
            if (walks is null || walks.Length == 0) throw new InvalidDataException("缺少移动恢复记录");
            foreach (var walk in walks) ValidateWalkRecovery(walk);
        }
        if (boundary.MapStopFile is { } stopFile)
        {
            if (stopFile != "map-stop.json") throw new InvalidDataException("成就停止工件必须位于当前任务目录");
            string stopPath = Path.Combine(taskRoot, stopFile);
            if ((File.GetAttributes(stopPath) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("成就工件必须是普通文件");
            var stop = JsonSerializer.Deserialize<CampaignStopEvidence>(ArtifactReader.ReadAllText(stopPath), TaskQueue.Json);
            if (stop is null || string.IsNullOrWhiteSpace(stop.RuleId) || stop.CancelClicks < 0 || stop.Info is not { FrameSequence: > 0 } info ||
                !double.IsFinite(info.ClearPercentage) || info.ClearPercentage is < 0 or >= 1.4 ||
                !Rules.CampaignObjectives.Reached(Rules.CampaignObjectives.Parse(stop.Achievement), info) ||
                stop.ReturnedFrame is { } frame && frame <= info.FrameSequence ||
                stop.Disabled is not null && (stop.ReturnedFrame is null || stop.Disabled.Value != (stop.NextStage is null)) ||
                stop.Persisted && (stop.ReturnedFrame is null || stop.Disabled is null))
                throw new InvalidDataException("成就停止缺少一致的观测、返页或写回记录");
        }
        if (boundary.FleetSwitchFile is { } switchFile)
        {
            if (switchFile != "fleet-switch.json") throw new InvalidDataException("舰队切换工件必须位于当前任务目录");
            string switchPath = Path.Combine(taskRoot, switchFile);
            if ((File.GetAttributes(switchPath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("舰队切换工件必须是普通文件");
            var switches = JsonSerializer.Deserialize<FleetSwitchEvidence[]>(ArtifactReader.ReadAllText(switchPath), TaskQueue.Json);
            if (switches is null || switches.Length == 0 || switches.Any(e => e is null || e.From is not (1 or 2) ||
                    e.To is not (1 or 2) || e.From == e.To || e.Location.Column < 1 || e.Location.Row < 1 ||
                    e.Selection is { } selection && (selection.LogicalIndex != e.To || selection.DisplayedIndex is not (1 or 2) ||
                        selection.Clicks < 0 || selection.FrameSequence <= 0) ||
                    e.CameraFrame is { } frame && (e.Selection is null || frame <= e.Selection.FrameSequence) ||
                    e.Ready && (e.Selection is null || e.CameraFrame is null)))
                throw new InvalidDataException("舰队切换记录缺少一致的身份或新定位帧");
        }
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

    private static void ValidateSubmarine(SubmarineLocationEvidence? evidence)
    {
        static bool Valid(Rules.Cell cell) => cell.Column > 0 && cell.Row > 0;
        if (evidence is null || evidence.Observations is null ||
            evidence.Location is { } location && !Valid(location) || evidence.Pending is { } pending && !Valid(pending))
            throw new InvalidDataException("潜艇定位缺少有效坐标或观察列表");
        long sequence = 0;
        var visited = new HashSet<Rules.Cell>();
        foreach (var observation in evidence.Observations)
        {
            if (observation is null || !Valid(observation.Location) || !Valid(observation.Camera) ||
                observation.FrameSequence <= 0 || observation.FrameSequence < sequence || !visited.Add(observation.Location))
                throw new InvalidDataException("潜艇定位包含重复位置、旧帧或无效观察");
            sequence = observation.FrameSequence;
        }
        bool consistent = evidence.Method switch
        {
            "disabled" or "no_spawn" => evidence.Location is null && evidence.Pending is null && evidence.Observations.Count == 0,
            "initial_observation" or "single_spawn" or "covered_spawn" =>
                evidence.Location is not null && evidence.Pending is null && evidence.Observations.Count == 0,
            "searched_observation" => evidence.Pending is null && evidence.Observations.Count > 0 &&
                evidence.Observations[^1] is { Present: true } match && match.Location == evidence.Location &&
                !evidence.Observations.SkipLast(1).Any(item => item.Present),
            "map_center_assumption" => evidence.Location is not null && evidence.Pending is null &&
                evidence.Observations.Count > 0 && !evidence.Observations.Any(item => item.Present),
            "failed" => evidence.Location is null,
            _ => false
        };
        if (!consistent) throw new InvalidDataException("潜艇定位结果与观察来源不一致");
    }

    internal static void ValidateWalkRecovery(WalkRecoveryEvidence? walk)
    {
        if (walk is null || walk.Fleet is not (1 or 2) || walk.Origin.Column < 1 || walk.Origin.Row < 1 ||
            walk.Target.Column < 1 || walk.Target.Row < 1 || walk.Steps.IsDefault ||
            walk.Steps.Any(cell => cell.Column < 1 || cell.Row < 1) ||
            walk.Phase is not ("observed" or "recovering" or "walking" or "completed" or "redispatched") ||
            walk.Interruption is not { Outcome: MapArrivalOutcome.WalkOutOfStep, Encounter: MapEncounterKind.WalkOutOfStep,
                FreshFrames: > 0, FrameSequence: > 0 } ||
            walk.CompletedSteps < 0 || walk.CompletedSteps > walk.Steps.Length ||
            walk.Phase is "observed" or "recovering" && (!walk.Steps.IsEmpty || walk.RecoveredFrame is not null) ||
            walk.Phase is "walking" or "completed" or "redispatched" &&
                (walk.Steps.IsEmpty || walk.Steps[^1] != walk.Target || walk.RecoveredFrame is null || walk.RecoveredFrame <= walk.Interruption.FrameSequence) ||
            walk.Phase == "completed" && walk.CompletedSteps != walk.Steps.Length)
            throw new InvalidDataException("移动恢复缺少一致的中断、路径或帧记录");
    }

    internal static void ValidateSubmarineMove(SubmarineMoveEvidence? move)
    {
        if (move is null || new[] { move.Boss, move.Origin, move.Target }.Any(cell => cell.Column < 1 || cell.Row < 1) ||
            move.Phase is not ("opening" or "entering" or "selecting" or "confirming" or "cancelling" or "hiding_zone" or "closing" or "completed") ||
            move.Attempts < 0 || move.SelectionFrame is <= 0 || move.ReturnedFrame is <= 0 ||
            (move.SelectionFrame is null) != (move.Moved is null) ||
            move.Phase is "opening" or "entering" && (move.Attempts != 0 || move.SelectionFrame is not null) ||
            move.Phase is "selecting" && move.SelectionFrame is not null ||
            move.Phase is "confirming" or "cancelling" or "hiding_zone" or "closing" or "completed" &&
                (move.Attempts == 0 || move.SelectionFrame is null) ||
            move.Phase == "confirming" && move.Moved != true || move.Phase == "cancelling" && move.Moved != false ||
            move.Phase == "completed" && (move.ReturnedFrame is null || move.ReturnedFrame <= move.SelectionFrame) ||
            move.Phase != "completed" && move.ReturnedFrame is not null)
            throw new InvalidDataException("潜艇移动缺少一致的选择、阶段或返回帧记录");
    }

    private static void ValidateSubmarineCall(SubmarineCallEvidence? call)
    {
        if (call is null || !Enum.IsDefined(call.Mode) || call.Attempts is null ||
            call.StartedFrame is <= 0 || call.LastFrame is <= 0 || call.ObservedFrame is <= 0 ||
            call.LastFrame is not null && (call.StartedFrame is null || call.LastFrame < call.StartedFrame) ||
            call.ObservedFrame is not null && (call.LastFrame is null || call.ObservedFrame > call.LastFrame || call.ObservedFrame < call.StartedFrame))
            throw new InvalidDataException("潜艇呼叫缺少有效模式或帧号");
        long previous = 0;
        foreach (var attempt in call.Attempts)
        {
            if (attempt is null || attempt.FrameSequence <= previous || call.LastFrame is null ||
                attempt.FrameSequence > call.LastFrame || attempt.FrameSequence < call.StartedFrame ||
                !attempt.Completed && (!ReferenceEquals(attempt, call.Attempts[^1]) || call.State != "failed"))
                throw new InvalidDataException("潜艇呼叫动作记录与观察不一致");
            previous = attempt.FrameSequence;
        }
        if (call.ObservedFrame is { } observed && (observed <= previous || call.Attempts.Any(attempt => !attempt.Completed)))
            throw new InvalidDataException("潜艇呼叫确认早于动作或包含未完成点击");
        bool disabled = call.Mode is Rules.SubmarineMode.DoNotUse or Rules.SubmarineMode.HuntOnly or Rules.SubmarineMode.HuntAndBoss;
        if (disabled && (call.Attempts.Count > 0 || call.ObservedFrame is not null))
            throw new InvalidDataException("未呼叫模式出现潜艇动作或呼叫确认");
        bool valid = call.State switch
        {
            "not_started" => call.StartedFrame is null && call.LastFrame is null && call.ObservedFrame is null && call.Attempts.Count == 0,
            "disabled" => disabled && call.StartedFrame is not null && call.LastFrame is not null,
            "called_observed" => !disabled && call.ObservedFrame is not null && call.ObservedFrame > previous,
            "window_expired" => !disabled && call.LastFrame is not null && call.ObservedFrame is null,
            "battle_ended_unconfirmed" => call.StartedFrame is not null && call.ObservedFrame is null,
            "failed" => true,
            _ => false
        };
        if (!valid) throw new InvalidDataException("潜艇呼叫状态缺少相符的观察证据");
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
