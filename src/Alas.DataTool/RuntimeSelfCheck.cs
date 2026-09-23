using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Runtime;
using Alas.Vision;

namespace Alas.DataTool;

/// <summary>
/// `alashub selftest-runtime`：R1 运行时的离线自检。**不启动 Python、不连设备** ——
/// 用一个替身识图后端（<see cref="StubVisionEngine"/>）喂预先准备好的结果文档，
/// 于是"宿主只初始化一次""失败即停""取消在关卡边界生效""工件落盘"这些运行时行为
/// 都可以在没有设备的情况下被断言。
///
/// 夹具格式：
/// <code>
/// { "cases": [ { "name": "...", "dry_run": true, "allow_actions": false, "serial": null,
///                "artifacts": true, "stop_on_failure": true, "cancel_before_stage": null,
///                "chapters": [ { "chapter": "campaign...campaign_1_1",
///                                "document": {...} | "error": "上游炸了" } ],
///                "expect": { "outcome": "cleared", "cleared": true, "host_start_count": 1,
///                            "device_configure_count": 0, "backend_calls": 1,
///                            "stages": [ { "chapter": "...", "outcome": "cleared",
///                                          "cleared": true, "failed": false, "violations": [] } ],
///                            "artifacts": ["index.json"] } } ] }
/// </code>
/// 判定与结果合同一致：**结论只认合同**，自检不发明第二套通关条件。
/// </summary>
internal static class RuntimeSelfCheck
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static int Run(string fixture, string? jsonOut, string? workspace)
    {
        if (!File.Exists(fixture))
        {
            Console.Error.WriteLine($"找不到夹具: {fixture}");
            return 2;
        }
        var root = JsonNode.Parse(File.ReadAllText(fixture)) ?? new JsonObject();
        var cases = root["cases"]?.AsArray();
        if (cases is null)
        {
            Console.Error.WriteLine("夹具缺少 cases 数组");
            return 2;
        }

        var reported = new JsonArray();
        int failed = 0;
        Console.WriteLine($"运行时自检：{cases.Count} 例（替身宿主，不启动 Python / 不连设备）");
        foreach (var node in cases)
        {
            // 有 `tasks` 就是任务队列用例（R2）；否则是单批战役用例（R1）。
            var report = node?["tasks"] is JsonArray
                ? RunQueueCase(node!, workspace)
                : RunCase(node!, workspace);
            reported.Add(report);
            bool ok = report["ok"]!.GetValue<bool>();
            if (!ok) failed++;
            Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {report["name"]!.GetValue<string>(),-30} " +
                              $"outcome={report["outcome"]!.GetValue<string>(),-14} " +
                              $"cleared={report["cleared"]!.GetValue<bool>(),-5} " +
                              $"宿主={report["host_start_count"]!.GetValue<int>()} " +
                              $"设备={report["device_configure_count"]!.GetValue<int>()} " +
                              $"后端调用={report["backend_calls"]!.GetValue<int>()}");
            if (!ok)
                foreach (var problem in report["problems"]!.AsArray())
                    Console.WriteLine($"        ← {problem!.GetValue<string>()}");
        }

        if (jsonOut is not null)
        {
            var payload = new JsonObject { ["cases"] = reported };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(jsonOut))!);
            File.WriteAllText(jsonOut, payload.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
            }));
            Console.WriteLine($"裁决已写入: {jsonOut}");
        }
        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "结果: OK（运行时行为与期望一致）" : $"结果: FAIL（{failed} 例不符）");
        return failed == 0 ? 0 : 1;
    }

    private static JsonObject RunCase(JsonNode node, string? workspace)
    {
        string name = node["name"]?.GetValue<string>() ?? "<未命名>";
        bool dryRun = node["dry_run"]?.GetValue<bool>() ?? true;
        bool allowActions = node["allow_actions"]?.GetValue<bool>() ?? false;
        string? serial = node["serial"]?.GetValue<string>();
        bool wantArtifacts = node["artifacts"]?.GetValue<bool>() ?? false;
        bool stopOnFailure = node["stop_on_failure"]?.GetValue<bool>() ?? true;
        string? cancelAfterChapter = node["cancel_after_chapter"]?.GetValue<string>();
        var chapters = node["chapters"]!.AsArray();
        var expect = node["expect"];

        var documents = new Dictionary<string, JsonObject>();
        var errors = new Dictionary<string, string>();
        var order = new List<string>();
        foreach (var chapter in chapters)
        {
            string key = chapter!["chapter"]!.GetValue<string>();
            order.Add(key);
            if (chapter["error"] is JsonNode error)
                errors[key] = error.GetValue<string>();
            else if (chapter["document"] is JsonObject document)
                documents[key] = (JsonObject)document.DeepClone();
        }

        var problems = new List<string>();
        string? artifactRoot = null;
        if (wantArtifacts)
        {
            string baseDir = workspace is null
                ? Path.Combine(Path.GetTempPath(), "alas-runtime-selftest")
                : workspace;
            artifactRoot = Path.Combine(baseDir, "case-" + Sanitize(name));
            if (Directory.Exists(artifactRoot)) Directory.Delete(artifactRoot, recursive: true);
            Directory.CreateDirectory(artifactRoot);
        }

        var options = new SessionOptions
        {
            RepoDirectory = "stub://alas",
            ToolsDirectory = "stub://tools",
            Serial = serial,
            DryRun = dryRun,
            AllowActions = allowActions,
            ReadOnlyDevice = node["read_only_device"]?.GetValue<bool>() ?? false,
            ArtifactsDirectory = artifactRoot,
            MaxSeconds = 60,
            MaxRounds = 3,
        };

        int engineCreations = 0;
        StubVisionEngine? stub = null;
        CampaignBatchResult? batch = null;
        var log = new SessionLog(echo: false);
        using var cancel = new CancellationTokenSource();
        AlasSession? session = null;
        try
        {
            session = AlasSession.Start(options, _ =>
            {
                engineCreations++;
                // `cancel_after_chapter`：替身**交付完这一关的文档之后**取消 ——
                // 取消点因此落在关卡边界上，与真实 CLI 的 Ctrl-C 语义一致（不打断进行中的出击）。
                stub = new StubVisionEngine(documents, errors, answered =>
                {
                    if (cancelAfterChapter is not null && answered == cancelAfterChapter)
                        cancel.Cancel();
                });
                return stub;
            }, log);
            batch = new CampaignBatchRunner(session) { StopOnFailure = stopOnFailure }
                .Run(order, cancel.Token);
        }
        catch (Exception error)
        {
            problems.Add($"运行时抛异常: {error.GetType().Name}: {error.Message}");
        }
        finally
        {
            // 显式释放（而不是等 using 作用域结束）：释放是 R1 的验收项，必须能被断言。
            session?.Dispose();
        }
        if (session is not null) problems.AddRange(CheckReleased(session, stub));

        if (batch is null)
            return Report(name, problems, null, engineCreations, artifactRoot, null, log);

        // 结构性不变量：与夹具内容无关，运行时自己必须成立。
        if (engineCreations != 1)
            problems.Add($"宿主构造了 {engineCreations} 次（常驻会话必须只构造一次）");
        if (stub is not null && stub.DeviceConfigureCalls > 1)
            problems.Add($"设备后端配置了 {stub.DeviceConfigureCalls} 次（一次会话只配一次）");
        if (wantArtifacts && batch.IndexPath is null)
            problems.Add("配置了工件目录却没有写出 index.json");

        if (expect is not null)
        {
            Compare(expect, "outcome", batch.Outcome, problems);
            Compare(expect, "cleared", batch.Cleared, problems);
            Compare(expect, "host_start_count", 1, problems);
            Compare(expect, "device_configure_count", stub?.DeviceConfigureCalls ?? 0, problems);
            if (expect["stages"] is JsonArray wantStages)
            {
                if (wantStages.Count != batch.Stages.Count)
                    problems.Add($"关卡数 期望 {wantStages.Count} 实为 {batch.Stages.Count}");
                else
                    for (int i = 0; i < wantStages.Count; i++)
                    {
                        var want = wantStages[i]!;
                        var got = batch.Stages[i];
                        Compare(want, "outcome", got.Outcome, problems, $"第{i + 1}关");
                        Compare(want, "cleared", got.Cleared, problems, $"第{i + 1}关");
                        Compare(want, "failed", got.Failed, problems, $"第{i + 1}关");
                        if (want["violations"] is JsonArray wantViolations)
                        {
                            var expected = wantViolations.Select(v => v!.GetValue<string>())
                                .OrderBy(v => v, StringComparer.Ordinal).ToList();
                            var actual = got.ContractViolations.OrderBy(v => v, StringComparer.Ordinal).ToList();
                            if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
                                problems.Add($"第{i + 1}关违例 期望[{string.Join(",", expected)}] " +
                                             $"实为[{string.Join(",", actual)}]");
                        }
                    }
            }
            if (expect["artifacts"] is JsonArray wantFiles && artifactRoot is not null)
            {
                // 夹具按**文件名**断言：工件落在 `<artifacts>/<runstamp>/` 下，
                // 时间戳目录名不该写进夹具（那会让夹具每天都得改）。
                var names = ListArtifacts(artifactRoot)
                    .Select(Path.GetFileName).OfType<string>().ToList();
                foreach (var want in wantFiles)
                {
                    string file = want!.GetValue<string>();
                    if (!names.Contains(file))
                        problems.Add($"缺少工件 {file}（实际 {string.Join(",", names)}）");
                }
            }
        }

        return Report(name, problems, batch, engineCreations, artifactRoot, stub, log);
    }

    /// <summary>
    /// 任务队列用例（R2）：用替身宿主跑 `TaskQueue` + `CampaignBatchTask`，
    /// 断言"没跑"与"跑失败"分开记、失败即停、断点续跑、宿主仍然只起一次。
    /// </summary>
    private static JsonObject RunQueueCase(JsonNode node, string? workspace)
    {
        string name = node["name"]?.GetValue<string>() ?? "<未命名>";
        bool dryRun = node["dry_run"]?.GetValue<bool>() ?? true;
        bool allowActions = node["allow_actions"]?.GetValue<bool>() ?? false;
        string? serial = node["serial"]?.GetValue<string>();
        bool wantArtifacts = node["artifacts"]?.GetValue<bool>() ?? false;
        bool stopOnFailure = node["stop_on_failure"]?.GetValue<bool>() ?? true;
        var taskNodes = node["tasks"]!.AsArray();
        var expect = node["expect"];

        var requests = new List<Alas.Tasks.TaskRequest>();
        var documents = new Dictionary<string, JsonObject>();
        var errors = new Dictionary<string, string>();
        foreach (var taskNode in taskNodes)
        {
            var request = new Alas.Tasks.TaskRequest
            {
                Id = taskNode!["id"]!.GetValue<string>(),
                Kind = taskNode["kind"]!.GetValue<string>(),
                Input = taskNode["input"] as JsonObject,
                Required = taskNode["required"]?.GetValue<bool>() ?? false,
            };
            requests.Add(request);
            if (taskNode["errors"] is JsonObject chapterErrors)
                foreach (var (chapter, message) in chapterErrors)
                    errors[chapter] = message!.GetValue<string>();
            if (taskNode["documents"] is JsonObject chapterDocuments)
                foreach (var (chapter, document) in chapterDocuments)
                    documents[chapter] = (JsonObject)document!.DeepClone();
        }

        var problems = new List<string>();
        string? artifactRoot = null;
        if (wantArtifacts)
        {
            string baseDir = workspace ?? Path.GetTempPath();
            artifactRoot = Path.Combine(baseDir, "queue-" + Sanitize(name));
            if (Directory.Exists(artifactRoot)) Directory.Delete(artifactRoot, recursive: true);
            Directory.CreateDirectory(artifactRoot);
        }

        var options = new SessionOptions
        {
            RepoDirectory = "stub://alas",
            ToolsDirectory = "stub://tools",
            Serial = serial,
            DryRun = dryRun,
            AllowActions = allowActions,
            ReadOnlyDevice = node["read_only_device"]?.GetValue<bool>() ?? false,
            ArtifactsDirectory = artifactRoot,
            MaxSeconds = 60,
            MaxRounds = 3,
        };

        int engineCreations = 0;
        StubVisionEngine? stub = null;
        Alas.Tasks.QueueResult? queue = null;
        var log = new SessionLog(echo: false);
        using var cancel = new CancellationTokenSource();
        string? cancelAfterOperation = node["cancel_after_op"]?.GetValue<string>();
        int cancelAfterCall = node["cancel_after_call"]?.GetValue<int>() ?? 1;
        AlasSession? session = null;
        try
        {
            session = AlasSession.Start(options, _ =>
            {
                engineCreations++;
                stub = new StubVisionEngine(documents, errors,
                    operationResponses: node["stub_responses"] as JsonObject,
                    afterOperation: (operation, count) =>
                    {
                        if (operation == cancelAfterOperation && count == cancelAfterCall)
                            cancel.Cancel();
                    });
                return stub;
            }, log);
            var runner = new Alas.Tasks.TaskQueue(session)
            {
                StopOnFailure = stopOnFailure,
            }.Register(new Alas.Tasks.CampaignBatchTask())
             .Register(new Alas.Tasks.NavigateTask())    // 小型导航环境（docs/runtime.md 第十五节）
             .Register(new Alas.Tasks.ObserveTask())
             .Register(new Alas.Tasks.PeriodicRunTask())
             .Register(new PreconditionFailureTask());
            if (node["resume_completed"] is JsonArray resumed)
                foreach (var id in resumed)
                    runner.ResumeCompleted.Add(id!.GetValue<string>());
            queue = runner.Run(requests, cancel.Token);
        }
        catch (Exception error)
        {
            problems.Add($"运行时抛异常: {error.GetType().Name}: {error.Message}");
        }
        finally
        {
            session?.Dispose();
        }
        if (session is not null) problems.AddRange(CheckReleased(session, stub));

        if (queue is null)
            return QueueReport(name, problems, null, engineCreations, artifactRoot, stub, log);

        if (engineCreations != 1)
            problems.Add($"宿主构造了 {engineCreations} 次（常驻会话必须只构造一次）");
        if (wantArtifacts && queue.IndexPath is null)
            problems.Add("配置了工件目录却没有写出 queue.json");

        if (expect is not null)
        {
            Compare(expect, "outcome", queue.Outcome, problems);
            Compare(expect, "host_start_count", 1, problems);
            Compare(expect, "device_configure_count", stub?.DeviceConfigureCalls ?? 0, problems);
            Compare(expect, "stopped_early", queue.StoppedEarly, problems);
            if (expect["tasks"] is JsonArray wantTasks)
            {
                if (wantTasks.Count != queue.Tasks.Count)
                    problems.Add($"任务数 期望 {wantTasks.Count} 实为 {queue.Tasks.Count}");
                else
                    for (int i = 0; i < wantTasks.Count; i++)
                    {
                        var want = wantTasks[i]!;
                        var got = queue.Tasks[i];
                        Compare(want, "id", got.Id, problems, $"第{i + 1}个任务");
                        Compare(want, "outcome", got.OutcomeName, problems, $"第{i + 1}个任务");
                        if (want["evidence_equals"] is JsonObject wantValues)
                            foreach (var (path, expectedValue) in wantValues)
                            {
                                JsonNode? actual = got.Evidence;
                                foreach (string segment in path.Split('.'))
                                    actual = actual is JsonObject obj ? obj[segment] : null;
                                if (!JsonNode.DeepEquals(expectedValue, actual))
                                    problems.Add($"第{i + 1}个任务 evidence.{path} 期望 "
                                        + $"{expectedValue?.ToJsonString() ?? "null"} 实为 "
                                        + $"{actual?.ToJsonString() ?? "null"}");
                            }
                        // 失败**文案**也要能断言：第 156 轮那句"入口可能未解锁"的诊断后缀就靠它钉住
                        if (want["error_contains"] is JsonNode wantError)
                        {
                            string needle = wantError.GetValue<string>();
                            if (got.Error is null || !got.Error.Contains(needle, StringComparison.Ordinal))
                                problems.Add($"第{i + 1}个任务 的错误信息里没有 `{needle}`"
                                             + $"（实际：{got.Error ?? "null"}）");
                        }                        // 证据**内容**也要能断言：CI/界面消费的是证据里的字段（如跳数、目标页），
                        // 只断言"结论对"不够 —— 证据缺了字段，看报告的人照样看不到。
                        if (want["evidence_contains"] is JsonArray wantEvidence)
                            foreach (var needleNode in wantEvidence)
                            {
                                string needle = needleNode!.GetValue<string>();
                                string text = got.Evidence?.ToJsonString() ?? "";
                                if (!text.Contains(needle, StringComparison.Ordinal))
                                    problems.Add($"第{i + 1}个任务 的证据里没有 `{needle}`");
                            }                        if (want["error_kind"] is JsonNode wantKind)
                            Compare(want, "error_kind", RuntimeErrors.Name(got.ErrorKind),
                                    problems, $"第{i + 1}个任务");
                    }
            }
            if (expect["artifacts"] is JsonArray wantFiles && artifactRoot is not null)
            {
                var names = ListArtifacts(artifactRoot)
                    .Select(Path.GetFileName).OfType<string>().ToList();
                foreach (var want in wantFiles)
                {
                    string file = want!.GetValue<string>();
                    if (!names.Contains(file))
                        problems.Add($"缺少工件 {file}（实际 {string.Join(",", names)}）");
                }
            }
            if (expect["backend_calls"]?.GetValue<int>() is int wantCalls &&
                stub is not null && stub.Calls.Count != wantCalls)
                problems.Add($"后端调用次数 期望 {wantCalls} 实为 {stub.Calls.Count}");
        }

        return QueueReport(name, problems, queue, engineCreations, artifactRoot, stub, log);
    }

    private static JsonObject QueueReport(string name, List<string> problems,
                                          Alas.Tasks.QueueResult? queue, int engineCreations,
                                          string? artifactRoot, StubVisionEngine? stub, SessionLog log)
    {
        var tasks = new JsonArray();
        if (queue is not null)
            foreach (var task in queue.Tasks)
                tasks.Add(new JsonObject
                {
                    ["id"] = task.Id,
                    ["kind"] = task.Kind,
                    ["outcome"] = task.OutcomeName,
                    ["error_kind"] = RuntimeErrors.Name(task.ErrorKind),
                    ["error"] = task.Error,
                    ["artifact"] = task.ArtifactPath,
                });
        var files = artifactRoot is null || !Directory.Exists(artifactRoot)
            ? new JsonArray()
            : new JsonArray(ListArtifacts(artifactRoot)
                .OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => (JsonNode)JsonValue.Create(f)!).ToArray());
        return new JsonObject
        {
            ["name"] = name,
            ["ok"] = problems.Count == 0,
            ["problems"] = new JsonArray(problems.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray()),
            ["outcome"] = queue?.Outcome ?? "failed",
            ["cleared"] = queue?.Succeeded ?? false,
            // 运行目录给出来，Python 侧才能直接读工件做断言（例如边界快照）。
            ["run_directory"] = queue?.IndexPath is null ? null : Path.GetDirectoryName(queue.IndexPath),
            ["stopped_early"] = queue?.StoppedEarly ?? false,
            ["stop_reason"] = queue?.StopReason,
            ["host_start_count"] = engineCreations,
            ["device_configure_count"] = stub?.DeviceConfigureCalls ?? 0,
            ["backend_calls"] = stub?.Calls.Count ?? 0,
            ["backend_ops"] = stub is null ? new JsonArray() : new JsonArray(
                stub.Calls.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray()),
            ["artifacts"] = files,
            ["artifact_names"] = new JsonArray(files
                .Select(f => f!.GetValue<string>())
                .Select(Path.GetFileName).OfType<string>()
                .OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => (JsonNode)JsonValue.Create(f)!).ToArray()),
            ["log_entries"] = log.Entries.Count,
            ["tasks"] = tasks,
            ["stages"] = new JsonArray(),
        };
    }

    private static IEnumerable<string> CheckReleased(AlasSession session, StubVisionEngine? stub)    {
        if (stub is not null && !session.Vision.Equals(stub))
            yield return "会话持有的宿主与替身不一致";
        if (stub is not null && !stub.Disposed)
            yield return "会话释放后替身宿主没有被 Dispose";
    }

    /// <summary>
    /// 工件是按**运行批次**分目录的（`<artifacts>/<runstamp>/...`），所以这里列相对路径：
    /// 夹具期望写 `sortie-1-1.json`，人看报告时也知道它属于哪一次运行。
    /// </summary>
    private static IEnumerable<string> ListArtifacts(string root)
    {
        if (!Directory.Exists(root)) yield break;
        foreach (var path in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            yield return Path.GetRelativePath(root, path).Replace('\\', '/');
    }

    private static JsonObject Report(string name, List<string> problems, CampaignBatchResult? batch,
                                     int engineCreations, string? artifactRoot,
                                     StubVisionEngine? stub, SessionLog log)
    {
        var stages = new JsonArray();
        if (batch is not null)
            foreach (var stage in batch.Stages)
                stages.Add(new JsonObject
                {
                    ["chapter"] = stage.Chapter,
                    ["stage"] = stage.Stage,
                    ["outcome"] = stage.Outcome,
                    ["cleared"] = stage.Cleared,
                    ["failed"] = stage.Failed,
                    ["skipped"] = stage.Skipped,
                    ["error_kind"] = RuntimeErrors.Name(stage.ErrorKind),
                    ["error"] = stage.Error,
                    ["violations"] = new JsonArray(
                        stage.ContractViolations.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()),
                    ["artifact"] = stage.ArtifactPath,
                });
        var files = artifactRoot is null || !Directory.Exists(artifactRoot)
            ? new JsonArray()
            : new JsonArray(ListArtifacts(artifactRoot)
                .OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => (JsonNode)JsonValue.Create(f)!).ToArray());
        return new JsonObject
        {
            ["name"] = name,
            ["ok"] = problems.Count == 0,
            ["problems"] = new JsonArray(problems.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray()),
            ["outcome"] = batch?.Outcome ?? "error",
            ["cleared"] = batch?.Cleared ?? false,
            ["stopped_early"] = batch?.StoppedEarly ?? false,
            ["stop_reason"] = batch?.StopReason,
            ["host_start_count"] = engineCreations,
            ["device_configure_count"] = stub?.DeviceConfigureCalls ?? 0,
            ["backend_calls"] = stub?.Calls.Count ?? 0,
            ["backend_ops"] = stub is null ? new JsonArray() : new JsonArray(
                stub.Calls.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray()),
            ["artifacts"] = files,
            ["artifact_names"] = new JsonArray(files
                .Select(f => f!.GetValue<string>())
                .Select(Path.GetFileName).OfType<string>()
                .OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => (JsonNode)JsonValue.Create(f)!).ToArray()),
            ["log_entries"] = log.Entries.Count,
            ["error_kinds"] = new JsonArray(log.Entries
                .Where(e => e.Level == "ERROR")
                .Select(e => (JsonNode)JsonValue.Create(e.Scope)!).ToArray()),
            ["stages"] = stages,
        };
    }

    private static void Compare(JsonNode expect, string key, object? actual, List<string> problems,
                                string prefix = "")
    {
        if (expect[key] is not JsonNode want) return;
        string wantText = want.ToJsonString().Trim('"');
        string actualText = actual switch
        {
            null => "null",
            bool b => b ? "true" : "false",
            _ => actual.ToString()!,
        };
        if (!string.Equals(wantText, actualText, StringComparison.Ordinal))
            problems.Add($"{prefix}{key} 期望 {wantText} 实为 {actualText}");
    }

    private static string Sanitize(string name)
        => new(name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
    private sealed class PreconditionFailureTask : Alas.Tasks.ITaskRunner
    {
        public string Kind => "test_precondition_error";
        public IReadOnlyList<string> Preconditions(Alas.Tasks.TaskRequest request,
                                                   Alas.Tasks.TaskContext context)
            => throw new InvalidOperationException("fixture precondition failure");
        public Alas.Tasks.TaskResult Run(Alas.Tasks.TaskRequest request,
                                        Alas.Tasks.TaskContext context, CancellationToken token)
            => throw new InvalidOperationException("precondition failure must not run");
    }
}

