using System.Text.Json.Nodes;
using Alas.Contracts;
using Alas.UI.ViewModels;

namespace Alas.UI.Headless;

/// <summary>Wire-format fixtures from upstream report and strategy contracts.</summary>
internal static class ControlUiBackendChecks
{
    public static async Task Verify()
    {
        var backend = new FixtureBackend();
        var reports = new EngineMeowfficerReportBackend(backend);
        var report = await reports.LoadAsync("fixture", CancellationToken.None)
            ?? throw new Exception("Missing report");
        var cat = report.Cats.Single();
        Check(report.GeneratedAt == "2026-01-01" && report.Count == 1 && cat.Cat == "fixture-cat", "report envelope");
        Check(cat.Tags!.Single() == "SSR" && cat.PointsSpent == 5 && cat.Primary == "fleet", "cat fields");
        Check(cat.Talents!.Single() is { Level: 2, Inferred: true, Kind: "special" }, "talent fields");
        Check(cat.Rubrics!.Single() is { Key: "fleet", X: null, YLabel: "weighted", Primary: true }
              && cat.Rubrics![0].YHits!.Single() == "hit", "rubric fields");
        Check(cat.Advice is { Cost: 400, CostEstimated: true, PointsSpent: 5, CostText: "fixture cost" }
              && cat.Advice.Targets!.Single() == "target", "advice fields");
        await reports.ClearAsync("fixture", CancellationToken.None);
        Check(backend.Cleared == "fixture", "clear selected instance");
        await ExpectFailure(() => reports.LoadAsync("wrong-instance", CancellationToken.None));

        var editor = new EngineTaskEditorBackend(backend);
        var queue = await editor.SaveQueueAsync("fixture", "observe", new JsonObject(), CancellationToken.None);
        Check(queue["tasks"] is JsonArray { Count: 1 } && queue["tasks"]![0]!["kind"]!.GetValue<string>() == "observe",
            "Engine task input is saved as a queue");
        await editor.RunQueueAsync("fixture", "campaign_run", new JsonObject { ["stage"] = "1-1" }, CancellationToken.None);
        Check(backend.StartedRun is { Instance: "fixture", Mode: ControlRunMode.Actions, ConfirmActions: true }
              && backend.StartedRun.Queue["tasks"]![0]!["kind"]!.GetValue<string>() == "campaign_run",
            "Engine task intent preserves selected instance and runner kind");
        backend.State["active"] = new JsonObject { ["status"] = "idle" };
        await VerifyEngineQueue(backend);
        VerifyObservation();
        VerifyInstanceProjection();
        VerifyLateInstanceResponse();
        await VerifyConfigAdapter();
        Console.WriteLine("PASS: shared Engine adapters preserve report fields and strict queue contracts");
    }

