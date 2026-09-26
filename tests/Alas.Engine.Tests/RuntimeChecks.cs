using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Imaging;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static class RuntimeChecks
{
    public static async Task<int> FakeAdbAsync(string[] arguments)
    {
        string fixture = Environment.GetEnvironmentVariable("ALAS_TEST_ADB_FIXTURE") ?? throw new InvalidOperationException("Offline fixture is missing");
        var data = JsonNode.Parse(await File.ReadAllTextAsync(fixture))!;
        string folder = Path.GetDirectoryName(fixture)!;
        string stateFile = Path.Combine(folder, "state.txt");
        if (arguments.SequenceEqual(new[] { "exec-out", "screencap", "-p" }))
        {
            bool advanced = File.Exists(stateFile);
            await Console.OpenStandardOutput().WriteAsync(await File.ReadAllBytesAsync(data[advanced ? "second" : "first"]!.GetValue<string>()));
            return 0;
        }
        if (arguments is ["shell", "input", "tap", _, _])
        {
            await File.AppendAllTextAsync(Path.Combine(folder, "attempts.jsonl"), JsonSerializer.Serialize(arguments) + "\n");
            if (data["failTap"]!.GetValue<bool>()) { Console.Error.Write("synthetic tap failure"); return 1; }
            if (data["advance"]!.GetValue<bool>()) await File.WriteAllTextAsync(stateFile, "1");
            return 0;
        }
        if (arguments.SequenceEqual(new[] { "shell", "dumpsys", "window", "windows" })) { Console.Write("mCurrentFocus=Window{abcd u0 org.example.game/.Main}"); return 0; }
        if (arguments.SequenceEqual(new[] { "shell", "dumpsys", "display" })) { Console.Write("DisplayViewport{valid=true, orientation=1, deviceWidth=1280, deviceHeight=720}"); return 0; }
        Console.Error.Write("Unexpected offline device command");
        return 2;
    }

    public static async Task RunAsync(string python, string upstream, string artifacts)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Alas.Engine.Tests.exe" : "Alas.Engine.Tests");
        if (!File.Exists(executable)) throw new InvalidOperationException("Build the test apphost for offline process replay");
        var files = new AssetFiles(Path.Combine(upstream, "assets"));
        string first = Path.Combine(artifacts, "source.png"), second = Path.Combine(artifacts, "destination.png");
        await File.WriteAllBytesAsync(first, (await files.ReadAsync(UiAssets.UiWhite.MAIN_GOTO_CAMPAIGN_WHITE.For(GameServer.Cn))).ToArray());
        await File.WriteAllBytesAsync(second, (await files.ReadAsync(UiAssets.Ui.CAMPAIGN_CHECK.For(GameServer.Cn))).ToArray());
        string? previous = Environment.GetEnvironmentVariable("ALAS_TEST_ADB_FIXTURE");
        int checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        async Task<NavigationRunResult> Replay(string name, bool failTap = false, bool advance = true,
            string? initial = null, bool observe = false, CancellationToken token = default, TimeSpan? timeout = null)
        {
            string folder = Path.Combine(artifacts, name + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string fixture = Path.Combine(folder, "fixture.json");
            await File.WriteAllTextAsync(fixture, JsonSerializer.Serialize(new { first = initial ?? first, second, failTap, advance }));
            Environment.SetEnvironmentVariable("ALAS_TEST_ADB_FIXTURE", fixture);
            return await NavigationRun.RunAsync(new NavigationRunOptions(executable, "offline-replay", GameServer.Cn,
                Path.Combine(upstream, "assets"), python, folder, observe ? null : "page_campaign", observe ? null : "org.example.game", timeout), token);
        }
        try
        {
            var navigation = await Replay("navigation");
            Check(navigation.Error is null && navigation.Pages.SequenceEqual(new[] { "page_campaign" }) && navigation.ActionAttempts > 0,
                "Independent navigation did not reach the actual destination image: " + navigation.Error);
            var actions = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(navigation.Artifacts, "actions.json")))!.AsArray();
            Check(actions.Count == navigation.ActionAttempts && actions.All(a => a!["completed"]!.GetValue<bool>()), "Navigation action evidence is incomplete");
            var observe = await Replay("observe", observe: true);
            Check(observe.Error is null && observe.ActionAttempts == 0 && observe.Pages.Count > 0, "Read-only observation performed actions or found no page");
            var failure = await Replay("tap-failure", failTap: true);
            Check(failure.Error == "IOException" && failure.Pages.Count == 0 && failure.ActionAttempts == 1, "Failed device action became navigation success");
            var failedActions = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(failure.Artifacts, "actions.json")))!.AsArray();
            var failedEvidence = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(failure.Artifacts, "failure.json")))!;
            Check(!failedActions[0]!["completed"]!.GetValue<bool>() && failedActions[0]!["error"]!.GetValue<string>() == "IOException" &&
                failedEvidence["failure_frames"]!.AsArray().Count == 1 && failedEvidence["error"]!.GetValue<string>().Contains("synthetic tap failure"),
                "Failure lost its frame, original cause, or action state");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var cancellation = await Replay("cancelled", token: cancelled.Token);
            Check(cancellation.Error == "OperationCanceledException" && cancellation.ActionAttempts == 0, "Runtime cancellation was not preserved");
            var timedOut = await Replay("timeout", advance: false, timeout: TimeSpan.FromMilliseconds(300));
            Check(timedOut.Error == "TimeoutException" && timedOut.Pages.Count == 0, "Overall navigation deadline was not preserved");
            string unsupportedFrame = Path.Combine(artifacts, "unsupported-size.png");
            await File.WriteAllBytesAsync(unsupportedFrame, VisionChecks.Png(32, 24, new byte[32 * 24 * 3]));
            var dimensions = await Replay("unsupported-size", initial: unsupportedFrame);
            Check(dimensions.Error == "NotSupportedException" && dimensions.ActionAttempts == 0 &&
                File.Exists(Path.Combine(dimensions.Artifacts, "failure.png")), "Unnormalized device frame reached UI actions or lost evidence");
            Console.WriteLine($"Independent runtime replay: {checks} checks passed; real CV/processes, static upstream image frames, synthetic ADB only.");
        }
        finally { Environment.SetEnvironmentVariable("ALAS_TEST_ADB_FIXTURE", previous); }
    }
}