/// <summary>
/// 替身识图后端：按 op + chapter 返回预先准备好的文档。
/// **它替换的是 Python 宿主，不是运行时** —— 批处理、合同裁决、工件落盘、
/// 取消与释放全部走真实代码路径（这是本自检有意义的前提）。
/// </summary>
internal sealed class StubVisionEngine : VisionEngineBase
{
    private readonly Dictionary<string, JsonObject> _documents;
    private readonly Dictionary<string, string> _errors;
    private readonly Action<string>? _afterResponse;
    private readonly JsonObject? _operationResponses;
    private readonly Action<string, int>? _afterOperation;
    private readonly Dictionary<string, int> _operationCounts = new(StringComparer.Ordinal);

    public List<string> Calls { get; } = new();
    public int DeviceConfigureCalls { get; private set; }
    public bool Disposed { get; private set; }

    public StubVisionEngine(Dictionary<string, JsonObject> documents, Dictionary<string, string> errors,
                            Action<string>? afterResponse = null, JsonObject? operationResponses = null,
                            Action<string, int>? afterOperation = null)
    {
        _documents = documents;
        _errors = errors;
        _afterResponse = afterResponse;
        _operationResponses = operationResponses;
        _afterOperation = afterOperation;
    }

    protected override JsonNode CallRaw(string op, object? args)
    {
        var payload = args is null
            ? new JsonObject()
            : JsonNode.Parse(JsonSerializer.Serialize(args))!.AsObject();
        string chapter = payload["chapter"]?.GetValue<string>() ?? "";
        Calls.Add($"{op}:{chapter}");
        int count = _operationCounts.GetValueOrDefault(op) + 1;
        _operationCounts[op] = count;
        try
        {
            // 脚本只替换宿主返回；用例可以让第一次抓帧成功、后续抓帧失败，或返回页面错误。
            // 序列耗尽后重复最后一项，避免测试结果依赖调度器的精确 tick 时序。
            if (_operationResponses?[op] is JsonArray { Count: > 0 } sequence)
            {
                var response = sequence[Math.Min(count - 1, sequence.Count - 1)]!;
                if (response["error"] is JsonNode error)
                    throw new VisionWorkerException(op, error.GetValue<string>(), "<stub>");
                return response["result"]?.DeepClone() ?? new JsonObject();
            }
            return DefaultResponse(op, payload, chapter);
        }
        finally
        {
            // 注入取消时先完整交付本次宿主调用；runner 必须在 tick 边界才响应它。
            _afterOperation?.Invoke(op, count);
        }
    }

