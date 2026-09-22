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
            var report = RunCase(node!, workspace);
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

    private static IEnumerable<string> CheckReleased(AlasSession session, StubVisionEngine? stub)
    {
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

    public List<string> Calls { get; } = new();
    public int DeviceConfigureCalls { get; private set; }
    public bool Disposed { get; private set; }

    public StubVisionEngine(Dictionary<string, JsonObject> documents, Dictionary<string, string> errors,
                            Action<string>? afterResponse = null)
    {
        _documents = documents;
        _errors = errors;
        _afterResponse = afterResponse;
    }

    protected override JsonNode CallRaw(string op, object? args)
    {
        var payload = args is null
            ? new JsonObject()
            : JsonNode.Parse(JsonSerializer.Serialize(args))!.AsObject();
        string chapter = payload["chapter"]?.GetValue<string>() ?? "";
        Calls.Add($"{op}:{chapter}");
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
            default:
                return new JsonObject();
        }
    }

    public override void Dispose()
    {
        Disposed = true;
        base.Dispose();
    }
}
