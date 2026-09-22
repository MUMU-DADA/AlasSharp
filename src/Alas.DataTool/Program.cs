using Alas.Core;
using Alas.Device;
using Alas.Vision;

namespace Alas.DataTool;

/// <summary>
/// S0 工具：读取上游数据契约并用 C# 独立复现校验。
///
/// 之所以用 C# 重写一遍校验（而不是信任 Python 侧的结论），是因为 S0 的验收标准是
/// 「C# 能消费这些数据」，而不是「Python 说数据没问题」。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        ProjectPaths paths = ProjectPaths.Resolve();
        string dataDir = paths.DataDirectory;
        string repoDir = paths.RepoDirectory;

        string command = args.Length > 0 ? args[0] : "verify";
        string? fixture = null;
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == "--data") dataDir = args[i + 1];
            if (args[i] == "--repo") repoDir = args[i + 1];
            if (args[i] == "--fixture") fixture = args[i + 1];
        }
        string? target = args.Length > 1 && !args[1].StartsWith("--") && command != "verify"
            ? args[1] : null;

        try
        {
            if (command == "imaging")
            {
                fixture ??= Path.Combine(dataDir, "fixtures", "imaging.json");
                return ImagingCheck.Run(fixture, repoDir);
            }
            if (command == "matching")
            {
                fixture ??= Path.Combine(dataDir, "fixtures", "matching.json");
                return MatchingCheck.Run(fixture, repoDir);
            }
            if (command == "run")
            {
                // 常驻 runner 骨架（S3 的壳）：设备层只构造一次，之后按 tick 循环抓帧+判定
                string toolsDir6 = paths.ToolsDirectory;
                string? runAdb = null, runSerial = null;
                string runShot = "scrcpy", runCtrl = "MaaTouch";
                double runTick = 0.5, runSeconds = 20;
                string? runMap = null;
                for (int i = 1; i < args.Length - 1; i++)
                {
                    if (args[i] == "--adb") runAdb = args[i + 1];
                    if (args[i] == "--serial") runSerial = args[i + 1];
                    if (args[i] == "--screenshot") runShot = args[i + 1];
                    if (args[i] == "--control") runCtrl = args[i + 1];
                    if (args[i] == "--tick" && double.TryParse(args[i + 1], out double tk)) runTick = tk;
                    if (args[i] == "--seconds" && double.TryParse(args[i + 1], out double sc)) runSeconds = sc;
                    if (args[i] == "--map") runMap = args[i + 1];
                }
                if (runAdb is null || runSerial is null)
                {
                    Console.WriteLine("用法: run --adb <adb> --serial <serial> " +
                        "[--screenshot scrcpy] [--control MaaTouch] [--tick 0.5] [--seconds 20] [--map main|os]");
                    return 2;
                }
                return RunLoop.Run(runAdb, runSerial, repoDir, toolsDir6, runShot, runCtrl,
                    runTick, runSeconds, runMap);
            }
            if (command == "campaign")
            {
                // S3：上游 Campaign.run() 负责整次出击；C# 传配置并报告结果。
                string toolsDir7 = paths.ToolsDirectory;
                var flags = ParseRunFlags(args, "--chapter");
                if (flags.Positional is null)
                {
                    Console.WriteLine("用法: campaign <章模块[,章模块...]> [--run --allow-actions] " +
                                      "[--serial <设备>] [--clear-all] [--max-seconds 1500] [--max-rounds 20] " +
                                      "[--artifacts <目录>] [--continue-on-error]");
                    return 2;
                }
                if (flags.Run && !flags.AllowActions)
                {
                    Console.WriteLine("[拒绝    ] 真跑需要 --allow-actions");
                    return 2;
                }
                // 在同一个宿主内连续运行；各关入口由上游 ensure_campaign_ui 导航。
                var stageList = flags.Positional.Contains(',')
                    ? flags.Positional.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray()
                    : new[] { flags.Positional };
                Console.WriteLine($"[批量    ] {stageList.Length} 关，同一进程内连续驱动");
                if (flags.Run && flags.AllowActions && flags.Options.AdbPath is not null)
                    Console.WriteLine("[兼容    ] campaign 使用上游配置的 ADB；--adb 保留兼容，不覆盖宿主配置");

                // R1：编排、判定、工件全部在 Alas.Core 的运行时里；CLI 只解析参数和排版报告。
                var campOptions = flags.Options;
                campOptions.RepoDirectory = repoDir;
                campOptions.ToolsDirectory = toolsDir7;
                campOptions.DataDirectory = dataDir;
                using var campSession = Alas.Runtime.AlasSession.Start(campOptions);
                var batch = new Alas.Runtime.CampaignBatchRunner(campSession)
                {
                    StopOnFailure = !flags.ContinueOnError,
                }.Run(stageList);
                PrintCampaignReport(batch, campOptions);
                return batch.FailedCount == 0 ? 0 : 1;
            }
            if (command == "queue")
            {
                // R2：任务队列。队列文件描述"跑哪些任务"，业务判定在各域的 ITaskRunner 里。
                var queueFlags = ParseRunFlags(args, "--file");
                string queueFile = queueFlags.Positional ?? queueFlags.File ?? "";
                if (queueFile.Length == 0)
                {
                    Console.WriteLine("用法: queue --file <队列.json> [--run --allow-actions] [--serial <设备>] " +
                                      "[--artifacts <目录>] [--resume] [--continue-on-error]");
                    return 2;
                }
                if (!File.Exists(queueFile))
                {
                    Console.Error.WriteLine($"找不到队列文件: {queueFile}");
                    return 2;
                }
                if (queueFlags.Run && !queueFlags.AllowActions)
                {
                    Console.WriteLine("[拒绝    ] 真跑需要 --allow-actions");
                    return 2;
                }
                var queueOptions = queueFlags.Options;
                queueOptions.RepoDirectory = repoDir;
                queueOptions.ToolsDirectory = paths.ToolsDirectory;
                queueOptions.DataDirectory = dataDir;
                var requests = Alas.Tasks.TaskQueueFile.Parse(File.ReadAllText(queueFile));
                Console.WriteLine($"[队列    ] {requests.Count} 个任务");
                using var queueSession = Alas.Runtime.AlasSession.Start(queueOptions);
                var queue = new Alas.Tasks.TaskQueue(queueSession)
                {
                    StopOnFailure = !queueFlags.ContinueOnError,
                }.Register(new Alas.Tasks.CampaignBatchTask())
                 .Register(new Alas.Tasks.AccountStateTask())
                 .Register(new Alas.Tasks.OsStateTask())
                 .Register(new Alas.Tasks.EventStateTask());
                if (queueFlags.Resume)
                {
                    // `--resume <state.json>` 显式给路径；只写 `--resume` 则取工件根目录下
                    // **上一次**运行的断点（本次运行目录里永远没有 state.json，直接读它等于没续跑）。
                    string? statePath = queueFlags.ResumeState
                        ?? Alas.Tasks.TaskQueueFile.LatestState(queueOptions.ArtifactsDirectory,
                                                                queueSession.RunDirectory);
                    var done = Alas.Tasks.TaskQueueFile.ReadCompletedState(
                        statePath is null ? null : Path.GetDirectoryName(statePath));
                    foreach (var id in done) queue.ResumeCompleted.Add(id);
                    if (statePath is null)
                        Console.WriteLine("[断点    ] 没找到可续跑的 state.json（本次仍从头跑）");
                    else
                        Console.WriteLine($"[断点    ] 依据 {statePath} 跳过 {done.Count} 个已完成任务"
                                          + (done.Count == 0 ? "" : $": {string.Join(", ", done)}"));
                }
                var queueResult = queue.Run(requests);
                PrintQueueReport(queueResult, requests);
                return queueResult.FailedCount == 0 ? 0 : 1;
            }
            if (command == "report")
            {
                // R2：把一次运行的工件读回来 —— 只读汇总 + 证据完整性检查（不重判通关）。
                string? reportRun = null, reportArtifacts = null, reportJson = null;
                for (int i = 1; i < args.Length - 1; i++)
                {
                    if (args[i] == "--run") reportRun = args[i + 1];
                    if (args[i] == "--artifacts") reportArtifacts = args[i + 1];
                    if (args[i] == "--json") reportJson = args[i + 1];
                }
                if (reportRun is null && reportArtifacts is not null)
                    reportRun = Alas.Runtime.RunReport.LatestRun(reportArtifacts);
                if (reportRun is null)
                {
                    Console.WriteLine("用法: report --run <运行目录> [--json <报告.json>] "
                                      + "（或 report --artifacts <工件根目录> 取最新一次运行）");
                    return 2;
                }
                var report = Alas.Runtime.RunReport.Build(reportRun);
                PrintRunReport(report);
                if (reportJson is not null)
                {
                    string full = Path.GetFullPath(reportJson);
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    File.WriteAllText(full, report.ToJson().ToJsonString(
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                    Console.WriteLine($"[报告工件] {full}");
                }
                // 报告本身的产出与"这次运行成没成功"无关：读得出来就成功（0）。
                return report.Findings.Any(f => f.Code == "run_not_found") ? 1 : 0;
            }
            if (command == "selftest-runtime")
            {
                // R1 运行时自检：替身宿主，不启动 Python、不连设备。
                string? runtimeFixture = null, runtimeJson = null;
                for (int i = 1; i < args.Length - 1; i++)
                {
                    if (args[i] == "--fixture") runtimeFixture = args[i + 1];
                    if (args[i] == "--json") runtimeJson = args[i + 1];
                }
                if (runtimeFixture is null)
                {
                    Console.WriteLine("用法: selftest-runtime --fixture <用例.json> [--json <裁决.json>]");
                    return 2;
                }
                return RuntimeSelfCheck.Run(runtimeFixture, runtimeJson, Path.GetTempPath());
            }
            if (command == "contract")
            {
                // 结果合同（sortie-result/1）的离线裁决：与 Python 侧逐例对拍用。
                string? contractFixture = null, contractArtifacts = null, contractJson = null;
                for (int i = 1; i < args.Length - 1; i++)
                {
                    if (args[i] == "--fixture") contractFixture = args[i + 1];
                    if (args[i] == "--artifacts") contractArtifacts = args[i + 1];
                    if (args[i] == "--json") contractJson = args[i + 1];
                }
                if (contractFixture is null)
                {
                    Console.WriteLine("用法: contract --fixture <用例.json> [--artifacts <失败帧目录>] " +
                                      "[--json <裁决.json>]");
                    return 2;
                }
                return ContractCheck.Run(contractFixture, contractArtifacts, contractJson);
            }
            if (command == "capture")
            {
                // 设备通道对比：C# 自截 vs 引擎截图（后端可切），见 CaptureCheck
                string toolsDir5 = paths.ToolsDirectory;
                string? capAdb = null, capSerial = null, capShot = "scrcpy", capCtrl = "MaaTouch";
                int capRepeat = 3;
                for (int i = 1; i < args.Length - 1; i++)
                {
                    if (args[i] == "--adb") capAdb = args[i + 1];
                    if (args[i] == "--serial") capSerial = args[i + 1];
                    if (args[i] == "--screenshot") capShot = args[i + 1];
                    if (args[i] == "--control") capCtrl = args[i + 1];
                    if (args[i] == "--repeat" && int.TryParse(args[i + 1], out int n)) capRepeat = n;
                }
                if (capAdb is null || capSerial is null)
                {
                    Console.WriteLine("用法: capture --adb <adb> --serial <serial> " +
                        "[--screenshot adb|droidcast|...] [--control ADB|MaaTouch|...] [--repeat N]");
                    return 2;
                }
                return CaptureCheck.Run(capAdb, capSerial, repoDir, toolsDir5, capShot, capCtrl, capRepeat);
            }
            if (command == "map-ir")
            {
                string? mirFile = null, mirDir = null;
                for (int i = 1; i < args.Length - 1; i++)
                {
                    if (args[i] == "--file") mirFile = args[i + 1];
                    if (args[i] == "--dir") mirDir = args[i + 1];
                }
                mirDir ??= (mirFile is null ? Path.Combine(dataDir, "campaign") : null);
                return MapIRCheck.Run(mirFile, mirDir, dataDir);
            }
            if (command == "map")
            {
                // S2 地图识别的产品路径验收；无 --fixture 时可用 --adb/--serial 抓真机画面
                string toolsDir4 = paths.ToolsDirectory;
                string? mapAdb = null, mapSerial = null;
                for (int i = 1; i < args.Length - 1; i++)
                {
                    if (args[i] == "--adb") mapAdb = args[i + 1];
                    if (args[i] == "--serial") mapSerial = args[i + 1];
                    if (args[i] == "--fixture") fixture = args[i + 1];
                }
                string? mapChapter = null, mapMode = "main";
                for (int i = 1; i < args.Length - 1; i++)
                {
                    if (args[i] == "--chapter") mapChapter = args[i + 1];
                    if (args[i] == "--mode") mapMode = args[i + 1];
                }
                fixture ??= (mapAdb is null
                    ? Path.Combine(dataDir, "fixtures", "os_map.png") : null);
                return MapCheck.Run(fixture, repoDir, toolsDir4, mapAdb, mapSerial,
                    mapChapter, dataDir, mapMode);
            }
            if (command == "device")
            {
                fixture ??= Path.Combine(dataDir, "fixtures", "imaging.json");
                string toolsDir2 = paths.ToolsDirectory;
                string? realAdb = null, realSerial = null, server = null; string[]? assetCsv = null;
                for (int i = 1; i < args.Length - 1; i++)
                {
                    if (args[i] == "--adb") realAdb = args[i + 1];
                    if (args[i] == "--serial") realSerial = args[i + 1];
                    if (args[i] == "--server") server = args[i + 1];
                    if (args[i] == "--assets") assetCsv = args[i + 1].Split(',');
                }
                return realAdb is not null && realSerial is not null
                    ? DeviceCheck.RunReal(realAdb, realSerial, repoDir, toolsDir2,
                        server ?? "cn", assetCsv)
                    : DeviceCheck.Run(fixture, repoDir, toolsDir2, dataDir);
            }
            if (command == "goto")
            {
                // 沿上游页面图真机导航：goto <page_target> [--adb .. --serial .. --server cn]
                string? realAdb2 = null, realSerial2 = null, targetPage = null;
                bool gotoEngineCapture = false;
                // 默认取实测最优：scrcpy 抓图 e2e 128ms（比 adb 快 2.5 倍）、MaaTouch 点击稳态 53ms
                string gotoScreen = "scrcpy", gotoCtrl = "MaaTouch";
                int gotoRounds = 1;
                for (int i = 1; i < args.Length - 1; i++)
                {
                    if (args[i] == "--adb") realAdb2 = args[i + 1];
                    if (args[i] == "--serial") realSerial2 = args[i + 1];
                    if (args[i] == "--to") targetPage = args[i + 1];
                    if (args[i] == "--capture-engine") gotoEngineCapture = true;
                    if (args[i] == "--screenshot") gotoScreen = args[i + 1];
                    if (args[i] == "--control") gotoCtrl = args[i + 1];
                    if (args[i] == "--rounds" && int.TryParse(args[i + 1], out int rd)) gotoRounds = rd;
                }
                targetPage ??= args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;
                if (realAdb2 is null || realSerial2 is null || targetPage is null)
                    return Fail("用法: goto <page_目标> --adb <adb.exe> --serial <serial> " +
                                "[--capture-engine [--screenshot droidcast] [--control ADB]]");
                string toolsDir3 = paths.ToolsDirectory;
                return DeviceCheck.RunGoto(realAdb2, realSerial2, repoDir, toolsDir3, targetPage,
                    gotoEngineCapture, gotoScreen, gotoCtrl, gotoRounds);
            }
            if (command == "vision")
            {
                fixture ??= Path.Combine(dataDir, "fixtures", "imaging.json");
                int limit = 200;
                string mode = Environment.GetEnvironmentVariable("ALAS_VISION_MODE") ?? "worker";
                for (int i = 1; i < args.Length - 1; i++)
                {
                    if (args[i] == "--limit" && int.TryParse(args[i + 1], out int n)) limit = n;
                    if (args[i] == "--mode") mode = args[i + 1];
                }
                // bin/Release/net8.0 -> csharp/tools
                string toolsDir = paths.ToolsDirectory;
                return VisionCheck.Run(fixture, repoDir, toolsDir, limit, mode);
            }

            var catalog = UpstreamData.Catalog.Open(dataDir);
            return command switch
            {
                "verify" => Verify(catalog, repoDir),
                "list" => List(catalog, target),
                "show" => Show(catalog, target),
                "campaign" => CampaignCheck.Run(catalog, target),
                _ => Fail($"未知命令: {command}"
                          + "（可用: verify / list / show / imaging / matching / vision / campaign / "
                          + "map / map-ir / capture / device / goto / run / contract）"),
            };
        }
        catch (Exception ex)
        {
            return Fail($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    // ------------------------------------------------------------------ verify
    private static int Verify(UpstreamData.Catalog catalog, string repoDir)
    {
        var problems = new List<string>();

        Console.WriteLine($"数据目录      : {catalog.DataDirectory}");
        Console.WriteLine($"上游提交      : {catalog.UpstreamCommit ?? "(未知)"}"
                          + (catalog.UpstreamDirty ? "  ⚠ 导出时工作区不干净" : ""));
        Console.WriteLine($"素材绑定      : {catalog.Assets.Assets.Count}");
        Console.WriteLine($"关卡          : {catalog.Campaign.Chapters.Count}");
        Console.WriteLine();

        // ---- 素材：文件存在性 + 字段完整性
        // 注意：上游 Template 只有 file（无 area/color/button），area 只对 Button/Mask 是必需的。
        int missingFiles = 0, noServers = 0, noArea = 0, noFile = 0;
        var missingSample = new List<string>();
        foreach (var (id, a) in catalog.Assets.Assets)
        {
            bool needsArea = a.Kind is "Button" or "Mask";
            if (a.Servers.Count == 0) noServers++;
            if (needsArea && (a.Area is null || a.Area.Count == 0)) noArea++;
            if (a.File is null || a.File.Count == 0)
            {
                noFile++;
                continue;
            }
            foreach (var (server, rel) in a.File)
            {
                string p = Path.Combine(repoDir, rel.Replace("./", "")
                    .Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(p))
                {
                    missingFiles++;
                    if (missingSample.Count < 5) missingSample.Add($"{id} [{server}] -> {rel}");
                }
            }
        }
        Console.WriteLine($"[素材] 缺图 {missingFiles}，无 file {noFile}，"
                          + $"无服务器变体 {noServers}，Button/Mask 缺 area {noArea}");
        foreach (var s in missingSample) Console.WriteLine($"       {s}");
        if (missingFiles > 0) problems.Add($"{missingFiles} 个素材引用的图片不存在");
        if (noFile > 0) problems.Add($"{noFile} 个素材没有 file（无法加载图片）");
        if (noServers > 0) problems.Add($"{noServers} 个素材没有任何服务器变体");
        if (noArea > 0) problems.Add($"{noArea} 个 Button/Mask 没有 area（无法定位）");

        // ---- 关卡：网格自洽 + Config 导出 + 计划不变量 + 分级
        var tiers = new Dictionary<string, int>();
        int gridBad = 0, planBad = 0, sirenCount = 0, bossKnown = 0;
        int configModules = 0, configComplete = 0, configFields = 0, configIncomplete = 0;
        int chaptersWithOverrides = 0, superDelegateCount = 0;
        var overrideMethods = new SortedSet<string>(StringComparer.Ordinal);
        var badSample = new List<string>();
        foreach (var entry in catalog.Campaign.Chapters)
        {
            tiers[entry.Tier] = tiers.GetValueOrDefault(entry.Tier) + 1;
            if (entry.HasSiren) sirenCount++;
            if (entry.BossBattle is not null) bossKnown++;
            if (entry.NativeOverrides.Count > 0) chaptersWithOverrides++;
            superDelegateCount += entry.SuperDelegates.Count;
            foreach (var m in entry.NativeOverrides) overrideMethods.Add(m);

            var ir = catalog.LoadCampaign(entry);

            if (ir.ConfigMeta.Present)
            {
                configModules++;
                configFields += ir.Config.Count;
                if (ir.ConfigMeta.Complete)
                    configComplete++;
                else
                    configIncomplete++;
            }

            var shape = CampaignIr.ParseShape(ir.Shape);
            string? grid = ir.MapData;
            if (shape is not null && grid is not null)
            {
                var rows = grid.Split('\n')
                    .Select(r => r.Trim())
                    .Where(r => r.Length > 0)
                    .ToList();
                if (rows.Count != shape.Value.Rows)
                {
                    gridBad++;
                    if (badSample.Count < 5)
                        badSample.Add($"{entry.Source}: 行数 {rows.Count} != shape {shape.Value.Rows}");
                }
                else
                {
                    for (int i = 0; i < rows.Count; i++)
                    {
                        int cells = rows[i].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                        if (cells != shape.Value.Columns)
                        {
                            gridBad++;
                            if (badSample.Count < 5)
                                badSample.Add($"{entry.Source}: 第 {i} 行 {cells} 列 != shape {shape.Value.Columns}");
                            break;
                        }
                    }
                }
            }

            foreach (var b in ir.Campaign.Battles)
            {
                if (!b.PlanComplete && b.Steps.Count > 0)
                {
                    planBad++;
                    if (badSample.Count < 5)
                        badSample.Add($"{entry.Source}.{b.Method}: plan_complete=false 却有 steps");
                }
                if (b.PlanComplete && b.Steps.Count == 0)
                {
                    planBad++;
                    if (badSample.Count < 5)
                        badSample.Add($"{entry.Source}.{b.Method}: plan_complete=true 却无 steps");
                }
            }
        }
        Console.WriteLine($"[关卡] 网格不自洽 {gridBad}，计划不变量违例 {planBad}，"
                          + $"有塞壬 {sirenCount}，boss 回合已知 {bossKnown}");
        Console.WriteLine($"[Config] 有效模块 {configModules}，完整 {configComplete}，"
                          + $"字段 {configFields}，不完整 {configIncomplete}");
        foreach (var s in badSample) Console.WriteLine($"       {s}");
        if (gridBad > 0) problems.Add($"{gridBad} 个关卡网格与 shape 不自洽");
        if (planBad > 0) problems.Add($"{planBad} 处计划不变量违例");
        if (configIncomplete > 0) problems.Add($"{configIncomplete} 个章节 Config 导出不完整");

        // ---- 分级（决定 S3 的工作量构成）
        Console.WriteLine();
        Console.WriteLine("[分级] A = JSON 规则表即可；B = 计划完整但用词表外算子；C = 需插件/原生实现");
        foreach (var t in tiers.OrderBy(kv => kv.Key))
            Console.WriteLine($"       tier {t.Key}: {t.Value,5}  ({t.Value * 100.0 / catalog.Campaign.Chapters.Count:F1}%)");

        Console.WriteLine();
        Console.WriteLine("[引擎钩子] 非 battle_* 的覆写 —— S3 必须逐个在 C# 里实现");
        Console.WriteLine($"       涉及关卡 {chaptersWithOverrides}，"
                          + $"去重后 {overrideMethods.Count} 个钩子方法，"
                          + $"另有 {superDelegateCount} 处纯 super 委托（无需新增逻辑）");
        foreach (var m in overrideMethods.Take(15)) Console.WriteLine($"       {m}");
        if (overrideMethods.Count > 15) Console.WriteLine($"       ...（其余 {overrideMethods.Count - 15} 个）");

        Console.WriteLine();
        if (problems.Count == 0)
        {
            Console.WriteLine("结果: OK");
            return 0;
        }
        Console.WriteLine("结果: FAIL");
        foreach (var p in problems) Console.WriteLine($"  - {p}");
        return 1;
    }

    // ------------------------------------------------------------------ list
    private static int List(UpstreamData.Catalog catalog, string? tier)
    {
        var rows = catalog.Campaign.Chapters
            .Where(c => tier is null || c.Tier == tier)
            .OrderBy(c => c.Source, StringComparer.Ordinal);
        foreach (var c in rows)
        {
            Console.WriteLine($"{c.Tier}  {c.Name,-24} {c.Source}"
                              + (c.NeedsReview ? "   [需复核]" : ""));
        }
        Console.WriteLine($"共 {rows.Count()} 个（tier 过滤: {tier ?? "全部"}）");
        return 0;
    }

    // ------------------------------------------------------------------ show
    private static int Show(UpstreamData.Catalog catalog, string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return Fail("用法: alashub show <关卡名 | 源文件路径片段>");

        var entry = catalog.Campaign.Chapters.FirstOrDefault(c =>
            string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase))
            ?? catalog.Campaign.Chapters.FirstOrDefault(c =>
                c.Source.Contains(key, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return Fail($"找不到关卡: {key}");

        var ir = catalog.LoadCampaign(entry);
        Console.WriteLine($"源文件   : {entry.Source}");
        Console.WriteLine($"关卡名   : {ir.Name}（来源 {ir.NameSource}）");
        Console.WriteLine($"分级     : {entry.Tier}   plan_complete={entry.PlanComplete}"
                          + $"   template_only={entry.TemplateOnly}");
        Console.WriteLine($"boss 回合: {entry.BossBattle}   有塞壬: {entry.HasSiren}");
        Console.WriteLine($"shape    : {ir.Shape}");
        Console.WriteLine($"map 字段 : {string.Join(", ", ir.Map.Keys.OrderBy(k => k, StringComparer.Ordinal))}");
        Console.WriteLine($"config   : {string.Join(", ", ir.Config.Keys.OrderBy(k => k, StringComparer.Ordinal))}");
        Console.WriteLine($"Config 导出: present={ir.ConfigMeta.Present} complete={ir.ConfigMeta.Complete} " +
                          $"fields={ir.Config.Count}");
        foreach (var group in ir.ConfigMeta.Origins.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                     .GroupBy(kv => $"{kv.Value.Module}.{kv.Value.Class}"))
            Console.WriteLine($"配置来源 : {group.Key} → {string.Join(", ", group.Select(kv => kv.Key))}");

        string? grid = ir.MapData;
        if (grid is not null)
        {
            Console.WriteLine("网格     :");
            foreach (var row in grid.Split('\n').Where(r => r.Trim().Length > 0))
                Console.WriteLine($"  {row.Trim()}");
        }

        Console.WriteLine("战斗计划 :");
        foreach (var b in ir.Campaign.Battles)
        {
            if (!b.PlanComplete)
            {
                Console.WriteLine($"  {b.Method}: 计划不完整（未识别语句: "
                                  + $"{string.Join(",", b.Unparsed)}），需插件或原生实现");
                continue;
            }
            Console.WriteLine($"  {b.Method}:");
            foreach (var s in b.Steps)
                Console.WriteLine($"    {s.Kind,-20} {s.Op}"
                                  + (s.Target is null ? "" : $" -> {s.Target}"));
        }
        if (ir.Unresolved.Count > 0)
            Console.WriteLine("未解析   : " + string.Join(" | ", ir.Unresolved));
        return 0;
    }

    /// <summary>运行报告排版：事实来自工件，判定仍是结果合同的那一份（报告不重判）。</summary>
    private static void PrintRunReport(Alas.Runtime.RunReport report)
    {
        Console.WriteLine($"[运行    ] {report.RunDirectory}");
        Console.WriteLine($"[模式    ] dry_run={report.DryRun} 工件={report.FileCount} " +
                          $"宿主启动={report.HostStartCount?.ToString() ?? "?"} " +
                          $"设备配置={report.DeviceConfigureCount?.ToString() ?? "?"}");
        if (report.QueueOutcome is not null)
            Console.WriteLine($"[队列    ] outcome={report.QueueOutcome} 任务={report.Tasks} " +
                              $"成功={report.TasksSucceeded} 失败={report.TasksFailed} 跳过={report.TasksSkipped}");
        if (report.BatchOutcome is not null)
            Console.WriteLine($"[批次    ] outcome={report.BatchOutcome} 关卡={report.Stages} " +
                              $"通关={report.StagesCleared}");
        Console.WriteLine($"[日志    ] 条目={report.LogEntries} 错误={report.LogErrors} 警告={report.LogWarnings}");
        foreach (var item in report.Items)
            Console.WriteLine($"[条目    ] {item.ToJsonString()}");
        if (report.Findings.Count == 0)
            Console.WriteLine("[发现    ] 无（证据链完整、没有失败项）");
        else
            foreach (var finding in report.Findings)
                Console.WriteLine($"[发现    ] {finding.Code}: {finding.Detail}");
        Console.WriteLine($"[结论    ] 有失败={report.HasFailures} 证据完整={report.EvidenceComplete}");
    }

    /// <summary>
    /// CLI 的公共参数层：`campaign` 与 `queue` 共用同一份解析，**不各写一套** ——
    /// 参数字段填进 <see cref="Alas.Runtime.SessionOptions"/>，校验留给运行时。
    /// </summary>
    private sealed class CliRunFlags
    {
        public Alas.Runtime.SessionOptions Options { get; } = new();
        public bool Run { get; set; }
        public bool AllowActions { get; set; }
        public bool ContinueOnError { get; set; }
        public bool Resume { get; set; }
        /// <summary>位置参数或 `--chapter`/`--file` 给的值。</summary>
        public string? Positional { get; set; }
        public string? File { get; set; }
        /// <summary>`--resume <state.json>` 显式指定的断点文件（可为空 = 自动找上一次）。</summary>
        public string? ResumeState { get; set; }
    }

    private static CliRunFlags ParseRunFlags(string[] args, string positionalFlag)
    {
        var flags = new CliRunFlags { Positional = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null };
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            if (a is "--run" or "--allow-actions" or "--repeat" or "--clear-all"
                or "--continue-on-error" or "--resume")
            {
                if (a == "--run") flags.Run = true;
                if (a == "--allow-actions") flags.AllowActions = true;
                if (a == "--repeat") flags.Options.RepeatUntilCleared = true;
                if (a == "--clear-all") flags.Options.ClearAll = true;
                if (a == "--continue-on-error") flags.ContinueOnError = true;
                if (a == "--resume") flags.Resume = true;
                continue;
            }
            if (a.StartsWith("--") && i + 1 >= args.Length)
                throw new ArgumentException($"{a} 缺少参数值");
            string v = i + 1 < args.Length ? args[i + 1] : "";
            if (a == "--chapter" || a == positionalFlag) flags.Positional = v;
            else if (a == "--resume-state") flags.ResumeState = v;
            else if (a == "--file") flags.File = v;
            else if (a == "--adb") flags.Options.AdbPath = v;
            else if (a == "--serial") flags.Options.Serial = v;
            else if (a == "--artifacts") flags.Options.ArtifactsDirectory = v;
            else if (a == "--screenshot") flags.Options.ScreenshotBackend = v;
            else if (a == "--control") flags.Options.ControlBackend = v;
            else if (a == "--max-seconds" && double.TryParse(v, out double ms)) flags.Options.MaxSeconds = ms;
            else if (a == "--max-rounds" && int.TryParse(v, out int mr)) flags.Options.MaxRounds = mr;
            else if (a == "--fleet1" && int.TryParse(v, out int f1)) flags.Options.Fleet1 = f1;
            else if (a == "--fleet2" && int.TryParse(v, out int f2)) flags.Options.Fleet2 = f2;
            else if (a == "--submarine" && int.TryParse(v, out int fs)) flags.Options.SubmarineFleet = fs;
        }
        flags.Options.DryRun = !flags.Run;
        flags.Options.AllowActions = flags.AllowActions;
        return flags;
    }

    /// <summary>任务队列报告：只排版，判定在 <see cref="Alas.Tasks.TaskQueue"/> 里做完了。</summary>
    private static void PrintQueueReport(Alas.Tasks.QueueResult queue,
                                         IReadOnlyList<Alas.Tasks.TaskRequest> requests)
    {
        var byId = requests.ToDictionary(r => r.Id, r => r);
        foreach (var task in queue.Tasks)
        {
            byId.TryGetValue(task.Id, out var request);
            Console.WriteLine($"[任务    ] {task.Id} kind={task.Kind} outcome={task.OutcomeName} " +
                              $"elapsed={task.ElapsedSeconds}s " +
                              $"error_kind={Alas.Runtime.RuntimeErrors.Name(task.ErrorKind)}" +
                              (task.Error is null ? "" : $" error={task.Error}") +
                              (request?.Required == true ? " required=true" : ""));
            if (task.Evidence is not null)
            {
                if (task.Evidence["batch_outcome"] is System.Text.Json.Nodes.JsonNode batch)
                    Console.WriteLine($"[任务证据] batch_outcome={batch} cleared={task.Evidence["cleared"]} " +
                                      $"stages={task.Evidence["stages"]?.AsArray().Count ?? 0}");
                if (task.Evidence["in_map"] is System.Text.Json.Nodes.JsonNode inMap)
                    Console.WriteLine($"[任务证据] server={task.Evidence["server"]} " +
                                      $"pages=[{string.Join(",", (task.Evidence["pages"]?.AsArray() ?? new())!)}] " +
                                      $"in_map={inMap} 相似度={task.Evidence["in_map_tolerance"]} " +
                                      $"来源={task.Evidence["source"]}");
                if (task.Evidence["detected"] is System.Text.Json.Nodes.JsonNode detected)
                    Console.WriteLine($"[任务证据] mode={task.Evidence["mode"]} detected={detected} " +
                                      $"grid_count={task.Evidence["grid_count"]} " +
                                      $"来源={task.Evidence["source"]}");
            }
            if (task.ArtifactPath is not null) Console.WriteLine($"[任务工件] {task.ArtifactPath}");
        }
        Console.WriteLine($"[队列结果] outcome={queue.Outcome} 任务={queue.Tasks.Count} " +
                          $"失败={queue.FailedCount} 跳过={queue.SkippedCount} " +
                          $"提前停止={queue.StoppedEarly}" +
                          (queue.StopReason is null ? "" : $" 原因={queue.StopReason}"));
        if (queue.IndexPath is not null) Console.WriteLine($"[队列工件] {queue.IndexPath}");
        if (queue.StatePath is not null) Console.WriteLine($"[断点文件] {queue.StatePath}");
    }

    /// <summary>
    /// 把一批关卡的结果排版出来。**这里只排版**：判定（通关/失败/违例）已经在
    /// <see cref="Alas.Runtime.CampaignBatchRunner"/> 与结果合同里做完了，
    /// CLI 不再自己算一遍（R1 阶段门槛：CLI 不复制业务状态机）。
    /// </summary>
    private static void PrintCampaignReport(Alas.Runtime.CampaignBatchResult batch,
                                            Alas.Runtime.SessionOptions options)
    {
        foreach (var stage in batch.Stages)
        {
            var r = stage.Result;
            Console.WriteLine($"[plan    ] {r?.Chapter ?? stage.Chapter} stage={stage.Stage} " +
                              $"tier={r?.Tier} dry_run={r?.DryRun ?? options.DryRun}");
            if (r is null)
            {
                Console.WriteLine($"[跳过    ] {stage.Error}");
                continue;
            }
            Console.WriteLine($"[steps   ] {string.Join(" → ", r.PlanSteps ?? new())}");
            Console.WriteLine($"[语义轨迹] {string.Join(", ", r.SemanticTrace ?? new())}");
            if (r.ConfigCount is not null)
            {
                Console.WriteLine($"[Config  ] present={r.ConfigPresent?.ToString() ?? "unknown"} " +
                                  $"complete={r.ConfigComplete?.ToString() ?? "unknown"} fields={r.ConfigCount}");
                Console.WriteLine($"[配置来源] {string.Join(", ", r.ConfigSources ?? new())}");
                Console.WriteLine($"[原生配置] {r.RuntimeConfigSource}");
            }
            if (r.Refused == true) Console.WriteLine($"[拒绝    ] {r.Reason ?? r.Error}");
            if (r.Error is not null) Console.WriteLine($"[错误    ] {r.Error}");
            if (r.Steps is not null)
                foreach (var step in r.Steps)
                {
                    Console.WriteLine("  " + string.Join(" ", step
                        .Where(kv => kv.Key != "traceback_tail")
                        .Select(kv => $"{kv.Key}={kv.Value}")));
                    // **把调用栈也打出来**：上游内部抛错时，栈是唯一定位线索
                    // （实测 execute_a_battle 报 KeyError: () 时只有类型没有栈 ✗）
                    if (step.TryGetValue("traceback_tail", out var tb) &&
                        tb.ValueKind == System.Text.Json.JsonValueKind.Array)
                        foreach (var ln in tb.EnumerateArray())
                            Console.WriteLine("      | " + ln.GetString());
                }
            if (r.ElapsedSeconds is not null)
                Console.WriteLine($"[结果    ] elapsed={r.ElapsedSeconds}s stopped_early={r.StoppedEarly} " +
                                  $"stop_reason={r.StopReason} outcome={r.Outcome} cleared={r.Cleared} " +
                                  $"campaign_end={r.CampaignEnd} end_reason={r.EndReason}");
            // 结算证据单独打出来：判"通关"靠的是它，不是 campaign_end（撤退也抛 CampaignEnd）。
            if (r.EndEvidence is not null)
                Console.WriteLine($"[结算证据] rank={r.EndEvidence.BattleRank ?? "-"} " +
                                  $"source={r.EndEvidence.RankSource ?? "-"} " +
                                  $"combat_status={r.EndEvidence.CombatStatus?.ToString() ?? "-"} " +
                                  $"stage_observed={r.EndEvidence.StageObserved?.ToString() ?? "-"} " +
                                  $"withdrawn={r.EndEvidence.Withdrawn?.ToString() ?? "-"}");
            if (r.Failure is not null)
                Console.WriteLine($"[失败    ] step={r.Failure.Step} error={r.Failure.Error} " +
                                  $"frame={r.Failure.Frame ?? "-"}");
            Console.WriteLine($"[合同    ] {Alas.Campaign.SortieContract.Describe(r, options.ArtifactsDirectory)}");
            if (stage.ArtifactPath is not null) Console.WriteLine($"[工件    ] {stage.ArtifactPath}");
        }
        if (batch.Stages.Count > 1 || batch.StoppedEarly)
        {
            Console.WriteLine($"[批次    ] outcome={batch.Outcome} cleared={batch.Cleared} " +
                              $"关数={batch.Stages.Count} 失败={batch.FailedCount} " +
                              $"提前停止={batch.StoppedEarly}" +
                              (batch.StopReason is null ? "" : $" 原因={batch.StopReason}"));
            if (batch.IndexPath is not null) Console.WriteLine($"[批次工件] {batch.IndexPath}");
        }
    }
}