    private JsonNode DefaultResponse(string op, JsonObject payload, string chapter)
    {
        switch (op)
        {
            case "device_configure":
                DeviceConfigureCalls++;
                return new JsonObject
                {
                    ["configured"] = new JsonObject
                    {
                        ["serial"] = payload["serial"]?.GetValue<string>(),
                        ["screenshot"] = payload["screenshot"]?.GetValue<string>(),
                        ["control"] = payload["control"]?.GetValue<string>(),
                    },
                };
            case "s3_run_plan":
                if (_errors.TryGetValue(chapter, out var message))
                    throw new VisionWorkerException(op, message, "<stub>");
                if (_documents.TryGetValue(chapter, out var document))
                {
                    var clone = (JsonObject)document.DeepClone();
                    _afterResponse?.Invoke(chapter);
                    return clone;
                }
                throw new VisionWorkerException(op, $"替身没有这一关的文档: {chapter}", "<stub>");
            // ---- 小型导航环境（规格见 docs/runtime.md 第十五节）----
            // 一张固定小图 + 一个"随点击迁移"的当前页状态机，用来在离线自检里真正驱动
            // PageNavigator（不再只是"构造得出来"，而是"跑得起来、且能跑到失败"）。
            // 注意：这里的小图是**测试夹具**，不是第二份页面表 —— 产品路径永远用真上游图。
            case "ui_page_graph":
                return NavigationGraphJson();
            case "page_current":
                return new JsonObject { ["hit"] = new JsonArray(_currentPage) };
            case "button_match":
                return NavigationButtonMatch(payload);
            case "device_click":
                NavigationClick(payload);
                return new JsonObject { ["ms"] = 1 };
            case "device_back":
                return new JsonObject { ["ms"] = 1 };
            case "device_capture_set":
                return new JsonObject
                {
                    ["capture_ms"] = 12.5,
                    ["method"] = "stub",
                    ["raw"] = payload["raw"]?.GetValue<bool>() ?? true,
                    ["shape"] = new JsonArray(720, 1280, 3),
                };
            case "map_detect":
                return new JsonObject
                {
                    ["backend"] = "upstream-stub",
                    ["detected"] = true,
                    ["grid_count"] = payload["mode"]?.GetValue<string>() == "os" ? 18 : 24,
                    ["reason"] = null,
                };
            default:
                return new JsonObject();
        }
    }

