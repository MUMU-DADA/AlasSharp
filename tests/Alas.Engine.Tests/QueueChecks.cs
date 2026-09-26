using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Imaging;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static class QueueChecks
{
    public static async Task RunAsync(string python, string upstream, string artifacts)
    {
        int checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        async Task Reject<T>(Func<Task> action, string message) where T : Exception
        {
            try { await action(); }
            catch (T) { checks++; return; }
            throw new InvalidOperationException(message);
        }
        var queue = new TaskQueue();
        var offline = new EngineSessionOptions("must-not-start-adb", "offline", GameServer.Cn, "missing-assets", "must-not-start-python");
        TaskRequest[] chain = [new("navigate", "navigate", new JsonObject { ["page"] = "page_campaign" }), new("observe", "observe", DependsOn: ["navigate"], Required: true)];
        var dry = await queue.RunAsync(chain, offline, new(artifacts, DryRun: true));
        Check(!dry.Failed && dry.Tasks.All(t => t.Outcome == TaskOutcome.DryRun), "Dry-run dependencies were treated as incomplete");
        Check(!File.Exists(Path.Combine(dry.Directory, "state.json")), "Dry-run wrote resumable completion state");
        var refused = await queue.RunAsync(chain, offline, new(artifacts));
        Check(refused.Failed && refused.Tasks[0] is { Outcome: TaskOutcome.Refused, Reason: "actions_disabled" } &&
            refused.Tasks[1] is { Outcome: TaskOutcome.Skipped, Reason: "previous_failure" }, "Action refusal did not stop the queue before device creation");
        var resumeRequest = new TaskRequest("resume", "campaign_resume",
            new JsonObject { ["campaign"] = "campaign_main/campaign_1_1" });
        var resumeDry = await queue.RunAsync([resumeRequest], offline, new(artifacts, DryRun: true));
        var resumeRefused = await queue.RunAsync([resumeRequest], offline, new(artifacts));
        Check(resumeDry.Tasks.Single().Outcome == TaskOutcome.DryRun &&
            resumeRefused.Tasks.Single() is { Outcome: TaskOutcome.Refused, Reason: "actions_disabled" },
            "Campaign resume bypassed the queue's dry-run or action gate");
        var skipped = await queue.RunAsync([new("key", "data_key"), new("observe", "observe")], offline, new(artifacts, DryRun: true));
        Check(!skipped.Failed && skipped.Tasks[0] is { Outcome: TaskOutcome.Skipped, Reason: "preconditions_unmet" } &&
            skipped.Tasks[1].Outcome == TaskOutcome.DryRun, "Optional precondition failure did not remain skipped");
        var required = await queue.RunAsync([new("key", "data_key", Required: true), new("observe", "observe")], offline, new(artifacts, DryRun: true));
        Check(required.Failed && required.Tasks[1].Reason == "previous_failure", "Required skipped task did not stop the queue");
        var continued = await queue.RunAsync([new("bad", "unknown"), new("independent", "observe"), new("dependent", "observe", DependsOn: ["bad"])],
            offline, new(artifacts, DryRun: true, ContinueOnFailure: true));
        Check(continued.Failed && continued.Tasks.Select(t => t.Outcome).SequenceEqual(new[] { TaskOutcome.Refused, TaskOutcome.DryRun, TaskOutcome.Skipped }) &&
            continued.Tasks[2].Reason == "dependency_not_completed", "ContinueOnFailure bypassed dependencies or stopped independent validation");
        var invalid = await queue.RunAsync([new("bad", "navigate", new JsonObject { ["page"] = "missing" })], offline, new(artifacts, DryRun: true));
        Check(invalid.Failed && invalid.Tasks[0].Outcome == TaskOutcome.Refused, "Unknown destination passed validation");
        await Reject<ArgumentException>(() => queue.RunAsync([new("duplicate", "observe"), new("duplicate", "observe")], offline, new(artifacts)), "Duplicate task id accepted");
        await Reject<ArgumentException>(() => queue.RunAsync([new("../escape", "observe")], offline, new(artifacts)), "Unsafe task id accepted");
        await Reject<ArgumentException>(() => queue.RunAsync([new("first", "observe", DependsOn: ["later"]), new("later", "observe")], offline, new(artifacts)), "Forward dependency accepted");
        await Reject<ArgumentException>(() => queue.RunAsync([new("bad", "observe", TimeoutSeconds: double.NaN)], offline, new(artifacts)), "Invalid timeout accepted");
        string invalidFile = Path.Combine(artifacts, "invalid-queue.json");
        foreach (string content in new[] { "[{\"kind\":\"observe\"}]", "[{\"id\":null,\"kind\":\"observe\"}]", "[{\"id\":\"one\",\"kind\":\"observe\",\"unexpected\":true}]" })
        {
            await File.WriteAllTextAsync(invalidFile, content);
            await Reject<JsonException>(() => TaskQueue.ReadAsync(invalidFile), "Malformed queue accepted");
        }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var result = await queue.RunAsync([new("cancelled", "observe", Required: true), new("next", "observe")], offline, new(artifacts), cancelled.Token);
            Check(result.Failed && result.Tasks.All(t => t.Reason == "cancelled_before_task"), "Cancelled queue lost task artifacts or required status");
        }
        var empty = await queue.RunAsync([], offline, new(artifacts));
        Check(!(await queue.RunAsync([], offline, new(artifacts, ResumeDirectory: empty.Directory))).Failed, "Empty queue could not resume");

        string executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Alas.Engine.Tests.exe" : "Alas.Engine.Tests");
        var files = new AssetFiles(Path.Combine(upstream, "assets"));
        string first = Path.Combine(artifacts, "queue-source.png"), second = Path.Combine(artifacts, "queue-destination.png");
        await File.WriteAllBytesAsync(first, (await files.ReadAsync(UiAssets.UiWhite.MAIN_GOTO_CAMPAIGN_WHITE.For(GameServer.Cn))).ToArray());
        await File.WriteAllBytesAsync(second, (await files.ReadAsync(UiAssets.Ui.CAMPAIGN_CHECK.For(GameServer.Cn))).ToArray());
        var options = new EngineSessionOptions(executable, "offline-replay", GameServer.Cn, Path.Combine(upstream, "assets"), python,
            "org.example.game", AllowActions: true);
        string? previous = Environment.GetEnvironmentVariable("ALAS_TEST_ADB_FIXTURE");
        async Task Fixture(bool failTap)
        {
            string folder = Path.Combine(artifacts, "device-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string fixture = Path.Combine(folder, "fixture.json");
            await File.WriteAllTextAsync(fixture, JsonSerializer.Serialize(new { first, second, failTap, advance = true }));
            Environment.SetEnvironmentVariable("ALAS_TEST_ADB_FIXTURE", fixture);
        }
        try
        {
            await Fixture(false);
            var success = await queue.RunAsync(chain, options, new(artifacts));
            Check(!success.Failed && success.Tasks.All(t => t.Outcome == TaskOutcome.Succeeded), "Real-CV queue replay failed");
            Check(success.Tasks[0].Evidence!["boundary"]!["actionAttempts"]!.GetValue<int>() > 0 &&
                success.Tasks[1].Evidence!["boundary"]!["actionAttempts"]!.GetValue<int>() == 0 &&
                success.Tasks[1].Evidence!["boundary"]!["frameSequence"]!.GetValue<long>() > success.Tasks[0].Evidence!["boundary"]!["frameSequence"]!.GetValue<long>(),
                "Task boundary retained stale action/frame evidence");
            Environment.SetEnvironmentVariable("ALAS_TEST_ADB_FIXTURE", null);
            var resume = await queue.RunAsync(chain, options, new(artifacts, ResumeDirectory: success.Directory));
            Check(!resume.Failed && resume.Tasks.All(t => t is { Outcome: TaskOutcome.Skipped, Reason: "previously_completed" }), "Resume executed completed tasks or lost dependency success");
            await Reject<ArgumentException>(() => queue.RunAsync(chain, options, new(artifacts, DryRun: true, ResumeDirectory: success.Directory)), "Dry-run resumed real state");
            await Reject<InvalidDataException>(() => queue.RunAsync(chain, options with { Serial = "different" }, new(artifacts, ResumeDirectory: success.Directory)), "Resume accepted changed device identity");
            string statePath = Path.Combine(success.Directory, "state.json");
            var state = JsonNode.Parse(await File.ReadAllTextAsync(statePath))!;
            string taskFile = Path.Combine(success.Directory, state["state"]!["completed"]!["navigate"]!["artifact"]!.GetValue<string>());
            string taskDirectory = Path.GetDirectoryName(taskFile)!;
            foreach (string file in new[] { statePath, Path.Combine(success.Directory, "queue.json"), taskFile,
                Path.Combine(taskDirectory, "actions.json"), Path.Combine(taskDirectory, "frame.png"), Path.Combine(taskDirectory, "request.json") })
            {
                byte[] original = await File.ReadAllBytesAsync(file);
                if (file == statePath)
                {
                    var changed = state.DeepClone();
                    changed["state"]!["fingerprint"] = "changed";
                    await File.WriteAllTextAsync(file, changed.ToJsonString(TaskQueue.Json));
                }
                else await File.AppendAllTextAsync(file, " ");
                try { await Reject<InvalidDataException>(() => queue.RunAsync(chain, options, new(artifacts, ResumeDirectory: success.Directory)), "Changed evidence accepted: " + Path.GetFileName(file)); }
                finally { await File.WriteAllBytesAsync(file, original); }
            }
            string frame = Path.Combine(taskDirectory, "frame.png");
            byte[] frameBytes = await File.ReadAllBytesAsync(frame);
            File.Delete(frame);
            try { await Reject<InvalidDataException>(() => queue.RunAsync(chain, options, new(artifacts, ResumeDirectory: success.Directory)), "Missing completion image accepted"); }
            finally { await File.WriteAllBytesAsync(frame, frameBytes); }

            await Fixture(true);
            var failure = await queue.RunAsync(chain, options, new(artifacts));
            Check(failure.Failed && failure.Tasks[0].Outcome == TaskOutcome.Failed && failure.Tasks[1].Reason == "previous_failure", "Device failure became queue success");
            Check(failure.Tasks[0].Error?.Contains("synthetic tap failure", StringComparison.Ordinal) == true &&
                failure.Tasks[0].FailureFrames is ["failure.png"], "Failure lost cause or registered frame");
            await Fixture(true);
            var keepGoing = await queue.RunAsync([chain[0], new("independent", "observe")], options, new(artifacts, ContinueOnFailure: true));
            Check(keepGoing.Failed && keepGoing.Tasks[1].Outcome == TaskOutcome.Succeeded, "ContinueOnFailure could not execute an independent observation");
            await Fixture(false);
            var retry = await queue.RunAsync(chain, options, new(artifacts, ResumeDirectory: failure.Directory));
            Check(!retry.Failed && retry.Tasks.All(t => t.Outcome == TaskOutcome.Succeeded), "Resume incorrectly skipped failed or unexecuted tasks");
            var deadline = await new TaskQueue([new DeadlineTask()]).RunAsync([new("deadline", "deadline", TimeoutSeconds: 0.02)], options, new(artifacts));
            Check(deadline.Failed && deadline.Tasks[0] is { Outcome: TaskOutcome.Failed, Reason: "TimeoutException" }, "Task deadline became cancellation or success");
        }
        finally { Environment.SetEnvironmentVariable("ALAS_TEST_ADB_FIXTURE", previous); }
        Console.WriteLine($"Independent task queue: {checks} checks passed; real CV/processes, synthetic ADB, no game actions.");
    }
    private sealed class DeadlineTask : ITaskRunner
    {
        public string Kind => "deadline";
        public bool RequiresActions => false;
        public void Validate(JsonObject? input) { }
        public IReadOnlyList<string> Preconditions(TaskRequest request, TaskCapabilities capabilities) => [];
        public async ValueTask<TaskResult> RunAsync(TaskRequest request, TaskContext context, CancellationToken token)
        { await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException("Deadline failed"); }
    }
}
