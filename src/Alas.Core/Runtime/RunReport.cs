using System.Text.Json;
using System.Text.Json.Nodes;

namespace Alas.Runtime;

/// <summary>报告里的一条发现。**码是稳定的**（诊断脚本/前端按它分派），detail 给人看。</summary>
public sealed record RunFinding(string Code, string Detail, string? Artifact = null);

/// <summary>
/// 一次运行的只读报告：把工件目录里的东西汇总成"这次到底发生了什么、证据全不全"。
///
/// 为什么需要它（R2 门槛"结果能追溯到上游调用和设备证据"）：工件是**写给人翻的**，
/// 而调度、前端和验收需要的是**结构化事实**。报告把工件读回来的时候顺手做完整性检查 ——
/// 引用了却不在的工件、结果里的合同违例、根本没落盘的日志，都会变成 findings。
///
/// 它**只读**：不跑游戏、不改工件、不重新判定通关（结论以工件里的合同裁决为准）。
/// </summary>
public sealed class RunReport
{
    /// <summary>运行目录（完整路径）。名字不叫 `Directory`，免得在类里遮蔽 `System.IO.Directory`。</summary>
    public string RunDirectory { get; set; } = "";
    public string? Stamp { get; set; }
    public int FileCount { get; set; }
    public bool DryRun { get; set; }
    public int? HostStartCount { get; set; }
    public int? DeviceConfigureCount { get; set; }
    public string? QueueOutcome { get; set; }
    public string? BatchOutcome { get; set; }
    /// <summary>是否提前停止，以及原因（人停的 / 失败即停 / 被前序任务带过）。</summary>
    public bool StoppedEarly { get; set; }
    public string? StopReason { get; set; }
    public int Tasks { get; set; }
    public int TasksSucceeded { get; set; }
    public int TasksFailed { get; set; }
    public int TasksSkipped { get; set; }
    public int Stages { get; set; }
    public int StagesCleared { get; set; }
    public int LogEntries { get; set; }
    public int LogErrors { get; set; }
    public int LogWarnings { get; set; }
    /// <summary>日志的 scope 分布（部件 → 条数）。</summary>
    public Dictionary<string, int> LogScopes { get; } = new(StringComparer.Ordinal);
    /// <summary>各任务开始时的边界状态快照（任务 id → 快照）。</summary>
    public Dictionary<string, JsonObject?> Boundaries { get; } = new(StringComparer.Ordinal);
    /// <summary>边界上拿到画面 / 拿不到画面的任务数（dry-run 下拿不到是正常的）。</summary>
    public int BoundariesWithFrame { get; set; }
    public int BoundariesWithoutFrame { get; set; }
    public List<RunFinding> Findings { get; } = new();
    /// <summary>工件里的原始条目（任务/关卡/批次），按出现顺序。</summary>
    public List<JsonObject> Items { get; } = new();

    public bool HasFailures => Findings.Any(f => f.Code is "task_failed" or "stage_not_cleared"
        or "contract_violation" or "batch_failed");
    public bool EvidenceComplete => !Findings.Any(f => f.Code is "missing_artifact"
        or "unreadable_artifact" or "no_artifacts" or "log_missing" or "duplicate_batch_index");