    /// <summary>固定小图：a → b → c，另有 b → dead（点了**不动**，用来模拟"入口未解锁"）。</summary>
    private static readonly (string From, string To, string Button, int X, int Y)[] NavEdges =
    {
        ("page_a", "page_b", "ui/A_TO_B", 500, 300),
        ("page_b", "page_c", "ui/B_TO_C", 600, 320),
        ("page_b", "page_dead", "ui/B_TO_DEAD", 700, 340),
    };

    /// <summary>"点了不动的入口"：走到它不迁移当前页 —— 正是真机上账号未解锁时的表现。</summary>
    private static readonly string[] InertTargets = { "page_dead" };

    private string _currentPage = "page_a";

    internal string CurrentPage => _currentPage;
    internal void ResetNavigation() => _currentPage = "page_a";

    private static JsonObject NavigationGraphJson()
    {
        // **所有出现在边上的页都要成为节点**（含只作为目标的页）——
        // 第一版只按 From 分组，于是 page_c / page_dead 不是节点，
        // 导航任务的前置条件直接判"目标页不在图里"→ skipped（用例当场抓出来）。
        var names = NavEdges.SelectMany(e => new[] { e.From, e.To }).Distinct().ToList();
        var nodes = new JsonArray();
        foreach (var name in names)
        {
            var links = new JsonArray();
            foreach (var edge in NavEdges.Where(e => e.From == name))
                links.Add(new JsonObject
                {
                    ["to"] = edge.To,
                    ["button"] = edge.Button,
                    ["variants"] = new JsonArray(edge.Button),
                });
            nodes.Add(new JsonObject
            {
                ["name"] = name,
                ["check"] = $"ui/{name.ToUpperInvariant()}_CHECK",
                ["links"] = links,
            });
        }
        return new JsonObject
        {
            ["nodes"] = nodes,
            ["node_count"] = names.Count,
            ["edge_count"] = NavEdges.Length,
        };
    }

