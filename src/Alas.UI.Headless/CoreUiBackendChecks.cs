using System.Text.Json.Nodes;
using Alas.Contracts;
using Alas.UI.ViewModels;

namespace Alas.UI.Headless;

/// <summary>Wire-format fixtures from upstream report and strategy contracts.</summary>
internal static class CoreUiBackendChecks
{
    public static async Task Verify()
    {
        var backend = new FixtureBackend();
        var reports = new CoreMeowfficerReportBackend(backend);
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

        var editor = new CoreTaskEditorBackend(backend);
        var validation = await editor.ValidateScriptAsync("fixture", "Shop", "bad", CancellationToken.None);
        Check(!validation.Valid && validation.Diagnostics.Single() is
            { Code: "forbidden_statement", Line: 2, Column: 3, Message: "fixture diagnostic" }, "Lua diagnostics preserve code and location");
        backend.Validation = new JsonObject { ["ok"] = true };
        await ExpectFailure(() => editor.ValidateScriptAsync("fixture", "Shop", "bad", CancellationToken.None));
        backend.Validation = JsonNode.Parse("""{"valid":true,"diagnostics":[null]}""")!.AsObject();
        await ExpectFailure(() => editor.ValidateScriptAsync("fixture", "Shop", "bad", CancellationToken.None));
        await editor.RunAsync("fixture", "Reward", CancellationToken.None);
        Check(backend.Started is { Instance: "fixture", Task: "Reward", ConfirmActions: true }, "task intent preserves selected instance");
        await VerifyScheduler(backend);
        VerifyObservation();
        Console.WriteLine("PASS: shared Core adapters preserve upstream report fields and strict strategy diagnostics");
    }

    private static void VerifyObservation()
    {
        var overview = new OverviewViewModel();
        var rail = new RailViewModel(overview);
        overview.SetInstance("fixture");
        var state = JsonNode.Parse("""
            {"active":{"instance":"fixture","status":"running","started_at":"run-one",
              "scheduler":{"phase":"running","task":"Reward","next_run":"2026-01-01 00:00:00",
                "pending":[{"name":"Reward","next_run":"2026-01-01 00:00:00"},{"name":"Commission","next_run":"2026-01-02 00:00:00"}],
                "waiting":[{"name":"Research","next_run":"2030-01-01 00:00:00"}],
                "resources":[{"name":"Oil","value":1234,"limit":25000,"record":"2026-01-02 03:04:05"},
                             {"name":"Coin","value":9999,"record":"2020-01-01 00:00:00"}],
                "logs":{"entries":[{"id":1,"level":"WARNING","message":"native","time":"2026-01-02T00:00:02Z"}]}}},
             "recent_logs":[{"id":1,"level":"INFO","message":"core","time":"2026-01-02T00:00:01Z"}]}
            """)!.AsObject();
        overview.ApplyState(state);
        Check(rail.RunningCount == "1" && rail.PendingCount == "1" && rail.WaitingCount == "1" && rail.PlanCountText == "3",
            "native current task is not counted twice in pending");
        Check(rail.Groups[0].Tasks.Single().Name == "收获", "native command maps to upstream UI label");
        Check(overview.Resources[0].Value.Contains("234") && overview.Resources[0].HasLimit
              && overview.Resources[1].Value == "—", "recorded resource versus sentinel timestamp");
        Check(overview.VisibleLogs.Select(row => row.Message).SequenceEqual(["core", "native"]), "merge log sources chronologically");
        overview.ApplyState(state);
        Check(overview.CachedLogCount == 2, "repeated snapshot does not duplicate logs");
        overview.ClearCommand.Execute(null);
        overview.ApplyState(state);
        Check(overview.CachedLogCount == 0, "clear retains cursor floor across refresh");
        state["active"]!["scheduler"]!["logs"]!["entries"]!.AsArray().Add(new JsonObject
            { ["id"] = 2L, ["level"] = "ERROR", ["message"] = "new native", ["time"] = "2026-01-02T00:00:03Z" });
        state["active"]!["scheduler"]!["phase"] = "waiting";
        overview.ApplyState(state);
        Check(overview.VisibleLogs.Single().Message == "new native" && rail.RunningCount == "0" && rail.PendingCount == "2",
            "waiting scheduler is not a running task; only new logs appear");
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
        var cards = SchedulerObservation.Resources(resourceSnapshot, ["CustomKey", "ActionPoint", "Chip"]);
        Check(cards[0].Name == "来自上游" && cards[0].HasFallback && cards[1].Limit == "/ 总行动力 160"
              && cards[2].Value == "—", "upstream resource order, unknown keys and total action points");
        Console.WriteLine("PASS: native scheduler observations, resource records, log cursors and instance isolation");
    }

