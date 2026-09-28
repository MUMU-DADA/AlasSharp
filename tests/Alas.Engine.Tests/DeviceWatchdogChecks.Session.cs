using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static partial class DeviceWatchdogChecks
{
    public static async Task<int> FakeAdbAsync(string[] args)
    {
        if (args is ["exec-out", "screencap", "-p"])
        {
            byte[] image = await File.ReadAllBytesAsync(Environment.GetEnvironmentVariable("ALAS_TEST_WATCHDOG_IMAGE")!);
            await Console.OpenStandardOutput().WriteAsync(image); return 0;
        }
        if (args is ["shell", "dumpsys", "window", "windows"])
        {
            string package = Environment.GetEnvironmentVariable("ALAS_TEST_WATCHDOG_FOREGROUND") ?? "org.example.game";
            Console.Write("mCurrentFocus=Window{123 u0 " + package + "/.Main}"); return 0;
        }
        if (args is ["shell", "input", "tap", _, _] or ["shell", "input", "swipe", _, _, _, _, _] or
            ["shell", "am", "force-stop", _]) return 0;
        Console.Error.Write("Unsupported watchdog ADB fixture operation"); return 1;
    }
    private static async Task SessionAsync(string python, string upstream, string artifacts)
    {
        string? imageBefore = Environment.GetEnvironmentVariable("ALAS_TEST_WATCHDOG_IMAGE");
        string? appBefore = Environment.GetEnvironmentVariable("ALAS_TEST_WATCHDOG_FOREGROUND");
        Environment.SetEnvironmentVariable("ALAS_TEST_WATCHDOG_IMAGE", Path.Combine(artifacts, "watchdog.png"));
        string exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Alas.Engine.Tests.exe" : "Alas.Engine.Tests");
        var options = new EngineSessionOptions(exe, "offline-watchdog", GameServer.Cn, Path.Combine(upstream, "assets"), python,
            ApplicationPackage: "org.example.game", AllowActions: true);
        try
        {
            var runner = new Runner();
            var queue = await new TaskQueue([runner]).RunAsync([new("repeated", runner.Kind), new("alternating", runner.Kind), new("next", runner.Kind)],
                options, new(Path.Combine(artifacts, "queue"), ContinueOnFailure: true));
            Check(queue.Tasks is [{ Outcome: TaskOutcome.Failed, Reason: nameof(GameTooManyClicksException) },
                { Outcome: TaskOutcome.Failed, Reason: nameof(GameTooManyClicksException) }, { Outcome: TaskOutcome.Succeeded }],
                "Queue did not fail before the repeated physical action: " + string.Join(';', queue.Tasks.Select(task => task.Error)));
            Check(queue.Tasks.Take(2).All(task => task.FailureFrames is ["failure.png"] &&
                task.Evidence!["boundary"]!["actionAttempts"]!.GetValue<int>() == 12) &&
                queue.Tasks[2].Evidence!["boundary"]!["actionAttempts"]!.GetValue<int>() == 1 && Complete(),
                "Queue failure frames, action attempts or task isolation are inconsistent");
            var files = Directory.GetFiles(queue.Directory, "device-watchdog.json", SearchOption.AllDirectories);
            Check(files.Length == 2, "Watchdog artifacts leaked across tasks");
            foreach (string file in files)
            {
                var original = await File.ReadAllTextAsync(file);
                var record = JsonSerializer.Deserialize<DeviceWatchdogEvidence[]>(original, TaskQueue.Json)!.Single();
                Check(record.Controls.Length == 12 && record.FrameSequence is > 0, "Session watchdog omitted control identities");
                var actions = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(file)!, "actions.json")))!.AsArray();
                Check(actions.Take(11).All(action => action!["completed"]!.GetValue<bool>()) &&
                    !actions[^1]!["completed"]!.GetValue<bool>() && actions[^1]!["error"]!.GetValue<string>() == nameof(GameTooManyClicksException),
                    "Blocked input was recorded as completed");
                try
                {
                    File.Delete(file); Check(!Complete(), "Missing watchdog artifact accepted");
                    foreach (string corrupt in new[] { "[]", "[null]", "{", JsonSerializer.Serialize(new[] { record with { Controls = ["button"] } }, TaskQueue.Json) })
                    { await File.WriteAllTextAsync(file, corrupt); Check(!Complete(), "Invalid watchdog evidence accepted"); }
                }
                finally { await File.WriteAllTextAsync(file, original); }
            }
            bool Complete() => RunReport.Build(queue.Directory).ToJson()["evidence_complete"]!.GetValue<bool>();

            foreach (bool running in new[] { true, false })
            {
                Environment.SetEnvironmentVariable("ALAS_TEST_WATCHDOG_FOREGROUND", running ? "org.example.game" : "org.example.launcher");
                var clock = new Clock();
                await using var session = new EngineSession(options, clock);
                _ = session.BeginTask(TimeSpan.FromMinutes(10));
                await session.Driver.ScreenshotAsync(default); session.Driver.ResetProgress();
                Check(await session.Driver.AppearsAsync(UiAssets.Handler.IN_MAP), "Actual session watchdog image did not match IN_MAP");
                clock.Advance(61);
                for (int i = 0; i < 60; i++) await session.Driver.ScreenshotAsync(default);
                long sequence = session.Driver.Frame!.Sequence;
                if (running) await RejectAsync<GameStuckException>(() => session.Driver.ScreenshotAsync(default));
                else await RejectAsync<GameNotRunningException>(() => session.Driver.ScreenshotAsync(default));
                string directory = Path.Combine(artifacts, running ? "stuck" : "dead");
                var evidence = await session.SaveEvidenceAsync(directory, true);
                Check(evidence.Image == "failure.png" && evidence.FrameSequence == sequence && evidence.ActionAttempts == 0 &&
                    evidence.DeviceWatchdogFile == "device-watchdog.json", "Actual session did not save a stopped screenshot and watchdog evidence");
                var record = JsonSerializer.Deserialize<DeviceWatchdogEvidence[]>(await File.ReadAllTextAsync(Path.Combine(directory, evidence.DeviceWatchdogFile!)), TaskQueue.Json)!.Single();
                Check(record.ApplicationRunning == running && record.Detections.Contains("IN_MAP"), "Actual foreground query did not distinguish a dead game");
                RunReport.ValidateDeviceWatchdog(record);
                _ = session.BeginTask(TimeSpan.FromMinutes(10));
                await session.Driver.ScreenshotAsync(default);
                var next = await session.SaveEvidenceAsync(Path.Combine(directory, "next"), false);
                Check(next.DeviceWatchdogFile is null, "Actual session retained prior watchdog failure across task boundary");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("ALAS_TEST_WATCHDOG_IMAGE", imageBefore);
            Environment.SetEnvironmentVariable("ALAS_TEST_WATCHDOG_FOREGROUND", appBefore);
        }
    }
    private sealed class Runner : ITaskRunner
    {
        public string Kind => "device_watchdog_probe";
        public bool RequiresActions => true;
        public void Validate(JsonObject? input) { }
        public IReadOnlyList<string> Preconditions(TaskRequest request, TaskCapabilities capabilities) => [];
        public async ValueTask<TaskResult> RunAsync(TaskRequest request, TaskContext context, CancellationToken token)
        {
            var session = (EngineSession)context.Campaign!;
            await session.Driver.ScreenshotAsync(token);
            Check(await session.Driver.AppearsAsync(UiAssets.Handler.IN_MAP, token: token), "Queue screenshot did not match fixture");
            for (int i = 0; i < (request.Id == "next" ? 1 : 12); i++)
                await session.Driver.ClickAsync(request.Id == "alternating" && i % 2 == 0 ? UiAssets.Ui.BACK_ARROW : UiAssets.Map.SWITCH_OVER, token);
            return new(request.Id, Kind, TaskOutcome.Succeeded, "next_task_has_new_control_history");
        }
    }
}
