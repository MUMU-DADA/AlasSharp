using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Imaging;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static partial class EmotionChecks
{
    private static TaskRequest Request(string mode = "calculate") => new("emotion-entry", "campaign_run", new JsonObject
    {
        ["campaign"] = "campaign_main/campaign_1_1", ["fleet1"] = 1, ["fleet2"] = 0, ["submarine"] = 0, ["emotionMode"] = mode
    }, Required: true);

    private static async Task ProductChecksAsync(string python, string upstream, string artifacts)
    {
        var (workspace, root) = await FixtureAsync(artifacts, Defaults(0, DateTimeOffset.Now.AddMinutes(10)));
        var options = new EngineSessionOptions("must-not-start-adb", "offline-replay", GameServer.Cn,
            "missing-assets", python, "org.example.game", Path.Combine(upstream, "bin/ocr_models"), true, root, "fixture");
        var result = await new TaskQueue().RunAsync([Request()], options, new(artifacts));
        var task = result.Tasks.Single();
        Check(result.Failed && task is { Outcome: TaskOutcome.Skipped, Reason: "emotion_recovery_required" },
            "Emotion entry delay was treated as a successful/complete required task");
        var boundary = task.Evidence!["boundary"]!.Deserialize<EngineSession.JsonObjectEvidence>(TaskQueue.Json)!;
        Check(boundary is { ActionAttempts: 0, Image: null, EmotionFile: "emotion.json" }, "Deferred entry touched the device or omitted evidence");
        Check(RunReport.Build(result.Directory).ToJson()["evidence_complete"]!.GetValue<bool>(), "Deferred task evidence was not readable");
        string emotionFile = Directory.GetFiles(result.Directory, "emotion.json", SearchOption.AllDirectories).Single();
        string originalEvidence = await File.ReadAllTextAsync(emotionFile);
        File.Delete(emotionFile);
        Check(!RunReport.Build(result.Directory).ToJson()["evidence_complete"]!.GetValue<bool>(), "Missing emotion evidence was reported complete");
        await File.WriteAllTextAsync(emotionFile, "[]");
        Check(!RunReport.Build(result.Directory).ToJson()["evidence_complete"]!.GetValue<bool>(), "Empty emotion evidence was reported complete");
        await File.WriteAllTextAsync(emotionFile, originalEvidence);
        Check(workspace.Get("fixture").Values["Main"]!["Scheduler"]!["NextRun"]!.GetValue<string>() != "2020-01-01 00:00:00", "Entry failed to persist the next run");
        var resumed = await new TaskQueue().RunAsync([Request()], options, new(artifacts, ResumeDirectory: result.Directory));
        Check(resumed.Tasks[0].Reason == "emotion_recovery_required", "Deferred task was marked previously completed");
        var missing = await new TaskQueue().RunAsync([Request()], options with { ConfigRoot = null, ConfigInstance = null }, new(artifacts));
        Check(missing.Tasks[0].Reason == "preconditions_unmet", "Calculated entry ran without configuration");
        var runner = new CampaignRunTask();
        foreach (var mode in new[] { "calculate", "calculate_ignore", "ignore", "nothing" })
        {
            var request = Request(mode); runner.Validate(request.Input);
            Check(runner.Preconditions(request, new(true, true)).Contains("emotion_config") == mode.Contains("calculate", StringComparison.Ordinal),
                "Emotion mode has incorrect persistence preconditions");
        }
        foreach (string field in new[] { "emotionMode", "configTask" })
        {
            var invalid = Request().Input!; invalid[field] = null;
            await Rejects<ArgumentException>(() => { runner.Validate(invalid); return Task.CompletedTask; });
        }
        var wrongInstance = await new TaskQueue().RunAsync([Request() with { Instance = "another" }], options, new(artifacts));
        Check(wrongInstance.Tasks[0].Outcome == TaskOutcome.Refused, "CLI queue accepted a task bound to another instance");
        var mismatch = await new TaskQueue().RunAsync([Request()], options with { Serial = "different-device" }, new(artifacts));
        Check(mismatch.Tasks[0].Outcome == TaskOutcome.Failed && mismatch.Tasks[0].Evidence!["boundary"]!["actionAttempts"]!.GetValue<int>() == 0,
            "Wrong-device configuration reached actions");
        await using (var session = new EngineSession(options))
        {
            session.BeginTask(TimeSpan.FromSeconds(5));
            Check(await session.PrepareAsync(new(), 8, true, default) is null, "Resume applied an entry delay");
            Check((await session.SaveEvidenceAsync(Path.Combine(artifacts, "resume-no-entry"), false)).EmotionFile is null, "Resume wrote entry records");
            await ((ICampaignInMapHost)session).EnsureEmotionAsync(new(), default);
            await Rejects<InvalidOperationException>(() => ((ICampaignInMapHost)session).EnsureEmotionAsync(new() { ConfigTask = "EventA" }, default).AsTask());
            session.BeginTask(TimeSpan.FromSeconds(5));
            await Rejects<InvalidOperationException>(() => ((ICampaignInMapHost)session).EnsureEmotionAsync(new(), default).AsTask());
        }
        foreach (var mode in new[] { CampaignEmotionMode.Ignore, CampaignEmotionMode.Nothing })
        {
            await using var session = new EngineSession(options with { ConfigRoot = null, ConfigInstance = null });
            session.BeginTask(TimeSpan.FromSeconds(1));
            Check(await session.PrepareAsync(new() { EmotionMode = mode }, 8, false, default) is null, "Non-calculated mode read persistence");
        }
        // Real session, pure CV worker and executable fake ADB: failed combat input retains the wait record.
        var high = await FixtureAsync(artifacts, Defaults());
        string folder = Path.Combine(artifacts, "combat-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var assets = new AssetFiles(Path.Combine(upstream, "assets"));
        string frame = Path.Combine(folder, "frame.png");
        await File.WriteAllBytesAsync(frame, (await assets.ReadAsync(UiAssets.Combat.BATTLE_PREPARATION.For(GameServer.Cn))).ToArray());
        string fixture = Path.Combine(folder, "fixture.json");
        await File.WriteAllTextAsync(fixture, JsonSerializer.Serialize(new { first = frame, second = frame, failTap = true, advance = false }));
        string? previous = Environment.GetEnvironmentVariable("ALAS_TEST_ADB_FIXTURE");
        Environment.SetEnvironmentVariable("ALAS_TEST_ADB_FIXTURE", fixture);
        try
        {
            string executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Alas.Engine.Tests.exe" : "Alas.Engine.Tests");
            await using var session = new EngineSession(options with { Adb = executable, Python = python, Assets = Path.Combine(upstream, "assets"),
                ModelDirectory = null, ConfigRoot = high.Root });
            session.BeginTask(TimeSpan.FromSeconds(10));
            var configuration = new CampaignConfiguration { Fleet2 = 2, FleetOrder = FleetOrder.Fleet1BossFleet2Mob };
            await session.PrepareAsync(configuration, 4, true, default);
            await session.Driver.ScreenshotAsync(default);
            var state = new CampaignState(RuleCatalog.Create("campaign_main/campaign_1_1").Map);
            await Rejects<IOException>(() => session.CreateCampaignCombatFlow(state, configuration).RunAutoAsync().AsTask());
            var saved = await session.SaveEvidenceAsync(folder, true);
            var events = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(folder, saved.EmotionFile!)))!.AsArray();
            Check(saved is { ActionAttempts: > 0, Image: "failure.png" } && events.Count == 1 &&
                events[0]!["operation"]!.GetValue<string>() == "wait" && events[0]!["fleet"]!.GetValue<int>() == 2 &&
                events[0]!["saved"]!.GetValue<bool>(), "Actual session lost displayed-fleet wait or charged a failed battle start");
            session.BeginTask(TimeSpan.FromSeconds(1));
            Check((await session.SaveEvidenceAsync(Path.Combine(folder, "next"), false)).EmotionFile is null, "Emotion evidence leaked across tasks");
        }
        finally { Environment.SetEnvironmentVariable("ALAS_TEST_ADB_FIXTURE", previous); }
        await ControlChecksAsync(python, upstream, artifacts);
    }

    private static async Task ControlChecksAsync(string python, string upstream, string artifacts)
    {
        var (_, root) = await FixtureAsync(artifacts, Defaults(0, DateTimeOffset.Now.AddMinutes(10)));
        var environment = new Dictionary<string, string>
        {
            ["ALAS_PYTHON"] = python, ["ALAS_ADB"] = "must-not-start-adb",
            ["ALAS_OCR_MODELS"] = Path.Combine(upstream, "bin/ocr_models")
        };
        var previous = environment.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        foreach (var pair in environment) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        var control = new EngineControlWorkspace(root, root, "unused", "unused", null, null);
        try
        {
            control.StartRun(new JsonObject
            {
                ["instance"] = "fixture", ["mode"] = "actions", ["confirm_actions"] = true,
                ["queue"] = new JsonObject { ["tasks"] = JsonSerializer.SerializeToNode(new[] { Request() }, TaskQueue.Json) }
            });
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (control.State()["active"]!["status"]!.GetValue<string>() == "running")
            {
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("Control emotion entry did not finish");
                await Task.Delay(20);
            }
            var report = control.State()["report"]!;
            Check(report["evidence_complete"]!.GetValue<bool>() &&
                report["items"]![0]!["reason"]!.GetValue<string>() == "emotion_recovery_required" &&
                report["items"]![0]!["evidence"]!["boundary"]!["actionAttempts"]!.GetValue<int>() == 0,
                "Server/desktop control did not bind its selected instance to the emotion service");
        }
        finally
        {
            await control.BeginShutdown().WaitAsync(TimeSpan.FromSeconds(15));
            foreach (var pair in previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }
}