    /// <summary>从工件目录构建报告。<paramref name="directory"/> 必须是**运行目录**本身。</summary>
    public static RunReport Build(string directory)
    {
        var report = new RunReport();
        if (!Directory.Exists(directory))
        {
            report.RunDirectory = directory;
            report.Findings.Add(new RunFinding("run_not_found", $"运行目录不存在: {directory}"));
            return report;
        }
        report.RunDirectory = Path.GetFullPath(directory);
        report.Stamp = Path.GetFileName(report.RunDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var files = Directory.GetFiles(report.RunDirectory, "*", SearchOption.AllDirectories);
        report.FileCount = files.Length;
        if (files.Length == 0)
        {
            report.Findings.Add(new RunFinding("no_artifacts", "运行目录里没有任何工件"));
            return report;
        }

        // ---- 队列层（`queue.json`）
        string queuePath = Path.Combine(report.RunDirectory, "queue.json");
        var queue = ReadJson(report, queuePath);
        if (queue is not null)
        {
            try
            {
                if (queue["tasks"] is not JsonArray queueTasks ||
                    queueTasks.Any(node => node is not JsonObject))
                    throw new JsonException("queue.tasks 必须是对象数组");
                report.QueueOutcome = queue["outcome"]?.GetValue<string>();
                report.DryRun = queue["dry_run"]?.GetValue<bool>() ?? false;
                report.HostStartCount = queue["host_start_count"]?.GetValue<int>();
                report.DeviceConfigureCount = queue["device_configure_count"]?.GetValue<int>();
                // 提前停止与原因也要上数据面：只给一个 `outcome=cancelled`，前端看不出"为什么停"
                // （是人停的、还是失败即停、还是被前序任务带过的）。
                report.StoppedEarly = queue["stopped_early"]?.GetValue<bool>() ?? false;
                report.StopReason = queue["stop_reason"]?.GetValue<string>();
                if (queue["tasks"] is JsonArray tasks)
                    foreach (var node in tasks)
                    {
                        report.Tasks++;
                        string outcome = node?["outcome"]?.GetValue<string>() ?? "unknown";
                        if (outcome is "succeeded" or "dry_run") report.TasksSucceeded++;
                        else if (outcome == "skipped") report.TasksSkipped++;
                        else report.TasksFailed++;
                        report.Items.Add(new JsonObject
                        {
                            ["level"] = "task",
                            ["id"] = node?["id"]?.DeepClone(),
                            ["kind"] = node?["kind"]?.DeepClone(),
                            ["outcome"] = outcome,
                            ["error_kind"] = node?["error_kind"]?.DeepClone(),
                            ["error"] = node?["error"]?.DeepClone(),
                            ["artifact"] = node?["artifact"]?.DeepClone(),
                        });
                        CheckArtifact(report, node?["artifact"]?.GetValue<string>());
                        if (outcome is not ("succeeded" or "dry_run" or "skipped"))
                            report.Findings.Add(new RunFinding("task_failed",
                                $"任务 {node?["id"]?.GetValue<string>()} 结论 {outcome}"
                                + (node?["error"] is JsonNode e ? $"：{e}" : ""),
                                node?["artifact"]?.GetValue<string>()));
                        if (report.QueueOutcome == "failed" && outcome == "skipped"
                            && node?["error"] is JsonNode reason)
                            report.Findings.Add(new RunFinding("task_skipped", $"任务被跳过：{reason}",
                                node?["artifact"]?.GetValue<string>()));
                    }
                if (report.QueueOutcome == "failed" && report.TasksFailed == 0)
                    report.Findings.Add(new RunFinding("batch_failed", "队列结论 failed 但没有失败任务（工件自相矛盾）"));
            }
            catch (Exception error) when (IsJsonShapeError(error))
            {
                MarkUnreadable(report, queuePath, error);
            }
        }

        // 单批沿用 index.json；同一队列的后续战役写 index-2.json 等独立索引。
        var batchPaths = Directory.GetFiles(report.RunDirectory, "index*.json")
            .Select(path => (path, number: BatchIndexNumber(Path.GetFileName(path))))
            .Where(item => item.number > 0)
            .OrderBy(item => item.number);
        foreach (var (batchPath, _) in batchPaths)
        {
            var batch = ReadJson(report, batchPath);
            if (batch is null) continue;
            try
            {
                if (batch["stages"] is not JsonArray batchStages ||
                    batchStages.Any(node => node is not JsonObject))
                    throw new JsonException("index.stages 必须是对象数组");
                string? outcome = batch["outcome"]?.GetValue<string>();
                report.BatchOutcome = report.BatchOutcome is null ? outcome
                    : report.BatchOutcome == outcome ? report.BatchOutcome : "mixed";
                report.DryRun |= batch["dry_run"]?.GetValue<bool>() ?? false;
                // 单批命令（`alashub campaign`）没有 queue.json：宿主/设备的初始化次数要**回头从
                // index.json 取**，否则数据面上是 `?`，而"宿主只起一次"正是 R1 门槛要看的东西。
                report.HostStartCount ??= batch["host_start_count"]?.GetValue<int>();
                report.DeviceConfigureCount ??= batch["device_configure_count"]?.GetValue<int>();
                report.StoppedEarly |= batch["stopped_early"]?.GetValue<bool>() ?? false;
                report.StopReason ??= batch["stop_reason"]?.GetValue<string>();
                if (batch["stages"] is JsonArray stages)
                    foreach (var node in stages)
                    {
                        report.Stages++;
                        bool cleared = node?["cleared"]?.GetValue<bool>() ?? false;
                        if (cleared) report.StagesCleared++;
                        report.Items.Add(new JsonObject
                        {
                            ["level"] = "stage",
                            ["batch_index"] = batchPath,
                            ["chapter"] = node?["chapter"]?.DeepClone(),
                            ["stage"] = node?["stage"]?.DeepClone(),
                            ["outcome"] = node?["outcome"]?.DeepClone(),
                            ["cleared"] = cleared,
                            ["failed"] = node?["failed"]?.DeepClone(),
                            ["skipped"] = node?["skipped"]?.DeepClone(),
                            ["error"] = node?["error"]?.DeepClone(),
                            ["artifact"] = node?["artifact"]?.DeepClone(),
                        });
                        CheckArtifact(report, node?["artifact"]?.GetValue<string>());
                        if (node?["failed"]?.GetValue<bool>() == true)
                            report.Findings.Add(new RunFinding("stage_not_cleared",
                                $"关卡 {node?["stage"]?.GetValue<string>() ?? node?["chapter"]?.GetValue<string>()} "
                                + $"未通过：{node?["outcome"]?.GetValue<string>()}"
                                + (node?["error"] is JsonNode e ? $"（{e}）" : ""),
                                node?["artifact"]?.GetValue<string>()));
                    }
            }
            catch (Exception error) when (IsJsonShapeError(error))
            {
                MarkUnreadable(report, batchPath, error);
            }
        }

        // ---- 单关结果的合同违例（`sortie-*.json` 里的 result.contract_violations）
        foreach (var path in files.Where(f => Path.GetFileName(f).StartsWith("sortie-", StringComparison.Ordinal)))
        {
            var document = ReadJson(report, path);
            try
            {
                if (document?["result"]?["contract_violations"] is JsonArray violations
                    && violations.Count > 0)
                    report.Findings.Add(new RunFinding("contract_violation",
                        $"结果未通过合同: {string.Join(",", violations.Select(v => v?.GetValue<string>()))}",
                        path));
            }
            catch (Exception error) when (IsJsonShapeError(error))
            {
                MarkUnreadable(report, path, error);
            }
        }

        // ---- 任务边界的只读状态快照（写在 `task-*.json` 里，R2 的"跨任务复位"证据）
        // 为什么报告要读它：快照不落到数据面，前端就看不到"这个任务是从什么画面开始的" ——
        // 而那正是判断"跨任务复位了没有"的依据。读了就顺手统计"有几个任务在边界上拿到了帧"。
        var claimedIndexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in files.Where(f => Path.GetFileName(f).StartsWith("task-", StringComparison.Ordinal)))
        {
            var document = ReadJson(report, path);
            try
            {
                if (document?["id"]?.GetValue<string>() is not string taskId) continue;
                if (document["evidence"] is JsonObject evidence
                    && evidence["index_artifact"] is JsonNode indexArtifact)
                {
                    string indexPath = indexArtifact.GetValue<string>();
                    CheckArtifact(report, indexPath);
                    if (!claimedIndexes.Add(Path.GetFileName(indexPath)))
                        report.Findings.Add(new RunFinding("duplicate_batch_index",
                            $"多个战役任务引用同一批次索引: {Path.GetFileName(indexPath)}", path));
                }
                var boundary = document["boundary_state"] as JsonObject;
                report.Boundaries[taskId] = boundary;
                if (boundary?["available"]?.GetValue<bool>() == true) report.BoundariesWithFrame++;
                else report.BoundariesWithoutFrame++;
                foreach (var item in report.Items)
                    if (item["level"]?.GetValue<string>() == "task"
                        && item["id"]?.GetValue<string>() == taskId)
                        item["boundary_state"] = boundary?.DeepClone();
            }
            catch (Exception error) when (IsJsonShapeError(error))
            {
                MarkUnreadable(report, path, error);
            }
        }

        // ---- 会话日志
        string logPath = Path.Combine(report.RunDirectory, "session-log.jsonl");
        if (File.Exists(logPath))
        {
            foreach (var line in File.ReadLines(logPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                report.LogEntries++;
                try
                {
                    if (JsonNode.Parse(line) is not JsonObject entry)
                        throw new JsonException("会话日志行必须是 JSON 对象");
                    string level = LogString(entry, "level");
                    if (level == "ERROR") report.LogErrors++;
                    if (level == "WARN") report.LogWarnings++;
                    // scope 分布：前端要能看出"这次运行里哪些部件说了话"
                    // （例如 queue/stage/task/artifacts/session），只给总数看不见来源。
                    string scope = LogString(entry, "scope");
                    if (scope.Length > 0)
                        report.LogScopes[scope] = report.LogScopes.GetValueOrDefault(scope) + 1;
                }
                catch (Exception error) when (IsJsonShapeError(error))
                {
                    MarkUnreadable(report, logPath, error);
                }
            }
        }
        else
        {
            report.Findings.Add(new RunFinding("log_missing",
                "缺少 session-log.jsonl（会话日志没落盘）"));
        }

        // ---- 断点文件与任务数是否对得上（能对上的前提是两者都在）
        var state = ReadJson(report, Path.Combine(report.RunDirectory, "state.json"));
        if (state?["completed"] is JsonObject done && report.Tasks > 0)
        {
            int recorded = done.Count;
            int finished = report.TasksSucceeded + report.TasksSkipped;
            if (recorded > finished)
                report.Findings.Add(new RunFinding("state_incomplete",
                    $"state.json 记了 {recorded} 个已完成，而队列只有 {finished} 个成功/跳过"));
        }
        return report;
    }

    /// <summary>工件引用了却不在 → 证据链断了。这是报告最该抓住的一类问题。</summary>
    private static void CheckArtifact(RunReport report, string? artifact)
    {
        if (string.IsNullOrEmpty(artifact)) return;
        if (File.Exists(artifact)) return;
        // 运行目录被整体归档/搬走后，工件里的绝对路径会失效；同名文件就在本目录里时
        // 按"随目录搬迁"记一条提示（不算证据缺失），否则才是真的缺。
        string local = Path.Combine(report.RunDirectory, Path.GetFileName(artifact));
        if (File.Exists(local))
        {
            report.Findings.Add(new RunFinding("relocated_artifact",
                $"工件随运行目录搬迁: {Path.GetFileName(artifact)}", local));
            return;
        }
        report.Findings.Add(new RunFinding("missing_artifact",
            $"引用的工件不存在: {artifact}", artifact));
    }

    private static JsonObject? ReadJson(RunReport report, string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is JsonObject document)
                return document;
            report.Findings.Add(new RunFinding("unreadable_artifact",
                $"{Path.GetFileName(path)} 必须是 JSON 对象", path));
            return null;
        }
        catch (Exception error)
        {
            report.Findings.Add(new RunFinding("unreadable_artifact",
                $"{Path.GetFileName(path)} 读不出来: {error.Message}", path));
            return null;
        }
    }

    private static bool IsJsonShapeError(Exception error)
        => error is JsonException or InvalidOperationException or FormatException or ArgumentException;

    private static int BatchIndexNumber(string name)
    {
        if (name == "index.json") return 1;
        if (name.StartsWith("index-", StringComparison.Ordinal)
            && name.EndsWith(".json", StringComparison.Ordinal)
            && int.TryParse(name.AsSpan(6, name.Length - 11), out int number)
            && number >= 2)
            return number;
        return 0;
    }

    private static void MarkUnreadable(RunReport report, string path, Exception error)
        => report.Findings.Add(new RunFinding("unreadable_artifact",
            $"{Path.GetFileName(path)} 格式错误: {error.Message}", path));

    private static string LogString(JsonObject entry, string key)
    {
        if (entry[key] is null) return "";
        if (entry[key] is JsonValue value && value.TryGetValue<string>(out var text))
            return text;
        throw new JsonException($"会话日志的 {key} 必须是字符串");
    }

    public JsonObject ToJson()
    {
        var items = new JsonArray(Items.Select(i => (JsonNode)i.DeepClone()!).ToArray());
        var findings = new JsonArray(Findings.Select(f => (JsonNode)new JsonObject
        {
            ["code"] = f.Code,
            ["detail"] = f.Detail,
            ["artifact"] = f.Artifact,
        }).ToArray());
        return new JsonObject
        {
            ["directory"] = RunDirectory,
            ["stamp"] = Stamp,
            ["files"] = FileCount,
            ["dry_run"] = DryRun,
            ["host_start_count"] = HostStartCount,
            ["device_configure_count"] = DeviceConfigureCount,
            ["queue_outcome"] = QueueOutcome,
            ["stopped_early"] = StoppedEarly,
            ["stop_reason"] = StopReason,
            ["batch_outcome"] = BatchOutcome,
            ["totals"] = new JsonObject
            {
                ["tasks"] = Tasks,
                ["tasks_succeeded"] = TasksSucceeded,
                ["tasks_failed"] = TasksFailed,
                ["tasks_skipped"] = TasksSkipped,
                ["stages"] = Stages,
                ["stages_cleared"] = StagesCleared,
                ["log_entries"] = LogEntries,
                ["log_errors"] = LogErrors,
                ["log_warnings"] = LogWarnings,
                ["log_scopes"] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(LogScopes)),
                ["boundaries_with_frame"] = BoundariesWithFrame,
                ["boundaries_without_frame"] = BoundariesWithoutFrame,
            },
            ["has_failures"] = HasFailures,
            ["evidence_complete"] = EvidenceComplete,
            ["findings"] = findings,
            ["items"] = items,
        };
    }

    /// <summary>
    /// 一个目录是不是**一次运行**：必须有自己的 `queue.json`（队列运行）或 `index.json`（单批运行）。
    ///
    /// 为什么要判：工件根目录下可能混进别的目录（例如生成的视图目录、手工建的临时目录），
    /// 而这里取"最新一次"是按**目录名**排序的 —— 混进来的目录会被当成一次运行，
    /// 表现为"读了它、于是报 log_missing 之类的**误导性发现**"（实测踩到过：
    /// 视图目录 `views` 比时间戳目录排序靠后，`report --artifacts` 就去读它了）。
    /// 判定口径必须**只有一处**，`report --artifacts` 与 `runs` 共用，否则两个命令会各认一套。
    /// </summary>
    public static bool IsRunDirectory(string directory)
        => File.Exists(Path.Combine(directory, "queue.json"))
           || File.Exists(Path.Combine(directory, "index.json"));

    /// <summary>找工件根目录下**最新**的一次运行（按目录名，即时间戳；只认真正的运行目录）。</summary>
    public static string? LatestRun(string artifactsRoot)
    {
        if (!Directory.Exists(artifactsRoot)) return null;
        return Directory.GetDirectories(artifactsRoot)
            .Where(IsRunDirectory)
            .OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal)
            .LastOrDefault();
    }

    /// <summary>
    /// 列出工件根目录下最近几次运行（R4 数据面：前端要能"看见有哪些运行"，
    /// 而不是靠人记住时间戳目录名）。每条只给**摘要字段**，详情用 `report --run`。
    /// 读不出来的目录如实记 `error`，不跳过 —— 静默跳过等于前端看不到坏数据。
    /// </summary>
    public static JsonObject Summarize(string artifactsRoot, int limit)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit), "运行列表上限必须为正整数");
        var runs = new JsonArray();
        var directories = Directory.Exists(artifactsRoot)
            ? Directory.GetDirectories(artifactsRoot)
                .Where(IsRunDirectory)          // 与 LatestRun 同一口径：只认真正的运行目录
                .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
                .Take(limit)
            : Enumerable.Empty<string>();
        foreach (var directory in directories)
        {
            var report = Build(directory);
            runs.Add(new JsonObject
            {
                ["stamp"] = report.Stamp,
                ["directory"] = report.RunDirectory,
                ["dry_run"] = report.DryRun,
                ["queue_outcome"] = report.QueueOutcome,
                ["stopped_early"] = report.StoppedEarly,
                ["stop_reason"] = report.StopReason,
                ["batch_outcome"] = report.BatchOutcome,
                ["tasks"] = report.Tasks,
                ["tasks_failed"] = report.TasksFailed,
                ["tasks_skipped"] = report.TasksSkipped,
                ["stages"] = report.Stages,
                ["stages_cleared"] = report.StagesCleared,
                ["has_failures"] = report.HasFailures,
                ["evidence_complete"] = report.EvidenceComplete,
                ["findings"] = report.Findings.Count,
                ["finding_codes"] = new JsonArray(report.Findings
                    .Select(f => (JsonNode)JsonValue.Create(f.Code)!).Distinct().ToArray()),
            });
        }
        return new JsonObject
        {
            ["artifacts_root"] = Path.GetFullPath(artifactsRoot),
            ["exists"] = Directory.Exists(artifactsRoot),
            ["returned"] = runs.Count,
            ["runs"] = runs,
        };
    }
}