    private static void VerifyObservation()
    {
        var overview = new OverviewViewModel();
        var rail = new RailViewModel(overview);
        overview.SetInstance("fixture");
        var state = JsonNode.Parse("""
            {"active":{"instance":"fixture","status":"running","started_at":"run-one",
              "engine":{"contract":"engine-activity/1","source":"engine-queue","phase":"running","task":"observe",
                "tasks":[{"id":"observe","kind":"observe","state":"running"},{"id":"navigate","kind":"navigate","state":"pending"},{"id":"map","kind":"map_observe","state":"waiting"}],
                "resources":[{"name":"Oil","value":1234,"limit":25000,"record":"2026-01-02 03:04:05"},
                             {"name":"Coin","value":9999,"record":"2020-01-01 00:00:00"}],
                "logs":{"entries":[{"id":1,"level":"WARNING","message":"native","time":"2026-01-02T00:04:02Z"}]}}},
             "recent_logs":[{"id":1,"level":"INFO","message":"core","time":"2026-01-02T00:00:01Z"}]}
            """)!.AsObject();
        overview.ApplyState(state);
        Check(rail.RunningCount == "1" && rail.PendingCount == "1" && rail.WaitingCount == "1" && rail.PlanCountText == "3",
            "native current task is not counted twice in pending");
        Check(rail.Groups[0].Tasks.Single().Name == "读取页面状态", "Engine runner maps to its UI label");
        Check(overview.Resources[0].Value.Contains("234") && overview.Resources[0].HasLimit
              && overview.Resources[1].Value == "—", "recorded resource versus sentinel timestamp");
        Check(overview.VisibleLogs.Select(row => row.Message).SequenceEqual(["core", "native"]), "merge log sources chronologically");
        overview.ApplyState(state);
        Check(overview.CachedLogCount == 2, "repeated snapshot does not duplicate logs");
        overview.ClearCommand.Execute(null);
        overview.ApplyState(state);
        Check(overview.CachedLogCount == 0, "clear retains cursor floor across refresh");
        state["active"]!["engine"]!["logs"]!["entries"]!.AsArray().Add(new JsonObject
            { ["id"] = 2L, ["level"] = "ERROR", ["message"] = "new native", ["time"] = "2026-01-02T00:00:03Z" });
        state["active"]!["engine"]!["phase"] = "waiting";
        state["active"]!["engine"]!["tasks"]![0]!["state"] = "pending";
        state["active"]!["engine"]!["tasks"]![2]!["state"] = "waiting";
        overview.ApplyState(state);
        if (overview.VisibleLogs.Single().Message != "new native" || rail.RunningCount != "0" || rail.PendingCount != "2")
            throw new Exception($"Engine UI: waiting Engine queue is not a running task; only new logs appear (running={rail.RunningCount}, pending={rail.PendingCount}, waiting={rail.WaitingCount}, tasks={state["active"]!["engine"]!["tasks"]!.ToJsonString()})");
        state["active"]!["started_at"] = "run-two";
        overview.ApplyState(state);
        Check(overview.CachedLogCount == 3, "new run resets stream cursors");
        overview.SetInstance("another");
        overview.ApplyState(state);
        Check(overview.CachedLogCount == 0 && rail.PlanCountText == "0" && overview.Resources.All(card => card.Value == "—"),
            "instance switch clears observed logs, tasks and resources");

        var resourceSnapshot = JsonNode.Parse("""
            {"resources":[{"name":"ActionPoint","value":100,"total":160,"record":"2026-01-02 03:04:05"},
                          {"name":"CustomKey","label":"来自上游","value":42,"record":"2026-01-02 03:04:05"}]}
            """)!.AsObject();
        var cards = EngineActivityObservation.Resources(resourceSnapshot, ["CustomKey", "ActionPoint", "Chip"]);
        Check(cards[0].Name == "来自上游" && cards[0].HasFallback && cards[1].Limit == "/ 总行动力 160"
              && cards[2].Value == "—", "upstream resource order, unknown keys and total action points");
        Console.WriteLine("PASS: Engine queue observations, resource records, log cursors and instance isolation");
    }

    private static async Task VerifyEngineQueue(FixtureBackend backend)
    {
        var overview = new OverviewViewModel(backend: backend);
        overview.SetInstance("fixture");
        Check(!overview.IsEngineControlEnabled, "Engine waits for an authoritative state");
        overview.ApplyState(backend.State);
        Check(overview.IsEngineControlEnabled, "idle Engine queue can start");
        await overview.ToggleEngineAsync();
        if (backend.StartedRun is not { } started || started.Queue["tasks"] is not JsonArray tasks ||
            tasks[0]?["kind"]?.GetValue<string>() != "observe" || !overview.IsEngineRunning ||
            started.Instance != "fixture" || started.Mode != ControlRunMode.ReadOnly)
            throw new Exception($"Engine UI: start reaches the Engine queue and updates state: instance={backend.StartedRun?.Instance}, mode={backend.StartedRun?.Mode}, queue={backend.StartedRun?.Queue.ToJsonString()}, running={overview.IsEngineRunning}");
        backend.State["active"]!["engine"] = new JsonObject { ["phase"] = "waiting" };
        overview.ApplyState(backend.State);
        Check(overview.EngineStatusText == "等待中", "native waiting status is shown");
        await overview.ToggleEngineAsync();
        Check(backend.StopRequests == 1 && !overview.IsEngineControlEnabled &&
              overview.EngineButtonText == "正在停止…", "stop waits for native boundary acknowledgement");
        overview.SetInstance("another");
        overview.ApplyState(backend.State);
        Check(!overview.IsEngineRunning && !overview.IsEngineControlEnabled,
              "another instance cannot claim or stop this run");
        backend.State["active"]!["status"] = "completed";
        overview.ApplyState(backend.State);
        Check(overview.IsEngineControlEnabled, "completed foreign run releases the single device slot");
        overview.ReportBackendError("fixture connection failure");
        Check(!overview.IsEngineControlEnabled, "unknown state does not allow duplicate starts");
    }