    /// <summary>只对**当前页的出边按钮**报命中；其余一律不命中（导航器按分数择优，够用）。</summary>
    private JsonObject NavigationButtonMatch(JsonObject payload)
    {
        string asset = payload["asset"]?.GetValue<string>() ?? "";
        var edge = NavEdges.FirstOrDefault(e => e.From == _currentPage && e.Button == asset);
        if (edge.Button is null)
            return new JsonObject { ["match"] = false, ["similarity"] = 0.85, ["score"] = 0.0 };
        return new JsonObject
        {
            ["match"] = true,
            ["similarity"] = 0.85,
            ["score"] = 0.99,
            ["button_offset"] = new JsonArray(edge.X - 10, edge.Y - 10, edge.X + 10, edge.Y + 10),
        };
    }

    /// <summary>按坐标反查边并迁移；查不到、或目标是"点了不动"的入口 → 当前页不变。</summary>
    private void NavigationClick(JsonObject payload)
    {
        int x = payload["x"]?.GetValue<int>() ?? -1;
        int y = payload["y"]?.GetValue<int>() ?? -1;
        var edge = NavEdges.FirstOrDefault(e => e.From == _currentPage && e.X == x && e.Y == y);
        if (edge.Button is null) return;
        if (InertTargets.Contains(edge.To)) return;      // 点了没反应：当前页不变
        _currentPage = edge.To;
    }

    public override void Dispose()
    {
        Disposed = true;
        base.Dispose();
    }
}
