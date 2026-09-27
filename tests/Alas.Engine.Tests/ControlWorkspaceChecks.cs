using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static class ControlWorkspaceChecks
{
    public static async Task RunAsync(string artifacts)
    {
        await LiveSnapshotChecksAsync(artifacts);
        string root = Path.Combine(artifacts, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        await File.WriteAllTextAsync(Path.Combine(root, "config", "template.json"), "{}");
        var workspace = new EngineControlWorkspace(root, root, "missing-data", "missing-tools", null, null);
        JsonObject Request(params TaskRequest[] requests) => new()
        {
            ["queue"] = new JsonObject { ["tasks"] = JsonSerializer.SerializeToNode(requests, TaskQueue.Json) }
        };
        var request = Request(new TaskRequest("first", "observe", Required: true),
            new("second", "navigate", new JsonObject { ["page"] = "page_main" }, DependsOn: ["first"]));
        workspace.StartRun(request);
        await Finished(workspace);
        var state = workspace.State();
        Check(state["active"]!["status"]!.GetValue<string>() == "completed", "Dry-run did not finish: " + state["active"]);
        var report = state["report"]!.AsObject();
        Check(report["queue_outcome"]!.GetValue<string>() == "dry_run" && report["evidence_complete"]!.GetValue<bool>(), "Engine report could not read Engine artifacts");
        Check(report["totals"]!["tasks_dry_run"]!.GetValue<int>() == 2 &&
            report["totals"]!["tasks_succeeded"]!.GetValue<int>() == 0, "Dry-run counted as executed success");
        Check(state["live_tasks"]!.AsArray().Count == 2, "Current task evidence missing");
        string run = state["active"]!["run_directory"]!.GetValue<string>();
        string stamp = Path.GetFileName(run);
        Check(workspace.Report(stamp)!["items"]!.AsArray().Count == 2, "Report lookup lost tasks");
        string taskFile = report["items"]![0]!["artifact"]!.GetValue<string>();
        string original = await File.ReadAllTextAsync(taskFile);
        await File.WriteAllTextAsync(taskFile, "{}");
        Check(!RunReport.Build(run).ToJson()["evidence_complete"]!.GetValue<bool>(), "Changed evidence reported complete");
        await File.WriteAllTextAsync(taskFile, original);
        var snapshotPath = Path.Combine(run, "run.json");
        var snapshot = JsonNode.Parse(await File.ReadAllTextAsync(snapshotPath))!.AsObject();
        snapshot["complete"] = false;
        await File.WriteAllTextAsync(snapshotPath, snapshot.ToJsonString());
        Check(RunReport.Build(run).ToJson()["queue_outcome"]!.GetValue<string>() == "running", "Incomplete run reported successful");
        snapshot["complete"] = true;
        await File.WriteAllTextAsync(snapshotPath, snapshot.ToJsonString());

        // A second attempt must not leave old task entries in the product snapshot.
        string oldAttempt = Path.Combine(run, "attempt-" + new string('0', 32));
        Directory.CreateDirectory(Path.Combine(oldAttempt, "0000-old"));
        await File.WriteAllTextAsync(Path.Combine(oldAttempt, "0000-old", "task.json"), original);
        Check(workspace.State()["live_tasks"]!.AsArray().Count == 2, "Historical attempts duplicated in live tasks");

        // Multiple required tasks after a normal stop remain unexecuted, not failed.
        var queue = new TaskQueue();
        var options = new EngineSessionOptions("missing-adb", "offline", GameServer.Cn, "missing-assets", "missing-python");
        var stopped = await queue.RunAsync([new("a", "observe", Required: true), new("b", "observe", Required: true)],
            options, new(root, DryRun: true, StopRequested: () => true));
        Check(!stopped.Failed && stopped.Tasks.All(t => t.Reason == "stop_requested_at_boundary"), "Boundary stop became previous failure");
        var stopReport = RunReport.Build(stopped.Directory).ToJson();
        Check(stopReport["queue_outcome"]!.GetValue<string>() == "cancelled" && stopReport["evidence_complete"]!.GetValue<bool>(), "Stop lost final report");

        var refused = await queue.RunAsync([new("unknown", "unported"), new("next", "observe")], options, new(root, DryRun: true));
        Check(refused.Failed && refused.Tasks[1].Reason == "previous_failure", "Unsupported task did not stop queue");
        var refusalReport = RunReport.Build(refused.Directory).ToJson();
        Check(refusalReport["has_failures"]!.GetValue<bool>() && refusalReport["evidence_complete"]!.GetValue<bool>(), "Failure and evidence completeness conflated");
        var inconsistent = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(refused.Directory, "run.json")))!.AsObject();
        inconsistent["failed"] = false;
        await File.WriteAllTextAsync(Path.Combine(refused.Directory, "run.json"), inconsistent.ToJsonString());
        var invalidReport = RunReport.Build(refused.Directory).ToJson();
        Check(!invalidReport["evidence_complete"]!.GetValue<bool>() && invalidReport["queue_outcome"]!.GetValue<string>() != "succeeded",
            "Contradictory queue verdict reported successful");
        await File.WriteAllTextAsync(Path.Combine(refused.Directory, "run.json"), "{broken");
        Check(!RunReport.Build(refused.Directory).ToJson()["evidence_complete"]!.GetValue<bool>(), "Broken snapshot accepted");

        Reject<ArgumentException>(() => workspace.StartRun(Request(new TaskRequest("../escape", "observe"))));
        Reject<EngineCapabilityUnavailableException>(() => workspace.StartTask(new()));
        Reject<EngineCapabilityUnavailableException>(() => workspace.StartScheduler(new()));
        Reject<EngineCapabilityUnavailableException>(() => workspace.ReadHostJson("statistics_report", new()));
        var mixed = Request(new TaskRequest("a", "observe", Instance: "second"));
        mixed["instance"] = "first";
        Reject<EngineCapabilityUnavailableException>(() => workspace.StartRun(mixed));
        var config = new JsonObject { ["Alas"] = new JsonObject { ["Emulator"] = new JsonObject
        { ["Serial"] = "offline-device", ["ServerName"] = "login-shard", ["PackageName"] = "com.YoStarEN.AzurLane" } } };
        await File.WriteAllTextAsync(Path.Combine(root, "config", "sample.json"), config.ToJsonString());
        var mismatched = Request(new TaskRequest("a", "observe"));
        mismatched["instance"] = "sample"; mismatched["serial"] = "different-device";
        Reject<ArgumentException>(() => workspace.StartRun(mismatched));
        var before = await File.ReadAllBytesAsync(Path.Combine(root, "config", "sample.json"));
        var validInstance = Request(new TaskRequest("a", "observe", Instance: "sample"));
        workspace.StartRun(validInstance);
        await Finished(workspace);
        Check(workspace.State("sample")["overview"]!["instance"]!.GetValue<string>() == "sample", "Selected instance lost");
        var after = await File.ReadAllBytesAsync(Path.Combine(root, "config", "sample.json"));
        Check(before.SequenceEqual(after), "Dry-run changed configuration");
        Check(GameServerRules.FromPackage("com.YoStarEN.AzurLane") == GameServer.En &&
            GameServerRules.FromPackage("com.YoStarJP.AzurLane") == GameServer.Jp &&
            GameServerRules.FromPackage("com.hkmanjuu.azurlane.gp.mc") == GameServer.Tw &&
            GameServerRules.FromPackage("unknown-channel") == GameServer.Cn, "Package-to-server rule differs from upstream");
        await workspace.BeginShutdown();
        Reject<EngineControlWorkspaceUnavailableException>(() => workspace.StartRun(request));
        Reject<EngineControlWorkspaceUnavailableException>(() => workspace.SaveQueueRequest(request));
        Check(!workspace.RequestStop(), "Idle shutdown accepted a stop");
        Check(AppDomain.CurrentDomain.GetAssemblies().All(a => a.GetName().Name != "Alas.Core"), "Engine control path kept retired Core unloaded");
    }

    private static async Task LiveSnapshotChecksAsync(string artifacts)
    {
        FileStream? reader = null;
        try
        {
            var options = new EngineSessionOptions("missing-adb", "offline", GameServer.Cn, "missing-assets", "missing-python");
            var result = await new TaskQueue().RunAsync([new("first", "observe"), new("second", "observe")], options,
                new(artifacts, DryRun: true, OnStarted: directory => reader = new FileStream(Path.Combine(directory, "run.json"),
                    FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)));
            Check(!result.Failed && result.Tasks.All(task => task.Outcome == TaskOutcome.DryRun),
                "A live report reader prevented queue snapshot replacement");
        }
        finally { reader?.Dispose(); }
    }

    private static async Task Finished(EngineControlWorkspace workspace)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (workspace.State()["active"]!["status"]!.GetValue<string>() == "running")
            await Task.Delay(10, deadline.Token);
    }
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
