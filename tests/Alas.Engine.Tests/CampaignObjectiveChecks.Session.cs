using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static partial class CampaignObjectiveChecks
{
    private static async Task SessionChecksAsync(string python, string upstream, string artifacts)
    {
        string? previous = Environment.GetEnvironmentVariable("ALAS_TEST_ADB_FIXTURE");
        try
        {
            foreach (string scenario in new[] { "disable", "increase", "input-failure", "conflict" })
            {
                string root = Path.Combine(artifacts, scenario + "-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(root, "config"));
                string configFile = Path.Combine(root, "config/fixture.json");
                await File.WriteAllTextAsync(Path.Combine(root, "config/template.json"), "{}");
                await File.WriteAllTextAsync(configFile, """
                    {"Alas":{"Emulator":{"Serial":"offline-replay","PackageName":"org.example.game"}},
                     "Main":{"Campaign":{"Event":"campaign_main","Name":"1-1"},"Scheduler":{"Enable":true}}}
                    """);
                string fixture = Path.Combine(root, "adb.json");
                await File.WriteAllTextAsync(fixture, JsonSerializer.Serialize(new
                {
                    first = Path.Combine(artifacts, "objective-preparation.png"), second = Path.Combine(artifacts, "objective-stage.png"),
                    failTap = scenario == "input-failure", advance = true
                }));
                Environment.SetEnvironmentVariable("ALAS_TEST_ADB_FIXTURE", fixture);
                string executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Alas.Engine.Tests.exe" : "Alas.Engine.Tests");
                var options = new EngineSessionOptions(executable, "offline-replay", GameServer.Cn, Path.Combine(upstream, "assets"),
                    python, "org.example.game", AllowActions: true, ConfigRoot: root, ConfigInstance: "fixture");
                var result = await new TaskQueue([new AchievementProbe(configFile, scenario)]).RunAsync(
                    [new("stop", "objective_probe", new(), Required: false, TimeoutSeconds: 30)], options, new(root));
                bool success = scenario is "disable" or "increase";
                Check(result.Tasks[0].Outcome == (success ? TaskOutcome.Skipped : TaskOutcome.Failed),
                    "Actual EngineSession stop differs: " + JsonSerializer.Serialize(result.Tasks));
                string evidenceFile = Directory.GetFiles(result.Directory, "map-stop.json", SearchOption.AllDirectories).Single();
                string evidenceText = await File.ReadAllTextAsync(evidenceFile);
                var record = JsonSerializer.Deserialize<CampaignStopEvidence>(evidenceText, TaskQueue.Json)!;
                Check(record.Persisted == success && (record.ReturnedFrame is not null) == (scenario != "input-failure") && record.CancelClicks == 1,
                    "Actual session lost cancellation/return/persistence evidence");
                var saved = JsonNode.Parse(await File.ReadAllTextAsync(configFile))!;
                Check(saved["Main"]!["Scheduler"]!["Enable"]!.GetValue<bool>() == (scenario != "disable") &&
                    saved["Main"]!["Campaign"]!["Name"]!.GetValue<string>() == (scenario == "increase" ? "1-2" : scenario == "conflict" ? "1-3" : "1-1"),
                    "Actual session wrote wrong task state or overwrote conflicting user edits");
                Check(RunReport.Build(result.Directory).ToJson()["evidence_complete"]!.GetValue<bool>(), "Stop boundary failed report validation");
                File.Delete(evidenceFile);
                Check(!RunReport.Build(result.Directory).ToJson()["evidence_complete"]!.GetValue<bool>(), "Missing stop artifact was accepted");
                await File.WriteAllTextAsync(evidenceFile, JsonSerializer.Serialize(record with { ReturnedFrame = record.Info!.FrameSequence }, TaskQueue.Json));
                Check(!RunReport.Build(result.Directory).ToJson()["evidence_complete"]!.GetValue<bool>(), "Stale return artifact was accepted");
                await File.WriteAllTextAsync(evidenceFile, evidenceText);
                if (scenario == "disable")
                {
                    await using var session = new EngineSession(options);
                    session.BeginTask(TimeSpan.FromSeconds(10));
                    await session.PrepareAchievementAsync(RuleCatalog.Create("campaign_main/campaign_1_1"),
                        new() { MapAchievement = MapAchievement.FullyCleared }, default);
                    session.BeginTask(TimeSpan.FromSeconds(10));
                    await Rejects<InvalidOperationException>(() => session.StopForAchievementAsync(ReachedInfo, TimeSpan.FromSeconds(10), default).AsTask());
                    Check((await session.SaveEvidenceAsync(Path.Combine(root, "reset"), false)).MapStopFile is null,
                        "Task boundary retained previous achievement controller");
                }
            }
        }
        finally { Environment.SetEnvironmentVariable("ALAS_TEST_ADB_FIXTURE", previous); }
        Console.WriteLine("Achievement session: real CV/session/queue/config persistence, four synthetic ADB replays and missing/stale artifacts passed; no real sortie.");
    }
    private sealed class AchievementProbe(string configFile, string scenario) : ITaskRunner
    {
        public string Kind => "objective_probe";
        public bool RequiresActions => true;
        public void Validate(JsonObject? input) { }
        public IReadOnlyList<string> Preconditions(TaskRequest request, TaskCapabilities capabilities) => [];
        public async ValueTask<TaskResult> RunAsync(TaskRequest request, TaskContext context, CancellationToken token)
        {
            var configuration = new CampaignConfiguration { MapAchievement = MapAchievement.FullyCleared, StageIncrease = scenario == "increase" };
            var service = context.Achievement ?? throw new InvalidOperationException("Session omitted achievement service");
            await service.PrepareAchievementAsync(RuleCatalog.Create("campaign_main/campaign_1_1"), configuration, token);
            var map = await context.MapPreparation!.PrepareMapAsync(configuration, context.Timeout, token);
            Check(map.Info.FullyCleared, "Synthetic native progress image was not observed as fully cleared");
            if (scenario == "conflict")
            {
                var changed = JsonNode.Parse(await File.ReadAllTextAsync(configFile, token))!;
                changed["Main"]!["Campaign"]!["Name"] = "1-3";
                await File.WriteAllTextAsync(configFile, changed.ToJsonString(), token);
            }
            var evidence = await service.StopForAchievementAsync(map.Info, context.Timeout, token);
            return new(request.Id, Kind, TaskOutcome.Skipped, "map_achievement_reached", JsonSerializer.SerializeToNode(evidence, TaskQueue.Json)!.AsObject());
        }
    }
}
