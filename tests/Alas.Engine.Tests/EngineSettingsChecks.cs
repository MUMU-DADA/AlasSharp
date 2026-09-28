using System.Text.Json.Nodes;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static class EngineSettingsChecks
{
    public static async Task RunAsync(string artifacts)
    {
        string root = Path.Combine(Path.GetFullPath(artifacts), Guid.NewGuid().ToString("N"));
        var profiles = new EngineProfileStore(root);
        profiles.Create("fixture");
        string profilePath = Path.Combine(root, "profiles", "fixture.json");
        var profile = JsonNode.Parse(await File.ReadAllTextAsync(profilePath))!;
        profile["device"]!["serial"] = "offline-settings-fixture";
        profile["device"]!["package"] = "org.example.game";
        await File.WriteAllTextAsync(profilePath, profile.ToJsonString());
        string path = Path.Combine(root, "settings.json"), startup = Path.Combine(root, "startup.json");
        Directory.CreateDirectory(Path.Combine(root, "config"));
        string retired = Path.Combine(root, "config", "deploy.yaml");
        await File.WriteAllTextAsync(retired, "PythonExecutable: must-not-run\nRun: fixture\n");
        var settings = new EngineSettingsWorkspace(root, profiles, () => false);
        var schema = settings.Read();
        Check(schema["groups"]!.AsArray().SelectMany(group => group!["fields"]!.AsArray())
            .Select(field => field!["key"]!.GetValue<string>()).SequenceEqual(["AdbPath", "VisionRuntime", "OcrModelDirectory"]),
            "Schema advertises unavailable or missing settings");
        foreach (string language in new[] { "zh-CN", "zh-MIAO", "zh-TW", "en-US", "ja-JP" }) settings.Read(language);
        Reject<ArgumentException>(() => settings.Read("unknown"));
        Check(!settings.ReadStartup("fixture")["enabled"]!.GetValue<bool>(), "Retired startup list was consumed");
        Check(!File.Exists(path) && !File.Exists(startup), "Reads wrote settings");

        settings.Patch(new() { ["AdbPath"] = "tools/adb", ["VisionRuntime"] = "runtime/python", ["OcrModelDirectory"] = "models" });
        string before = await File.ReadAllTextAsync(path);
        foreach (var invalid in new JsonObject[]
        {
            new() { ["AdbPath"] = "changed", ["PythonExecutable"] = "retired" },
            new() { ["AdbPath"] = true }, new() { ["VisionRuntime"] = "" }, new() { ["VisionRuntime"] = "bad\npath" },
            new() { ["OcrModelDirectory"] = new JsonArray() }, new() { ["Run"] = "fixture" },
        })
        {
            Reject<ArgumentException>(() => settings.Patch(invalid));
            Check(await File.ReadAllTextAsync(path) == before, "Rejected patch partially saved");
        }
        Reject<EngineProfileException>(() => new EngineSettingsWorkspace(root, demo: () => true).Patch(new() { ["AdbPath"] = "demo" }));
        Reject<EngineProfileException>(() => settings.SetStartup("missing", true));
        Reject<ArgumentException>(() => settings.SetStartup("../escape", true));
        settings.SetStartup("fixture", true);
        settings.Patch(new() { ["OcrModelDirectory"] = "" });
        Check(settings.ReadStartup("fixture")["enabled"]!.GetValue<bool>(), "Ordinary save cleared startup preference");
        Check(JsonNode.Parse(await File.ReadAllTextAsync(startup))!["contract"]!.GetValue<string>() == "engine-startup/1",
            "Startup preferences do not have their own contract");

        var environment = new[] { "ALAS_ADB", "ALAS_CV_RUNTIME", "ALAS_OCR_MODELS" }
            .ToDictionary(key => key, Environment.GetEnvironmentVariable);
        try
        {
            foreach (string key in environment.Keys) Environment.SetEnvironmentVariable(key, null);
            var snapshot = settings.ReadRuntime();
            Check(snapshot == new EngineRuntimeSettings(Path.Combine(root, "tools", "adb"), Path.Combine(root, "runtime", "python"), null),
                "Relative runtime settings are not based on Engine root");
            settings.Patch(new() { ["AdbPath"] = "adb-on-path", ["VisionRuntime"] = "python-on-path", ["OcrModelDirectory"] = "ocr" });
            Check(snapshot.Adb != settings.ReadRuntime().Adb, "Saved settings mutated an existing queue snapshot");
            Check(settings.ReadRuntime().ModelDirectory == Path.Combine(root, "ocr"), "Model directory was not resolved");
            Environment.SetEnvironmentVariable("ALAS_CV_RUNTIME", "environment-python");
            Check(settings.ReadRuntime().VisionRuntime == "environment-python", "Explicit environment override lost");
            Environment.SetEnvironmentVariable("ALAS_CV_RUNTIME", null);

            // A non-existent executable is harmless in dry-run; control-plane settings
            // reads must not create a device or a CV process.
            var workspace = new EngineControlWorkspace(root, root, "", "", Path.Combine(root, "runs"), Path.Combine(root, "control"));
            var requests = new[] { new TaskRequest("observe", "observe") };
            workspace.StartRun(new() { ["queue"] = new JsonObject { ["tasks"] = System.Text.Json.JsonSerializer.SerializeToNode(requests, TaskQueue.Json) } });
            for (int attempt = 0; attempt < 200 && workspace.State()["active"]!["status"]!.GetValue<string>() == "running"; attempt++)
                await Task.Delay(25);
            await workspace.BeginShutdown();
            Check(workspace.State()["active"]!["error"] is null, "Settings broke dry-run or started an executable");
            Check(workspace.State()["report"]!["queue_outcome"]!.GetValue<string>() == "dry_run", "Dry-run verdict changed");
            settings.Patch(new() { ["VisionRuntime"] = "missing-vision-runtime-fixture.exe", ["OcrModelDirectory"] = "" });
            var live = new EngineControlWorkspace(root, root, "", "", Path.Combine(root, "runs"), Path.Combine(root, "live-control"));
            live.StartRun(new() { ["mode"] = "read_only", ["serial"] = "offline-settings-fixture",
                ["queue"] = new JsonObject { ["tasks"] = System.Text.Json.JsonSerializer.SerializeToNode(requests, TaskQueue.Json) } });
            // Shut down only after the request is accepted. The current task
            // must use the saved runtime and fail before any device I/O.
            for (int attempt = 0; attempt < 200 && live.State()["active"]!["status"]!.GetValue<string>() == "running"; attempt++)
                await Task.Delay(25);
            await live.BeginShutdown();
            string runDirectory = live.State()["active"]!["run_directory"]!.GetValue<string>();
            Check(Directory.EnumerateFiles(runDirectory, "task.json", SearchOption.AllDirectories)
                .Any(file => File.ReadAllText(file).Contains("missing-vision-runtime-fixture.exe", StringComparison.Ordinal)),
                "Control workspace ignored the saved CV runtime or did not record the startup failure");
        }
        finally { foreach (var (key, value) in environment) Environment.SetEnvironmentVariable(key, value); }

        string[] keys = ["AdbPath", "VisionRuntime", "OcrModelDirectory"];
        await Task.WhenAll(keys.Select((key, index) => Task.Run(() =>
            new EngineSettingsWorkspace(root, demo: () => false).Patch(new() { [key] = "concurrent-" + index }))));
        var persisted = JsonNode.Parse(await File.ReadAllTextAsync(path))!["values"]!;
        for (int index = 0; index < keys.Length; index++)
            Check(persisted[keys[index]]!.GetValue<string>() == "concurrent-" + index, "Concurrent update lost a different field");

        if (OperatingSystem.IsWindows())
        {
            before = await File.ReadAllTextAsync(path);
            using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bool rejected = false;
                try { settings.Patch(new() { ["AdbPath"] = "locked" }); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { rejected = true; }
                Check(rejected, "Denied replacement was reported as saved");
                Check(await File.ReadAllTextAsync(path) == before, "Denied atomic replacement damaged the original");
            }
            Check(!Directory.EnumerateFiles(root, "settings.json.*.tmp").Any(), "Failed transaction left temporary files");
        }
        foreach (string invalid in new[] { "{broken", "[]", "{\"contract\":\"engine-settings/2\",\"values\":{}}",
            "{\"contract\":\"engine-settings/1\",\"values\":{\"AdbPath\":\"a\",\"AdbPath\":\"b\"}}",
            "{\"contract\":\"engine-settings/1\",\"values\":{\"VisionRuntime\":false}}" })
        {
            await File.WriteAllTextAsync(path, invalid);
            Reject<EngineProfileException>(() => settings.Read());
            Reject<EngineProfileException>(() => settings.Patch(new() { ["AdbPath"] = "must-not-repair" }));
            Check(await File.ReadAllTextAsync(path) == invalid, "Corrupt settings silently replaced");
        }
        await File.WriteAllTextAsync(startup, "{\"contract\":\"engine-startup/1\",\"run\":[42]}");
        Reject<EngineProfileException>(() => settings.ReadStartup("fixture"));
        Check(await File.ReadAllTextAsync(retired) == "PythonExecutable: must-not-run\nRun: fixture\n", "Retired local data was modified");
        Console.WriteLine("PASS: Engine settings schema, atomic validation, concurrent saves, runtime resolution, dry-run, startup isolation and corrupt-file refusal");
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