    private static async Task VerifyScheduler(FixtureBackend backend)
    {
        var overview = new OverviewViewModel(backend: backend);
        overview.SetInstance("fixture");
        Check(!overview.IsSchedulerControlEnabled, "scheduler waits for an authoritative state");
        overview.ApplyState(backend.State);
        Check(overview.IsSchedulerControlEnabled, "idle scheduler can start");
        await overview.ToggleSchedulerAsync();
        Check(backend.SchedulerStarted is { Instance: "fixture", ConfirmActions: true }
              && overview.IsSchedulerRunning, "start reaches Core capability and updates state");
        backend.State["active"]!["scheduler"] = new JsonObject { ["phase"] = "waiting" };
        overview.ApplyState(backend.State);
        Check(overview.SchedulerStatusText == "等待中", "native waiting status is shown");
        await overview.ToggleSchedulerAsync();
        Check(backend.StopRequests == 1 && !overview.IsSchedulerControlEnabled &&
              overview.SchedulerButtonText == "正在停止…", "stop waits for native boundary acknowledgement");
        overview.SetInstance("another");
        overview.ApplyState(backend.State);
        Check(!overview.IsSchedulerRunning && !overview.IsSchedulerControlEnabled,
              "another instance cannot claim or stop this run");
        backend.State["active"]!["status"] = "completed";
        overview.ApplyState(backend.State);
        Check(overview.IsSchedulerControlEnabled, "completed foreign run releases the single device slot");
        overview.ReportBackendError("fixture connection failure");
        Check(!overview.IsSchedulerControlEnabled, "unknown state does not allow duplicate starts");
    }

    private static async Task ExpectFailure(Func<Task> action)
    {
        try { await action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Malformed upstream response was accepted");
    }
    private static void Check(bool ok, string message)
    { if (!ok) throw new Exception("Core UI: " + message); }

    private sealed class FixtureBackend : IAlasControlBackend
    {
        public string? Cleared;
        public InstanceTaskRunRequest? Started;
        public InstanceSchedulerRunRequest? SchedulerStarted;
        public int StopRequests;
        public JsonObject State = new() { ["active"] = new JsonObject { ["status"] = "idle" } };
        public JsonObject Validation = JsonNode.Parse("""
            {"valid":false,"diagnostics":[{"code":"forbidden_statement","message":"fixture diagnostic","line":2,"column":3}]}
            """)!.AsObject();
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
        public Task<JsonObject> ValidateShopStrategyAsync(string script, CancellationToken cancellationToken = default)
            => Task.FromResult((JsonObject)Validation.DeepClone());
        public Task StartTaskAsync(InstanceTaskRunRequest request, CancellationToken cancellationToken = default)
        { Started = request; return Task.CompletedTask; }
        public Task StartSchedulerAsync(InstanceSchedulerRunRequest request, CancellationToken cancellationToken = default)
        {
            SchedulerStarted = request;
            State["active"] = new JsonObject { ["status"] = "running", ["kind"] = "scheduler_run", ["instance"] = request.Instance };
            return Task.CompletedTask;
        }
        public Task<JsonObject> ReadStateAsync(CancellationToken cancellationToken = default) => Task.FromResult((JsonObject)State.DeepClone());
        public Task<JsonObject?> ReadReportAsync(string stamp, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SchemaResponse> ReadSchemaAsync(string language = "zh-CN", CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ConfigResponse> ReadConfigAsync(string instance, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ConfigResponse> PatchConfigAsync(ConfigPatchRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ConfigResponse> CreateInstanceAsync(InstanceCreateRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteInstanceAsync(InstanceDeleteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<InstanceImportSource> ImportInstanceAsync(InstanceImportRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<InstanceImportListResponse> ReadInstanceImportsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveQueueAsync(JsonObject queue, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StartRunAsync(ControlRunRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> RequestStopAsync(CancellationToken cancellationToken = default)
        { StopRequests++; State["active"]!["stop_requested"] = true; return Task.FromResult(true); }
        public Task<JsonObject> ReadStatisticsAsync(StatisticsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JsonObject> RefreshStatisticsLootAsync(string instance, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
