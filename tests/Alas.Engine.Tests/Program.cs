using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Imaging;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tests;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;
if (args is ["-s", "offline-replay", ..]) return await RuntimeChecks.FakeAdbAsync(args[2..]);

if (args is ["--echo", var argument])
{
    Console.Write(argument);
    Console.Error.Write(new string('e', 100_000));
    return 7;
}
if (args is ["--wait"]) { await Task.Delay(Timeout.Infinite); return 0; }
if (args is ["--large"])
{
    await Console.OpenStandardOutput().WriteAsync(new byte[33 * 1024 * 1024]);
    return 0;
}

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = false };
int checks = 0;
void Check(bool value, string message)
{
    checks++;
    if (!value) throw new InvalidOperationException(message);
}
async Task Throws<T>(Func<Task> action, string message) where T : Exception
{
    try { await action(); }
    catch (T) { checks++; return; }
    throw new InvalidOperationException(message);
}

try
{
    var references = typeof(CampaignExecution).Assembly.GetReferencedAssemblies();
    Check(references.All(r => r.Name is { } name && (name.StartsWith("System.", StringComparison.Ordinal) || name == "Microsoft.Win32.Primitives")),
        "New engine references the legacy backend or an unexpected runtime: " + string.Join(", ", references.Select(r => r.Name)));
    Check(Cell.Parse("AA12").ToString() == "AA12", "Multi-letter map coordinates");
    await Throws<FormatException>(() => Task.Run(() => Cell.Parse("A0")), "Invalid cell accepted");
    await Throws<ArgumentException>(() => Task.Run(() => new MapDefinition("B2", "-- -- -- --", [], [], [])), "Ragged map accepted");
    await Throws<NotSupportedException>(() => Task.Run(() => RuleCatalog.Create("unported/rule")), "Missing rule fell back");
    var one = RuleCatalog.Create("campaign_main/campaign_1_1");
    var first = new CampaignState(one.Map);
    var second = new CampaignState(one.Map);
    first.Cells[0].IsEnemy = true;
    Check(!second.HasNonBossEnemy && !second.HasBoss, "Map declarations or another sortie contaminated state");
    var configuration = one.Configure(new CampaignConfiguration { Fleet2 = 2, Submarine = 1, ClearAllThisTime = true });
    Check(configuration is { Fleet2: 0, Submarine: 0, ClearAllThisTime: true }, "Chapter configuration overwrote unrelated values");
    Check(RuleCatalog.Create("campaign_main/campaign_1_2").Map.Tiles[3] == MapTile.LowPriorityEnemy, "ME/Me distinction lost");
    var mapIds = CampaignMapCatalog.Ids.ToArray();
    Check(mapIds.Length >= 1370, $"Compiled map catalog is incomplete: {mapIds.Length}");
    var eventMap = CampaignMapCatalog.Get("event_20220224_cn/a1").Map;
    Check(eventMap.Mechanisms.FortressEnemies.Contains(Cell.Parse("E3")) &&
          eventMap.Mechanisms.FortressBlocks.Contains(Cell.Parse("E2")) &&
          eventMap.Mechanisms.BouncingRoutes.Single().SequenceEqual([Cell.Parse("C2"), Cell.Parse("C3"), Cell.Parse("C4")]),
        "Map mechanism declarations were not compiled");
    Check(CampaignMapCatalog.Get("war_archives_20210422_cn/b1").Map.Mechanisms.Mazes.Length == 3,
        "Maze declarations were not compiled");
    Check(new CampaignState(CampaignMapCatalog.Get("campaign_main/campaign_15_3").Map).Cells[0] is Alas.Engine.Rules.Main.W15CellState,
        "Custom grid behavior was not compiled");
    var customGrid = new Alas.Engine.Rules.Main.W15CellState(new(1, 1), MapTile.Siren);
    Check(customGrid.Merge(new(IsBoss: true, IsFleet: true)) && customGrid.IsSiren && !customGrid.IsFleet,
        "Custom grid merge behavior was not applied");
    Check(CampaignMapCatalog.Get("event_20200326_cn/d3").Map.SwipePreset == new SwipePreset(0, 2),
        "Swipe preset declaration was not compiled");
    var ignoredMap = CampaignMapCatalog.Get("campaign_main/campaign_9_1").Map;
    var ignoredState = new CampaignState(ignoredMap);
    var ignoredResult = ignoredState.ApplyObservation(new(
        [new MapCellObservation(new(0, 0), new(IsEnemy: true, EnemyScale: 1, EnemyGenre: "Enemy"))],
        Cell.Parse("D5"), new(0, 0)));
    Check(ignoredResult.Accepted && ignoredResult.Ignored.Contains(Cell.Parse("D5")) && !ignoredState[Cell.Parse("D5")].IsEnemy,
        "Compiled ignore_prediction rule was not applied");

    // Real process execution verifies binary reads, parallel stderr, arguments, timeout and cancellation.
    var process = new ProcessRunner();
    string executable = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
    string assembly = Assembly.GetExecutingAssembly().Location;
    string literal = "spaces ; & $() 中文";
    var echo = await process.RunAsync(executable, [assembly, "--echo", literal], TimeSpan.FromSeconds(15));
    Check(echo.ExitCode == 7 && Encoding.UTF8.GetString(echo.Output) == literal && echo.Error.Length == 100_000,
        $"Process transport changed arguments, exit code or concurrent output: exit={echo.ExitCode}, output={Encoding.UTF8.GetString(echo.Output)}, stderr_length={echo.Error.Length}");
    await Throws<TimeoutException>(() => process.RunAsync(executable, [assembly, "--wait"], TimeSpan.FromMilliseconds(300)), "Timeout missing");
    using (var cancelled = new CancellationTokenSource(300))
        await Throws<OperationCanceledException>(() => process.RunAsync(executable, [assembly, "--wait"], TimeSpan.FromSeconds(15), cancelled.Token), "Cancellation became timeout");
    await Throws<IOException>(() => process.RunAsync(executable, [assembly, "--large"], TimeSpan.FromSeconds(15)), "Oversized output accepted or misclassified");
    var transport = new FakeProcess();
    var readOnlyDevice = new AdbDevice("adb", "test-device", false, transport);
    await Throws<InvalidOperationException>(() => readOnlyDevice.BackAsync().AsTask(), "Read-only device executed action");
    Check(transport.Calls.Count == 0, "Refused action started a process");
    var device = new AdbDevice("adb", "test-device", true, transport);
    await device.TapAsync(new PixelPoint(10, 20));
    Check(transport.Calls.Single().SequenceEqual(new[] { "-s", "test-device", "shell", "input", "tap", "10", "20" }), "ADB tap arguments differ");
    transport.Response = new ProcessResponse(1, [], "synthetic failure");
    await Throws<IOException>(() => device.CaptureAsync().AsTask(), "ADB failure swallowed");
    transport.Response = new ProcessResponse(0, Encoding.UTF8.GetBytes("not a screenshot"), "");
    await Throws<IOException>(() => device.CaptureAsync().AsTask(), "Invalid screenshot accepted");
    var application = new AdbApplication("adb", "test-device", "org.example.game", false, transport);
    int beforeStop = transport.Calls.Count;
    await Throws<InvalidOperationException>(() => application.StopAsync(default).AsTask(), "Read-only application was stopped");
    Check(transport.Calls.Count == beforeStop, "Rejected application stop reached ADB");
    transport.Response = new ProcessResponse(0, Encoding.UTF8.GetBytes("mCurrentFocus=Window{abcd u0 org.example.other/.Main}"), "");
    Check(!await application.IsRunningAsync(default), "Background application was treated as foreground");
    transport.Response = new ProcessResponse(0, Encoding.UTF8.GetBytes("mCurrentFocus=Window{abcd u0 org.example.game/.Main}"), "");
    Check(await application.IsRunningAsync(default), "Focused application was rejected");
    transport.Response = new ProcessResponse(0, Encoding.UTF8.GetBytes("ACTIVITY org.example.other/.Main abcd pid=123\nACTIVITY org.example.game/.Main efab pid=456"), "");
    Check(await application.IsRunningAsync(default), "Activity fallback did not use the final activity");
    transport.Response = new ProcessResponse(0, Encoding.UTF8.GetBytes("DisplayViewport{type=INTERNAL, valid=true, orientation=1, deviceWidth=1280, deviceHeight=720}"), "");
    await application.RefreshOrientationAsync(default);
    Check(application.Orientation == 1, "ADB orientation parsing failed");
    transport.Response = new ProcessResponse(0, [], "");
    await application.RefreshOrientationAsync(default);
    Check(application.Orientation == 0 && !application.OrientationWasReported, "Unknown orientation did not preserve upstream normal-orientation assumption");
    await Throws<IOException>(() => application.IsRunningAsync(default).AsTask(), "Unknown foreground silently accepted");
    Console.WriteLine($"Local engine/transport checks passed: {checks}");

    if (args is ["--maps", var mapsPython, var mapsUpstream, var mapsArtifacts])
    {
        string folder = Path.GetFullPath(mapsArtifacts);
        Directory.CreateDirectory(folder);
        await MapCatalogChecks.RunAsync(Path.GetFullPath(mapsPython), Path.GetFullPath(mapsUpstream), folder);
        return 0;
    }
    if (args is ["--recognition", var recognitionPython, var recognitionUpstream, var recognitionArtifacts])
    {
        string folder = Path.GetFullPath(recognitionArtifacts);
        Directory.CreateDirectory(folder);
        await RecognitionChecks.RunAsync(Path.GetFullPath(recognitionPython), Path.GetFullPath(recognitionUpstream), folder);
        return 0;
    }
    if (args is ["--observation", var observationPython, var observationUpstream, var observationArtifacts])
    {
        string folder = Path.GetFullPath(observationArtifacts);
        Directory.CreateDirectory(folder);
        await ObservationChecks.RunAsync(Path.GetFullPath(observationPython), Path.GetFullPath(observationUpstream), folder);
        return 0;
    }
    if (args is ["--path", var pathPython, var pathUpstream, var pathArtifacts])
    {
        string folder = Path.GetFullPath(pathArtifacts);
        Directory.CreateDirectory(folder);
        await PathChecks.RunAsync(Path.GetFullPath(pathPython), Path.GetFullPath(pathUpstream), folder);
        return 0;
    }
    if (args is ["--grid", var gridPython, var gridUpstream, var gridArtifacts])
    {
        string folder = Path.GetFullPath(gridArtifacts);
        Directory.CreateDirectory(folder);
        await GridChecks.RunAsync(Path.GetFullPath(gridPython), Path.GetFullPath(gridUpstream), folder);
        return 0;
    }
    if (args is ["--data-key", var taskPython, var taskUpstream, var taskArtifacts])
    {
        string folder = Path.GetFullPath(taskArtifacts);
        Directory.CreateDirectory(folder);
        await DataKeyChecks.RunAsync(Path.GetFullPath(taskPython), Path.GetFullPath(taskUpstream), folder);
        return 0;
    }
    if (args is ["--queue", var queuePython, var queueUpstream, var queueArtifacts])
    {
        string folder = Path.GetFullPath(queueArtifacts);
        Directory.CreateDirectory(folder);
        await QueueChecks.RunAsync(Path.GetFullPath(queuePython), Path.GetFullPath(queueUpstream), folder);
        return 0;
    }
    if (args is ["--ocr", var ocrPython, var ocrUpstream, var ocrArtifacts])
    {
        string folder = Path.GetFullPath(ocrArtifacts);
        Directory.CreateDirectory(folder);
        await OcrChecks.RunAsync(Path.GetFullPath(ocrPython), Path.GetFullPath(ocrUpstream), folder);
        return 0;
    }
    if (args is ["--runtime", var runtimePython, var runtimeUpstream, var runtimeArtifacts])
    {
        string folder = Path.GetFullPath(runtimeArtifacts);
        Directory.CreateDirectory(folder);
        await RuntimeChecks.RunAsync(Path.GetFullPath(runtimePython), Path.GetFullPath(runtimeUpstream), folder);
        return 0;
    }
    if (args is ["--recovery", var recoveryPython, var recoveryUpstream, var recoveryArtifacts])
    {
        string folder = Path.GetFullPath(recoveryArtifacts);
        Directory.CreateDirectory(folder);
        await RecoveryChecks.RunAsync(Path.GetFullPath(recoveryPython), Path.GetFullPath(recoveryUpstream), folder);
        return 0;
    }
    if (args is ["--ui", var uiPython, var uiUpstream, var uiArtifacts])
    {
        string folder = Path.GetFullPath(uiArtifacts);
        Directory.CreateDirectory(folder);
        await UiChecks.RunAsync(Path.GetFullPath(uiPython), Path.GetFullPath(uiUpstream), folder);
        return 0;
    }

    if (args is ["--vision", var visionPython, var visionArtifacts])
    {
        string folder = Path.GetFullPath(visionArtifacts);
        Directory.CreateDirectory(folder);
        Console.WriteLine($"Pure CV checks passed: {await VisionChecks.RunAsync(Path.GetFullPath(visionPython), folder)}; native campaign comparison NOT RUN.");
        return 0;
    }

    if (args.Length == 0)
    {
        Console.WriteLine("Native comparison and pure CV checks NOT RUN: supply --python <executable> --upstream <source> --artifacts <directory>.");
        return 0;
    }
    if (args.Length != 6 || args[0] != "--python" || args[2] != "--upstream" || args[4] != "--artifacts")
        throw new ArgumentException("Expected --python <executable> --upstream <source> --artifacts <directory>");
    string python = Path.GetFullPath(args[1]), upstream = Path.GetFullPath(args[3]), artifacts = Path.GetFullPath(args[5]);
    Directory.CreateDirectory(artifacts);
    int visionChecks = await VisionChecks.RunAsync(python, artifacts);
    Console.WriteLine($"Pure CV checks passed: {visionChecks}; synthetic pixels, no game action.");
    foreach (var source in RuleCatalog.Ids.SelectMany(id => RuleCatalog.Create(id).Sources).Distinct())
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(upstream, source.Path))));
        Check(hash == source.Sha256, $"Upstream source changed: {source.Path}");
    }
    foreach (var source in CampaignMapCatalog.Sources)
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(upstream, source.Path))));
        Check(hash == source.Sha256, $"Upstream map source changed: {source.Path}");
    }
    var cases = new List<Scenario>();
    foreach (string id in RuleCatalog.Ids)
    {
        foreach (bool poor in new[] { false, true })
        foreach (bool clear in new[] { false, true })
        foreach (bool movable in new[] { false, true })
        foreach (int count in new[] { 0, 1, 2, 3, 10, 13 })
        foreach (string cells in new[] { "empty", "boss", "enemy", "siren", "fortress", "boss_enemy" })
        foreach (string? yes in new[] { null, "fleet_2_break_siren_caught", "clear_siren", "clear_bouncing_enemy", "clear_any_enemy:cost_2", "clear_all_mystery", "pick_up_ammo", "clear_mechanism" })
        foreach (bool returned in new[] { false, true })
            cases.Add(new Scenario(id, BattleCount: count, Poor: poor, ClearAll: clear, Movable: movable,
                Cells: cells, TrueOperation: yes, CombatReturn: returned));
        cases.Add(new Scenario(id, "refocus"));
        foreach (bool handle in new[] { false, true })
        {
            foreach (string? signal in new[] { null, "moved", "moved_after_battle", "ended", "error" })
            foreach (int repeats in new[] { 1, 10, 11 })
                cases.Add(new Scenario(id, "execute", Signal: signal, SignalCount: repeats, HandleError: handle));
            cases.Add(new Scenario(id, "execute", CombatReturn: false, HandleError: handle));
            cases.Add(new Scenario(id, "execute", CombatReturn: false, HandleError: handle, Signal: "ended", SignalOperation: "withdraw"));
            cases.Add(new Scenario(id, "run", Advance: true, HandleError: handle));
            cases.Add(new Scenario(id, "run", Advance: true, HandleError: handle, Signal: "ended", SignalOperation: "clear_boss"));
            cases.Add(new Scenario(id, "run", HandleError: handle, CombatReturn: false));
            cases.Add(new Scenario(id, "run", AutoSearch: true, HandleError: handle));
            cases.Add(new Scenario(id, "run", AutoSearch: true, HandleError: handle, Signal: "ended", SignalOperation: "auto_search_combat:1"));
            cases.Add(new Scenario(id, "run", HandleError: handle, Signal: "ended", SignalOperation: "enter_map:normal"));
            cases.Add(new Scenario(id, "run", HandleError: handle, Signal: "error", SignalOperation: "map_init"));
        }
    }
    var expected = new List<ProbeResult>();
    foreach (var sample in cases) expected.Add(await Execute(sample));
    string input = Path.Combine(artifacts, "cases.json"), output = Path.Combine(artifacts, "native.json");
    await File.WriteAllTextAsync(input, JsonSerializer.Serialize(cases, json));
    await File.WriteAllTextAsync(Path.Combine(artifacts, "csharp.json"), JsonSerializer.Serialize(expected, json));
    var oracle = await process.RunAsync(python,
        [Path.Combine(AppContext.BaseDirectory, "native_campaign_reference.py"), upstream, input, output], TimeSpan.FromSeconds(90));
    if (oracle.ExitCode != 0) throw new InvalidOperationException($"Native reference failed: {oracle.Error}");
    var native = JsonNode.Parse(await File.ReadAllTextAsync(output))!.AsArray();
    Check(native.Count == cases.Count, "Native reference omitted scenarios");
    var failures = new List<object>();
    for (int index = 0; index < cases.Count; index++)
    {
        var actual = JsonSerializer.SerializeToNode(expected[index], json);
        if (!JsonNode.DeepEquals(native[index], actual)) failures.Add(new { index, sample = cases[index], csharp = actual, native = native[index]!.DeepClone() });
    }
    await File.WriteAllTextAsync(Path.Combine(artifacts, "differences.json"), JsonSerializer.Serialize(failures, json));
    Check(failures.Count == 0, $"Native differences: {failures.Count}/{cases.Count}; inspect differences.json");
    Console.WriteLine($"Native compiled-rule comparisons passed: {cases.Count}; source hashes matched. Synthetic actions, no device or settlement validation.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}

static async Task<ProbeResult> Execute(Scenario scenario)
{
    var rule = RuleCatalog.Create(scenario.Rule);
    var operations = new ProbeOperations(scenario);
    var execution = new CampaignExecution(rule, new CampaignConfiguration
    {
        PoorMapData = scenario.Poor, ClearAllThisTime = scenario.ClearAll, HasMovableNormalEnemy = scenario.Movable,
        HandleError = scenario.HandleError
    }, operations);
    var state = operations.State = execution.Context.State;
    state.BattleCount = scenario.BattleCount;
    state.Cells[0].IsBoss = scenario.Cells is "boss" or "boss_enemy";
    state.Cells[0].IsEnemy = scenario.Cells is "enemy" or "boss_enemy";
    state.Cells[0].IsSiren = scenario.Cells == "siren";
    state.Cells[0].IsFortress = scenario.Cells == "fortress";
    object? value = null;
    string? exception = null;
    try
    {
        if (scenario.Operation == "dispatch") value = await rule.DispatchAsync(execution.Context);
        else if (scenario.Operation == "execute") value = await execution.ExecuteBattleAsync();
        else if (scenario.Operation == "run")
            value = await execution.RunAsync() == CampaignLoopExit.Ended ? true : null;
        else if (scenario.Operation == "refocus") await rule.RefocusBossAsync(execution.Context);
        else throw new ArgumentException("Unknown test operation");
    }
    catch (CampaignEndedException) { exception = "CampaignEnd"; }
    catch (CampaignScriptException) { exception = "ScriptError"; }
    catch (IOException) { exception = "IOError"; }
    return new ProbeResult(operations.Calls.ToArray(), value, exception, state.BattleCount);
}

internal sealed class FakeProcess : IProcessRunner
{
    public List<string[]> Calls { get; } = [];
    public ProcessResponse Response { get; set; } = new(0, [], "");
    public Task<ProcessResponse> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken token = default)
    {
        Calls.Add(arguments.ToArray());
        return Task.FromResult(Response);
    }
}
