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
        Console.WriteLine("PASS: shared Core adapters preserve upstream report fields and strict strategy diagnostics");
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
        public Task<JsonObject> ReadStateAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JsonObject?> ReadReportAsync(string stamp, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SchemaResponse> ReadSchemaAsync(string language = "zh-CN", CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ConfigResponse> ReadConfigAsync(string instance, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ConfigResponse> PatchConfigAsync(ConfigPatchRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ConfigResponse> CreateInstanceAsync(InstanceCreateRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteInstanceAsync(InstanceDeleteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveQueueAsync(JsonObject queue, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StartRunAsync(ControlRunRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> RequestStopAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JsonObject> ReadStatisticsAsync(StatisticsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JsonObject> RefreshStatisticsLootAsync(string instance, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