    private static async Task ExpectFailure(Func<Task> action)
    {
        try { await action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Malformed upstream response was accepted");
    }
    private static void Check(bool ok, string message)
    { if (!ok) throw new Exception("Engine UI: " + message); }

    private static void VerifyInstanceProjection()
    {
        var overview = new OverviewViewModel();
        var rail = new RailViewModel(overview);
        overview.SetInstance("fixture");
        var state = JsonNode.Parse("""
            {"active":{"instance":"another","status":"running"},"overview":{"instance":"fixture",
              "pending":[{"name":"Reward","next_run":"2020-01-01 00:00:00"}],
              "waiting":[{"name":"Research","next_run":"2999-01-01 00:00:00"}],
              "resources":[{"name":"Oil","value":1234,"record":"2026-01-01 00:00:00"}]},
             "recent_logs":[{"id":1,"message":"another instance log"}]}
            """)!.AsObject();
        overview.ApplyState(state);
        Check(!overview.IsEngineRunning && rail.PendingCount == "1" && rail.WaitingCount == "1" &&
              overview.Resources[0].Value.Contains("234") && overview.CachedLogCount == 0,
            "idle instance uses its saved plan/resources even while another instance runs");
        state["active"] = JsonNode.Parse("""
            {"instance":"fixture","status":"running","engine":{"source":"engine-queue","phase":"running","task":"observe",
              "tasks":[{"id":"observe","kind":"observe","state":"running"}],"resources":[],"logs":{"entries":[]}}}
            """);
        state["recent_logs"] = new JsonArray();
        overview.ApplyState(state);
        Check(rail.RunningCount == "1" && rail.PendingCount == "0", "live native lists take precedence over config display");
        state["active"]!["status"] = "completed";
        state["active"]!["engine"]!["logs"]!["entries"]!.AsArray().Add(new JsonObject
            { ["id"] = 1L, ["message"] = "final native log", ["level"] = "INFO" });
        state["recent_logs"] = new JsonArray();
        overview.ApplyState(state);
        Check(rail.RunningCount == "0" && rail.PendingCount == "1" && overview.VisibleLogs.Single().Message == "final native log",
            "completed run returns to saved plan and still consumes its final log tail");
    }

    private static void VerifyLateInstanceResponse()
    {
        var pending = new List<TaskCompletionSource<JsonObject>>();
        var backend = new FixtureBackend { InstanceRead = name =>
        {
            var response = new TaskCompletionSource<JsonObject>();
            pending.Add(response);
            return response.Task;
        } };
        var shell = new ShellViewModel(new Alas.UI.Theming.MemoryThemeStore(), backend);
        shell.SelectInstance("first");
        shell.SelectInstance("second");
        shell.SelectInstance("first");
        Check(pending.Count == 3, "changing instance does not wait for an obsolete request");
        JsonObject Snapshot(string name, int value) => JsonNode.Parse($$$"""
            {"active":{"status":"idle"},"overview":{"instance":"{{{name}}}","pending":[],"waiting":[],
              "resources":[{"name":"Oil","value":{{{value}}},"record":"2026-01-01 00:00:00"}]}}
            """)!.AsObject();
        pending[2].SetResult(Snapshot("first", 333));
        pending[0].SetResult(Snapshot("first", 111));
        pending[1].SetResult(Snapshot("second", 222));
        Check(shell.Overview.Resources[0].Value == "333", "late A/B responses cannot overwrite the newest A selection");
        Console.WriteLine("PASS: idle instance overview, native precedence, final logs and delayed instance switch isolation");
    }

    private static async Task VerifyConfigAdapter()
    {
        var backend = new FixtureBackend
        {
            Listed = [new() { Instance = "fixture", Revision = "rev", Server = "en", Serial = "fixture-device", Status = "running" },
                new() { Instance = "disabled", Revision = "rev", Server = "disabled" }],
        };
        var adapter = new EngineConfigInstancesBackend(backend);
        var list = await adapter.ListInstancesAsync();
        Check(list[0] is { Name: "fixture", Server: "国际服", Serial: "fixture-device", Status: "running" }
            && list[1].Server == "", "instance list preserves live status and uses upstream server translations");
        var config = await adapter.ReadConfigAsync("fixture");
        Check(config.Revision == "rev-fixture" && JsonNode.DeepEquals(JsonNode.Parse(config.ValuesJson), backend.ConfigValues),
            "export contains only configuration values and deletion retains the revision");
        Check(await adapter.CreateInstanceAsync("normalized ", "source", null) == "normalized" &&
            backend.Created is { Instance: "normalized ", Source: "source", ImportFile: null }, "create preserves source and uses normalized result");
        await adapter.CreateInstanceAsync("imported", null, "upload");
        Check(backend.Created is { Source: null, ImportFile: "upload" }, "import source stays distinct from template source");
        await adapter.ImportConfigAsync("upload", "{\"Alas\":{}}");
        Check(backend.Imported is { Name: "upload", Content: "{\"Alas\":{}}" } &&
            (await adapter.ListImportsAsync()).Single().Name == "upload", "local import passes content and refreshes candidate sources");
        await adapter.DeleteInstanceAsync("fixture", config.Revision);
        Check(backend.Deleted is { Instance: "fixture", Revision: "rev-fixture" } && backend.RefreshCalls == 3,
            "revision-safe delete and create update the shared instance source");
    }

    internal sealed class FixtureBackend : IAlasUiBackend
    {
        public Func<Task<DeploySettingsResponse>>? DeployRead;
        public DeploySettingsPatchRequest? DeployPatch;
        public Func<DeploySettingsPatchRequest, Task<DeploySettingsPatchResponse>>? DeployPatchHandler;
        public Task<DeploySettingsResponse> ReadDeploySettingsAsync(string language = "zh-CN", CancellationToken cancellationToken = default)
            => DeployRead?.Invoke() ?? Task.FromException<DeploySettingsResponse>(new NotSupportedException());
        public Task<DeploySettingsPatchResponse> PatchDeploySettingsAsync(DeploySettingsPatchRequest request, CancellationToken cancellationToken = default)
        {
            DeployPatch = request;
            if (DeployPatchHandler is not null) return DeployPatchHandler(request);
            return Task.FromResult(new DeploySettingsPatchResponse { Updated = request.Values.Select(item => item.Key).ToArray() });
        }
        public Task<StartupRunResponse> ReadStartupRunAsync(string instance, CancellationToken cancellationToken = default) => Task.FromException<StartupRunResponse>(new NotSupportedException());
        public Task<StartupRunResponse> SetStartupRunAsync(StartupRunRequest request, CancellationToken cancellationToken = default) => Task.FromException<StartupRunResponse>(new NotSupportedException());
        public bool IsConnected => true;
        public IReadOnlyList<InstanceCardViewModel> Instances => [];
        public event EventHandler? Changed { add { } remove { } }
        public int RefreshCalls;
        public void Refresh() { RefreshCalls++; }
        public void Dispose() { }
        public Func<string, Task<JsonObject>>? InstanceRead;
        public string? Cleared;
        public ControlRunRequest? StartedRun;
        public int StopRequests;
        public IReadOnlyList<InstanceSummary> Listed = [];
        public InstanceCreateRequest? Created;
        public InstanceDeleteRequest? Deleted;
        public InstanceImportRequest? Imported;
        public JsonObject ConfigValues = new() { ["Alas"] = new JsonObject() };
        public JsonObject SavedQueue = new();
        public JsonObject State = new() { ["active"] = new JsonObject { ["status"] = "idle" } };
        public Task<JsonObject> ReadMeowfficerAsync(MeowfficerRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(JsonNode.Parse("""
            {"instance":"fixture","generatedAt":"2026-01-01","count":1,"cats":[{
              "cat":"fixture-cat","tags":["SSR"],"pointsSpent":5,"primary":"fleet",
              "talents":[{"name":"fixture","level":2,"kind":"special","inferred":true}],
              "rubrics":[{"key":"fleet","label":"fixture","x":null,"yLabel":"weighted","yHits":["hit"],"primary":true}],
              "advice":{"verdict":"keep","headline":"fixture","reason":"fixture","cost":400,
                        "costEstimated":true,"pointsSpent":5,"costText":"fixture cost","targets":["target"]}
            }]}
            """)!.AsObject());
        public Task<JsonObject> ClearMeowfficerAsync(string instance, CancellationToken cancellationToken = default)
        { Cleared = instance; return Task.FromResult(new JsonObject { ["cleared"] = true }); }
        public Task StartRunAsync(ControlRunRequest request, CancellationToken cancellationToken = default)
        {
            StartedRun = request;
            State["active"] = new JsonObject { ["status"] = "running", ["kind"] = "queue", ["instance"] = request.Instance };
            return Task.CompletedTask;
        }
        public Task<JsonObject> ReadStateAsync(CancellationToken cancellationToken = default) => Task.FromResult((JsonObject)State.DeepClone());
        public Task<InstanceListResponse> ReadInstancesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new InstanceListResponse { Instances = Listed });
        public Task<JsonObject> ReadInstanceStateAsync(string instance, CancellationToken cancellationToken = default)
            => InstanceRead?.Invoke(instance) ?? ReadStateAsync(cancellationToken);
        public Task<JsonObject?> ReadReportAsync(string stamp, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ConfigResponse> ReadConfigAsync(string instance, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConfigResponse { Instance = instance, Revision = "rev-" + instance, Values = (JsonObject)ConfigValues.DeepClone() });
        public Task<ConfigResponse> CreateInstanceAsync(InstanceCreateRequest request, CancellationToken cancellationToken = default)
        { Created = request; return ReadConfigAsync(request.Instance.Trim(), cancellationToken); }
        public Task DeleteInstanceAsync(InstanceDeleteRequest request, CancellationToken cancellationToken = default)
        { Deleted = request; return Task.CompletedTask; }
        public Task<InstanceImportSource> ImportInstanceAsync(InstanceImportRequest request, CancellationToken cancellationToken = default)
        { Imported = request; return Task.FromResult(new InstanceImportSource { Name = request.Name, ModifiedAt = DateTimeOffset.UnixEpoch }); }
        public Task<InstanceImportListResponse> ReadInstanceImportsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new InstanceImportListResponse { Sources = Imported is null ? [] :
                [new InstanceImportSource { Name = Imported.Name, ModifiedAt = DateTimeOffset.UnixEpoch }] });
        public Task SaveQueueAsync(JsonObject queue, CancellationToken cancellationToken = default)
        { SavedQueue = (JsonObject)queue.DeepClone(); return Task.CompletedTask; }
        public Task<bool> RequestStopAsync(CancellationToken cancellationToken = default)
        { StopRequests++; State["active"]!["stop_requested"] = true; return Task.FromResult(true); }
        public Task<JsonObject> ReadStatisticsAsync(StatisticsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JsonObject> RefreshStatisticsLootAsync(string instance, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
