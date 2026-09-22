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
    public int Tasks { get; set; }
    public int TasksSucceeded { get; set; }
    public int TasksFailed { get; set; }
    public int TasksSkipped { get; set; }
    public int Stages { get; set; }
    public int StagesCleared { get; set; }
    public int LogEntries { get; set; }
    public int LogErrors { get; set; }
    public int LogWarnings { get; set; }
    public List<RunFinding> Findings { get; } = new();
    /// <summary>工件里的原始条目（任务/关卡/批次），按出现顺序。</summary>
    public List<JsonObject> Items { get; } = new();

    public bool HasFailures => Findings.Any(f => f.Code is "task_failed" or "stage_not_cleared"
        or "contract_violation" or "batch_failed");
    public bool EvidenceComplete => !Findings.Any(f => f.Code is "missing_artifact"
        or "unreadable_artifact" or "no_artifacts" or "log_missing");

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
        var queue = ReadJson(report, Path.Combine(report.RunDirectory, "queue.json"));
        if (queue is not null)
        {
            report.QueueOutcome = queue["outcome"]?.GetValue<string>();
            report.DryRun = queue["dry_run"]?.GetValue<bool>() ?? false;
            report.HostStartCount = queue["host_start_count"]?.GetValue<int>();
            report.DeviceConfigureCount = queue["device_configure_count"]?.GetValue<int>();
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

        // ---- 批次层（`index.json`，`campaign` 命令或队列里的战役任务都会写）
        var batch = ReadJson(report, Path.Combine(report.RunDirectory, "index.json"));
        if (batch is not null)
        {
            report.BatchOutcome = batch["outcome"]?.GetValue<string>();
            report.DryRun |= batch["dry_run"]?.GetValue<bool>() ?? false;
            if (batch["stages"] is JsonArray stages)
                foreach (var node in stages)
                {
                    report.Stages++;
                    bool cleared = node?["cleared"]?.GetValue<bool>() ?? false;
                    if (cleared) report.StagesCleared++;
                    report.Items.Add(new JsonObject
                    {
                        ["level"] = "stage",
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

        // ---- 单关结果的合同违例（`sortie-*.json` 里的 result.contract_violations）
        foreach (var path in files.Where(f => Path.GetFileName(f).StartsWith("sortie-", StringComparison.Ordinal)))
        {
            var document = ReadJson(report, path);
            if (document?["result"]?["contract_violations"] is JsonArray violations
                && violations.Count > 0)
                report.Findings.Add(new RunFinding("contract_violation",
                    $"结果未通过合同: {string.Join(",", violations.Select(v => v?.GetValue<string>()))}",
                    path));
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
                    var entry = JsonNode.Parse(line);
                    string level = entry?["level"]?.GetValue<string>() ?? "";
                    if (level == "ERROR") report.LogErrors++;
                    if (level == "WARN") report.LogWarnings++;
                }
                catch (JsonException)
                {
                    report.Findings.Add(new RunFinding("unreadable_artifact",
                        "会话日志里有非 JSON 行", logPath));
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

    private static JsonNode? ReadJson(RunReport report, string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonNode.Parse(File.ReadAllText(path));
        }
        catch (Exception error)
        {
            report.Findings.Add(new RunFinding("unreadable_artifact",
                $"{Path.GetFileName(path)} 读不出来: {error.Message}", path));
            return null;
        }
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
            },
            ["has_failures"] = HasFailures,
            ["evidence_complete"] = EvidenceComplete,
            ["findings"] = findings,
            ["items"] = items,
        };
    }

    /// <summary>找工件根目录下**最新**的一次运行（按目录名，即时间戳）。</summary>
    public static string? LatestRun(string artifactsRoot)
    {
        if (!Directory.Exists(artifactsRoot)) return null;
        return Directory.GetDirectories(artifactsRoot)
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
        var runs = new JsonArray();
        var directories = Directory.Exists(artifactsRoot)
            ? Directory.GetDirectories(artifactsRoot)
                .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
                .Take(Math.Max(1, limit))
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
