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
if (args is ["-s", "offline-map", ..]) return await DetectorChecks.FakeAdbAsync(args[2..]);
if (args is ["-s", "offline-watchdog", ..]) return await DeviceWatchdogChecks.FakeAdbAsync(args[2..]);

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
    if (args is ["--device-watchdog", var watchdogPython, var watchdogUpstream, var watchdogArtifacts])
    {
        await DeviceWatchdogChecks.RunAsync(Path.GetFullPath(watchdogPython), Path.GetFullPath(watchdogUpstream), Path.GetFullPath(watchdogArtifacts));
        return 0;
    }
    var references = typeof(CampaignExecution).Assembly.GetReferencedAssemblies();
    Check(references.All(r => r.Name is { } name && (name.StartsWith("System.", StringComparison.Ordinal) || name is "Microsoft.Win32.Primitives" or "YamlDotNet")),
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
    Check(CampaignRuleSourceCatalog.All.Length >= 1437,
        $"Campaign source contract is incomplete: {CampaignRuleSourceCatalog.All.Length}");
    Check(RuleCatalog.Ids.All(id => CampaignRuleSourceCatalog.TryGet(id, out var source) && source.HasCampaign),
        "A registered C# campaign rule has no upstream Campaign source contract");
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

    if (args is ["--map-recovery", var recoveryMapPython, var recoveryMapUpstream, var recoveryMapArtifacts])
    {
        string folder = Path.GetFullPath(recoveryMapArtifacts);
        Directory.CreateDirectory(folder);
        await MapRecoveryChecks.RunAsync(Path.GetFullPath(recoveryMapPython), Path.GetFullPath(recoveryMapUpstream), folder);
        return 0;
    }
    if (args is ["--detector", var detectorPython, var detectorUpstream, var detectorArtifacts, .. var savedFrames])
    {
        string folder = Path.GetFullPath(detectorArtifacts);
        Directory.CreateDirectory(folder);
        await DetectorChecks.RunAsync(Path.GetFullPath(detectorPython), Path.GetFullPath(detectorUpstream), folder, savedFrames);
        return 0;
    }
    if (args is ["--view", var viewPython, var viewUpstream, var viewArtifacts])
    {
        string folder = Path.GetFullPath(viewArtifacts);
        Directory.CreateDirectory(folder);
        await MapViewChecks.RunAsync(Path.GetFullPath(viewPython), Path.GetFullPath(viewUpstream), folder);
        return 0;
    }
    if (args is ["--spawn", var spawnPython, var spawnUpstream, var spawnArtifacts])
    {
        string folder = Path.GetFullPath(spawnArtifacts);
        Directory.CreateDirectory(folder);
        await MapSpawnChecks.RunAsync(Path.GetFullPath(spawnPython), Path.GetFullPath(spawnUpstream), folder);
        await MapScannerChecks.RunAsync();
        return 0;
    }
    if (args is ["--scanner"])
    {
        await MapScannerChecks.RunAsync();
        return 0;
    }
    if (args is ["--combat-rank"])
    {
        await CombatRankChecks.RunAsync();
        return 0;
    }
    if (args is ["--combat-flow"])
    {
        await CombatFlowChecks.RunAsync();
        return 0;
    }
    if (args is ["--walk-recovery"])
    {
        await MapWalkRecoveryChecks.RunAsync();
        return 0;
    }
    if (args is ["--walk-popups", var popupPython, var popupUpstream, var popupArtifacts])
    {
        await MapWalkPopupChecks.RunAsync(Path.GetFullPath(popupPython), Path.GetFullPath(popupUpstream), Path.GetFullPath(popupArtifacts));
        return 0;
    }
    if (args is ["--walk-interruptions", var interruptionPython, var interruptionUpstream, var interruptionArtifacts])
    {
        await MapWalkInterruptionChecks.RunAsync(Path.GetFullPath(interruptionPython), Path.GetFullPath(interruptionUpstream), Path.GetFullPath(interruptionArtifacts));
        return 0;
    }
    if (args is ["--walk-timeout"])
    {
        await MapWalkTimeoutChecks.RunAsync();
        return 0;
    }
    if (args is ["--walk-timeout", var timeoutPython, var timeoutUpstream, var timeoutArtifacts])
    {
        await MapWalkTimeoutChecks.RunAsync();
        await MapWalkTimeoutChecks.NativeAsync(Path.GetFullPath(timeoutPython), Path.GetFullPath(timeoutUpstream), Path.GetFullPath(timeoutArtifacts));
        return 0;
    }
    if (args is ["--walk-recovery", var walkPython, var walkUpstream, var walkArtifacts])
    {
        await MapWalkRecoveryChecks.RunAsync();
        await MapWalkRecoveryChecks.NativeAsync(Path.GetFullPath(walkPython), Path.GetFullPath(walkUpstream), Path.GetFullPath(walkArtifacts));
        return 0;
    }
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
    if (args is ["--roadblocks", var roadPython, var roadUpstream, var roadArtifacts])
    {
        string folder = Path.GetFullPath(roadArtifacts);
        Directory.CreateDirectory(folder);
        await RoadblockChecks.RunAsync(Path.GetFullPath(roadPython), Path.GetFullPath(roadUpstream), folder);
        await CampaignMapCombatChecks.RunAsync(Path.GetFullPath(roadPython), Path.GetFullPath(roadUpstream));
        await CampaignFleetSwitcherChecks.RunAsync();
        return 0;
    }
    if (args is ["--boss-refocus", var refocusPython, var refocusUpstream, var refocusArtifacts])
    {
        string folder = Path.GetFullPath(refocusArtifacts);
        Directory.CreateDirectory(folder);
        await MapViewChecks.BossRefocusChecksAsync(Path.GetFullPath(refocusPython), Path.GetFullPath(refocusUpstream), folder);
        return 0;
    }
    if (args is ["--declared-roads", var declaredRoadPython, var declaredRoadUpstream, var declaredRoadArtifacts])
    {
        string folder = Path.GetFullPath(declaredRoadArtifacts);
        Directory.CreateDirectory(folder);
        await CampaignMapCombatChecks.DeclaredRoadChecksAsync(Path.GetFullPath(declaredRoadPython), Path.GetFullPath(declaredRoadUpstream), folder);
        return 0;
    }
    if (args is ["--second-fleet", var secondFleetPython, var secondFleetUpstream, var secondFleetArtifacts])
    {
        string folder = Path.GetFullPath(secondFleetArtifacts);
        Directory.CreateDirectory(folder);
        await CampaignMapCombatChecks.SecondFleetChecksAsync(Path.GetFullPath(secondFleetPython), Path.GetFullPath(secondFleetUpstream), folder);
        return 0;
    }
    if (args is ["--main-chapters", var chapterPython, var chapterUpstream, var chapterArtifacts])
    {
        string folder = Path.GetFullPath(chapterArtifacts);
        Directory.CreateDirectory(folder);
        await CampaignMapCombatChecks.MainChapterChecksAsync(Path.GetFullPath(chapterPython), Path.GetFullPath(chapterUpstream), folder);
        return 0;
    }
    if (args is ["--enemy-filter", var filterPython, var filterUpstream, var filterArtifacts])
    {
        string folder = Path.GetFullPath(filterArtifacts);
        Directory.CreateDirectory(folder);
        await CampaignMapCombatChecks.EnemyFilterChecksAsync(Path.GetFullPath(filterPython), Path.GetFullPath(filterUpstream), folder);
        return 0;
    }
    if (args is ["--chapter-fourteen", var fourteenPython, var fourteenUpstream, var fourteenArtifacts])
    {
        string folder = Path.GetFullPath(fourteenArtifacts);
        Directory.CreateDirectory(folder);
        await CampaignMapCombatChecks.ChapterFourteenAsync(Path.GetFullPath(fourteenPython), Path.GetFullPath(fourteenUpstream), folder);
        return 0;
    }
    if (args is ["--chapters-eleven-twelve", var laterPython, var laterUpstream, var laterArtifacts])
    {
        string folder = Path.GetFullPath(laterArtifacts);
        Directory.CreateDirectory(folder);
        await CampaignMapCombatChecks.ChaptersElevenTwelveAsync(Path.GetFullPath(laterPython), Path.GetFullPath(laterUpstream), folder);
        return 0;
    }
    if (args is ["--chapter-camera", var cameraPython, var cameraUpstream, var cameraArtifacts])
    {
        string folder = Path.GetFullPath(cameraArtifacts);
        Directory.CreateDirectory(folder);
        await DetectorChecks.ChapterStoredCameraAsync(Path.GetFullPath(cameraPython), Path.GetFullPath(cameraUpstream), folder);
        return 0;
    }
    if (args is ["--chapter-ten", var tenPython, var tenUpstream, var tenArtifacts])
    {
        string folder = Path.GetFullPath(tenArtifacts);
        Directory.CreateDirectory(folder);
        await CampaignMapCombatChecks.ChapterTenChecksAsync(Path.GetFullPath(tenPython), Path.GetFullPath(tenUpstream), folder);
        return 0;
    }
    if (args is ["--chapter-nine", var ninePython, var nineUpstream, var nineArtifacts])
    {
        string folder = Path.GetFullPath(nineArtifacts);
        Directory.CreateDirectory(folder);
        await CampaignMapCombatChecks.ChapterNineChecksAsync(Path.GetFullPath(ninePython), Path.GetFullPath(nineUpstream), folder);
        return 0;
    }
    if (args is ["--target-selection", var targetPython, var targetUpstream, var targetArtifacts])
    {
        string folder = Path.GetFullPath(targetArtifacts);
        Directory.CreateDirectory(folder);
        await CampaignMapCombatChecks.TargetSelectionChecksAsync(Path.GetFullPath(targetPython), Path.GetFullPath(targetUpstream), folder);
        return 0;
    }
    if (args is ["--fleet-overlap", var overlapPython, var overlapUpstream, var overlapArtifacts])
    {
        await CampaignMapCombatChecks.FleetOverlapChecksAsync(Path.GetFullPath(overlapPython), Path.GetFullPath(overlapUpstream), Path.GetFullPath(overlapArtifacts));
        return 0;
    }
    if (args is ["--fleet-position", var positionPython, var positionUpstream, var positionArtifacts])
    {
        string folder = Path.GetFullPath(positionArtifacts);
        Directory.CreateDirectory(folder);
        await CampaignMapCombatChecks.FleetPositionChecksAsync(Path.GetFullPath(positionPython), Path.GetFullPath(positionUpstream), folder);
        return 0;
    }
    if (args is ["--carrier", var carrierPython, var carrierUpstream, var carrierArtifacts])
    {
        string folder = Path.GetFullPath(carrierArtifacts);
        Directory.CreateDirectory(folder);
        await CarrierChecks.RunAsync(Path.GetFullPath(carrierPython), Path.GetFullPath(carrierUpstream), folder);
        return 0;
    }
    if (args is ["--ambush", var ambushPython, var ambushUpstream, var ambushArtifacts])
    {
        string folder = Path.GetFullPath(ambushArtifacts);
        Directory.CreateDirectory(folder);
        await AmbushChecks.RunAsync(Path.GetFullPath(ambushPython), Path.GetFullPath(ambushUpstream), folder);
        return 0;
    }
    if (args is ["--decoy", var decoyPython, var decoyUpstream, var decoyArtifacts])
    {
        string folder = Path.GetFullPath(decoyArtifacts);
        Directory.CreateDirectory(folder);
        await CampaignMapCombatChecks.DecoyChecksAsync(Path.GetFullPath(decoyPython), Path.GetFullPath(decoyUpstream), folder);
        return 0;
    }
    if (args is ["--special-enemies", var specialPython, var specialUpstream, var specialArtifacts])
    {
        string folder = Path.GetFullPath(specialArtifacts);
        Directory.CreateDirectory(folder);
        await CampaignMapCombatChecks.SpecialEnemyChecksAsync(Path.GetFullPath(specialPython), Path.GetFullPath(specialUpstream), folder);
        return 0;
    }
    if (args is ["--maze", var mazePython, var mazeUpstream, var mazeArtifacts])
    {
        string folder = Path.GetFullPath(mazeArtifacts);
        Directory.CreateDirectory(folder);
        await MapMazeChecks.RunAsync(Path.GetFullPath(mazePython), Path.GetFullPath(mazeUpstream), folder);
        return 0;
    }
    if (args is ["--movable", var movablePython, var movableUpstream, var movableArtifacts])
    {
        string folder = Path.GetFullPath(movableArtifacts);
        Directory.CreateDirectory(folder);
        await MapMovableChecks.RunAsync(Path.GetFullPath(movablePython), Path.GetFullPath(movableUpstream), folder);
        return 0;
    }
    if (args is ["--mechanisms", var mechanismPython, var mechanismUpstream, var mechanismArtifacts])
    {
        string folder = Path.GetFullPath(mechanismArtifacts);
        Directory.CreateDirectory(folder);
        await MapMechanismChecks.RunAsync(Path.GetFullPath(mechanismPython), Path.GetFullPath(mechanismUpstream), folder);
        return 0;
    }
    if (args is ["--objectives", var goalPython, var goalUpstream, var goalArtifacts])
    {
        string folder = Path.GetFullPath(goalArtifacts);
        Directory.CreateDirectory(folder);
        await CampaignObjectiveChecks.RunAsync(Path.GetFullPath(goalPython), Path.GetFullPath(goalUpstream), folder);
        await CampaignMapCombatChecks.RunAsync(Path.GetFullPath(goalPython), Path.GetFullPath(goalUpstream));
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

    if (args is ["--levels", var levelPython, var levelUpstream, var levelArtifacts])
    {
        string folder = Path.GetFullPath(levelArtifacts);
        Directory.CreateDirectory(folder);
        await FleetLevelChecks.RunAsync(Path.GetFullPath(levelPython), Path.GetFullPath(levelUpstream), folder);
        await CampaignMapCombatChecks.RunAsync(Path.GetFullPath(levelPython), Path.GetFullPath(levelUpstream));
        return 0;
    }

    if (args is ["--preparation", var preparationPython, var preparationUpstream, var preparationArtifacts])
    {
        string folder = Path.GetFullPath(preparationArtifacts);
        Directory.CreateDirectory(folder);
        await CampaignPreparationChecks.RunAsync(Path.GetFullPath(preparationPython), Path.GetFullPath(preparationUpstream), folder);
        return 0;
    }
    if (args is ["--emotion", var emotionPython, var emotionUpstream, var emotionArtifacts])
    {
        string folder = Path.GetFullPath(emotionArtifacts);
        Directory.CreateDirectory(folder);
        await EmotionChecks.RunAsync(Path.GetFullPath(emotionPython), Path.GetFullPath(emotionUpstream), folder);
        await CombatFlowChecks.RunAsync();
        return 0;
    }
    if (args is ["--retirement", var retirementPython, var retirementUpstream, var retirementArtifacts])
    {
        string folder = Path.GetFullPath(retirementArtifacts);
        Directory.CreateDirectory(folder);
        await RetirementChecks.RunAsync(Path.GetFullPath(retirementPython), Path.GetFullPath(retirementUpstream), folder);
        return 0;
    }
    if (args is ["--combat-health", var combatHealthPython, var combatHealthUpstream, var combatHealthArtifacts])
    {
        string folder = Path.GetFullPath(combatHealthArtifacts);
        Directory.CreateDirectory(folder);
        await CombatHealthChecks.RunAsync(Path.GetFullPath(combatHealthPython), Path.GetFullPath(combatHealthUpstream), folder);
        await CombatFlowChecks.RunAsync();
        return 0;
    }
    if (args is ["--health", var healthPython, var healthUpstream, var healthArtifacts])
    {
        string folder = Path.GetFullPath(healthArtifacts);
        Directory.CreateDirectory(folder);
        await FleetHealthChecks.RunAsync(Path.GetFullPath(healthPython), Path.GetFullPath(healthUpstream), folder);
        await CampaignMapCombatChecks.RunAsync(Path.GetFullPath(healthPython), Path.GetFullPath(healthUpstream));
        return 0;
    }

    if (args is ["--ammo-state", var ammoPython, var ammoUpstream, var ammoArtifacts])
    {
        string folder = Path.GetFullPath(ammoArtifacts);
        Directory.CreateDirectory(folder);
        await MapArrivalChecks.RunAsync(Path.GetFullPath(ammoUpstream));
        await MapArrivalChecks.AmmoChecksAsync(Path.GetFullPath(ammoPython), Path.GetFullPath(ammoUpstream), folder);
        await AmmoPickupChecks.RunAsync(Path.GetFullPath(ammoPython), Path.GetFullPath(ammoUpstream), folder);
        await CampaignMapCombatChecks.RunAsync(Path.GetFullPath(ammoPython), Path.GetFullPath(ammoUpstream));
        return 0;
    }

    if (args is ["--fleet-selection", var fleetPython, var fleetUpstream, var fleetArtifacts])
    {
        string folder = Path.GetFullPath(fleetArtifacts);
        Directory.CreateDirectory(folder);
        await CampaignFleetSelectorChecks.RunAsync(Path.GetFullPath(fleetPython), Path.GetFullPath(fleetUpstream), folder);
        return 0;
    }

    if (args is ["--strategy", var strategyPython, var strategyUpstream, var strategyArtifacts])
    {
        string folder = Path.GetFullPath(strategyArtifacts);
        Directory.CreateDirectory(folder);
        await CampaignStrategyChecks.RunAsync(Path.GetFullPath(strategyPython), Path.GetFullPath(strategyUpstream), folder);
        return 0;
    }

    if (args is ["--settings", var settingsArtifacts])
    {
        await EngineSettingsChecks.RunAsync(settingsArtifacts);
        return 0;
    }

    if (args is ["--control-workspace", var controlArtifacts])
    {
        string folder = Path.GetFullPath(controlArtifacts);
        Directory.CreateDirectory(folder);
        await ControlWorkspaceChecks.RunAsync(folder);
        Console.WriteLine("Engine control checks passed: dry-run, report evidence, boundary stop, instance identity and shutdown.");
        return 0;
    }

    if (args is ["--campaign-command", var campaignArtifacts])
    {
        string folder = Path.GetFullPath(campaignArtifacts);
        Directory.CreateDirectory(folder);
        await CampaignCommandChecks.RunAsync(folder);
        Console.WriteLine("Campaign command checks passed: canonical C# rule IDs, dry-run and unsupported-rule refusal.");
        return 0;
    }

    if (args is ["--submarine", var submarinePython, var submarineUpstream, var submarineArtifacts])
    {
        await SubmarineChecks.RunAsync(Path.GetFullPath(submarinePython), Path.GetFullPath(submarineUpstream), Path.GetFullPath(submarineArtifacts));
        return 0;
    }
    if (args is ["--submarine-call", var callPython, var callUpstream, var callArtifacts])
    {
        await SubmarineCallChecks.RunAsync(Path.GetFullPath(callPython), Path.GetFullPath(callUpstream), Path.GetFullPath(callArtifacts));
        return 0;
    }
    if (args is ["--submarine-move", var movePython, var moveUpstream, var moveArtifacts])
    {
        await SubmarineMoveChecks.RunAsync(Path.GetFullPath(movePython), Path.GetFullPath(moveUpstream), Path.GetFullPath(moveArtifacts));
        return 0;
    }
    if (args is ["--map-initializer"])
    {
        await CampaignMapInitializerChecks.RunAsync();
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
        if (RuleCatalog.Create(id) is Alas.Engine.Rules.Main.ObservedBossRule observedBossRule)
        {
            var spawns = new CampaignState(observedBossRule.Map).Cells.Where(cell => cell.MayBoss).Select(cell => cell.Location.ToString()).Reverse().ToArray();
            foreach (int count in new[] { 0, 3, 4, 5, 12, 13, 14, 15 })
            foreach (bool accessible in new[] { false, true })
            foreach (string? yes in new[] { null, "clear_roadblocks", "clear_potential_roadblocks", "fleet_2_step_on" })
            foreach (string cells in new[] { "empty", "boss", "boss_enemy" })
                cases.Add(new Scenario(id, BattleCount: count, Accessible: accessible, TrueOperation: yes, Cells: cells));
            foreach (bool accessible in new[] { false, true })
                cases.Add(new Scenario(id, BattleCount: observedBossRule.Map.ExpectedBattles - 1, Accessible: accessible, BossCells: string.Join(',', spawns)));
        }
        if (RuleCatalog.Create(id) is Alas.Engine.Rules.Main.ChapterSixRule)
            foreach (string operation in new[] { "fleet_2_step_on", "clear_roadblocks", "clear_all_mystery", "clear_potential_roadblocks" })
            foreach (string signal in new[] { "moved", "moved_after_battle", "ended", "error" })
                cases.Add(new Scenario(id, "execute", Signal: signal, SignalOperation: operation));
        if (RuleCatalog.Create(id) is Alas.Engine.Rules.Main.ChapterSevenRule)
        {
            foreach (int count in new[] { 0, 3, 5, 14, 15 })
            foreach (int configuredSecond in new[] { 0, 2 })
            foreach (int bossFleet in new[] { 1, 2 })
            foreach (string? yes in new[] { null, "fleet_2_step_on", "clear_roadblocks", "clear_potential_roadblocks" })
                cases.Add(new Scenario(id, BattleCount: count, Fleet2: configuredSecond, BossFleet: bossFleet, TrueOperation: yes, CombatReturn: false));
            foreach (string operation in new[] { "fleet_2_push_forward", "fleet_2_step_on", "clear_roadblocks", "clear_all_mystery", "clear_potential_roadblocks", "brute_clear_boss", "pick_up_ammo" })
            foreach (int count in new[] { 0, 3, 5 })
            foreach (string signal in new[] { "moved", "moved_after_battle", "ended", "error" })
                cases.Add(new Scenario(id, "execute", BattleCount: count, Signal: signal, SignalOperation: operation, Fleet2: 2));
        }
        if (RuleCatalog.Create(id) is Alas.Engine.Rules.Main.ChapterEightRule)
        {
            foreach (int count in new[] { 0, 3, 4, 5, 14, 15 })
            foreach (int mysteries in new[] { 0, 1, 2 })
            foreach (int collected in new[] { 0, 1 })
            foreach (int bossFleet in new[] { 1, 2 })
            foreach (string? yes in new[] { null, "fleet_2_step_on", "clear_roadblocks", "clear_potential_roadblocks", "clear_first_roadblocks" })
                cases.Add(new Scenario(id, BattleCount: count, Fleet2: 2, BossFleet: bossFleet, TrueOperation: yes,
                    CombatReturn: false, MysteryCount: mysteries, CollectedMysteries: collected));
            foreach (string operation in new[] { "fleet_2_step_on", "clear_roadblocks", "clear_all_mystery", "clear_potential_roadblocks", "clear_first_roadblocks", "brute_clear_boss" })
            foreach (int count in new[] { 0, 4 })
            foreach (string signal in new[] { "moved", "moved_after_battle", "ended", "error" })
                cases.Add(new Scenario(id, "execute", BattleCount: count, Signal: signal, SignalOperation: operation));
        }
        if (RuleCatalog.Create(id) is Alas.Engine.Rules.Main.ChapterNineRule)
        {
            foreach (int count in new[] { 0, 3, 5, 12, 14, 15 })
            foreach (int fleet2 in new[] { 0, 2 })
            foreach (int bossFleet in new[] { 1, 2 })
            foreach (string? secondLocation9 in new[] { null, "D5", "F4", "F5", "G5" })
            foreach (string? yes in new[] { null, "fleet_2_step_on", "clear_roadblocks", "clear_potential_roadblocks" })
                cases.Add(new Scenario(id, BattleCount: count, Fleet2: fleet2, BossFleet: bossFleet, SecondFleet: secondLocation9, TrueOperation: yes));
            foreach (string operation in new[] { "fleet_2_step_on", "clear_roadblocks", "clear_potential_roadblocks", "pick_up_ammo", "clear_boss" })
            foreach (int count in new[] { 0, 5 })
            foreach (string signal in new[] { "moved", "moved_after_battle", "ended", "error" })
                cases.Add(new Scenario(id, "execute", BattleCount: count, Signal: signal, SignalOperation: operation, SecondFleet: "D5"));
        }
        if (RuleCatalog.Create(id) is Alas.Engine.Rules.Main.ChapterTenRule)
        {
            foreach (int count in new[] { 0, 5, 6, 7, 15, 16 })
            foreach (int fleet2 in new[] { 0, 2 })
            foreach (int bossFleet in new[] { 1, 2 })
            foreach (bool accessible in new[] { false, true })
            foreach (string? yes in new[] { null, "fleet_2_push_forward", "fleet_2_step_on", "clear_roadblocks", "clear_potential_roadblocks" })
                cases.Add(new Scenario(id, BattleCount: count, Fleet2: fleet2, BossFleet: bossFleet, Accessible: accessible, TrueOperation: yes));
            foreach (string operation in new[] { "fleet_2_push_forward", "fleet_2_step_on", "clear_roadblocks", "clear_potential_roadblocks", "clear_all_mystery", "clear_boss" })
            foreach (int count in new[] { 0, 6 })
            foreach (string signal in new[] { "moved", "moved_after_battle", "ended", "error" })
                cases.Add(new Scenario(id, "execute", BattleCount: count, Signal: signal, SignalOperation: operation, Fleet2: 2));
        }
        if (RuleCatalog.Create(id) is Alas.Engine.Rules.Main.ChapterElevenRule or Alas.Engine.Rules.Main.ChapterTwelveRule)
        {
            foreach (int count in new[] { 0, 2, 3, 5, 6, 7, 15, 16 })
            foreach (int fleet2 in new[] { 0, 2 })
            foreach (int bossFleet in new[] { 1, 2 })
            foreach (bool accessible in new[] { false, true })
            foreach (string cells in new[] { "empty", "boss", "boss_enemy" })
            foreach (string? yes in new[] { null, "fleet_2_push_forward", "fleet_2_step_on", "clear_roadblocks", "clear_potential_roadblocks", "pick_up_ammo" })
                cases.Add(new Scenario(id, BattleCount: count, Fleet2: fleet2, BossFleet: bossFleet, Cells: cells,
                    Accessible: accessible, TrueOperation: yes, CombatReturn: false));
            foreach (string operation in new[] { "fleet_2_push_forward", "fleet_2_step_on", "clear_roadblocks", "clear_potential_roadblocks", "pick_up_ammo", "clear_boss" })
            foreach (int count in new[] { 0, 3, 6 })
            foreach (string signal in new[] { "moved", "moved_after_battle", "ended", "error" })
                cases.Add(new Scenario(id, "execute", BattleCount: count, Signal: signal, SignalOperation: operation, Fleet2: 2));
        }
        if (RuleCatalog.Create(id) is Alas.Engine.Rules.Main.ChapterThirteenRule)
        {
            foreach (int count in new[] { 0, 3, 4, 5, 6, 7, 14, 15, 16, 17 })
            foreach (int fleet2 in new[] { 0, 2 })
            foreach (int bossFleet in new[] { 1, 2 })
            foreach (string? yes in new[] { null, "clear_filter_enemy", "clear_siren", "pick_up_ammo" })
                cases.Add(new Scenario(id, BattleCount: count, Fleet2: fleet2, BossFleet: bossFleet, TrueOperation: yes, CombatReturn: false));
            foreach (string operation in new[] { "clear_filter_enemy", "clear_siren", "pick_up_ammo", "clear_boss" })
            foreach (int count in new[] { 0, 3, 5, 6, 7 })
            foreach (string signal in new[] { "moved", "moved_after_battle", "ended", "error" })
                cases.Add(new Scenario(id, "execute", BattleCount: count, Signal: signal, SignalOperation: operation, Fleet2: 2));
        }
        if (RuleCatalog.Create(id) is Alas.Engine.Rules.Main.ChapterFourteenRule)
        {
            foreach (int count in new[] { 0, 3, 5, 6, 7, 15, 16, 17 })
            foreach (int fleet2 in new[] { 0, 2 })
            foreach (int bossFleet in new[] { 1, 2 })
            foreach (bool accessible in new[] { false, true })
            foreach (bool picked in new[] { false, true })
            foreach (string? yes in new[] { null, "clear_filter_enemy", "clear_roadblocks", "pick_up_ammo" })
                cases.Add(new Scenario(id, BattleCount: count, Fleet2: fleet2, BossFleet: bossFleet,
                    Accessible: accessible, FlarePicked: picked, TrueOperation: yes, CombatReturn: false));
            foreach (string operation in new[] { "clear_filter_enemy", "clear_roadblocks", "pick_up_ammo", "clear_boss",
                "pick_up_flare:H7", "pick_up_flare:A5", "pick_up_flare:C5", "pick_up_flare:D5", "pick_up_flare:H9",
                "pick_up_light_house:E3", "pick_up_light_house:J7", "pick_up_light_house:A9", "goto:D6" })
            foreach (int count in new[] { 0, 3, 5, 6, 7 })
            foreach (string signal in new[] { "moved", "moved_after_battle", "ended", "error" })
                cases.Add(new Scenario(id, "execute", BattleCount: count, Signal: signal, SignalOperation: operation, Fleet2: 2));
        }
        if (RuleCatalog.Create(id) is Alas.Engine.Rules.Main.Campaign72)
            foreach (int count in new[] { 0, 4, 5, 6 })
            foreach (string secondLocation in new[] { "A3", "G3", "C3" })
            foreach (string firstFleet in new[] { "A1", "H1" })
            foreach (int scale in new[] { 0, 2, 3 })
            foreach (string? mysteries in new[] { null, "A2", "H3", "A2,H3" })
            foreach (string? signal in new[] { null, "ended", "error" })
                cases.Add(new Scenario(id, "execute", BattleCount: count, FirstFleet: firstFleet, SecondFleet: secondLocation,
                    FirstScale: scale, Mysteries: mysteries, Fleet2: 2, Signal: signal,
                    SignalOperation: "chosen_mystery:" + (secondLocation == "G3" ? "H3" : "A2")));
        if (RuleCatalog.Create(id) is Alas.Engine.Rules.Main.Campaign73)
            foreach (string boss in new[] { "A1", "C6", "H1", "H5", "D3", "A1,H5" })
            foreach (bool accessible in new[] { false, true })
                cases.Add(new Scenario(id, BattleCount: 5, BossCells: boss, Accessible: accessible, Fleet2: 2));
        if (RuleCatalog.Create(id) is Alas.Engine.Rules.Main.ChapterThreeRule)
        {
            foreach (int count in new[] { 0, 1, 2, 3, 4, 12, 13 })
            foreach (bool accessible in new[] { false, true })
            foreach (string? yes in new[] { null, "fleet_2_push_forward", "fleet_2_rescue:G2", "fleet_2_rescue:H1", "fleet_2_rescue:A4", "fleet_2_rescue:H3" })
                cases.Add(new Scenario(id, BattleCount: count, Accessible: accessible, TrueOperation: yes));
            foreach (string operation in new[] { "fleet_2_push_forward", "fleet_2_rescue:G2", "fleet_2_rescue:H1", "fleet_2_rescue:A4", "fleet_2_rescue:H3" })
            foreach (string signal in new[] { "moved", "moved_after_battle", "ended", "error" })
                cases.Add(new Scenario(id, "execute", Signal: signal, SignalOperation: operation));
        }
        if (RuleCatalog.Create(id) is Alas.Engine.Rules.Main.ChapterTwoRule)
            foreach (int count in new[] { 0, 1, 2, 3, 4, 12, 13 })
            foreach (bool accessible in new[] { false, true })
            foreach (string? yes in new[] { null, "clear_roadblocks", "clear_potential_roadblocks" })
                cases.Add(new Scenario(id, BattleCount: count, Accessible: accessible, TrueOperation: yes));
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
    string input = Path.Combine(artifacts, "cases.json");
    await File.WriteAllTextAsync(input, JsonSerializer.Serialize(cases, json));
    await File.WriteAllTextAsync(Path.Combine(artifacts, "csharp.json"), JsonSerializer.Serialize(expected, json));
    var failures = new List<object>();
    int compared = 0, batchNumber = 0;
    // Keep every scenario for a rule together and preserve catalog/scenario order.
    // A fixed number of rules per process bounds memory/time as the catalog grows.
    foreach (var rules in cases.GroupBy(sample => sample.Rule, StringComparer.Ordinal).Chunk(8))
    {
        var batch = rules.SelectMany(rule => rule).ToArray();
        string batchInput = Path.Combine(artifacts, $"cases-{++batchNumber:D2}.json");
        string output = Path.Combine(artifacts, $"native-{batchNumber:D2}.json");
        await File.WriteAllTextAsync(batchInput, JsonSerializer.Serialize(batch, json));
        var oracle = await process.RunAsync(python,
            [Path.Combine(AppContext.BaseDirectory, "native_campaign_reference.py"), upstream, batchInput, output], TimeSpan.FromMinutes(3));
        if (oracle.ExitCode != 0) throw new InvalidOperationException($"Native reference batch {batchNumber} failed: {oracle.Error}");
        var native = JsonNode.Parse(await File.ReadAllTextAsync(output))!.AsArray();
        Check(native.Count == batch.Length, "Native reference batch omitted scenarios");
        for (int local = 0; local < batch.Length; local++)
        {
            int index = compared + local;
            Check(batch[local] == cases[index], "Native batching changed scenario order");
            var actual = JsonSerializer.SerializeToNode(expected[index], json);
            if (!JsonNode.DeepEquals(native[local], actual)) failures.Add(new { index, sample = cases[index], csharp = actual, native = native[local]!.DeepClone() });
        }
        compared += batch.Length;
        Console.WriteLine($"Native rule batch {batchNumber}: compared {compared}/{cases.Count}; differences so far: {failures.Count}.");
    }
    Check(compared == cases.Count, "Native reference omitted a rule batch");
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
        HandleError = scenario.HandleError, Fleet2 = scenario.Fleet2, BossFleet = scenario.BossFleet
    }, operations);
    var state = operations.State = execution.Context.State;
    state.BattleCount = scenario.BattleCount;
    state.MysteryCount = scenario.MysteryCount;
    if (scenario.FlarePicked) state.RecordFlare(Cell.Parse("A5"));
    if (rule is Alas.Engine.Rules.Main.ChapterFourteenRule)
        foreach (var cell in state.Cells) cell.Cost = scenario.Accessible ? 0 : 9999;
    state.Fleet1Location = scenario.FirstFleet is null ? null : Cell.Parse(scenario.FirstFleet);
    state.Fleet2Location = scenario.SecondFleet is null ? null : Cell.Parse(scenario.SecondFleet);
    state.Cells[0].EnemyScale = scenario.FirstScale;
    if (scenario.Mysteries is { } mysteries)
        foreach (var cell in mysteries.Split(',')) state[Cell.Parse(cell)].IsMystery = true;
    state.Cells[0].IsBoss = scenario.Cells is "boss" or "boss_enemy";
    state.Cells[0].IsEnemy = scenario.Cells is "enemy" or "boss_enemy";
    state.Cells[0].IsSiren = scenario.Cells == "siren";
    state.Cells[0].IsFortress = scenario.Cells == "fortress";
    if (scenario.BossCells is { } bossCells)
        foreach (var cell in bossCells.Split(',')) state[Cell.Parse(cell)].IsBoss = true;
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
    return new ProbeResult(operations.Calls.ToArray(), value, exception, state.BattleCount,
        rule is Alas.Engine.Rules.Main.Campaign92 ? state.Cells.Select(cell => cell.Weight).ToArray() : null);
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
