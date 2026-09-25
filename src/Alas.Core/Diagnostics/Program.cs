using Alas.Core;
using Alas.Device;
using Alas.Vision;

namespace Alas.Core.Diagnostics;

/// <summary>
/// S0 工具：读取上游数据契约并用 C# 独立复现校验。
///
/// 之所以用 C# 重写一遍校验（而不是信任 Python 侧的结论），是因为 S0 的验收标准是
/// 「C# 能消费这些数据」，而不是「Python 说数据没问题」。
/// </summary>
public static class DiagnosticCommands
{
    public static int Run(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        // `--adb` 传相对路径会在真机上失败：宿主把工作目录切到了 engine 目录（实测
        // `Win32Exception: 找不到指定的文件`）。在**入口统一**解析成绝对路径 ——
        // 一次覆盖所有子命令，而不是在每个参数解析循环里各修一遍（那样迟早漏一个）。
        // 只在路径确实存在时改写：不存在就让原来的报错照旧出现，别把错误信息改成另一种。
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == "--adb" && !Path.IsPathRooted(args[i + 1]) && File.Exists(args[i + 1]))
                args[i + 1] = Path.GetFullPath(args[i + 1]);
        }
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
                return Fail("run 已弃用：请把 observe 的 seconds、tick_seconds、map 写入队列 JSON，"
                            + "再使用 queue --file <队列.json> 和公共运行参数执行");
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
                    Console.WriteLine("用法: queue --file <队列.json> [--run (--allow-actions|--read-only-device)] [--serial <设备>] " +
                                      "[--artifacts <目录>] [--resume] [--continue-on-error]");
                    return 2;
                }
                if (!File.Exists(queueFile))
                {
                    Console.Error.WriteLine($"找不到队列文件: {queueFile}");
                    return 2;
                }
                if (queueFlags.Run && !queueFlags.AllowActions && !queueFlags.Options.ReadOnlyDevice)
                {
                    Console.WriteLine("[拒绝    ] 真跑需要 --allow-actions 或 --read-only-device");
                    return 2;
                }
                var queueOptions = queueFlags.Options;
                queueOptions.RepoDirectory = repoDir;
                queueOptions.ToolsDirectory = paths.ToolsDirectory;
                queueOptions.DataDirectory = dataDir;
                // Ctrl-C 只提供取消信号；断点、停止文件和队列调度由运行时处理。
                using var stop = new CancellationTokenSource();
                ConsoleCancelEventHandler requestStop = (_, e) => { e.Cancel = true; stop.Cancel(); };
                Console.CancelKeyPress += requestStop;
                try
                {
                    var execution = Alas.Runtime.QueueExecution.RunFile(
                        queueFile, queueOptions,
                        stopOnFailure: !queueFlags.ContinueOnError,
                        resume: queueFlags.Resume,
                        resumeState: queueFlags.ResumeState,
                        stopFile: queueFlags.StopFile,
                        token: stop.Token);
                    Console.WriteLine($"[队列    ] {execution.Requests.Count} 个任务");
                    if (queueFlags.Resume)
                    {
                        if (execution.ResumeStatePath is null)
                            Console.WriteLine("[断点    ] 没找到可续跑的 state.json（本次仍从头跑）");
                        else
                            Console.WriteLine($"[断点    ] 依据 {execution.ResumeStatePath} "
                                + $"跳过 {execution.ResumeCompleted.Count} 个已完成任务"
                                + (execution.ResumeCompleted.Count == 0 ? ""
                                   : $": {string.Join(", ", execution.ResumeCompleted)}"));
                    }
                    if (execution.StopFileTriggered)
                        Console.WriteLine("[停止    ] 检测到停止文件，已请求在任务边界停止");
                    PrintQueueReport(execution.Queue, execution.Requests);
                    return execution.Queue.FailedCount == 0 ? 0 : 1;
                }
                finally { Console.CancelKeyPress -= requestStop; }
            }
            if (command == "plan-queue")
            {
                // R2 数据面：把活动清点结果翻译成**普通队列文件**（后面照样 queue/report/--resume）。
                string prefix = "event_", outPath = ""; bool onlyComplete = false, dryRun = true;
                bool captureAfter = false;
                int limit = 5, maxRounds = 20; double maxSeconds = 1500;
                for (int i = 1; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "--folder-prefix":
                            prefix = RequireOptionValue(args, ref i, "--folder-prefix");
                            break;
                        case "--out":
                            outPath = RequireOptionValue(args, ref i, "--out");
                            break;
                        case "--limit":
                            limit = ParsePositiveIntOption(args, ref i, "--limit");
                            break;
                        case "--max-rounds":
                            maxRounds = ParsePositiveIntOption(args, ref i, "--max-rounds");
                            break;
                        case "--max-seconds":
                            maxSeconds = ParsePositiveDoubleOption(args, ref i, "--max-seconds");
                            break;
                        // 全局路径参数已在入口预解析；这里仍要消费它们，避免被
                        // plan-queue 的专用参数校验误报为未知参数。
                        case "--data":
                        case "--repo":
                            RequireOptionValue(args, ref i, args[i]);
                            break;
                        case "--only-complete":
                            onlyComplete = true;
                            break;
                        case "--run":
                            dryRun = false;
                            break;
                        case "--capture-after":
                            captureAfter = true;
                            break;
                        default:
                            throw new ArgumentException($"plan-queue 未知参数: {args[i]}");
                    }
                }
                if (outPath.Length == 0)
                {
                    Console.WriteLine("用法: plan-queue --out <队列.json> [--folder-prefix event_] "
                                      + "[--only-complete] [--limit 5] [--max-rounds 20] [--max-seconds 1500] [--capture-after] [--run]");
                    return 2;
                }
                var (count, document) = Alas.Tasks.TaskQueuePlanner.Build(dataDir, prefix, onlyComplete,
                    limit, maxRounds, maxSeconds, dryRun, captureAfter);
                string full = Path.GetFullPath(outPath);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, document.ToJsonString(
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"[计划    ] {count} 个章节，队列 {((System.Text.Json.Nodes.JsonArray)document["tasks"]!).Count} 个任务"
                                  + $"（匹配 {document["matched"]}/"
                                  + $"{document["chapters_total"]} 章，前缀 {prefix}"
                                  + (onlyComplete ? "，只要计划完整" : "") + "）");
                Console.WriteLine($"[队列文件] {full}");
                Console.WriteLine($"[提醒    ] 生成的是普通队列文件：Alas.Server queue --file <该文件>"
                                  + (dryRun ? "（默认 dry-run）" : "（--run --allow-actions）"));
                return 0;
            }
            if (command == "runs")
            {
                // R4 数据面：列出工件根目录下最近的运行（摘要），详情用 report --run。
                string runsRoot = Path.Combine(dataDir, "runs"), runsJson = "";
                int runsLimit = 10;
                for (int i = 1; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "--artifacts":
                            runsRoot = RequireOptionValue(args, ref i, "--artifacts");
                            break;
                        case "--json":
                            runsJson = RequireOptionValue(args, ref i, "--json");
                            break;
                        case "--limit":
                            runsLimit = ParsePositiveIntOption(args, ref i, "--limit");
                            break;
                        case "--data":
                        case "--repo":
                            RequireOptionValue(args, ref i, args[i]);
                            break;
                        default:
                            throw new ArgumentException($"runs 未知参数: {args[i]}");
                    }
                }
                var summary = Alas.Runtime.RunReport.Summarize(runsRoot, runsLimit);
                Console.WriteLine($"[运行列表] {summary["artifacts_root"]}（存在={summary["exists"]}，"
                                  + $"返回 {summary["returned"]} 条）");
                foreach (var run in (summary["runs"] ?? new System.Text.Json.Nodes.JsonArray()).AsArray())
                    Console.WriteLine($"[运行    ] {run!["stamp"]} dry_run={run["dry_run"]} "
                                      + $"队列={run["queue_outcome"]} 任务={run["tasks"]} "
                                      + $"失败={run["tasks_failed"]} 关卡={run["stages"]} "
                                      + $"通关={run["stages_cleared"]} 证据完整={run["evidence_complete"]} "
                                      + $"findings={run["findings"]}");
                if (runsJson.Length > 0)
                {
                    string full = Path.GetFullPath(runsJson);
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    File.WriteAllText(full, summary.ToJsonString(
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                    Console.WriteLine($"[列表工件] {full}");
                }
                return 0;
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
                string? runtimeFixture = null, runtimeJson = null, runtimeWorkspace = null;
                for (int i = 1; i < args.Length - 1; i++)
                {
                    if (args[i] == "--fixture") runtimeFixture = args[i + 1];
                    if (args[i] == "--json") runtimeJson = args[i + 1];
                    if (args[i] == "--workspace") runtimeWorkspace = args[i + 1];
                }
                if (runtimeFixture is null)
                {
                    Console.WriteLine("用法: selftest-runtime --fixture <用例.json> [--json <裁决.json>]");
                    return 2;
                }
                return RuntimeSelfCheck.Run(runtimeFixture, runtimeJson, runtimeWorkspace);
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
                for (int i = 1; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "--adb":
                            capAdb = RequireOptionValue(args, ref i, "--adb");
                            break;
                        case "--serial":
                            capSerial = RequireOptionValue(args, ref i, "--serial");
                            break;
                        case "--screenshot":
                            capShot = RequireOptionValue(args, ref i, "--screenshot");
                            break;
                        case "--control":
                            capCtrl = RequireOptionValue(args, ref i, "--control");
                            break;
                        case "--repeat":
                            capRepeat = ParsePositiveIntOption(args, ref i, "--repeat");
                            break;
                        case "--repo":
                        case "--data":
                            RequireOptionValue(args, ref i, args[i]);
                            break;
                        default:
                            throw new ArgumentException($"capture 未知参数: {args[i]}");
                    }
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
            if (command == "r5-loop")
            {
                // 关卡循环（干跑）：按上游 run()/execute_a_battle() 语义跑轮次，动作只被记录。
                string loopFixture = Path.Combine(paths.ToolsDirectory, "diagnostics", "r5-loop-fixture.json");
                for (int i = 1; i < args.Length - 1; i++)
                {
                    if (args[i] == "--fixture") loopFixture = args[i + 1];
                }
                return CampaignLoopCheck.Run(dataDir, loopFixture);
            }
            if (command == "r5-path")
            {
                // 寻路成本场（离线）：按上游 find_path_initial 语义算成本与连接，供逐格对拍。
                string pathFixture = Path.Combine(paths.ToolsDirectory, "diagnostics", "r5-path-fixture.json");
                int pathRepeat = 1;
                for (int i = 1; i < args.Length - 1; i++)
                {
                    if (args[i] == "--fixture") pathFixture = args[i + 1];
                    if (args[i] == "--repeat" && int.TryParse(args[i + 1], out int parsed)) pathRepeat = parsed;
                }
                return CampaignPathCheck.Run(pathFixture, pathRepeat);
            }
            if (command == "r5-exec")
            {
                // 原语执行闭环（干跑）：读夹具、驱动已登记原语、记录动作，不连设备。
                string executionFixture = Path.Combine(paths.ToolsDirectory, "diagnostics",
                                                       "r5-execution-fixture.json");
                for (int i = 1; i < args.Length - 1; i++)
                {
                    if (args[i] == "--fixture") executionFixture = args[i + 1];
                }
                return CampaignExecutionCheck.Run(dataDir, executionFixture);
            }
            if (command == "r5-select")
            {
                // 只读目标选择对拍：读夹具、跑 C# 移植的选择器、输出选中结果（不连设备）。
                string selectionFixture = Path.Combine(dataDir, "fixtures", "r5-selection.json");
                for (int i = 1; i < args.Length - 1; i++)
                {
                    if (args[i] == "--fixture") selectionFixture = args[i + 1];
                }
                return CampaignSelectionCheck.Run(selectionFixture);
            }
            if (command == "r5-plan")
            {
                // 只读导出规则：不执行关卡、不导入游戏代码、不连设备（见 CampaignPlanCheck）。
                string? planChapter = target;
                string? planLevel = null;
                bool dryRunAll = args.Contains("--dry-run-all");
                for (int i = 1; i < args.Length - 1; i++)
                {
                    if (args[i] == "--chapter") planChapter = args[i + 1];
                    if (args[i] == "--level") planLevel = args[i + 1];
                }
                return CampaignPlanCheck.Run(dataDir, planChapter, planLevel, dryRunAll);
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
                return Fail("goto 已弃用：请把 navigate 的 to、rounds 写入队列 JSON，"
                            + "再使用 queue --file <队列.json> --run --allow-actions 执行");
            }
            if (command == "vision")
            {
                fixture ??= Path.Combine(dataDir, "fixtures", "imaging.json");
                int limit = 200;
                string mode = Environment.GetEnvironmentVariable("ALAS_VISION_MODE") ?? "worker";
                for (int i = 1; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "--limit":
                            limit = ParsePositiveIntOption(args, ref i, "--limit");
                            break;
                        case "--mode":
                            mode = RequireOptionValue(args, ref i, "--mode");
                            break;
                        case "--fixture":
                            fixture = RequireOptionValue(args, ref i, "--fixture");
                            break;
                        case "--data":
                        case "--repo":
                            RequireOptionValue(args, ref i, args[i]);
                            break;
                        default:
                            throw new ArgumentException($"vision 未知参数: {args[i]}");
                    }
                }
                // bin/Release/net10.0 -> csharp/tools
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
                           + "map / map-ir / capture / device / queue / plan-queue / report / runs / run / goto / "
                           + "contract / selftest-runtime / r5-plan / r5-select / r5-exec / r5-loop / r5-path）"),
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

    private static string RequireOptionValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"{option} 缺少参数值");
        return args[++index];
    }

    private static int ParsePositiveIntOption(string[] args, ref int index, string option)
    {
        string value = RequireOptionValue(args, ref index, option);
        if (!int.TryParse(value, System.Globalization.NumberStyles.Integer,
                          System.Globalization.CultureInfo.InvariantCulture, out int parsed)
            || parsed <= 0)
            throw new ArgumentException($"{option} 必须为正整数: {value}");
        return parsed;
    }

    private static double ParsePositiveDoubleOption(string[] args, ref int index, string option)
    {
        string value = RequireOptionValue(args, ref index, option);
        if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out double parsed)
            || !double.IsFinite(parsed) || parsed <= 0)
            throw new ArgumentException($"{option} 必须为有限正数: {value}");
        return parsed;
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

        // Compare the exported inventory and provenance with the selected source.
        string relative(string path) => Path.GetRelativePath(repoDir, path).Replace('\\', '/');
        var assetSources = Directory.EnumerateFiles(Path.Combine(repoDir, "module"), "assets.py",
            SearchOption.AllDirectories).Select(relative).ToHashSet(StringComparer.Ordinal);
        var campaignSources = Directory.EnumerateFiles(Path.Combine(repoDir, "campaign"), "*.py",
            SearchOption.AllDirectories).Where(p => Path.GetFileName(p) != "__init__.py"
                && !p.Split(Path.DirectorySeparatorChar).Contains("__pycache__"))
            .Select(relative).ToHashSet(StringComparer.Ordinal);
        var indexSources = catalog.Campaign.Chapters.Select(c => c.Source).ToList();
        string[] exportServers = ["cn", "en", "jp", "tw"];
        if (catalog.Assets.Servers.Count != exportServers.Length
            || !exportServers.ToHashSet(StringComparer.Ordinal).SetEquals(catalog.Assets.Servers))
            problems.Add("素材目录服务器清单缺失、重复或与导出契约不一致");
        if (indexSources.Distinct(StringComparer.Ordinal).Count() != indexSources.Count
            || !campaignSources.SetEquals(indexSources))
            problems.Add("关卡索引缺失、重复或包含非上游来源");
        if (catalog.Manifest is null)
            problems.Add("缺少导出 manifest");
        else
        {
            var manifest = catalog.Manifest.RootElement;
            foreach (var (section, sources) in new[] { ("assets", assetSources), ("campaign", campaignSources) })
            {
                if (!manifest.TryGetProperty(section, out var summary)
                    || !summary.TryGetProperty("source_hashes", out var hashes)
                    || hashes.ValueKind != System.Text.Json.JsonValueKind.Object
                    || !sources.SetEquals(hashes.EnumerateObject().Select(p => p.Name)))
                {
                    problems.Add($"{section} 源哈希清单缺失或不一致");
                    continue;
                }
                foreach (var source in sources)
                {
                    var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                        File.ReadAllBytes(Path.Combine(repoDir, source))));
                    if (hashes.GetProperty(source).GetString() != hash)
                        problems.Add($"{section} 源哈希变化: {source}");
                }
                if (!summary.TryGetProperty("source_files", out var sourceCount)
                    || sourceCount.GetInt32() != sources.Count)
                    problems.Add($"{section} 源文件计数不一致");
            }
            if (!manifest.TryGetProperty("assets", out var assets)
                || !assets.TryGetProperty("count", out var count)
                || count.GetInt32() != catalog.Assets.Assets.Count)
                problems.Add("素材 manifest 计数不一致");
            else
            {
                if (!assets.TryGetProperty("all_four_servers", out var completeCount)
                    || completeCount.GetInt32() != catalog.Assets.Assets.Values.Count(a => a.AllServers))
                    problems.Add("素材 manifest 四服完整计数不一致");
                foreach (var (key, groups) in new[] {
                    ("by_kind", catalog.Assets.Assets.Values.GroupBy(a => a.Kind)),
                    ("by_module", catalog.Assets.Assets.Values.GroupBy(a => a.Module)) })
                {
                    var expected = groups.ToDictionary(g => g.Key, g => g.Count());
                    if (!assets.TryGetProperty(key, out var counts)
                        || counts.EnumerateObject().Count() != expected.Count
                        || counts.EnumerateObject().Any(p => !expected.TryGetValue(p.Name, out var total)
                            || p.Value.GetInt32() != total))
                        problems.Add($"素材 manifest {key} 不一致");
                }
                if (!assets.TryGetProperty("unresolved", out var unresolved) || unresolved.GetArrayLength() != 0)
                    problems.Add("素材存在未解析项");
            }
            if (!manifest.TryGetProperty("errors", out var errors) || errors.GetArrayLength() != 0)
                problems.Add("导出源错误清单缺失或非空");
            if (!manifest.TryGetProperty("campaign", out var campaign)
                || !campaign.TryGetProperty("files", out var files)
                || files.GetInt32() != catalog.Campaign.Chapters.Count)
                problems.Add("关卡 manifest 计数不一致");
        }

        // ---- 素材：文件存在性 + 字段完整性
        // 注意：上游 Template 只有 file（无 area/color/button），area 只对 Button/Mask 是必需的。
        int missingFiles = 0, noServers = 0, noArea = 0, noFile = 0, invalidBindings = 0;
        var missingSample = new List<string>();
        foreach (var (id, a) in catalog.Assets.Assets)
        {
            var servers = a.Servers.ToHashSet(StringComparer.Ordinal);
            bool sameServers<T>(Dictionary<string, T>? values) => values is not null
                && servers.SetEquals(values.Keys);
            bool validVectors(Dictionary<string, int[]>? values, int size) => values is not null
                && values.Values.All(value => value is not null && value.Length == size);
            if (id != a.Id || id != $"{a.Module}/{a.Name}"
                || a.Source != $"module/{a.Module.Replace('.', '/')}/assets.py"
                || !assetSources.Contains(a.Source)
                || a.Kind is not ("Button" or "Template" or "Mask")
                || servers.Count != a.Servers.Count
                || !servers.IsSubsetOf(catalog.Assets.Servers)
                || a.AllServers != servers.IsSupersetOf(catalog.Assets.Servers)
                || !sameServers(a.File)
                || a.File?.Values.Any(string.IsNullOrWhiteSpace) == true
                || (a.Kind == "Button" && (!sameServers(a.Area)
                    || !sameServers(a.Color) || !sameServers(a.Button)
                    || !validVectors(a.Area, 4) || !validVectors(a.Color, 3) || !validVectors(a.Button, 4)))
                || (a.Kind == "Mask" && (!sameServers(a.Area) || !validVectors(a.Area, 4))))
                invalidBindings++;
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
        if (invalidBindings > 0) problems.Add($"{invalidBindings} 个素材来源或服务器字段不完整");

        // ---- 关卡：网格自洽 + Config 导出 + 计划不变量 + 分级
        var tiers = new Dictionary<string, int>();
        int gridBad = 0, planBad = 0, sirenCount = 0, bossKnown = 0;
        int configModules = 0, configComplete = 0, configFields = 0, configIncomplete = 0;
        int mapModules = 0, mapComplete = 0, mapFields = 0, mapBad = 0;
        int campaignModules = 0, campaignComplete = 0, campaignFields = 0, campaignAliases = 0, campaignBad = 0;
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
            if (ir.Source != entry.Source || entry.Json != entry.Source[..^3] + ".json")
                problems.Add($"关卡索引与 IR 来源不一致: {entry.Source}");
            if (!MapExportValidation.Check(ir, entry, repoDir))
            {
                mapBad++;
                if (badSample.Count < 5) badSample.Add($"{entry.Source}: MAP 字段/类型/来源/完整性不一致");
            }
            if (ir.MapMeta.Present) mapModules++;
            if (ir.MapMeta.Present && ir.MapMeta.Complete) mapComplete++;
            mapFields += ir.Map.Count;
            if (!CampaignExportValidation.Check(ir, entry, repoDir))
            {
                campaignBad++;
                if (badSample.Count < 5) badSample.Add($"{entry.Source}: Campaign 声明/类型/来源/完整性不一致");
            }
            if (ir.Campaign.AttributesMeta is { } declarations)
            {
                if (declarations.Present) campaignModules++;
                if (declarations.Present && declarations.Complete) campaignComplete++;
                campaignAliases += declarations.MethodAliases?.Count ?? 0;
            }
            campaignFields += ir.Campaign.Attributes.Count;

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
        Console.WriteLine($"[MAP] 声明模块 {mapModules}，完整 {mapComplete}，字段 {mapFields}，问题 {mapBad}");
        Console.WriteLine($"[Campaign] 声明模块 {campaignModules}，完整 {campaignComplete}，"
                          + $"属性 {campaignFields}，方法别名 {campaignAliases}，问题 {campaignBad}");
        if (campaignBad > 0) problems.Add($"{campaignBad} 个模块的 Campaign 声明导出不完整或不一致");
        if (catalog.Manifest is { } mapManifest
            && mapManifest.RootElement.TryGetProperty("campaign", out var mapSummary))
        {
            foreach (var (key, expected) in new[] { ("map_modules", mapModules),
                ("map_complete", mapComplete), ("map_fields", mapFields),
                ("campaign_modules", campaignModules), ("campaign_complete", campaignComplete),
                ("campaign_attributes", campaignFields), ("campaign_aliases", campaignAliases) })
                if (!mapSummary.TryGetProperty(key, out var actual) || actual.GetInt32() != expected)
                    problems.Add($"关卡 manifest {key} 不一致");
        }
        foreach (var s in badSample) Console.WriteLine($"       {s}");
        if (gridBad > 0) problems.Add($"{gridBad} 个关卡网格与 shape 不自洽");
        if (planBad > 0) problems.Add($"{planBad} 处计划不变量违例");
        if (configIncomplete > 0) problems.Add($"{configIncomplete} 个章节 Config 导出不完整");
        if (mapBad > 0) problems.Add($"{mapBad} 个 MAP 声明导出不完整或不一致");

        // ---- 分级（决定 S3 的工作量构成）
        Console.WriteLine();
        Console.WriteLine("[分级] 仅表示离线摘要完整度；所有战役均由上游原生流程执行");
        foreach (var t in tiers.OrderBy(kv => kv.Key))
            Console.WriteLine($"       tier {t.Key}: {t.Value,5}  ({t.Value * 100.0 / catalog.Campaign.Chapters.Count:F1}%)");

        Console.WriteLine();
        Console.WriteLine("[引擎钩子] 非 battle_* 的覆写 —— 由原生 Campaign 继承与调度保留");
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
            return Fail("用法: Alas.Server show <关卡名 | 源文件路径片段>");

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
        /// <summary>--stop-file <路径>：文件一出现就请求停止（在任务边界生效）。</summary>
        public string? StopFile { get; set; }
    }

    private static CliRunFlags ParseRunFlags(string[] args, string positionalFlag)
    {
        var flags = new CliRunFlags { Positional = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null };
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            if (a is "--run" or "--allow-actions" or "--read-only-device" or "--repeat" or "--clear-all"
                or "--continue-on-error" or "--resume")
            {
                if (a == "--run") flags.Run = true;
                if (a == "--allow-actions") flags.AllowActions = true;
                if (a == "--read-only-device") flags.Options.ReadOnlyDevice = true;
                if (a == "--repeat") flags.Options.RepeatUntilCleared = true;
                if (a == "--clear-all") flags.Options.ClearAll = true;
                if (a == "--continue-on-error") flags.ContinueOnError = true;
                if (a == "--resume") flags.Resume = true;
                continue;
            }
            if (!a.StartsWith("--"))
            {
                if (i == 1) continue;
                throw new ArgumentException($"多余的位置参数: {a}");
            }
            if (a is not ("--chapter" or "--file" or "--resume-state" or "--stop-file"
                or "--adb" or "--serial" or "--artifacts" or "--screenshot" or "--control"
                or "--max-seconds" or "--max-rounds" or "--fleet1" or "--fleet2"
                or "--submarine" or "--data" or "--repo"))
                throw new ArgumentException($"未知参数: {a}");
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--"))
                throw new ArgumentException($"{a} 缺少参数值");
            string v = args[++i];
            if (a == "--chapter" || a == positionalFlag) flags.Positional = v;
            else if (a == "--resume-state") flags.ResumeState = v;
            else if (a == "--stop-file") flags.StopFile = v;
            else if (a == "--file") flags.File = v;
            else if (a == "--adb") flags.Options.AdbPath = v;
            else if (a == "--serial") flags.Options.Serial = v;
            else if (a == "--artifacts") flags.Options.ArtifactsDirectory = v;
            else if (a == "--screenshot") flags.Options.ScreenshotBackend = v;
            else if (a == "--control") flags.Options.ControlBackend = v;
            else if (a == "--max-seconds")
                flags.Options.MaxSeconds = double.TryParse(v, out double ms) ? ms
                    : throw new ArgumentException($"{a} 需要数字: {v}");
            else if (a == "--max-rounds")
                flags.Options.MaxRounds = int.TryParse(v, out int mr) ? mr
                    : throw new ArgumentException($"{a} 需要整数: {v}");
            else if (a == "--fleet1")
                flags.Options.Fleet1 = int.TryParse(v, out int f1) ? f1
                    : throw new ArgumentException($"{a} 需要整数: {v}");
            else if (a == "--fleet2")
                flags.Options.Fleet2 = int.TryParse(v, out int f2) ? f2
                    : throw new ArgumentException($"{a} 需要整数: {v}");
            else if (a == "--submarine")
                flags.Options.SubmarineFleet = int.TryParse(v, out int fs) ? fs
                    : throw new ArgumentException($"{a} 需要整数: {v}");
        }
        flags.Options.DryRun = !flags.Run;
        flags.Options.AllowActions = flags.AllowActions;
        return flags;
    }

    /// <summary>只排版原生 UI.ui_ensure 已保存的逐段导航证据。</summary>
    private static void PrintNavigationEvidence(System.Text.Json.Nodes.JsonObject evidence)
    {
        if (evidence["rounds"] is System.Text.Json.Nodes.JsonArray rounds)
            foreach (var round in rounds.OfType<System.Text.Json.Nodes.JsonObject>())
            {
                foreach (string phase in new[] { "return_to_main", "to_target" })
                {
                    if (round[phase] is not System.Text.Json.Nodes.JsonObject leg) continue;
                    Console.WriteLine($"[原生导航] phase={phase} destination={leg["destination"]} " +
                                      $"arrived={leg["arrived"]} final={leg["final_page"]} " +
                                      $"changed={leg["changed"]} elapsed_ms={leg["elapsed_ms"]}");
                    if (leg["error"] is System.Text.Json.Nodes.JsonNode error)
                        Console.WriteLine($"[导航失败] kind={leg["error_kind"]} {error}");
                }
                Console.WriteLine($"[回合    ] round={round["round"]} success={round["success"]}");
            }
        if (evidence["success"]?.GetValue<bool>() == true)
            Console.WriteLine($"已到达 {evidence["target"]}（{evidence["rounds_completed"]}/{evidence["rounds_requested"]} 回合，最终页 {evidence["final_page"]}）");
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
                if (task.Kind == "navigate") PrintNavigationEvidence(task.Evidence);
                if (task.Evidence["ticks"] is System.Text.Json.Nodes.JsonNode ticks)
                    Console.WriteLine($"[观测    ] ticks={ticks} errors={task.Evidence["errors"]} " +
                                      $"pages={task.Evidence["pages"]?.ToJsonString()} " +
                                      $"capture={task.Evidence["capture"]?["method"]}");
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
                // 周期任务调度状态：报"总共几个任务、几个开着"（明细在工件里，不在这里刷屏）
                // 配置开关：报"查了几个、几个 true/false/missing"（明细在工件里，不在这刷屏）
                // 周期任务执行：报"判定 / 跑了什么类 / 耗时"（明细在工件里）
                if (task.Evidence["decision"] is System.Text.Json.Nodes.JsonNode runDecision)
                {
                    var target = task.Evidence["target"] as System.Text.Json.Nodes.JsonObject;
                    // 被闸门挡下时没有构造/运行/耗时 —— 如实显示 "—"，不要打印成空串
                    string Render(string key, string suffix = "")
                        => task.Evidence[key] is System.Text.Json.Nodes.JsonNode node
                           && node.GetValueKind() != System.Text.Json.JsonValueKind.Null
                            ? node.ToString() + suffix : "—";
                    Console.WriteLine($"[任务证据] 判定={runDecision} "
                                      + $"目标={(target?["class"]?.GetValue<string>() ?? "—")} "
                                      + $"构造={Render("constructed")} 已跑={Render("ran")} "
                                      + $"耗时={Render("elapsed_s", "s")}");
                }                if (task.Evidence["checked"] is System.Text.Json.Nodes.JsonNode cfgChecked)
                {
                    int CountOf(string key)
                        => task.Evidence[key] is System.Text.Json.Nodes.JsonArray array ? array.Count : 0;
                    Console.WriteLine($"[任务证据] 查了={cfgChecked} true={CountOf("true_keys")} "
                                      + $"false={CountOf("false_keys")} missing={CountOf("missing_keys")} "
                                      + $"来源={task.Evidence["config_source"]}");
                }                // 周期任务清点：报"读到多少个任务、两个来源是哪一份"（明细在工件里）
                if (task.Evidence["task_count"] is System.Text.Json.Nodes.JsonNode catalogCount)
                    Console.WriteLine($"[任务证据] 任务={catalogCount} "
                                      + $"分组来源={task.Evidence["group_source"]} "
                                      + $"清单来源={task.Evidence["task_source"]}");                if (task.Evidence["enabled_count"] is System.Text.Json.Nodes.JsonNode enabled)
                    Console.WriteLine($"[任务证据] 任务={task.Evidence["task_count"]} 原始配置启用={enabled} " +
                                      $"无Scheduler={task.Evidence["no_scheduler_count"]} " +
                                      $"来源={task.Evidence["config_source"]}");
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
