using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Alas.UI.Controls;
using Alas.UI.Theming;
using Alas.UI.ViewModels;
using Alas.UI.Views;

namespace Alas.UI.Headless;

/// <summary>
/// UI 性能基线（离屏、原生 Headless、**真实共享视图**）：被测对象是生产用的
/// <see cref="MainView"/> + <see cref="ShellViewModel"/> + <c>OverviewView</c> 日志列表，
/// 不是为测量另写的替代控件。
///
/// 覆盖以下四类主要场景：
/// ① 冷构造：真实外壳 + 窗口显示 + 首帧布局/渲染；
/// ② 预热后页面切换：真实导航命令 <c>SelectNavCommand</c> 逐页切换；
/// ③ 日志量：2000 / 10000 行的批量追加（含跟随滚动开/关对照）、上限稳态逐条追加、
///    以及**实际实现的可视控件数量**（日志列表是否虚拟化）；
/// ④ 重复导航 50 次：每次导航的分配量、托管堆与**事件订阅数量增长**（反射读字段式事件的调用列表）。
/// 另加一条关闭外壳后的可达性探针（事件/定时器是否把已关闭的界面留住）。
///
/// 口径与边界：
/// - 每个场景都报样本数、median、p95、min、max 与分配量；percentile 用 nearest-rank
///   （p95 在 n=20 时是第 19 小值），样本数写在场景里，**不用一次 Stopwatch 数字下结论**；
/// - 不设宽阈值当验收线：本检查只如实记录数值与结构性事实（订阅是否增长、控件是否随行数增长），
///   结构性事实才抛异常；
/// - 全部在原生 Headless + Skia 离屏里跑，不显示窗口、不连设备、不写后端配置；
/// - 现场浏览器/原生窗口的性能不由此文件证明。
///
/// 独立入口：<see cref="Run"/> 必须在 Headless 会话的 UI 线程上调用
/// （例如 <c>HeadlessUnitTestSession.Dispatch(() =&gt; UiPerformanceChecks.Run(output))</c>）。
/// </summary>
public static class UiPerformanceChecks
{
    public const string Schema = "ui-perf/1";

    private const int WindowWidth = 1280;
    private const int WindowHeight = 900;
    private const int ConfidenceSamples = 20;

    /// <summary>无实例外壳里的真实页面（顺序与上游导航一致）；logins 也在其中。</summary>
    private static readonly string[] GlobalPages =
        { "home", "updater", "interface", "remote", "configs", "settings", "dev", "login" };

    private static readonly string[] LogPagePrefixes =
        { "perf", "steady", "warm", "中文日志正文用于测量换行与渲染" };

    /// <summary>
    /// 结构性断言（不是性能阈值）：实现控件是否随视口而非容量、跟随是否只在贴最新端时生效、
    /// 释放后订阅是否回到基线、已关闭的外壳能否回收。报告先落盘，最后再按这些违例决定退出码，
    /// 这样"改前"的失败运行同样留下可与"改后"对照的数字。
    /// </summary>
    private static readonly List<Dictionary<string, object?>> Invariants = new();

    private static void Invariant(string id, bool pass, string detail) =>
        Invariants.Add(new Dictionary<string, object?> { ["id"] = id, ["pass"] = pass, ["detail"] = detail });

    /// <summary>
    /// 跑一个场景。单个场景自己炸掉时不让整轮测量作废：把错误记进该场景并记一条失败断言，
    /// 后面的场景照跑——"改前"的运行同样要留下可对照的完整报告。
    /// </summary>
    private static void Measure(string id, string label, Func<Dictionary<string, object?>> scenario,
        List<Dictionary<string, object?>> scenarios)
    {
        Console.WriteLine($"[ui-perf] {label} …");
        try
        {
            scenarios.Add(scenario());
        }
        catch (Exception error)
        {
            scenarios.Add(new Dictionary<string, object?>
            {
                ["id"] = id,
                ["scenario"] = label,
                ["error"] = error.ToString(),
            });
            Invariant($"scenario-{id}-completed", false, $"场景抛错：{error.Message}");
        }
    }

    /// <summary>跑完全部场景，把原始报告写到 <paramref name="output"/>（JSON + 文本）。</summary>
    public static void Run(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) throw new ArgumentException("an output directory is required", nameof(output));
        Invariants.Clear();

        var scenarios = new List<Dictionary<string, object?>>();
        var notes = new List<string>
        {
            "被测界面是生产路径：MainView + ShellViewModel + OverviewView（日志列表），后端走 MainView 默认的离线演示数据（previewData）。",
            "percentile 用 nearest-rank：p95(n=20) = 第 19 小值；每个场景都写了样本数。",
            "一次 Stopwatch 数字不构成结论：每个场景都报样本数、median、p95、min、max 与分配量。",
            "这里没有性能阈值：只把结构性事实（事件订阅是否随导航增长、控件是否随日志行数增长）当失败，耗时与分配如实记录。",
            "前后对照须固定构建、入口、数据和预热过程，串行运行并结合分配量、控件数和订阅数分析；"
                + "时间受进程负载与环境影响，不把一次运行或不同入口的数字直接比较，也不保证重复测量完全一致。",
            "只在原生 Headless 里测量；浏览器与原生窗口的现场性能未验证。",
        };

        Console.WriteLine("[ui-perf] ⓪ harness calibration …");
        Measure("pump-calibration", "⓪ harness calibration", PumpCalibration, scenarios);
        Measure("cold-construction", "① cold construction", ColdConstruction, scenarios);
        Measure("warm-page-switching", "② warm page switching", PageSwitching, scenarios);
        foreach (var lines in new[] { 2000, 10000 })
        {
            foreach (var follow in new[] { true, false })
            {
                var id = $"log-volume-{lines}-follow-{(follow ? "on" : "off")}";
                var captured = follow;
                Measure(id, $"③ log volume {lines} (follow {(captured ? "on" : "off")})", () => LogVolume(lines, captured), scenarios);
            }
        }
        Measure("log-steady-state", "③ log steady state at capacity", LogSteadyState, scenarios);
        Measure("log-virtualization", "③ log virtualization (viewport, not capacity)", LogVirtualization, scenarios);
        Measure("log-follow-behaviour", "③ log follow behaviour (pinned / scrolled up / resumes)", LogFollowBehaviour, scenarios);
        Measure("log-follow-races", "③ log follow races (resume→pause / detach→reattach / DataContext switch)", FollowRaceGuards, scenarios);
        Measure("log-follow-reflow", "③ log follow through mixed-height reflow and resize", FollowReflowGuards, scenarios);
        Measure("log-viewport-behaviour", "③ log viewport behaviour (wheel / keyboard / jumps / anchor / filter / resize / template)", LogViewportBehaviour, scenarios);
        Measure("log-viewport-unit", "③ log viewport unit (offset & anchor in isolation, variable row heights)", LogViewportUnit, scenarios);
        Measure("log-soak", "③ log soak (2,000 appends: realization / cache / pool / subscriptions stay bounded)", LogSoak, scenarios);
        Measure("log-viewport-padding-reset", "③ log viewport padding & content-reset regressions", LogViewportPaddingAndReset, scenarios);
        Measure("virtualized-reference", "③ virtualized reference (synthetic, for the suggestion only)", LogListVirtualizationReference, scenarios);
        Measure("listbox-reference", "③ listbox reference (synthetic: do containers recycle on head eviction?)", LogListListBoxReference, scenarios);
        Measure("product-template-reference", "③ product-template reference (is the row template the cost?)", ProductTemplateReference, scenarios);
        Measure("repeated-navigation", "④ repeated navigation 50×", () => RepeatedNavigation(50), scenarios);
        Measure("interface-settings-interactions", "⑤ interface settings palette interactions", InterfaceSettingsInteractions, scenarios);
        Measure("shell-lifecycle-retention", "⑥ shell lifecycle retention (attach / close / re-attach)", ShellLifecycleRetention, scenarios);

        var report = new Dictionary<string, object?>
        {
            ["schema"] = Schema,
            ["generated_at"] = DateTimeOffset.Now.ToString("O"),
            ["environment"] = EnvironmentReport(),
            ["notes"] = notes,
            ["invariants"] = Invariants,
            ["scenarios"] = scenarios,
        };

        Directory.CreateDirectory(output);
        var stamp = DateTime.Now.ToString("yyyyMMdd'T'HHmmss");
        var jsonPath = Path.Combine(output, $"ui-perf-{stamp}.json");
        var textPath = Path.Combine(output, $"ui-perf-{stamp}.txt");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(textPath, Describe(report));
        Console.WriteLine($"OK: ui performance baseline recorded — {scenarios.Count} scenarios, schema {Schema}");
        Console.WriteLine($"OK: raw report {jsonPath}");
        Console.WriteLine($"OK: raw report {textPath}");

        var failed = Invariants.Where(entry => entry["pass"] is false).ToList();
        Console.WriteLine($"OK: invariants {Invariants.Count - failed.Count}/{Invariants.Count} passed");
        if (failed.Count == 0) return;
        foreach (var entry in failed) Console.WriteLine($"FAIL: {entry["id"]} — {entry["detail"]}");
        throw new Exception($"{failed.Count} structural invariant(s) failed: "
            + string.Join(", ", failed.Select(entry => entry["id"])));
    }

    /// <summary>③ 日志列表虚拟化：实现控件随**视口**而不是随 400 行容量增长。</summary>
    private static Dictionary<string, object?> LogVirtualization()
    {
        var view = new MainView(new MemoryThemeStore());
        var window = Show(view);
        try
        {
            var overview = OpenLogs(view);
            var list = LogList(view);
            var scroller = LogScroll(view);
            for (var i = 0; i < OverviewViewModel.LogCapacity; i++) overview.AppendLog($"{LogPagePrefixes[0]} fill {i}: {LogPagePrefixes[3]}");
            Pump();

            var total = overview.VisibleLogs.Count;
            var extentAtCap = scroller.Extent.Height;
            var viewportAtDefault = scroller.Viewport.Height;
            var textblocksAtDefault = list.GetVisualDescendants().OfType<TextBlock>().Count();
            var rowsAtDefault = textblocksAtDefault / 4.0;              // 每行模板 4 个 TextBlock
            var rowHeight = total > 0 && extentAtCap > 0 ? extentAtCap / total : 18;
            var viewportRows = viewportAtDefault / rowHeight;
            var byHeight = new List<Dictionary<string, object?>>();
            foreach (var height in new[] { 520.0, WindowHeight, 1400.0 })
            {
                window.Height = height;
                Pump();
                byHeight.Add(new Dictionary<string, object?>
                {
                    ["window_height"] = height,
                    ["log_viewport_height"] = Round(scroller.Viewport.Height),
                    ["realized_controls"] = Realized(list),
                    ["realized_textblocks"] = list.GetVisualDescendants().OfType<TextBlock>().Count(),
                });
            }
            window.Height = WindowHeight;
            Pump();

            var atDefault = Realized(list);
            var positions = new List<Dictionary<string, object?>>();
            foreach (var (name, offset) in new (string, double)[]
                     {
                         ("top", 0),
                         ("middle", Math.Max(0, (scroller.Extent.Height - scroller.Viewport.Height) / 2)),
                         ("bottom", Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height)),
                     })
            {
                scroller.Offset = new Vector(0, offset);
                Pump();
                positions.Add(new Dictionary<string, object?>
                {
                    ["position"] = name,
                    ["realized_controls"] = Realized(list),
                    ["realized_textblocks"] = list.GetVisualDescendants().OfType<TextBlock>().Count(),
                });
            }
            var bound = Realized(list);
            var highest = Math.Max(atDefault, positions.Max(entry => (int)entry["realized_controls"]!));

            // 容量无关性：同一个窗口下把行数从 400 降到 100，实现控件数应当基本不变（只跟视口有关）。
            overview.ClearCommand.Execute(null);
            for (var i = 0; i < 100; i++) overview.AppendLog($"{LogPagePrefixes[0]} few {i}: {LogPagePrefixes[3]}");
            Pump();
            var atHundredRows = Realized(list);

            Invariant("log-list-realizes-viewport-rows",
                rowsAtDefault <= viewportRows + 6,
                $"400 行时实现 {rowsAtDefault:F0} 行，视口只放得下 {viewportRows:F1} 行（容差 = 缓存策略 Overscan + 余量）");
            Invariant("log-list-realized-independent-of-item-count",
                atHundredRows <= atDefault * 1.25,
                $"同一个窗口：400 行实现 {atDefault} 个控件、100 行实现 {atHundredRows} 个（期望相当，说明不随容量增长）");
            Invariant("log-list-realized-grows-with-viewport",
                (int)byHeight[^1]["realized_controls"]! > (int)byHeight[0]["realized_controls"]!,
                $"窗口 520 → 1400 的实现控件数 {(int)byHeight[0]["realized_controls"]!} → {(int)byHeight[^1]["realized_controls"]!}");
            Invariant("log-list-scroll-stays-bounded",
                highest <= atDefault * 1.25,
                $"滚动到顶/中/底后实现控件最多 {highest}（默认窗口下 {atDefault}，说明滚动是回收复用而不是继续实例化）");

            return new Dictionary<string, object?>
            {
                ["id"] = "log-virtualization",
                ["scenario"] = "日志列表虚拟化：400 行（上限）时实现控件随视口高度变化，滚动位置与行数都不改变量级",
                ["capacity_rows"] = total,
                ["realized_controls"] = atDefault,
                ["realized_textblocks"] = textblocksAtDefault,
                ["realized_rows"] = Math.Round(rowsAtDefault, 1),
                ["viewport_rows"] = Math.Round(viewportRows, 1),
                ["row_height_px"] = Math.Round(rowHeight, 2),
                ["realized_controls_with_100_rows"] = atHundredRows,
                ["by_window_height"] = byHeight,
                ["by_scroll_position"] = positions,
                ["bound_realized_controls"] = bound,
                ["samples"] = 1,
                ["method"] = "真实 MainView 打开日志视图 → 填满 400 行 → 依次改窗口高度、再依次滚到顶/中/底，"
                    + "每次都泵一帧后数 LogList 里实际存在的控件；最后清空并只留 100 行，验证实现控件数不随行数增长",
            };
        }
        finally
        {
            Close(window);
        }
    }

    /// <summary>③ 跟随行为：只在贴最新端时跟随；上翻不被拉回；显式恢复跟随时回到最新。</summary>
    private static Dictionary<string, object?> LogFollowBehaviour()
    {
        var view = new MainView(new MemoryThemeStore());
        var window = Show(view);
        try
        {
            var overview = OpenLogs(view);
            var scroller = LogScroll(view);
            for (var i = 0; i < 120; i++) overview.AppendLog($"{LogPagePrefixes[2]} follow {i}: {LogPagePrefixes[3]}");
            Pump();

            double BottomGap() => Round(scroller.Extent.Height - scroller.Viewport.Height - scroller.Offset.Y);
            double TopGap() => Round(scroller.Offset.Y);

            var pinnedAfterFill = BottomGap();
            Invariant("follow-pinned-scrolls-to-newest",
                Math.Abs(pinnedAfterFill) <= 2,
                $"贴底追加 120 行后距底 {pinnedAfterFill}px（0 表示一直跟到最新）");

            // 行为底线：贴底追加后，**最新一行必须真的被实现出来**（只量分配不看结果，会把"没刷新"当成快）。
            overview.AppendLog($"{LogPagePrefixes[1]} newest-visible-check");
            Pump();
            var newestMessage = overview.VisibleLogs[^1].Message;
            var realizedNewest = LogList(view).GetVisualDescendants().OfType<TextBlock>()
                .Any(text => text.Text == newestMessage);
            Invariant("follow-realizes-newest-row", realizedNewest,
                "贴底追加后最新一行出现在已实现的行里");

            // 模拟用户向上翻：直接改 Offset（真实滚动条拖动在离屏下等价于此）。
            scroller.Offset = new Vector(0, 20);
            Pump();
            var scrolledUpOffset = scroller.Offset.Y;
            overview.AppendLog("上翻后追加 1");
            Pump();
            var afterScrolledUpAppend = scroller.Offset.Y;
            Invariant("follow-scroll-up-is-not-yanked",
                Math.Abs(afterScrolledUpAppend - scrolledUpOffset) <= 0.5,
                $"用户停在 {scrolledUpOffset} 时追加一行，视口变成 {afterScrolledUpAppend}（应当不动）");

            // 用户自己滚回贴底：新的追加应恢复跟随。
            scroller.Offset = new Vector(0, Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height));
            Pump();
            overview.AppendLog("贴底后追加 1");
            Pump();
            var resumedGap = BottomGap();
            Invariant("follow-resumes-after-user-returns-to-bottom",
                Math.Abs(resumedGap) <= 2,
                $"用户滚回贴底后追加一行，距底 {resumedGap}px");

            // 暂停跟随 + 用户停在列表中间：追加不应移动视口（与既有 Program.cs 跟随检查同一语义）。
            overview.IsFollowing = false;
            Pump();
            scroller.Offset = new Vector(0, Math.Max(0, (scroller.Extent.Height - scroller.Viewport.Height) / 2));
            Pump();
            var pausedOffset = scroller.Offset.Y;
            overview.AppendLog("暂停跟随追加 1");
            Pump();
            var afterPausedAppend = scroller.Offset.Y;
            Invariant("follow-paused-does-not-move",
                Math.Abs(afterPausedAppend - pausedOffset) <= 0.5,
                $"暂停跟随且停在中间时追加一行，视口 {pausedOffset} → {afterPausedAppend}");

            // 观察项（不作为断言）：暂停时视口正好贴底，追加会让 Extent 变长，ScrollViewer 自己把 Offset
            // 顶到新的底部——这是框架的贴边行为，改前改后都一样，记下来免得被误读成本次修复的回归。
            scroller.Offset = new Vector(0, Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height));
            Pump();
            var pausedAtEndBefore = scroller.Offset.Y;
            overview.AppendLog("暂停跟随且贴底追加 1");
            Pump();
            var pausedAtEndAfter = scroller.Offset.Y;

            // 显式重新打开跟随（用户的动作）：回到最新一行。
            scroller.Offset = new Vector(0, 0);
            Pump();
            overview.IsFollowing = true;
            Pump();
            var explicitResumeGap = BottomGap();
            Invariant("follow-explicit-resume-returns-to-newest",
                Math.Abs(explicitResumeGap) <= 2,
                $"在顶部重新打开跟随，距底 {explicitResumeGap}px（0 表示回到最新）");

            // 倒序：最新一行在顶部，贴顶时应保持在最新端。
            overview.IsDescending = true;
            Pump();
            scroller.Offset = new Vector(0, 0);
            Pump();
            overview.AppendLog("倒序追加 1");
            Pump();
            var descendingTopGap = TopGap();
            overview.IsDescending = false;
            Pump();
            Invariant("follow-descending-keeps-newest-edge",
                descendingTopGap <= 2,
                $"倒序（最新在顶部）贴顶追加后距顶 {descendingTopGap}px");

            return new Dictionary<string, object?>
            {
                ["id"] = "log-follow-behaviour",
                ["scenario"] = "日志跟随：贴最新端才跟随、上翻不拉回、暂停不动、显式恢复回到最新、倒序看顶部",
                ["pinned_after_fill_bottom_gap_px"] = pinnedAfterFill,
                ["scrolled_up_offset_px"] = scrolledUpOffset,
                ["after_scrolled_up_append_offset_px"] = afterScrolledUpAppend,
                ["resumed_bottom_gap_px"] = resumedGap,
                ["paused_offset_px"] = pausedOffset,
                ["after_paused_append_offset_px"] = afterPausedAppend,
                ["paused_at_end_observation"] = $"暂停且贴底时：{pausedAtEndBefore} → {pausedAtEndAfter}（框架随 Extent 变长贴住底部，非本次改动引入）",
                ["explicit_resume_bottom_gap_px"] = explicitResumeGap,
                ["descending_top_gap_px"] = descendingTopGap,
                ["samples"] = 1,
                ["method"] = "真实 MainView 打开日志视图填 120 行；用 Offset 模拟用户上翻/回底，再追加行并读 Offset 与 Extent/Viewport 的差",
            };
        }
        finally
        {
            Close(window);
        }
    }

    /// <summary>
    /// ⓪ 测量口径校准：所有交互数字都含一次 <c>Pump()</c>，而 <c>Pump()</c> = 跑派发队列 +
    /// 一次离屏渲染 + 再跑队列。这里单独量出"只跑逻辑""只渲染"的成本，读其他场景时可以据此扣减，
    /// 避免把测量框架的渲染成本算到界面代码头上。
    /// </summary>
    private static Dictionary<string, object?> PumpCalibration()
    {
        var view = new MainView(new MemoryThemeStore());
        var window = Show(view);
        try
        {
            for (var i = 0; i < 3; i++) Pump();   // 先让布局稳定下来
            var logical = new List<double>();
            var render = new List<double>();
            var full = new List<double>();
            for (var i = 0; i < ConfidenceSamples; i++)
            {
                var watch = Stopwatch.StartNew();
                Dispatcher.UIThread.RunJobs();
                Dispatcher.UIThread.RunJobs();
                watch.Stop();
                logical.Add(watch.Elapsed.TotalMilliseconds);

                watch.Restart();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                watch.Stop();
                render.Add(watch.Elapsed.TotalMilliseconds);

                watch.Restart();
                Pump();
                watch.Stop();
                full.Add(watch.Elapsed.TotalMilliseconds);
            }

            // 一次"脏帧"的基准：改一个可见 TextBlock 的文本再泵，量出离屏渲染一帧的下限。
            var flip = view.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(block => block.IsEffectivelyVisible && !string.IsNullOrEmpty(block.Text));
            Check(flip is not null, "the shell must expose a visible TextBlock for the dirty-frame calibration");
            var dirty = new List<double>();
            for (var i = 0; i < ConfidenceSamples; i++)
            {
                flip!.Text = i % 2 == 0 ? "perf-calibration-a" : "perf-calibration-b";
                var watch = Stopwatch.StartNew();
                Pump();
                watch.Stop();
                dirty.Add(watch.Elapsed.TotalMilliseconds);
            }

            return new Dictionary<string, object?>
            {
                ["scenario"] = "测量口径校准：在静止的真实外壳上量「只跑派发队列」「只做一次离屏渲染」「完整 Pump」「一次脏帧（改文本+泵）」",
                ["idle_shell_visual_descendants"] = view.GetVisualDescendants().Count(),
                ["logical_only"] = Stats("pump-logical-only", "ms", logical),
                ["render_tick_only"] = Stats("pump-render-tick", "ms", render),
                ["full_pump"] = Stats("pump-full", "ms", full),
                ["dirty_frame"] = Stats("pump-dirty-frame", "ms", dirty),
                ["samples"] = ConfidenceSamples,
                ["note"] = "其他场景里每次交互都含一次完整 Pump；静止时它几乎为 0，所以交互数字=界面自身工作量；dirty_frame 是「一次真实布局+离屏渲染」的下限，可用于判断交互成本里有多少是离屏 Skia 软件渲染的测量成本。",
            };
        }
        finally
        {
            Close(window);
        }
    }

    /// <summary>① 冷构造：新外壳 + 显示 + 首帧，JIT 预热带外（JIT 冷的一次单记）。</summary>
    private static Dictionary<string, object?> ColdConstruction()
    {
        double firstEver = ConstructShell(out var firstControls, out var firstAllocated);
        for (var i = 0; i < 2; i++) ConstructShell(out _, out _);

        var times = new List<double>();
        var allocations = new List<double>();
        var controls = 0;
        for (var i = 0; i < ConfidenceSamples; i++)
        {
            times.Add(ConstructShell(out controls, out var allocated));
            allocations.Add(allocated);
        }

        var result = Stats("cold-construction", "ms", times);
        result["scenario"] = "冷构造：new MainView（真实外壳）+ Window.Show + 首帧布局与渲染（JIT 已预热）";
        result["allocated_bytes_median"] = Median(allocations);
        result["first_ever_ms"] = Round(firstEver);
        result["first_ever_allocated_bytes"] = firstAllocated;
        result["visual_descendants_after_first_frame"] = controls;
        result["method"] = "每根样本都新建 MainView、新建窗口、Show、跑一次 Dispatcher+渲染泵，然后关闭窗口";
        return result;
    }

    /// <summary>② 预热后页面切换：真实导航命令 + 一次布局/渲染泵，逐页各 20 根样本。</summary>
    private static Dictionary<string, object?> PageSwitching()
    {
        const int rounds = ConfidenceSamples;
        var view = new MainView(new MemoryThemeStore());
        var window = Show(view);
        try
        {
            foreach (var page in GlobalPages) Navigate(view.Model, page);

            var perPage = GlobalPages.ToDictionary(page => page, _ => new List<double>());
            for (var round = 0; round < rounds; round++)
                foreach (var page in GlobalPages)
                    perPage[page].Add(Navigate(view.Model, page));

            var all = perPage.Values.SelectMany(samples => samples).ToList();
            var result = Stats("warm-page-switching", "ms", all);
            result["scenario"] = "预热后页面切换：SelectNavCommand + 一次 Dispatcher/渲染泵（全部页面在可视树里，切换只改可见性）";
            result["rounds_per_page"] = rounds;
            result["pages"] = GlobalPages
                .Select(page => new Dictionary<string, object?>
                {
                    ["page"] = page,
                    ["samples"] = perPage[page].Count,
                    ["median"] = Median(perPage[page]),
                    ["p95"] = Percentile(perPage[page], 0.95),
                    ["min"] = Round(perPage[page].Min()),
                    ["max"] = Round(perPage[page].Max()),
                })
                .ToList();
            result["visual_descendants_after_all_pages_visited"] = view.GetVisualDescendants().Count();
            return result;
        }
        finally
        {
            Close(window);
        }
    }

    /// <summary>
    /// ③ 日志量：真实 <c>AppendLog</c> 批量追加 N 行，随后一次泵；跟随滚动开/关各测一遍。
    /// 追加按时限分段计量（填满缓存 400 行 / 超上限的其余行），并把"第一次泵（容器创建）"与
    /// "空转一次泵（列表已满时的布局成本）"分开，避免把固定成本摊进每行数字。
    /// </summary>
    private static Dictionary<string, object?> LogVolume(int lines, bool follow)
    {
        var view = new MainView(new MemoryThemeStore());
        var window = Show(view);
        try
        {
            var overview = OpenLogs(view);
            overview.IsFollowing = follow;
            var list = LogList(view);

            var fill = Math.Min(lines, OverviewViewModel.LogCapacity);
            var phases = new List<Dictionary<string, object?>>();
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            AppendRange(overview, 0, fill);
            watch.Stop();
            phases.Add(Phase("fill-to-capacity", fill, watch.Elapsed.TotalMilliseconds,
                GC.GetAllocatedBytesForCurrentThread() - allocatedBefore));

            if (lines > fill)
            {
                long overflowBefore = GC.GetAllocatedBytesForCurrentThread();
                watch.Restart();
                AppendRange(overview, fill, lines);
                watch.Stop();
                phases.Add(Phase("beyond-capacity", lines - fill, watch.Elapsed.TotalMilliseconds,
                    GC.GetAllocatedBytesForCurrentThread() - overflowBefore));
            }

            var pumpWatch = Stopwatch.StartNew();
            Pump();
            pumpWatch.Stop();
            var idleWatch = Stopwatch.StartNew();
            Pump();
            idleWatch.Stop();

            var result = new Dictionary<string, object?>
            {
                ["id"] = $"log-volume-{lines}-follow-{(follow ? "on" : "off")}",
                ["scenario"] = $"日志量 {lines} 行：真实 AppendLog 批量追加（跟随滚动 {(follow ? "开" : "关")}），分批计量",
                ["requested_lines"] = lines,
                ["phases"] = phases,
                ["allocated_bytes_total"] = phases.Sum(phase => (long)phase["allocated_bytes"]!),
                ["pump_after_append_ms"] = Round(pumpWatch.Elapsed.TotalMilliseconds),
                ["idle_pump_ms"] = Round(idleWatch.Elapsed.TotalMilliseconds),
                ["visible_logs"] = overview.VisibleLogs.Count,
                ["cached_logs"] = overview.CachedLogCount,
                ["capacity"] = OverviewViewModel.LogCapacity,
                ["realized_controls_in_log_list"] = list.GetVisualDescendants().Count(),
                ["realized_textblocks_in_log_list"] = list.GetVisualDescendants().OfType<TextBlock>().Count(),
                ["samples"] = 1,
                ["method"] = "每根样本一个全新外壳：选中实例 → 打开日志视图 → 设跟随 → 连续 AppendLog N 次（中间不泵，按填满/超限分段）→ 一次泵（容器创建）→ 再一次空转泵（满列表的布局成本）→ 读实际实现出来的控件数",
            };
            return result;
        }
        finally
        {
            Close(window);
        }
    }

    private static void AppendRange(OverviewViewModel overview, int from, int to)
    {
        for (var i = from; i < to; i++)
            overview.AppendLog($"{LogPagePrefixes[0]} line {i}: {LogPagePrefixes[3]}", i % 7 == 0 ? "WARNING" : "INFO");
    }

    private static Dictionary<string, object?> Phase(string name, int lines, double milliseconds, long allocated) => new()
    {
        ["phase"] = name,
        ["lines"] = lines,
        ["total_ms"] = Round(milliseconds),
        ["per_line_ms"] = Round(milliseconds / lines),
        ["allocated_bytes"] = allocated,
        ["allocated_bytes_per_line"] = allocated / lines,
    };

    /// <summary>③b 上限稳态：缓存已满后逐条追加，追加本身与随后的泵分开计（时间与分配都分开），20 根样本。</summary>
    private static Dictionary<string, object?> LogSteadyState()
    {
        var view = new MainView(new MemoryThemeStore());
        var window = Show(view);
        try
        {
            var overview = OpenLogs(view);
            for (var i = 0; i < OverviewViewModel.LogCapacity; i++) overview.AppendLog($"{LogPagePrefixes[2]} {i}");
            Pump();

            // 三相位：正序+跟随（默认路径）、正序+暂停（隔离滚动）、倒序+跟随（淘汰发生在尾部）。
            var (ascendingFollow, followChurn) = SteadyPhase(view, overview, "ascending-follow", follow: true, descending: false);
            var (ascendingPaused, pausedChurn) = SteadyPhase(view, overview, "ascending-no-follow", follow: false, descending: false);
            var (descendingFollow, descendingChurn) = SteadyPhase(view, overview, "descending-follow", follow: true, descending: true);
            var operations = SteadyOperationDiagnostics(view, overview);
            overview.IsDescending = false;
            overview.IsFollowing = true;
            Pump();

            var result = new Dictionary<string, object?>
            {
                ["id"] = "log-steady-state",
                ["scenario"] = $"日志上限（{OverviewViewModel.LogCapacity} 行）已满后：追加一行并泵一次（时间与分配都按追加/泵分开计；并对照正序头淘汰与倒序尾淘汰）",
                ["samples"] = ConfidenceSamples,
                ["append_only"] = ascendingFollow["append_only"],
                ["pump_after_append"] = ascendingFollow["pump_after_append"],
                ["pump_after_append_without_follow"] = ascendingPaused["pump_after_append"],
                ["allocated_bytes_total"] = (long)ascendingFollow["allocated_bytes_per_line"]! * ConfidenceSamples,
                ["allocated_bytes_per_line"] = ascendingFollow["allocated_bytes_per_line"],
                ["phases"] = new List<Dictionary<string, object?>> { ascendingFollow, ascendingPaused, descendingFollow },
                ["operations"] = operations,
                ["visible_logs"] = overview.VisibleLogs.Count,
                ["cached_logs"] = overview.CachedLogCount,
                ["realized_controls_in_log_list"] = LogList(view).GetVisualDescendants().Count(),
                ["method"] = "每个相位 20 根样本：读分配基线 → 追加一行（计时+计分配）→ 泵一次（计时+计分配）；正序淘汰 VisibleLogs[0]，倒序淘汰尾部",
            };

            // 逐样本检查容器计数，避免只检查最后一个样本而漏掉离群值。
            // 允许的容器动作只有两类：①每个真实新尾行的取用+测量；②高度估计变化时保留带的一次性伸缩。
            // 因此这里断言"每行典型值 ≤1、均值 ≤1.5、且任何样本都不新建模板"，同时把**峰值与违例样本数如实报出**，
            // 不用"只看最后一个样本"或放宽阈值来掩盖离群。
            Invariant("log-steady-per-line-container-churn",
                followChurn.Peak("rented") <= 1 && followChurn.Mean("rented") <= 1.5
                && followChurn.Peak("measured") <= 1 && followChurn.Mean("measured") <= 1.5
                && followChurn.Mean("released") <= 1.5
                && followChurn.Sum("built") == 0
                && pausedChurn.Mean("rented") <= 1.5 && pausedChurn.Mean("measured") <= 1.5
                && pausedChurn.Sum("built") == 0
                && descendingChurn.Sum("built") == 0,
                $"稳态容器计数（{ConfidenceSamples} 个样本，逐样本统计）："
                + $"正序跟随 取用 峰值{followChurn.Peak("rented")}/均值{followChurn.Mean("rented"):F2}、"
                + $"回收 峰值{followChurn.Peak("released")}/均值{followChurn.Mean("released"):F2}、"
                + $"测量 峰值{followChurn.Peak("measured")}/均值{followChurn.Mean("measured"):F2}、"
                + $"新建合计 {followChurn.Sum("built")}（违例样本 {followChurn.ViolatingSamples}/{followChurn.SampleCount}）；"
                + $"暂停 取用 峰值{pausedChurn.Peak("rented")}/均值{pausedChurn.Mean("rented"):F2}、"
                + $"测量 峰值{pausedChurn.Peak("measured")}/均值{pausedChurn.Mean("measured"):F2}"
                + $"（违例样本 {pausedChurn.ViolatingSamples}/{pausedChurn.SampleCount}）");
            return result;
        }
        finally
        {
            Close(window);
        }
    }

    /// <summary>相位独占的容器计数；坏样本不能冒充零值，未采满不能生成统计结论。</summary>
    private sealed class ChurnStatistics
    {
        private static readonly string[] Keys = { "rented", "released", "measured", "built" };
        private readonly string _phase;
        private readonly Dictionary<string, int> _peaks = new();
        private readonly Dictionary<string, int> _sums = new();
        private Dictionary<string, object?>? _lastSample;

        public ChurnStatistics(string phase) => _phase = phase;

        public int SampleCount { get; private set; }
        public int ViolatingSamples { get; private set; }

        public void Record(Dictionary<string, object?> sample)
        {
            if (SampleCount >= ConfidenceSamples)
                throw new InvalidOperationException($"{_phase}: expected exactly {ConfidenceSamples} churn samples; an extra sample was supplied.");

            // 先验证所有字段和合计，再更新状态；后面的坏字段不能留下半个样本。
            var values = new Dictionary<string, int>();
            foreach (var key in Keys)
            {
                if (!sample.TryGetValue(key, out var raw) || raw is not int number || number < 0)
                    throw new InvalidDataException($"{_phase}: churn sample {SampleCount + 1} requires a non-negative Int32 '{key}'.");
                values[key] = number;
                _ = checked(_sums.GetValueOrDefault(key) + number);
            }

            var violated = false;
            foreach (var (key, value) in values)
            {
                _peaks[key] = Math.Max(_peaks.GetValueOrDefault(key), value);
                _sums[key] = checked(_sums.GetValueOrDefault(key) + value);
                if (key == "built" ? value > 0 : value > 1) violated = true;
            }
            if (violated) ViolatingSamples++;
            _lastSample = new Dictionary<string, object?>(sample);
            SampleCount++;
        }

        public void RequireComplete()
        {
            if (SampleCount != ConfidenceSamples)
                throw new InvalidOperationException($"{_phase}: expected exactly {ConfidenceSamples} churn samples; received {SampleCount}.");
        }

        public int Peak(string key)
        {
            RequireComplete();
            return _peaks[key];
        }

        public int Sum(string key)
        {
            RequireComplete();
            return _sums[key];
        }

        public double Mean(string key) => Sum(key) / (double)SampleCount;

        public Dictionary<string, object?> LastSample()
        {
            RequireComplete();
            return new Dictionary<string, object?>(_lastSample!);
        }

        public Dictionary<string, int> Peaks()
        {
            RequireComplete();
            return new Dictionary<string, int>(_peaks);
        }

        public Dictionary<string, int> Sums()
        {
            RequireComplete();
            return new Dictionary<string, int>(_sums);
        }
    }

    /// <summary>稳态的单个相位：分别给出追加与泵的时间、分配，并记录每次泵之后的实现控件数。</summary>
    private static (Dictionary<string, object?> Report, ChurnStatistics Churn) SteadyPhase(
        MainView view, OverviewViewModel overview, string name, bool follow, bool descending)
    {
        overview.IsDescending = descending;
        overview.IsFollowing = follow;
        Pump();
        // 正序跟随看底部、倒序跟随看顶部：先把视口放到该在的一端，再测稳态追加。
        var scroller = LogScroll(view);
        var extent = scroller.Extent.Height;
        var viewport = scroller.Viewport.Height;
        if (extent > viewport) scroller.Offset = new Vector(0, descending ? 0 : extent - viewport);
        Pump();
        var list = LogList(view);
        var appendTimes = new List<double>();
        var pumpTimes = new List<double>();
        var realizedAfterPump = new List<double>();
        var measureDeltas = new List<double>();
        var realizeDeltas = new List<double>();
        var lastRealizedIndices = new List<double>();
        var churnStatistics = new ChurnStatistics(name);
        long appendAllocated = 0;
        long pumpAllocated = 0;
        for (var i = 0; i < ConfidenceSamples; i++)
        {
            var measuresBefore = list.MeasurePasses;
            var realizesBefore = list.RealizePasses;
            var builtBefore = list.RowsBuilt;
            var rentedBefore = list.RowsRented;
            var releasedBefore = list.RowsReleased;
            var measuredBefore = list.RowsMeasured;
            var resetCallsBefore = list.ResetHeightModelCalls;
            var widthMinBefore = list.MinLayoutWidth;
            var widthMaxBefore = list.MaxLayoutWidth;
            var rangeBefore = list.ReleasedOutOfRange;
            var cacheBefore = list.ReleasedByCacheLimit;
            var collectionBefore = list.ReleasedByCollectionChange;
            var resetBefore = list.ReleasedByReset;
            long before = GC.GetAllocatedBytesForCurrentThread();
            var appendWatch = Stopwatch.StartNew();
            overview.AppendLog($"{LogPagePrefixes[1]} {name} {i}: {LogPagePrefixes[3]}");
            appendWatch.Stop();
            long afterAppend = GC.GetAllocatedBytesForCurrentThread();
            var pumpWatch = Stopwatch.StartNew();
            Pump();
            pumpWatch.Stop();
            long afterPump = GC.GetAllocatedBytesForCurrentThread();
            appendTimes.Add(appendWatch.Elapsed.TotalMilliseconds);
            pumpTimes.Add(pumpWatch.Elapsed.TotalMilliseconds);
            realizedAfterPump.Add(Realized(list));
            measureDeltas.Add(list.MeasurePasses - measuresBefore);
            realizeDeltas.Add(list.RealizePasses - realizesBefore);
            var churn = new Dictionary<string, object?>
            {
                ["built"] = list.RowsBuilt - builtBefore,
                ["rented"] = list.RowsRented - rentedBefore,
                ["released"] = list.RowsReleased - releasedBefore,
                ["measured"] = list.RowsMeasured - measuredBefore,
                ["released_by_range"] = list.ReleasedOutOfRange - rangeBefore,
                ["released_by_cache"] = list.ReleasedByCacheLimit - cacheBefore,
                ["released_by_collection"] = list.ReleasedByCollectionChange - collectionBefore,
                ["released_by_reset"] = list.ReleasedByReset - resetBefore,
                ["reset_height_model_calls"] = list.ResetHeightModelCalls - resetCallsBefore,
                ["layout_width_min"] = Round(list.MinLayoutWidth),
                ["layout_width_max"] = Round(list.MaxLayoutWidth),
            };
            // 每一个正式样本都检查容器计数，并累计峰值与违例次数。
            churnStatistics.Record(churn);
            lastRealizedIndices.Add(list.LastRealizedIndex);
            appendAllocated += afterAppend - before;
            pumpAllocated += afterPump - afterAppend;
        }
        churnStatistics.RequireComplete();
        return (new Dictionary<string, object?>
        {
            ["phase"] = name,
            ["samples"] = churnStatistics.SampleCount,
            ["follow"] = follow,
            ["descending"] = descending,
            ["append_only"] = Stats($"steady-{name}-append", "ms", appendTimes),
            ["pump_after_append"] = Stats($"steady-{name}-pump", "ms", pumpTimes),
            ["realized_controls_after_pump"] = Stats($"steady-{name}-realized", "controls", realizedAfterPump),
            ["measure_passes_per_line"] = Stats($"steady-{name}-measure", "passes", measureDeltas),
            ["realize_passes_per_line"] = Stats($"steady-{name}-realize", "passes", realizeDeltas),
            ["last_realized_index"] = Stats($"steady-{name}-last-index", "index", lastRealizedIndices),
            ["churn"] = churnStatistics.LastSample(),
            ["churn_peak"] = churnStatistics.Peaks(),
            ["churn_sum"] = churnStatistics.Sums(),
            ["churn_violating_samples"] = churnStatistics.ViolatingSamples,
            ["capacity"] = overview.VisibleLogs.Count,
            ["append_allocated_bytes_per_line"] = appendAllocated / churnStatistics.SampleCount,
            ["pump_allocated_bytes_per_line"] = pumpAllocated / churnStatistics.SampleCount,
            ["allocated_bytes_per_line"] = (appendAllocated + pumpAllocated) / churnStatistics.SampleCount,
        }, churnStatistics);
    }

    /// <summary>稳态相位需要一个能拿到日志列表的重载（供 realized 计数）。</summary>

    /// <summary>
    /// ③c 合成对照（**不是产品界面，只为给"把日志列表换成虚拟化面板"这个建议一个量级**）：
    /// 同样 400 条 <see cref="LogLineViewModel"/>、同样的四列模板，只是外层换成
    /// <c>ScrollViewer + ItemsControl + VirtualizingStackPanel</c>，测实现控件数与稳态追加成本。
    /// 生产实现是 <c>OverviewView.axaml</c> 里的非虚拟化 ItemsControl（那是主 agent 的文件，我不改）。
    /// </summary>
    private static Dictionary<string, object?> LogListVirtualizationReference()
    {
        const int rows = OverviewViewModel.LogCapacity;
        var items = new ObservableCollection<LogLineViewModel>(
            Enumerable.Range(0, rows).Select(Index));
        var list = new ItemsControl
        {
            Name = "VirtualizedReferenceList",
            ItemsSource = items,
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
            ItemTemplate = new FuncDataTemplate<LogLineViewModel>((line, _) => ReferenceRow(line), supportsRecycling: true),
        };
        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = list,
        };
        var window = new Window
        {
            Width = WindowWidth,
            Height = WindowHeight,
            Content = scroller,
        };
        window.Show();
        Pump();
        try
        {
            var controlsAfterFill = list.GetVisualDescendants().Count();
            // 三个相位把成本拆开：① 加一行删一行（与产品稳态同形）② 只加不删（隔离 RemoveAt(0) 的搬移）
            // ③ 加一行删一行 + 每次都滚到底（产品里跟随打开时的形态）。
            var addRemove = RunReferencePhase(items, scroller, rows, remove: true, scrollToEnd: false);
            var addOnly = RunReferencePhase(items, scroller, rows, remove: false, scrollToEnd: false);
            var withScroll = RunReferencePhase(items, scroller, rows, remove: true, scrollToEnd: true);
            return new Dictionary<string, object?>
            {
                ["id"] = "virtualized-reference",
                ["scenario"] = "合成对照（非产品界面）：同样 400 行日志、同样四列模板，外层用 VirtualizingStackPanel，分三个相位拆成本",
                ["rows"] = rows,
                ["realized_controls"] = controlsAfterFill,
                ["realized_textblocks"] = list.GetVisualDescendants().OfType<TextBlock>().Count(),
                ["add_remove"] = addRemove,
                ["add_only"] = addOnly,
                ["add_remove_scroll_to_end"] = withScroll,
                ["samples"] = ConfidenceSamples,
                ["boundary"] = "这是测试侧的合成对照，不是产品界面，也不能当作「改完就是这个数」的承诺：样式、字体与真实数据都不同；它只说明同一数据形状下虚拟化面板与各相位的量级。",
            };
        }
        finally
        {
            window.Close();
            Pump();
        }
    }

    private static Dictionary<string, object?> RunReferencePhase(
        ObservableCollection<LogLineViewModel> items, ScrollViewer scroller, int rows, bool remove, bool scrollToEnd)
    {
        var appendTimes = new List<double>();
        var pumpTimes = new List<double>();
        var allocations = new List<long>();
        for (var i = 0; i < ConfidenceSamples; i++)
        {
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var appendWatch = Stopwatch.StartNew();
            items.Add(Index(rows * 10 + i));
            if (remove && items.Count > rows) items.RemoveAt(0);
            appendWatch.Stop();
            var pumpWatch = Stopwatch.StartNew();
            if (scrollToEnd)
            {
                scroller.ScrollToEnd();
                Dispatcher.UIThread.Post(() => scroller.ScrollToEnd(), DispatcherPriority.Loaded);
            }
            Pump();
            pumpWatch.Stop();
            appendTimes.Add(appendWatch.Elapsed.TotalMilliseconds);
            pumpTimes.Add(pumpWatch.Elapsed.TotalMilliseconds);
            allocations.Add(GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
        }
        return new Dictionary<string, object?>
        {
            ["remove_oldest"] = remove,
            ["scroll_to_end"] = scrollToEnd,
            ["rows_after_phase"] = items.Count,
            ["append_only"] = Stats("virtualized-reference-append", "ms", appendTimes),
            ["pump_after_append"] = Stats("virtualized-reference-pump", "ms", pumpTimes),
            ["allocated_bytes_per_line"] = allocations.Sum() / ConfidenceSamples,
        };
    }

    /// <summary>
    /// 合成对照之三（测试侧）：同样 400 行、同样四列模板，改用 <c>ListBox</c>（自带滚动与虚拟化面板），
    /// 用来判断"头部淘汰时容器是否回收"——产品里离线预览页的日志列表就是 ListBox。
    /// 相位与 ItemsControl 对照一致：视口贴底 + 每行"加尾删头"。
    /// </summary>
    private static Dictionary<string, object?> LogListListBoxReference()
    {
        const int rows = OverviewViewModel.LogCapacity;
        var items = new ObservableCollection<LogLineViewModel>(Enumerable.Range(0, rows).Select(Index));
        var list = new ListBox
        {
            Name = "ListBoxReferenceList",
            ItemsSource = items,
            Height = 525,
            Background = null,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Focusable = false,
            ItemTemplate = new FuncDataTemplate<LogLineViewModel>((line, _) => ReferenceRow(line), supportsRecycling: true),
        };
        var window = new Window { Width = WindowWidth, Height = WindowHeight, Content = list };
        window.Show();
        Pump();
        try
        {
            var scroller = list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()
                ?? throw new Exception("ListBox did not expose its ScrollViewer");
            scroller.ScrollToEnd();
            Pump();
            var realized = list.GetVisualDescendants().OfType<TextBlock>().Count();
            var appendTimes = new List<double>();
            var pumpTimes = new List<double>();
            long appendAllocated = 0;
            long pumpAllocated = 0;
            for (var i = 0; i < ConfidenceSamples; i++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                var appendWatch = Stopwatch.StartNew();
                items.Add(Index(rows * 10 + i));
                if (items.Count > rows) items.RemoveAt(0);
                appendWatch.Stop();
                long afterAppend = GC.GetAllocatedBytesForCurrentThread();
                var pumpWatch = Stopwatch.StartNew();
                Pump();
                pumpWatch.Stop();
                long afterPump = GC.GetAllocatedBytesForCurrentThread();
                appendTimes.Add(appendWatch.Elapsed.TotalMilliseconds);
                pumpTimes.Add(pumpWatch.Elapsed.TotalMilliseconds);
                appendAllocated += afterAppend - before;
                pumpAllocated += afterPump - afterAppend;
            }
            return new Dictionary<string, object?>
            {
                ["id"] = "listbox-reference",
                ["scenario"] = "合成对照（测试侧）：ListBox + 自带虚拟化，400 行贴底、每行加尾删头",
                ["realized_textblocks"] = realized,
                ["append_only"] = Stats("listbox-reference-append", "ms", appendTimes),
                ["pump_after_append"] = Stats("listbox-reference-pump", "ms", pumpTimes),
                ["append_allocated_bytes_per_line"] = appendAllocated / ConfidenceSamples,
                ["pump_allocated_bytes_per_line"] = pumpAllocated / ConfidenceSamples,
                ["allocated_bytes_per_line"] = (appendAllocated + pumpAllocated) / ConfidenceSamples,
                ["samples"] = ConfidenceSamples,
                ["note"] = "与 virtualized-reference(ItemsControl) 同口径对比：若 ListBox 的分配量低一个数量级，说明它回收容器，可考虑换控件。",
            };
        }
        finally
        {
            window.Close();
            Pump();
        }
    }

    /// <summary>
    /// 合成对照之四（测试侧）：**把产品真实的 ItemTemplate 取下来**，套到合成列表上（同样的 400 行、
    /// 同样贴底、同样每行加尾删头），用来把"模板/行本身的重建成本"与"页面、滚动锚定的成本"分开。
    /// </summary>
    private static Dictionary<string, object?> ProductTemplateReference()
    {
        const int rows = OverviewViewModel.LogCapacity;
        var shell = new MainView(new MemoryThemeStore());
        var window = Show(shell);
        try
        {
            var overview = OpenLogs(shell);
            var productTemplate = LogList(shell).ItemTemplate;
            for (var i = 0; i < rows; i++) overview.AppendLog($"{LogPagePrefixes[0]} seed {i}: {LogPagePrefixes[3]}");
            Pump();

            var items = new ObservableCollection<LogLineViewModel>(Enumerable.Range(0, rows).Select(Index));
            var list = new ItemsControl
            {
                Name = "ProductTemplateReferenceList",
                ItemsSource = items,
                ItemTemplate = productTemplate,
                ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
            };
            var scroller = new ScrollViewer
            {
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = list,
            };
            var second = new Window { Width = WindowWidth, Height = WindowHeight, Content = scroller };
            second.Show();
            Pump();
            try
            {
                scroller.ScrollToEnd();
                Pump();
                var realizedTextblocks = list.GetVisualDescendants().OfType<TextBlock>().Count();
                var pumpTimes = new List<double>();
                long allocated = 0;
                for (var i = 0; i < ConfidenceSamples; i++)
                {
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    items.Add(Index(rows * 10 + i));
                    if (items.Count > rows) items.RemoveAt(0);
                    var watch = Stopwatch.StartNew();
                    scroller.ScrollToEnd();
                    Pump();
                    watch.Stop();
                    allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                    pumpTimes.Add(watch.Elapsed.TotalMilliseconds);
                }
                return new Dictionary<string, object?>
                {
                    ["id"] = "product-template-reference",
                    ["scenario"] = "合成对照（测试侧）：产品真实 ItemTemplate + 合成虚拟化列表，400 行贴底、每行加尾删头",
                    ["realized_textblocks"] = realizedTextblocks,
                    ["pump_after_append"] = Stats("product-template-reference-pump", "ms", pumpTimes),
                    ["allocated_bytes_per_line"] = allocated / ConfidenceSamples,
                    ["samples"] = ConfidenceSamples,
                    ["note"] = "与 listbox-reference/virtualized-reference 同口径：这一项贵就说明成本在行模板本身，"
                        + "便宜就说明成本来自产品页面的滚动锚定/其它绑定。",
                };
            }
            finally
            {
                second.Close();
                Pump();
            }
        }
        finally
        {
            Close(window);
        }
    }

    /// <summary>
    /// ③ 跟随的异步竞态：055 点名的三种情形都必须"旧回调不拉动新视口"。
    /// 全部用真实生产追加路径（AppendLog）+ 泵，观察 ScrollViewer 的 Offset 有没有被旧回调拉到最新端。
    /// </summary>
    private static Dictionary<string, object?> FollowRaceGuards()
    {
        var view = new MainView(new MemoryThemeStore());
        var window = Show(view);
        try
        {
            var overview = OpenLogs(view);
            var scroller = LogScroll(view);
            var page = view.GetVisualDescendants().OfType<OverviewView>().Single();
            for (var i = 0; i < 120; i++) overview.AppendLog($"{LogPagePrefixes[2]} race {i}: {LogPagePrefixes[3]}");
            Pump();

            // ① 恢复跟随 → 派发前立刻暂停：排队中的恢复令牌必须作废，视口不得移动。
            overview.IsFollowing = false;
            scroller.Offset = new Vector(0, 20);
            Pump();
            var resumePauseBefore = scroller.Offset.Y;
            overview.IsFollowing = true;      // 排队一次"恢复"滚动
            overview.IsFollowing = false;     // 派发前又暂停
            Pump();                            // 回调在这里执行
            var resumePauseAfter = scroller.Offset.Y;

            // ② 离树 → 重新挂接：在途回调的代次已过期，不得拉动重新挂接后的视口。
            overview.IsFollowing = false;
            scroller.Offset = new Vector(0, 20);
            Pump();
            overview.IsFollowing = true;      // 排队
            window.Content = null;            // 离树（代次 +1）
            Pump();
            window.Content = view;            // 重新挂接（同一个 VM/view，不销毁）
            Pump();
            var detachReattachAfter = scroller.Offset.Y;

            // ③ 换 DataContext：旧模型的在途回调不得拉动新模型的视口。
            overview.IsFollowing = false;
            scroller.Offset = new Vector(0, 20);
            Pump();
            overview.IsFollowing = true;      // 旧模型排队
            var other = new OverviewViewModel(previewData: true);
            for (var i = 0; i < 120; i++) other.AppendLog($"{LogPagePrefixes[2]} other {i}");
            page.DataContext = other;         // 换数据上下文（代次 +1）
            Pump();
            var switchedAfter = scroller.Offset.Y;
            page.DataContext = overview;      // 还原
            Pump();

            // ④ 重新挂接后跟随必须恢复：贴底追加一行应重新滚到最新（离树只失效回调，不废掉订阅）。
            overview.IsFollowing = true;
            scroller.Offset = new Vector(0, Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height));
            Pump();
            overview.AppendLog($"{LogPagePrefixes[2]} after-reattach");
            Pump();
            var afterReattachFollowGap = Round(scroller.Extent.Height - scroller.Viewport.Height - scroller.Offset.Y);

            Invariant("follow-restored-after-reattach",
                Math.Abs(afterReattachFollowGap) <= 2,
                $"重新挂接后贴底追加一行，距底 {afterReattachFollowGap}px（0 表示跟随已恢复）");

            Invariant("follow-resume-then-pause-does-not-scroll",
                Math.Abs(resumePauseAfter - resumePauseBefore) <= 0.5,
                $"恢复跟随→派发前暂停：视口 {resumePauseBefore} → {resumePauseAfter}（应不动）");
            Invariant("follow-stale-callback-after-reattach-does-not-scroll",
                detachReattachAfter <= 25,
                $"离树→重新挂接后，在途回调未拉动视口（Offset={detachReattachAfter}，起点 20）");
            Invariant("follow-stale-callback-after-datacontext-switch-does-not-scroll",
                switchedAfter <= 25,
                $"换 DataContext 后，旧模型回调未拉动新视口（Offset={switchedAfter}，起点 20）");

            return new Dictionary<string, object?>
            {
                ["id"] = "log-follow-races",
                ["scenario"] = "跟随的异步竞态：恢复→立刻暂停 / 离树→重新挂接 / 换 DataContext，旧回调都不得拉动新视口",
                ["resume_then_pause_offset_before_px"] = resumePauseBefore,
                ["resume_then_pause_offset_after_px"] = resumePauseAfter,
                ["detach_reattach_offset_after_px"] = detachReattachAfter,
                ["datacontext_switch_offset_after_px"] = switchedAfter,
                ["after_reattach_follow_bottom_gap_px"] = afterReattachFollowGap,
                ["samples"] = 1,
                ["method"] = "真实 MainView + 生产 AppendLog；每次先把视口放到 20px（非贴边）再制造竞态，泵一帧后读 Offset；"
                    + "若旧回调生效，视口会被拉到列表末尾（数百 px）。",
            };
        }
        finally
        {
            Close(window);
        }
    }

    /// <summary>混合行高与缩放后的真实跟随；样本循环不直接滚动，避免掩盖产品跟随失效。</summary>
    private static Dictionary<string, object?> FollowReflowGuards()
    {
        var view = new MainView(new MemoryThemeStore());
        var window = Show(view);
        try
        {
            // 使用短实例名，为窄屏正文保留非零宽度，确保这里真的测到换行。
            view.Model.SelectInstance("demo-main");
            var overview = view.Model.Overview;
            overview.ShowLogsCommand.Execute(null);
            overview.ClearCommand.Execute(null);
            Pump();
            var scroller = LogScroll(view);
            var viewport = LogList(view);
            var sequence = 0;
            string Message() => (sequence++ % 4) switch
            {
                0 => $"[{sequence}] 短日志",
                1 => $"[{sequence}] 正在执行自动化任务并更新运行状态，等待下一阶段的识别结果。",
                2 => $"[{sequence}] " + string.Concat(Enumerable.Repeat("这是一条需要在窄屏中换行的混合中文日志，包含识别状态、任务详情与运行进度。", 4)),
                _ => $"[{sequence}] INFO step=ready / runtime checkpoint / recognition complete / mixed-width measurement",
            };
            double Gap() => scroller.Extent.Height - scroller.Viewport.Height - scroller.Offset.Y;
            bool TailVisible()
            {
                var row = viewport.GetVisualChildren().OfType<Control>()
                    .FirstOrDefault(control => ReferenceEquals(control.DataContext, overview.VisibleLogs[^1]));
                return row?.TranslatePoint(default, scroller) is { } origin
                    && origin.Y < scroller.Bounds.Height - scroller.Padding.Bottom
                    && origin.Y + row.Bounds.Height > scroller.Padding.Top;
            }
            void Resume()
            {
                overview.IsFollowing = false;
                overview.IsFollowing = true;
                Pump();
            }
            for (var i = 0; i < OverviewViewModel.LogCapacity; i++) overview.AppendLog(Message());
            Pump();

            var phases = new List<Dictionary<string, object?>>();
            foreach (var width in new[] { 1280, 390 })
            {
                window.Width = width;
                Pump();
                Resume();
                var tailAlwaysVisible = TailVisible();
                var maximumGap = Math.Abs(Gap());
                for (var i = 0; i < ConfidenceSamples; i++)
                {
                    overview.AppendLog(Message());
                    Pump();
                    tailAlwaysVisible &= TailVisible();
                    maximumGap = Math.Max(maximumGap, Math.Abs(Gap()));
                }
                var times = new List<double>();
                var allocations = new List<double>();
                var builtBefore = viewport.RowsBuilt;
                var rentedBefore = viewport.RowsRented;
                var measuredBefore = viewport.RowsMeasured;
                var maximumRows = 0;
                for (var i = 0; i < ConfidenceSamples; i++)
                {
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    var started = Stopwatch.GetTimestamp();
                    overview.AppendLog(Message());
                    Pump();
                    times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    allocations.Add(GC.GetAllocatedBytesForCurrentThread() - before);
                    tailAlwaysVisible &= TailVisible();
                    maximumGap = Math.Max(maximumGap, Math.Abs(Gap()));
                    maximumRows = Math.Max(maximumRows, viewport.RealizedRowCount);
                }
                Invariant($"follow-reflow-{width}-keeps-tail",
                    tailAlwaysVisible && maximumGap <= 2,
                    $"宽度 {width}：恢复与 40 次混合行追加均显示尾行={tailAlwaysVisible}，最大底部间距 {maximumGap:F1}px");
                Invariant($"follow-reflow-{width}-bounded-realization",
                    maximumRows > 0 && maximumRows < OverviewViewModel.LogCapacity,
                    $"宽度 {width}：最多实现 {maximumRows}/{OverviewViewModel.LogCapacity} 行");
                phases.Add(new Dictionary<string, object?>
                {
                    ["width"] = width,
                    ["samples"] = ConfidenceSamples,
                    ["append_and_pump"] = Stats($"reflow-{width}", "ms", times),
                    ["allocated_bytes"] = Stats($"reflow-{width}-allocation", "bytes", allocations),
                    ["maximum_bottom_gap_px"] = Round(maximumGap),
                    ["maximum_realized_rows"] = maximumRows,
                    ["built"] = viewport.RowsBuilt - builtBefore,
                    ["rented"] = viewport.RowsRented - rentedBefore,
                    ["measured"] = viewport.RowsMeasured - measuredBefore,
                });
                overview.IsFollowing = false;
                for (var i = 0; i < ConfidenceSamples; i++) { overview.AppendLog(Message()); Pump(); }
                Invariant($"follow-reflow-{width}-pause-stays-away", Gap() > 2,
                    $"宽度 {width}：贴底暂停后追加 20 行，距底 {Gap():F1}px，未强制跟随");
            }

            Resume();
            window.Width = 420;
            Pump();
            var resizePinned = TailVisible() && Math.Abs(Gap()) <= 2;
            window.Width = 1280;
            Pump();
            resizePinned &= TailVisible() && Math.Abs(Gap()) <= 2;
            Invariant("follow-reflow-resize-retains-pin", resizePinned,
                "跟随中 390→420→1280 缩放后，尾行仍在视口且距底不超过 2px");

            // 追加和用户上滚发生在同一帧：ScrollChanged 会合并 Offset/Extent 变化，仍须取消跟随。
            overview.AppendLog(Message());
            scroller.Offset = new Vector(0, scroller.Offset.Y / 2);
            Pump();
            var userScrollGap = Gap();
            overview.AppendLog(Message());
            Pump();
            Invariant("follow-reflow-user-scroll-cancels-queued-follow", userScrollGap > 2 && Gap() > 2,
                $"追加同帧上滚后距底 {userScrollGap:F1}px，再追加距底 {Gap():F1}px");

            Resume();
            overview.AppendLog(Message());
            var wheelPoint = scroller.TranslatePoint(new Point(scroller.Bounds.Width / 2, scroller.Bounds.Height / 2), window)
                ?? throw new InvalidOperationException("log scroller is not attached to the Headless window");
            window.MouseWheel(wheelPoint, new Vector(0, 3));
            Pump();
            var wheelGap = Gap();
            overview.AppendLog(Message());
            Pump();
            Invariant("follow-reflow-wheel-cancels-queued-follow", wheelGap > 2 && Gap() > 2,
                $"追加同帧滚轮上滚后距底 {wheelGap:F1}px，再追加距底 {Gap():F1}px");

            overview.IsFollowing = false;
            overview.IsFollowing = true;
            scroller.Offset = new Vector(0, scroller.Offset.Y / 2);
            Pump();
            Invariant("follow-reflow-user-scroll-cancels-resume", Gap() > 2,
                $"恢复跟随派发前上滚，距底 {Gap():F1}px，恢复令牌已取消");
            overview.IsFollowing = false;
            var pausedOffset = scroller.Offset.Y;
            overview.IsFollowing = true;
            overview.IsFollowing = false;
            Pump();
            Invariant("follow-reflow-pause-cancels-resume", Math.Abs(scroller.Offset.Y - pausedOffset) <= .5,
                $"恢复跟随派发前暂停，偏移 {pausedOffset:F1} → {scroller.Offset.Y:F1}px");
            return new Dictionary<string, object?>
            {
                ["id"] = "log-follow-reflow",
                ["scenario"] = "真实混合长短日志在宽窄缩放后的跟随、暂停与同帧上滚",
                ["phases"] = phases,
                ["method"] = "真实 MainView 与 AppendLog；400 行缓存，每种宽度先预热 20 行再计量 20 行；只用 IsFollowing 恢复，不在样本循环强制 ScrollToEnd。",
            };
        }
        finally
        {
            Close(window);
        }
    }

    /// <summary>
    /// ③ 日志视口的行为验收（不只看分配量）：滚轮、键盘翻页、跳顶/跳底、窗口缩放、
    /// 内部子元素请求滚入视口、空集与筛选、模板更换，以及"用户正在读的行被容量淘汰时锚点是否保持"。
    /// </summary>
    private static Dictionary<string, object?> LogViewportBehaviour()
    {
        var view = new MainView(new MemoryThemeStore());
        var window = Show(view);
        try
        {
            var overview = OpenLogs(view);
            var scroller = LogScroll(view);
            var viewport = LogList(view);
            for (var i = 0; i < 400; i++) overview.AppendLog($"{LogPagePrefixes[0]} behave {i}: {LogPagePrefixes[3]}");
            Pump();
            var capacity = overview.VisibleLogs.Count;

            // ① 滚轮：指针放在日志正文上滚一格，偏移变化且区间跟着换。
            scroller.ScrollToHome();
            Pump();
            var probe = viewport.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault()
                ?? throw new Exception("no realized log row to aim the wheel at");
            var wheelPoint = probe.TranslatePoint(new Point(5, 5), window)
                ?? throw new Exception("realized log row is not attached to the window");
            var wheelBefore = scroller.Offset.Y;
            window.MouseWheel(wheelPoint, new Vector(0, -3));
            Pump();
            var wheelAfter = scroller.Offset.Y;
            Invariant("viewport-wheel-scrolls", wheelAfter > wheelBefore + 1,
                $"滚轮向下滚动后偏移 {wheelBefore:F0} → {wheelAfter:F0}");

            // ② 键盘翻页：焦点在滚动容器上时 PageDown/PageUp 应按页滚动。
            scroller.Focus();
            Pump();
            var pageBefore = scroller.Offset.Y;
            window.KeyPress(Key.PageDown, RawInputModifiers.None, PhysicalKey.PageDown, null);
            window.KeyRelease(Key.PageDown, RawInputModifiers.None, PhysicalKey.PageDown, null);
            Pump();
            var pageAfter = scroller.Offset.Y;
            Invariant("viewport-keyboard-page-scrolls", pageAfter > pageBefore + 10,
                $"PageDown 后偏移 {pageBefore:F0} → {pageAfter:F0}");

            // ③ 跳顶/跳底：两端都必须真的实现出对应端的行。
            scroller.ScrollToHome();
            Pump();
            var atTopText = overview.VisibleLogs[0].Message;
            var topRealized = viewport.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == atTopText);
            scroller.ScrollToEnd();
            Pump();
            var atEndText = overview.VisibleLogs[^1].Message;
            var endRealized = viewport.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == atEndText);
            Invariant("viewport-jump-realizes-both-ends", topRealized && endRealized,
                $"跳顶实现首行={topRealized}、跳底实现末行={endRealized}");

            // ④ 锚点：**暂停跟随、停在中间阅读**时追加（头部会淘汰一行），正在读的那一行应留在原屏幕位置。
            //    贴最新端的情形不在此断言：那时按产品语义应保持贴边，行会随新行一起上移。
            overview.IsFollowing = false;
            Pump();
            scroller.ScrollToHome();
            Pump();
            var anchorScrollTarget = Math.Max(0, (scroller.Extent.Height - scroller.Viewport.Height) / 4);
            for (var step = 0; step < 40 && scroller.Offset.Y < anchorScrollTarget; step++)
            {
                window.MouseWheel(wheelPoint, new Vector(0, -3));
                Pump();
            }
            var anchorIndex = Math.Min(capacity - 1, Math.Max(0, viewport.FirstRealizedIndex) + 3);
            var anchorText = overview.VisibleLogs[anchorIndex].Message;
            double ScreenTop() => viewport.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.Text == anchorText)
                .Select(t => t.TranslatePoint(new Point(0, 0), window)?.Y ?? double.NaN)
                .DefaultIfEmpty(double.NaN).First();
            var screenBefore = ScreenTop();
            overview.AppendLog($"{LogPagePrefixes[1]} anchor-check");
            Pump();
            var screenAfter = ScreenTop();
            var anchorStillVisible = overview.VisibleLogs.Any(line => line.Message == anchorText);
            var anchorStillRealized = viewport.GetVisualDescendants().OfType<TextBlock>()
                .Any(t => t.Text == anchorText);
            Invariant("viewport-anchor-keeps-reading-position",
                !double.IsNaN(screenBefore) && !double.IsNaN(screenAfter) && Math.Abs(screenAfter - screenBefore) <= 3,
                $"追加一行后，正在读的第 {anchorIndex} 行屏幕位置 {screenBefore:F1} → {screenAfter:F1}"
                + $"（仍在缓存={anchorStillVisible}、仍在已实现集合={anchorStillRealized}、实现区间 {viewport.FirstRealizedIndex}..{viewport.LastRealizedIndex}）");
            overview.IsFollowing = true;
            Pump();

            // ⑤ 内部子元素请求滚入视口：滚动归框架，行内子元素的 BringIntoView 应当把视口带过去。
            scroller.ScrollToHome();
            Pump();
            var offsetBeforeBring = scroller.Offset.Y;
            var deep = overview.VisibleLogs.Count > 200 ? overview.VisibleLogs[200].Message : null;
            var targetRow = viewport.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(t => t.Text == deep);
            if (targetRow is null)
            {
                // 目标行还没实现出来：先把视口滚到它附近，再让它请求滚入视口。
                scroller.Offset = new Vector(0, scroller.Extent.Height / 2);
                Pump();
                targetRow = viewport.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text == deep);
            }
            targetRow?.BringIntoView();
            Pump();
            var scrolledIntoView = targetRow is not null && scroller.Offset.Y > offsetBeforeBring;
            Invariant("viewport-brings-inner-child-into-view", scrolledIntoView,
                $"行内子元素 BringIntoView 后偏移 {offsetBeforeBring:F0} → {scroller.Offset.Y:F0}px");

            // ⑥ 窗口缩放：实现控件随视口高度变化，且内容仍完整（换行文本不截断）。
            window.Height = 520;
            Pump();
            var smallControls = Realized(viewport);
            window.Height = 1400;
            Pump();
            var largeControls = Realized(viewport);
            var wraps = viewport.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.Name == "LogMessage").All(t => t.Bounds.Height > 0);
            window.Height = WindowHeight;
            Pump();
            Invariant("viewport-resize-scales-and-keeps-content",
                largeControls > smallControls && wraps,
                $"窗口 520/1400 实现控件 {smallControls}/{largeControls}，正文行高均有效={wraps}");

            // ⑦ 筛选与空集：筛选后只实现匹配行；清空后不实现任何行。
            overview.SearchText = "behave 1";
            Pump();
            var filteredText = viewport.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.Name == "LogMessage").Select(t => t.Text ?? string.Empty).ToList();
            var filteredOk = filteredText.Count > 0 && filteredText.All(text => text.Contains("behave 1"));
            overview.SearchText = string.Empty;
            overview.ClearCommand.Execute(null);
            Pump();
            var clearedControls = Realized(viewport);
            overview.ClearCommand.Execute(null);
            for (var i = 0; i < 60; i++) overview.AppendLog($"{LogPagePrefixes[0]} refill {i}");
            Pump();
            Invariant("viewport-filter-and-empty",
                filteredOk && clearedControls == 0,
                $"筛选后只实现匹配行={filteredOk}，清空后实现控件={clearedControls}");

            // ⑧ 模板更换：整批用新模板重建，不复用旧模板树。
            var productTemplate = viewport.ItemTemplate;
            viewport.ItemTemplate = new FuncDataTemplate<LogLineViewModel>((line, _) => new Border
            {
                Name = "AltRow",
                Child = new TextBlock { Text = line.Message },
            }, supportsRecycling: true);
            Pump();
            var altRows = viewport.GetVisualDescendants().OfType<Border>().Count(b => b.Name == "AltRow");
            var oldRows = viewport.GetVisualDescendants().OfType<Grid>().Count();
            Invariant("viewport-template-change-rebuilds", altRows > 0,
                $"换模板后新模板行 {altRows} 个（旧模板树残留 Grid {oldRows} 个）");

            // ⑨ 中部滚轮上/下：实际位移必须与用户意图同向，且正在读的行仍可见（058 第 1 点）。
            viewport.ItemTemplate = productTemplate;
            Pump();
            scroller.ScrollToHome();
            Pump();
            var midTarget = Math.Max(0, (scroller.Extent.Height - scroller.Viewport.Height) / 2);
            for (var step = 0; step < 60 && scroller.Offset.Y < midTarget; step++)
            {
                window.MouseWheel(wheelPoint, new Vector(0, -3));
                Pump();
            }
            var beforeUp = scroller.Offset.Y;
            var readingRow = overview.VisibleLogs[Math.Min(capacity - 1, Math.Max(0, viewport.FirstRealizedIndex) + 2)].Message;
            window.MouseWheel(wheelPoint, new Vector(0, 3));
            Pump();
            var afterUp = scroller.Offset.Y;
            window.MouseWheel(wheelPoint, new Vector(0, -3));
            Pump();
            var afterDown = scroller.Offset.Y;
            var readingVisible = viewport.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == readingRow);
            Invariant("viewport-wheel-up-and-down-follow-intent",
                afterUp < beforeUp && afterDown > afterUp && readingVisible,
                $"中段滚轮：向下 {beforeUp:F0} → 向上 {afterUp:F0} → 再向下 {afterDown:F0}px，正在读的行可见={readingVisible}");

            // ⑩ 滚动条式跳转：直接定位到目标偏移，必须落在目标且目标行已实现（058 第 1 点）。
            var jumpTarget = Math.Max(0, (scroller.Extent.Height - scroller.Viewport.Height) * 2 / 3);
            scroller.Offset = new Vector(0, jumpTarget);
            Pump();
            var jumpOffset = scroller.Offset.Y;
            var jumpFirst = Math.Max(0, viewport.FirstRealizedIndex);
            var jumpRowText = overview.VisibleLogs[Math.Min(capacity - 1, jumpFirst)].Message;
            var jumpRowVisible = viewport.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == jumpRowText);
            Invariant("viewport-scrollbar-jump-lands-on-target",
                Math.Abs(jumpOffset - jumpTarget) <= 2 && jumpRowVisible,
                $"跳转到 {jumpTarget:F0}px → 实际 {jumpOffset:F0}px，目标行（第 {jumpFirst} 行）可见={jumpRowVisible}");

            // ⑪ 反复清空/筛选后缓存不累积（058/059 第 2 点）。
            for (var round = 0; round < 10; round++)
            {
                overview.ClearCommand.Execute(null);
                Pump();
                for (var i = 0; i < 400; i++) overview.AppendLog($"{LogPagePrefixes[0]} churn {round}-{i}");
                Pump();
                overview.SearchText = "churn 1";
                Pump();
                overview.SearchText = string.Empty;
                Pump();
            }
            var churnCache = viewport.CachedHeightCount;
            var churnCapacity = overview.VisibleLogs.Count;
            var churnCacheBounded = churnCache <= churnCapacity;

            // ⑪b 059 要求的最短真实路径：挂载 → 灌满 → **离树** → 离树期间替换同一个集合的内容
            //     （面板收不到任何通知）→ 重挂接。旧对象必须**全部**可回收，且不显示旧内容。
            var detachWeak = new List<WeakReference>();
            for (var i = 0; i < 6; i++) detachWeak.Add(CaptureWeakLogRow(overview, 20 + i * 50));
            window.Content = null;                          // 离树
            Pump();
            overview.ClearCommand.Execute(null);             // 离树期间整体替换（保持非空）
            for (var i = 0; i < 400; i++) overview.AppendLog($"{LogPagePrefixes[0]} reattached {i}");
            window.Content = view;                           // 重挂接：先裁缓存，再重同步
            Pump();
            var reattachedTexts = viewport.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.Name == "LogMessage").Select(t => t.Text ?? string.Empty).ToList();
            var showsStale = reattachedTexts.Any(text => text.Contains("churn"));
            Collect();
            var released = detachWeak.Count(weak => !weak.IsAlive);
            var afterReattachCache = viewport.CachedHeightCount;
            Invariant("viewport-detached-swap-releases-all",
                churnCacheBounded && !showsStale && released == detachWeak.Count
                && afterReattachCache <= overview.VisibleLogs.Count,
                $"10 轮清空/筛选后高度缓存 {churnCache} ≤ 容量 {churnCapacity}；"
                + $"离树期间替换内容后：仍显示旧内容={showsStale}、旧对象可回收 {released}/{detachWeak.Count}、"
                + $"重挂接后高度缓存 {afterReattachCache} ≤ {overview.VisibleLogs.Count}");

            // ⑫ 切换实例：视口不能被搞坏，已实现的行必须与**当前集合**一一对应（058 第 2 点点名"换实例"）。
            //    说明：Headless 夹具没有第二个实例的日志可加载，所以这里验证的是重同步的不变量，
            //    内容整体替换的路径由 ②（Reset/清空/筛选）与 ⑪（离树清空后重挂接）覆盖。
            overview.ClearCommand.Execute(null);
            for (var i = 0; i < 400; i++) overview.AppendLog($"{LogPagePrefixes[0]} instance-a {i}");
            Pump();
            view.Model.SelectInstance("perf-fixture-2");
            Pump();
            var afterSwitchLogs = overview.VisibleLogs.Count;
            var currentMessages = overview.VisibleLogs.Select(line => line.Message).ToHashSet(StringComparer.Ordinal);
            var afterSwitchTexts = viewport.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.Name == "LogMessage").Select(t => t.Text ?? string.Empty).ToList();
            var orphans = afterSwitchTexts.Count(text => !currentMessages.Contains(text));
            var afterSwitchRows = viewport.RealizedRowCount;
            var afterSwitchCache = viewport.CachedHeightCount;
            Invariant("viewport-instance-switch-resyncs",
                orphans == 0 && afterSwitchRows <= Math.Max(2, afterSwitchLogs + 2)
                && afterSwitchCache <= Math.Max(1, afterSwitchLogs),
                $"切换实例后：可见日志 {afterSwitchLogs}、实现行 {afterSwitchRows}（视口量级）、"
                + $"不属于当前集合的已实现行 {orphans}、高度缓存 {afterSwitchCache}");

            // ⑬ 切换排序（倒序/正序）是用户动作：整体换序后视口必须落在最新端，且已实现行仍与当前集合一致。
            overview.ClearCommand.Execute(null);
            for (var i = 0; i < 400; i++) overview.AppendLog($"{LogPagePrefixes[0]} order {i}");
            Pump();
            overview.IsDescending = true;
            Pump();
            var descendingTop = scroller.Offset.Y;
            var descendingMessages = overview.VisibleLogs.Select(line => line.Message).ToHashSet(StringComparer.Ordinal);
            var descendingOrphans = viewport.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.Name == "LogMessage")
                .Count(t => !descendingMessages.Contains(t.Text ?? string.Empty));
            var newestVisible = viewport.GetVisualDescendants().OfType<TextBlock>()
                .Any(t => t.Text == overview.VisibleLogs[0].Message);
            overview.IsDescending = false;
            Pump();
            var ascendingBottomGap = scroller.Extent.Height - scroller.Viewport.Height - scroller.Offset.Y;
            Invariant("viewport-order-toggle-keeps-newest-edge",
                descendingTop <= 2 && descendingOrphans == 0 && newestVisible && Math.Abs(ascendingBottomGap) <= 2,
                $"倒序：距顶 {descendingTop:F0}px、最新行可见={newestVisible}、越界行 {descendingOrphans}；"
                + $"切回正序后距底 {ascendingBottomGap:F0}px");

            // ⑭ 产品支持的字体/字号变化路径是**主题切换**（字号由主题样式给出，没有单独的字号设置项）：
            //    切换后已实现的行必须重新测量，排布与高度模型重新一致。
            overview.ClearCommand.Execute(null);
            for (var i = 0; i < 400; i++) overview.AppendLog($"{LogPagePrefixes[0]} theme {i}: {LogPagePrefixes[3]}");
            Pump();
            var themeBefore = view.Model.CurrentTheme;
            var measuredBeforeTheme = viewport.RowsMeasured;
            view.Model.ApplyTheme(view.Model.Theme.Preference with
            {
                Theme = themeBefore is UiTheme.Light or UiTheme.LegacyLight ? UiTheme.Dark : UiTheme.Light,
            });
            Pump();
            var measuredAfterTheme = viewport.RowsMeasured;
            var themeMessages = overview.VisibleLogs.Select(line => line.Message).ToHashSet(StringComparer.Ordinal);
            var themeTexts = viewport.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.Name == "LogMessage").Select(t => t.Text ?? string.Empty).ToList();
            var themeOrphans = themeTexts.Count(text => !themeMessages.Contains(text));
            var themeBoxes = RowBoxes(viewport, window);
            var themeNoOverlap = !HasOverlap(themeBoxes);
            var themeHostTop = scroller.TranslatePoint(new Point(0, 0), window)?.Y ?? 0;
            // 可见内容区底边要去掉宿主 padding（产品 LogScroll 有 Padding="16,18"）。
            var themeContentBottom = themeHostTop + scroller.Bounds.Height - scroller.Padding.Bottom;
            var themeBottomCovered = themeBoxes.Count > 0 && themeBoxes[^1].Bottom >= themeContentBottom - 1;
            var themeCache = viewport.CachedHeightCount;
            view.Model.ApplyTheme(view.Model.Theme.Preference with { Theme = themeBefore });
            Pump();
            Invariant("viewport-theme-change-remeasures-rows",
                measuredAfterTheme > measuredBeforeTheme && themeNoOverlap && themeBottomCovered && themeOrphans == 0
                && themeCache <= overview.VisibleLogs.Count,
                $"主题切换（{themeBefore}）后：重测行数 +{measuredAfterTheme - measuredBeforeTheme}、"
                + $"不重叠={themeNoOverlap}、底边覆盖={themeBottomCovered}、越界行 {themeOrphans}、高度缓存 {themeCache}");

            return new Dictionary<string, object?>
            {
                ["id"] = "log-viewport-behaviour",
                ["scenario"] = "日志视口行为：滚轮/键盘翻页/跳顶跳底/锚点保持/子元素滚入/缩放/筛选空集/模板更换",
                ["capacity_rows"] = capacity,
                ["wheel_offset_before_px"] = wheelBefore,
                ["wheel_offset_after_px"] = wheelAfter,
                ["page_offset_before_px"] = pageBefore,
                ["page_offset_after_px"] = pageAfter,
                ["anchor_screen_before_px"] = double.IsNaN(screenBefore) ? -1 : Round(screenBefore),
                ["anchor_screen_after_px"] = double.IsNaN(screenAfter) ? -1 : Round(screenAfter),
                ["realized_controls_small_window"] = smallControls,
                ["realized_controls_large_window"] = largeControls,
                ["realized_controls_after_clear"] = clearedControls,
                ["alt_template_rows"] = altRows,
                ["legacy_template_grids_left"] = oldRows,
                ["samples"] = 1,
                ["method"] = "真实 MainView + 生产 AppendLog；滚轮用窗口 MouseWheel，键盘用 PageDown（焦点在 LogScroll），"
                    + "跳转用 ScrollToHome/ScrollToEnd，锚点用「正在读的那一行」的窗口坐标前后对比。",
            };
        }
        finally
        {
            Close(window);
        }
    }

    /// <summary>
    /// ③ 最小场景：只有 <see cref="LogViewport"/>（没有页面、没有 VM、没有跟随逻辑），
    /// 用来判定"偏移与锚点"的行为出在控件本身还是页面协作。行高刻意做成不一致（长文本换行）。
    /// </summary>
    private static Dictionary<string, object?> LogViewportUnit()
    {
        var items = new ObservableCollection<LogLineViewModel>(
            Enumerable.Range(0, 60).Select(index => new LogLineViewModel(
                "2026-01-01",
                "00:00:00",
                "INFO",
                "[unit]",
                index % 4 == 0
                    ? $"第 {index} 行：这条日志刻意写得很长，用来制造两行以上的换行文本，让行高不一致。"
                    : $"第 {index} 行：短")));
        var viewport = new LogViewport
        {
            Name = "UnitViewport",
            ItemsSource = items,
            ItemTemplate = new FuncDataTemplate<LogLineViewModel>((line, _) =>
            {
                // 必须用绑定而不是建树时写死文本：面板会复用行控件，只改 DataContext。
                var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2) };
                text.Bind(TextBlock.TextProperty, new Binding(nameof(LogLineViewModel.Message)));
                return text;
            }, supportsRecycling: true),
        };
        var scroller = new ScrollViewer { Width = 420, Height = 300, Content = viewport };
        var window = new Window { Width = 420, Height = 300, Content = scroller };
        window.Show();
        Pump();
        var extent = scroller.Extent.Height;
        var viewportHeight = scroller.Viewport.Height;
        var maxOffset = Math.Max(0, extent - viewportHeight);
        var wheelPoint = viewport.TranslatePoint(new Point(10, 10), window) ?? new Point(10, 10);

        // ① 用真实输入（滚轮）把视口挪到中段：定位必须落在中段，后面的锚点断言才有意义。
        scroller.ScrollToHome();
        Pump();
        for (var step = 0; step < 4 && scroller.Offset.Y < maxOffset / 3; step++)
        {
            window.MouseWheel(wheelPoint, new Vector(0, -3));
            Pump();
        }
        var middleOffset = scroller.Offset.Y;
        var middleFirst = viewport.FirstRealizedIndex;
        var inMiddle = middleOffset > maxOffset / 4 && middleOffset < maxOffset * 3 / 4;

        // ①b 混合高度：滚轮到大步位置后，行不重叠、视口底边被某一行覆盖（不留空白）、末行确实实现。
        window.MouseWheel(wheelPoint, new Vector(0, -3));
        Pump();
        double WindowY(Visual visual) => visual.TranslatePoint(new Point(0, 0), window)?.Y ?? double.NaN;
        var rowBoxes = RowBoxes(viewport, window);
        var noOverlap = !HasOverlap(rowBoxes);
        var scrollerBottom = WindowY(scroller) + scroller.Bounds.Height;
        var bottomCovered = rowBoxes.Count > 0 && rowBoxes[^1].Bottom >= scrollerBottom - 1;
        var lastRowRealized = viewport.LastRealizedIndex == items.Count - 1
            || viewport.LastRealizedIndex >= viewport.FirstRealizedIndex;
        Invariant("unit-mixed-heights-cover-viewport", noOverlap && bottomCovered && lastRowRealized,
            $"混合高度下：行不重叠={noOverlap}、视口底边被覆盖={bottomCovered}、实现区间 {viewport.FirstRealizedIndex}..{viewport.LastRealizedIndex}");

        // ①c 窄宽切换：行重新换行（高度变化）后仍不重叠、视口底边仍被覆盖，实现行数仍是视口量级。
        var wideControls = viewport.RealizedRowCount;
        scroller.Width = 200;
        Pump();
        var narrowControls = viewport.RealizedRowCount;
        var narrowBoxes = RowBoxes(viewport, window);
        var narrowNoOverlap = !HasOverlap(narrowBoxes);
        var narrowBottomCovered = narrowBoxes.Count > 0 && narrowBoxes[^1].Bottom >= scrollerBottom - 1;
        scroller.Width = 420;
        Pump();
        Invariant("unit-narrow-width-rewraps-and-covers",
            narrowNoOverlap && narrowBottomCovered && narrowControls > 0 && narrowControls <= wideControls * 4,
            $"窄宽（200px）后：行不重叠={narrowNoOverlap}、视口底边被覆盖={narrowBottomCovered}、"
            + $"实现行 {wideControls} → {narrowControls}");

        // ② 锚点：正在读的那一行在头部淘汰后应留在原屏幕位置。
        var anchorIndex = Math.Min(items.Count - 1, Math.Max(0, viewport.FirstRealizedIndex) + 1);
        var anchor = items[anchorIndex];
        double AnchorScreen() => viewport.GetVisualDescendants().OfType<TextBlock>()
            .Where(text => text.Text == anchor.Message)
            .Select(text => text.TranslatePoint(new Point(0, 0), window)?.Y ?? double.NaN)
            .DefaultIfEmpty(double.NaN).First();
        var anchorBefore = AnchorScreen();
        items.RemoveAt(0);
        items.Add(new LogLineViewModel("2026-01-01", "00:00:01", "INFO", "[unit]", "新来的一行"));
        Pump();
        var anchorAfter = AnchorScreen();

        // ③ 顶部插入（倒序日志"最新在顶部"）：视口在顶部时必须留在顶部，且新行要真的实现出来。
        scroller.ScrollToHome();
        Pump();
        var topBefore = scroller.Offset.Y;
        items.Insert(0, new LogLineViewModel("2026-01-01", "00:00:02", "INFO", "[unit]", "更新的行"));
        Pump();
        var topOffsetAfterInsert = scroller.Offset.Y;
        var newestInsertRealized = viewport.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "更新的行");

        // ①d 单行高于视口：必须实现该行，且视口底边仍被它覆盖（不能因为"一行占满"就留白）。
        var tallMessage = string.Join(" ", Enumerable.Repeat("这一行刻意写得非常长，用来制造高于整个视口的行。", 40));
        items[0] = new LogLineViewModel("2026-01-01", "00:00:03", "INFO", "[unit]", tallMessage);
        Pump();
        scroller.ScrollToHome();
        Pump();
        var tallBoxes = RowBoxes(viewport, window);
        var tallWindowBottom = (scroller.TranslatePoint(new Point(0, 0), window)?.Y ?? 0) + scroller.Bounds.Height;
        var tallRealized = viewport.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == tallMessage);
        var tallHeight = tallBoxes.Count > 0 ? tallBoxes[0].Bottom - tallBoxes[0].Top : 0;
        var tallCoversBottom = tallBoxes.Count > 0 && tallBoxes[0].Bottom >= tallWindowBottom - 1;
        var tallNoOverlap = !HasOverlap(tallBoxes);
        Invariant("unit-taller-than-viewport-row",
            tallRealized && tallHeight > scroller.Viewport.Height && tallCoversBottom && tallNoOverlap,
            $"高于视口的一行：已实现={tallRealized}、行高 {tallHeight:F0}px、视口 {scroller.Viewport.Height:F0}px、"
            + $"底边被覆盖={tallCoversBottom}、不重叠={tallNoOverlap}");

        // ①e 换整份 ItemsSource（绑定到另一个集合）：实现集合换成新集合、缓存按身份清理、旧集合不再被持有。
        //    "旧集合可回收"必须用**一次性集合**验证：如果测试自己还引用着旧集合，弱引用当然不会释放。
        var throwawayWeak = BindThrowawayCollection(viewport, 40);
        var swapped = new ObservableCollection<LogLineViewModel>(
            Enumerable.Range(0, 50).Select(index => new LogLineViewModel(
                "2026-01-01", "00:01:00", "INFO", "[unit-swap]", $"换集合后的第 {index} 行")));
        viewport.ItemsSource = swapped;
        Pump();
        var swapTexts = viewport.GetVisualDescendants().OfType<TextBlock>()
            .Select(text => text.Text ?? string.Empty).ToList();
        var swapMessages = swapped.Select(line => line.Message).ToHashSet(StringComparer.Ordinal);
        var swapOrphans = swapTexts.Count(text => !swapMessages.Contains(text));
        var swapCache = viewport.CachedHeightCount;
        Collect();
        var throwawayReleased = throwawayWeak.Count(weak => !weak.IsAlive);
        Invariant("unit-items-source-swap",
            swapOrphans == 0 && swapCache <= swapped.Count
            && viewport.FirstRealizedIndex >= 0 && viewport.LastRealizedIndex < swapped.Count
            && throwawayReleased == throwawayWeak.Count,
            $"换集合后：越界行 {swapOrphans}、高度缓存 {swapCache} ≤ {swapped.Count}、"
            + $"实现区间 {viewport.FirstRealizedIndex}..{viewport.LastRealizedIndex}、"
            + $"原一次性集合可回收 {throwawayReleased}/{throwawayWeak.Count}");

        window.Close();
        Pump();

        Invariant("unit-middle-offset-takes-effect", inMiddle && middleFirst > 5,
            $"滚轮定位到中段后偏移 {middleOffset:F0}px（可滚范围 {maxOffset:F0}px）、首个已实现行 {middleFirst}");
        Invariant("unit-anchor-keeps-reading-row", Math.Abs(anchorAfter - anchorBefore) <= 2,
            $"淘汰一行后，正在读的第 {anchorIndex} 行屏幕位置 {anchorBefore:F1} → {anchorAfter:F1}");
        Invariant("unit-top-edge-stays-at-top", topOffsetAfterInsert <= 1 && newestInsertRealized,
            $"顶部插入后偏移 {topBefore:F0} → {topOffsetAfterInsert:F0}px、最新行已实现={newestInsertRealized}");

        return new Dictionary<string, object?>
        {
            ["id"] = "log-viewport-unit",
            ["scenario"] = "最小场景：只有 LogViewport（无页面/VM/跟随），行高不一致",
            ["extent_height_px"] = Round(extent),
            ["viewport_height_px"] = Round(viewportHeight),
            ["middle_offset_px"] = Round(middleOffset),
            ["middle_first_index"] = middleFirst,
            ["anchor_index"] = anchorIndex,
            ["anchor_screen_before_px"] = double.IsNaN(anchorBefore) ? -1 : Round(anchorBefore),
            ["anchor_screen_after_px"] = double.IsNaN(anchorAfter) ? -1 : Round(anchorAfter),
            ["top_offset_after_insert_px"] = Round(topOffsetAfterInsert),
            ["newest_insert_realized"] = newestInsertRealized,
            ["samples"] = 1,
            ["method"] = "直接构造 LogViewport + ScrollViewer + Window（Headless），不经过页面与跟随逻辑；"
                + "行高故意不一致（每 4 行长文本换行）。",
        };
    }

    /// <summary>
    /// 把视口里已实现的行换算成窗口坐标下的矩形：按**行**归组（一个模板根算一行；
    /// 合成场景里模板根就是文本块本身）。用于断言"行不重叠""视口底边被覆盖"。
    /// </summary>
    private static List<(double Top, double Bottom)> RowBoxes(Control viewport, Window window)
    {
        return viewport.GetVisualDescendants().OfType<TextBlock>()
            .GroupBy(text => ReferenceEquals(text.Parent, viewport) ? (Visual)text : (Visual?)text.Parent ?? text)
            .Select(group => new
            {
                Top = group.Min(text => text.TranslatePoint(new Point(0, 0), window)?.Y ?? double.NaN),
                Bottom = group.Max(text => (text.TranslatePoint(new Point(0, 0), window)?.Y ?? double.NaN) + text.Bounds.Height),
            })
            .Where(box => !double.IsNaN(box.Top))
            .OrderBy(box => box.Top)
            .Select(box => (box.Top, box.Bottom))
            .ToList();
    }

    private static bool HasOverlap(List<(double Top, double Bottom)> boxes)
    {
        var previousBottom = double.NegativeInfinity;
        foreach (var box in boxes)
        {
            if (box.Top + 0.5 < previousBottom) return true;
            previousBottom = Math.Max(previousBottom, box.Bottom);
        }
        return false;
    }

    /// <summary>
    /// 在**独立栈帧**里把一次性集合绑到面板上，返回其中若干行的弱引用：本方法返回后调用方不再持有该集合，
    /// 于是只有面板可能还在引用这些行——用来验证"换数据源后旧集合不再被面板持有"。
    /// </summary>
    private static List<WeakReference> BindThrowawayCollection(LogViewport viewport, int count)
    {
        var throwaway = new ObservableCollection<LogLineViewModel>(
            Enumerable.Range(0, count).Select(index => new LogLineViewModel(
                "2026-01-01", "00:02:00", "INFO", "[unit-throwaway]", $"一次性集合第 {index} 行")));
        viewport.ItemsSource = throwaway;
        Pump();
        var weak = new List<WeakReference>();
        for (var i = 0; i < 4; i++) weak.Add(new WeakReference(throwaway[Math.Min(count - 1, i * (count / 4))]));
        return weak;
    }

    /// <summary>
    /// 取一条日志行的弱引用：单独一个方法，返回后它的栈帧就没了，
    /// 调用方不会再因为栈上的临时引用而让这条记录"看起来活着"。
    /// </summary>
    private static WeakReference CaptureWeakLogRow(OverviewViewModel overview, int index = 10) =>
        new(overview.VisibleLogs.Count > index ? overview.VisibleLogs[index] : overview.VisibleLogs[^1]);

    /// <summary>
    /// ③ 长时间追加不漂移（soak）：连续追加 2,000 行并逐行泵，比较"最先 100 行"与"最后 100 行"的
    /// 每行分配，检查实现行数、高度缓存、控件池与事件订阅在长时间运行后是否仍然有界。
    /// </summary>
    private static Dictionary<string, object?> LogSoak()
    {
        var singleton = DisconnectedInstanceSource.Instance;
        var view = new MainView(new MemoryThemeStore());
        var window = Show(view);
        try
        {
            var overview = OpenLogs(view);
            var viewport = LogList(view);
            var scroller = LogScroll(view);
            for (var i = 0; i < OverviewViewModel.LogCapacity; i++) overview.AppendLog($"{LogPagePrefixes[0]} soak-fill {i}");
            Pump();
            var viewportRows = Math.Max(1, scroller.Viewport.Height / 20);
            var handlersBefore = SingletonHandlers(singleton);

            long firstWindow = 0;
            long lastWindow = 0;
            const int windowSize = 100;
            const int total = 2000;
            for (var i = 0; i < total; i++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                overview.AppendLog($"{LogPagePrefixes[0]} soak {i}: {LogPagePrefixes[3]}");
                Pump();
                var cost = GC.GetAllocatedBytesForCurrentThread() - before;
                if (i < windowSize) firstWindow += cost;
                if (i >= total - windowSize) lastWindow += cost;
            }

            var realized = viewport.RealizedRowCount;
            var cache = viewport.CachedHeightCount;
            var pooled = viewport.PooledControlCount;
            var capacity = overview.VisibleLogs.Count;
            var handlersAfter = SingletonHandlers(singleton);
            var perLineFirst = firstWindow / windowSize;
            var perLineLast = lastWindow / windowSize;

            Invariant("soak-realization-stays-viewport-sized",
                realized <= viewportRows + 12 && realized >= 2,
                $"追加 {total} 行后实现行 {realized}（视口约 {viewportRows:F0} 行）");
            Invariant("soak-cache-and-pool-bounded",
                cache <= capacity && pooled <= viewportRows + 16,
                $"高度缓存 {cache} ≤ 容量 {capacity}、池中空闲行控件 {pooled}（视口约 {viewportRows:F0} 行）");
            Invariant("soak-per-line-cost-flat",
                perLineLast <= perLineFirst * 1.5 + 4096,
                $"每行分配：最先 {windowSize} 行 {perLineFirst}B → 最后 {windowSize} 行 {perLineLast}B");
            Invariant("soak-no-handler-growth",
                handlersAfter == handlersBefore,
                $"长时间追随后单例后端订阅 {handlersBefore} → {handlersAfter}");

            return new Dictionary<string, object?>
            {
                ["id"] = "log-soak",
                ["scenario"] = $"稳态连续追加 {total} 行（逐行泵）：实现/缓存/池/订阅是否仍然有界，每行成本是否漂移",
                ["appends"] = total,
                ["capacity_rows"] = capacity,
                ["viewport_rows_estimate"] = Round(viewportRows),
                ["realized_rows_after"] = realized,
                ["cached_heights_after"] = cache,
                ["pooled_controls_after"] = pooled,
                ["bytes_per_line_first_window"] = perLineFirst,
                ["bytes_per_line_last_window"] = perLineLast,
                ["singleton_handlers_before"] = handlersBefore,
                ["singleton_handlers_after"] = handlersAfter,
                ["samples"] = 1,
                ["method"] = "真实 MainView + 生产 AppendLog，每行后 Pump；逐行用 GC.GetAllocatedBytesForCurrentThread() 记分配。",
            };
        }
        finally
        {
            Close(window);
        }
    }

    /// <summary>
    /// 内边距与内容高度重置回归（独立成场景）：
    /// ① 滚动宿主的 <c>Padding</c> 必须换算到内容坐标——大步跳转/跳到底后，首个已实现行要覆盖宿主顶边（不留空带），
    ///    末行覆盖底边；同时保留"带 padding 的产品形态"与"不带 padding 的最小对照"。
    /// ② 清空 / 换 ItemsSource / 换 ItemTemplate 必须重置高度估计（长行不得污染新短行），Extent 回到新内容的量级。
    /// </summary>
    private static Dictionary<string, object?> LogViewportPaddingAndReset()
    {
        (int First, double TopGap, bool BottomCovered, bool LastRowVisible, double LastRowTailGap, double Extent, double Viewport, double Offset) Probe(bool padded, double? offsetOverride)
        {
            var items = new ObservableCollection<LogLineViewModel>(
                Enumerable.Range(0, 400).Select(index => new LogLineViewModel(
                    "2026-01-01", "00:00:00", "INFO", "[fixed]", $"第 {index} 行")));
            var viewport = new LogViewport
            {
                ItemsSource = items,
                ItemTemplate = new FuncDataTemplate<LogLineViewModel>((_, _) =>
                {
                    var text = new TextBlock { Height = 20 };
                    text.Bind(TextBlock.TextProperty, new Binding(nameof(LogLineViewModel.Message)));
                    return text;
                }, supportsRecycling: true),
            };
            var scroller = new ScrollViewer { Width = 420, Height = 300, Content = viewport };
            if (padded) scroller.Padding = new Thickness(16, 18);
            var window = new Window { Width = 420, Height = 300, Content = scroller };
            window.Show();
            Pump();
            // 传 null 表示"真实底部"：用 Extent/Viewport 算出来，而不是随便给一个中段偏移。
            var target = offsetOverride ?? Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
            scroller.Offset = new Vector(0, target);
            Pump();
            var hostTop = scroller.TranslatePoint(new Point(0, 0), window)?.Y ?? 0;
            var boxes = RowBoxes(viewport, window);
            var first = viewport.FirstRealizedIndex;
            var topGap = boxes.Count > 0 ? boxes[0].Top - hostTop : double.NaN;
            // 可见内容区的底边要把宿主 padding 去掉，否则带 padding 的场景会被误判为"没覆盖到底"。
            var contentBottom = hostTop + scroller.Bounds.Height - scroller.Padding.Bottom;
            var bottomCovered = boxes.Count > 0 && boxes[^1].Bottom >= contentBottom - 1;
            // 只判"已实现"不能排除末行落在 overscan 区——把尾行矩形换算到滚动宿主坐标，
            // 核对它与可视内容区（padding 之内）真的相交，并给出尾边到内容区底边的距离。
            var lastRowText = viewport.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(text => (text.Text ?? string.Empty).Contains("第 399 行"));
            var lastRowOrigin = lastRowText?.TranslatePoint(new Point(0, 0), scroller);
            var visibleTop = scroller.Padding.Top;
            var visibleBottom = scroller.Bounds.Height - scroller.Padding.Bottom;
            var lastRowTop = lastRowOrigin?.Y ?? double.NaN;
            var lastRowBottom = lastRowTop + (lastRowText?.Bounds.Height ?? 0);
            var lastRowVisible = lastRowText is not null && !double.IsNaN(lastRowTop)
                && lastRowBottom > visibleTop + 1 && lastRowTop < visibleBottom - 1;
            var lastRowTailGap = visibleBottom - lastRowBottom;
            var extent = scroller.Extent.Height;
            var viewportHeight = scroller.Viewport.Height;
            var offset = scroller.Offset.Y;
            window.Close();
            Pump();
            return (first, topGap, bottomCovered, lastRowVisible, lastRowTailGap, extent, viewportHeight, offset);
        }

        var unpadded = Probe(padded: false, offsetOverride: 2500);
        var padded = Probe(padded: true, offsetOverride: 2500);
        var paddedBottom = Probe(padded: true, offsetOverride: null);
        var unpaddedBottom = Probe(padded: false, offsetOverride: null);

        Invariant("viewport-padding-keeps-top-row-visible",
            unpadded.TopGap <= 1 && padded.TopGap <= 1 && padded.First <= unpadded.First - 1,
            $"跳转到 2500px 后：无 padding 首行顶距 {unpadded.TopGap:F1}px（首个已实现行 {unpadded.First}），"
            + $"有 padding(16,18) 首行顶距 {padded.TopGap:F1}px（首个已实现行 {padded.First}）——"
            + "两者都不留空带，且带 padding 的起始行必须比不带 padding 更早一行（坐标换算生效）");
        Invariant("viewport-padding-covers-both-edges",
            padded.BottomCovered && paddedBottom.BottomCovered && unpadded.BottomCovered && unpaddedBottom.BottomCovered,
            $"底边覆盖：有 padding 中段={padded.BottomCovered}、有 padding 底部={paddedBottom.BottomCovered}、"
            + $"无 padding 中段={unpadded.BottomCovered}、无 padding 底部={unpaddedBottom.BottomCovered}");
        Invariant("viewport-padding-bottom-row-visible",
            paddedBottom.LastRowVisible && unpaddedBottom.LastRowVisible
            && Math.Abs(paddedBottom.Offset - Math.Max(0, paddedBottom.Extent - paddedBottom.Viewport)) <= 1,
            $"真实底部（用 Extent/Viewport 计算）：Extent={paddedBottom.Extent:F0}px、Viewport={paddedBottom.Viewport:F0}px、"
            + $"Offset={paddedBottom.Offset:F0}px；尾行与可视区相交（有 padding={paddedBottom.LastRowVisible}、"
            + $"无 padding={unpaddedBottom.LastRowVisible}），尾边距内容区底边 "
            + $"{paddedBottom.LastRowTailGap:F1}px（有 padding）/ {unpaddedBottom.LastRowTailGap:F1}px（无 padding）");

        // ② 高度重置：**四条路径各自从 fresh-long 起点**（顺序执行会让后两条失去"长→短"的意义）。
        const int count = 400;

        static ObservableCollection<LogLineViewModel> LongRows() => new(
            Enumerable.Range(0, count).Select(index => new LogLineViewModel(
                "2026-01-01", "00:00:00", "INFO", "[long]",
                $"第 {index} 行：" + string.Join(" ", Enumerable.Repeat("这是一条很长的日志，用来把行撑得很高。", 12)))));

        static ObservableCollection<LogLineViewModel> ShortRows() => new(
            Enumerable.Range(0, count).Select(index => new LogLineViewModel(
                "2026-01-01", "00:00:00", "INFO", "[short]", $"第 {index} 行：短")));

        static FuncDataTemplate<LogLineViewModel> FixedTemplate(double height) =>
            new((_, _) =>
            {
                var text = new TextBlock { Height = height };
                text.Bind(TextBlock.TextProperty, new Binding(nameof(LogLineViewModel.Message)));
                return text;
            }, supportsRecycling: true);

        // 同一尺寸的全新控件对照：同样的内容与模板，从零构建，用来给"重置是否干净"一个相对误差判据。
        static double FreshControlExtent(ObservableCollection<LogLineViewModel> items, FuncDataTemplate<LogLineViewModel> template)
        {
            var viewport = new LogViewport { ItemsSource = items, ItemTemplate = template };
            var scroller = new ScrollViewer { Width = 300, Height = 300, Content = viewport };
            var window = new Window { Width = 300, Height = 300, Content = scroller };
            window.Show();
            Pump();
            var extent = scroller.Extent.Height;
            window.Close();
            Pump();
            return extent;
        }

        // 从 fresh-long 起点跑一条重置路径，返回（长行起点 Extent、路径后的 Extent、同类全新控件 Extent）。
        static (double Long, double After, double Fresh) ResetPath(string path)
        {
            var items = LongRows();
            var viewport = new LogViewport { ItemsSource = items, ItemTemplate = WrappingRowTemplate() };
            var scroller = new ScrollViewer { Width = 300, Height = 300, Content = viewport };
            var window = new Window { Width = 300, Height = 300, Content = scroller };
            window.Show();
            Pump();
            var longExtent = scroller.Extent.Height;
            switch (path)
            {
                case "clear":
                    items.Clear();
                    foreach (var line in ShortRows()) items.Add(line);
                    break;
                case "itemsource":
                    viewport.ItemsSource = ShortRows();
                    break;
                case "template":
                    viewport.ItemTemplate = FixedTemplate(15);
                    break;
                case "detach":
                    window.Content = null;                    // 离树期间替换内容：面板收不到通知
                    items.Clear();
                    foreach (var line in ShortRows()) items.Add(line);
                    window.Content = scroller;                 // 重挂接：必须重同步并按现存缓存重算账本
                    break;
            }
            Pump();
            Pump();
            var afterExtent = scroller.Extent.Height;
            window.Close();
            Pump();
            var fresh = path == "template"
                ? FreshControlExtent(ShortRows(), FixedTemplate(15))
                : FreshControlExtent(ShortRows(), WrappingRowTemplate());
            return (longExtent, afterExtent, fresh);
        }

        var clearPath = ResetPath("clear");
        var sourcePath = ResetPath("itemsource");
        var templatePath = ResetPath("template");
        var detachPath = ResetPath("detach");

        static double Relative(double value, double reference) => reference > 0 ? Math.Abs(value - reference) / reference : 1;
        var clearError = Relative(clearPath.After, clearPath.Fresh);
        var sourceError = Relative(sourcePath.After, sourcePath.Fresh);
        var templateError = Relative(templatePath.After, templatePath.Fresh);
        var detachError = Relative(detachPath.After, detachPath.Fresh);

        Invariant("viewport-content-reset-drops-stale-estimate",
            clearError <= 0.10 && sourceError <= 0.10 && clearPath.Long > clearPath.Fresh * 2 && sourcePath.Long > sourcePath.Fresh * 2,
            $"清空重填：长行 {clearPath.Long:F0}px → {clearPath.After:F0}px（同类全新控件 {clearPath.Fresh:F0}px，相对误差 {clearError:P1}）；"
            + $"换 ItemsSource：长行 {sourcePath.Long:F0}px → {sourcePath.After:F0}px（同类全新控件 {sourcePath.Fresh:F0}px，相对误差 {sourceError:P1}）");
        Invariant("viewport-content-reset-covers-template-and-detach",
            templateError <= 0.10 && detachError <= 0.10 && templatePath.Long > templatePath.Fresh * 2 && detachPath.Long > detachPath.Fresh * 2,
            $"换模板(15px)：长行 {templatePath.Long:F0}px → {templatePath.After:F0}px（同类全新控件 {templatePath.Fresh:F0}px，相对误差 {templateError:P1}）；"
            + $"离树期间换内容后重挂接：长行 {detachPath.Long:F0}px → {detachPath.After:F0}px（同类全新控件 {detachPath.Fresh:F0}px，相对误差 {detachError:P1}）");

        // ②b 短→长方向：从短行起点换成高行后，估计必须跟着涨回去。
        var shortToLongItems = ShortRows();
        var shortToLongViewport = new LogViewport { ItemsSource = shortToLongItems, ItemTemplate = WrappingRowTemplate() };
        var shortToLongScroller = new ScrollViewer { Width = 300, Height = 300, Content = shortToLongViewport };
        var shortToLongWindow = new Window { Width = 300, Height = 300, Content = shortToLongScroller };
        shortToLongWindow.Show();
        Pump();
        var shortBackExtent = shortToLongScroller.Extent.Height;
        shortToLongItems.Clear();
        foreach (var line in LongRows()) shortToLongItems.Add(line);
        Pump();
        var shortToLongExtent = shortToLongScroller.Extent.Height;
        shortToLongWindow.Close();
        Pump();
        var freshShortExtent = clearPath.Fresh;
        Invariant("viewport-content-reset-works-both-directions",
            shortBackExtent <= freshShortExtent * 1.2 && shortToLongExtent > freshShortExtent * 3,
            $"短→长：短行 {shortBackExtent:F0}px（同类全新控件 {freshShortExtent:F0}px）→ 换高行后 {shortToLongExtent:F0}px");

        // ②c 精确判据（审计方给出的 8000±1）：固定 20px 行高模板，400×2000px → Clear → 400×20px。
        var fixedItems = new ObservableCollection<LogLineViewModel>(
            Enumerable.Range(0, count).Select(index => new LogLineViewModel("", "", "", "2000", $"row-{index}")));
        var fixedViewport = new LogViewport { ItemsSource = fixedItems, ItemTemplate = HeightBoundRowTemplate() };
        var fixedScroller = new ScrollViewer { Width = 420, Height = 300, Content = fixedViewport };
        var fixedWindow = new Window { Width = 420, Height = 300, Content = fixedScroller };
        fixedWindow.Show();
        Pump();
        fixedItems.Clear();
        foreach (var index in Enumerable.Range(0, count)) fixedItems.Add(new LogLineViewModel("", "", "", "20", $"row-{index}"));
        Pump();
        Pump();
        var fixedExtent = fixedScroller.Extent.Height;
        fixedWindow.Close();
        Pump();
        Invariant("viewport-content-reset-exact-extent",
            Math.Abs(fixedExtent - 8000) <= 1,
            $"固定 20px 行高：400×2000px → Clear → 400×20px 之后 Extent={fixedExtent:F0}px（审计判据 8000±1）");
        return new Dictionary<string, object?>
        {
            ["id"] = "log-viewport-padding-reset",
            ["scenario"] = "宿主 Padding 换算后的可见行连续性（含真实底部）；四条各自 fresh-long 起点的高度重置路径",
            ["unpadded_jump_first_index"] = unpadded.First,
            ["unpadded_top_gap_px"] = double.IsNaN(unpadded.TopGap) ? -1 : Round(unpadded.TopGap),
            ["padded_jump_first_index"] = padded.First,
            ["padded_top_gap_px"] = double.IsNaN(padded.TopGap) ? -1 : Round(padded.TopGap),
            ["padded_bottom_offset_px"] = Round(paddedBottom.Offset),
            ["padded_bottom_extent_px"] = Round(paddedBottom.Extent),
            ["padded_bottom_viewport_px"] = Round(paddedBottom.Viewport),
            ["padded_bottom_last_row_visible"] = paddedBottom.LastRowVisible,
            ["padded_bottom_tail_gap_px"] = double.IsNaN(paddedBottom.LastRowTailGap) ? -1 : Round(paddedBottom.LastRowTailGap),
            ["unpadded_bottom_last_row_visible"] = unpaddedBottom.LastRowVisible,
            ["unpadded_bottom_tail_gap_px"] = double.IsNaN(unpaddedBottom.LastRowTailGap) ? -1 : Round(unpaddedBottom.LastRowTailGap),
            ["clear_path_long_px"] = Round(clearPath.Long),
            ["clear_path_after_px"] = Round(clearPath.After),
            ["clear_path_fresh_control_px"] = Round(clearPath.Fresh),
            ["clear_path_relative_error_pct"] = Round(clearError * 100),
            ["itemsource_path_after_px"] = Round(sourcePath.After),
            ["itemsource_path_relative_error_pct"] = Round(sourceError * 100),
            ["template_path_long_px"] = Round(templatePath.Long),
            ["template_path_after_px"] = Round(templatePath.After),
            ["template_path_fresh_control_px"] = Round(templatePath.Fresh),
            ["template_path_relative_error_pct"] = Round(templateError * 100),
            ["detach_path_long_px"] = Round(detachPath.Long),
            ["detach_path_after_px"] = Round(detachPath.After),
            ["detach_path_fresh_control_px"] = Round(detachPath.Fresh),
            ["detach_path_relative_error_pct"] = Round(detachError * 100),
            ["extent_short_back_px"] = Round(shortBackExtent),
            ["extent_short_to_long_px"] = Round(shortToLongExtent),
            ["extent_fixed20_after_clear_px"] = Round(fixedExtent),
            ["samples"] = 1,
            ["method"] = "固定 20px 行做 padding 对照（有/无 Padding=16,18；中段 2500 与用 Extent/Viewport 算出的真实底部）；"
                + "长文本换行行做四条独立的高度重置路径（Clear 重填 / 换 ItemsSource / 换模板 / 离树期间换内容后重挂接），"
                + "每条都从 fresh-long 起点并配一个同尺寸全新控件对照取相对误差；另含固定 20px 行高的 8000±1 精确判据。"
                + "全部原生 Headless，无可见窗口、无设备。",
        };
    }

    /// <summary>换行文本行模板（长文本撑高、短文本一行）。</summary>
    private static FuncDataTemplate<LogLineViewModel> WrappingRowTemplate() =>
        new((_, _) =>
        {
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2) };
            text.Bind(TextBlock.TextProperty, new Binding(nameof(LogLineViewModel.Message)));
            return text;
        }, supportsRecycling: true);

    /// <summary>行高由数据的 Scope 字段给出的模板（与独立审计的复验用例同构，用于精确 Extent 判据）。</summary>
    private static FuncDataTemplate<LogLineViewModel> HeightBoundRowTemplate() =>
        new((_, _) =>
        {
            var text = new TextBlock();
            text.Bind(TextBlock.TextProperty, new Binding(nameof(LogLineViewModel.Message)));
            text.Bind(TextBlock.HeightProperty, new Binding(nameof(LogLineViewModel.Scope)));
            return text;
        }, supportsRecycling: true);

    private static LogLineViewModel Index(int index) =>
        new("2026-09-24", "23:00:00", index % 7 == 0 ? "WARNING" : "INFO", "[perf]", $"{LogPagePrefixes[0]} line {index}: {LogPagePrefixes[3]}");

    private static Control ReferenceRow(LogLineViewModel line)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*") };
        grid.Children.Add(new TextBlock { Text = line.Date });
        var time = new TextBlock { Text = line.Time, Margin = new Thickness(5, 0, 0, 0) };
        Grid.SetColumn(time, 1);
        grid.Children.Add(time);
        var scope = new TextBlock { Text = line.Scope, Margin = new Thickness(6, 0, 0, 0) };
        Grid.SetColumn(scope, 2);
        grid.Children.Add(scope);
        var message = new TextBlock { Text = line.Message, Margin = new Thickness(6, 0, 0, 0), TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(message, 3);
        grid.Children.Add(message);
        return grid;
    }

    /// <summary>④ 重复导航 N 次：分配量、托管堆、可视控件数与事件订阅数量增长。</summary>
    private static Dictionary<string, object?> RepeatedNavigation(int count)
    {
        var view = new MainView(new MemoryThemeStore());
        var window = Show(view);
        try
        {
            foreach (var page in GlobalPages) Navigate(view.Model, page);

            var watched = new (string Name, object Target)[]
            {
                ("ShellViewModel", view.Model),
                ("OverviewViewModel", view.Model.Overview),
                ("HomeViewModel", view.Model.Home),
                ("RailViewModel", view.Model.Rail),
                ("InterfaceSettingsViewModel", view.Model.InterfaceSettings),
            };
            var before = HandlerCounts(watched);
            var controlsBefore = view.GetVisualDescendants().Count();

            Collect();
            long heapBefore = GC.GetTotalMemory(forceFullCollection: true);
            long threadAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            long totalAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);

            var watch = Stopwatch.StartNew();
            for (var i = 0; i < count; i++)
            {
                view.Model.SelectNavCommand.Execute(GlobalPages[i % GlobalPages.Length]);
                Pump();
            }
            watch.Stop();

            long threadAllocated = GC.GetAllocatedBytesForCurrentThread() - threadAllocatedBefore;
            long totalAllocated = GC.GetTotalAllocatedBytes(precise: true) - totalAllocatedBefore;
            Collect();
            long heapAfter = GC.GetTotalMemory(forceFullCollection: true);
            var after = HandlerCounts(watched);
            var growth = before
                .Where(entry => after.GetValueOrDefault(entry.Key) != entry.Value)
                .Select(entry => new Dictionary<string, object?>
                {
                    ["subscription"] = entry.Key,
                    ["handlers_before"] = entry.Value,
                    ["handlers_after"] = after.GetValueOrDefault(entry.Key),
                    ["delta"] = after.GetValueOrDefault(entry.Key) - entry.Value,
                })
                .ToList();

            var result = new Dictionary<string, object?>
            {
                ["scenario"] = $"重复导航 {count} 次：真实导航命令，逐次泵；比较分配、托管堆与事件订阅数量",
                ["navigations"] = count,
                ["total_ms"] = Round(watch.Elapsed.TotalMilliseconds),
                ["per_navigation_ms"] = Round(watch.Elapsed.TotalMilliseconds / count),
                ["allocated_bytes_ui_thread"] = threadAllocated,
                ["allocated_bytes_ui_thread_per_navigation"] = threadAllocated / count,
                ["allocated_bytes_process"] = totalAllocated,
                ["allocated_bytes_process_per_navigation"] = totalAllocated / count,
                ["managed_heap_before_bytes"] = heapBefore,
                ["managed_heap_after_bytes"] = heapAfter,
                ["managed_heap_delta_bytes"] = heapAfter - heapBefore,
                ["visual_descendants_before"] = controlsBefore,
                ["visual_descendants_after"] = view.GetVisualDescendants().Count(),
                ["subscription_growth"] = growth,
                ["subscriptions_checked"] = before.Count,
                ["samples"] = 1,
                ["method"] = "预热走完所有页面 → 记录订阅与堆 → 50 次轮转导航（每次一次泵）→ 再记录；订阅数用事件后备字段的调用列表长度（反射）",
            };

            Invariant("navigation-does-not-grow-subscriptions", growth.Count == 0,
                $"50 次轮转导航后事件订阅增长项 {growth.Count}（期望 0）"
                + (growth.Count == 0 ? string.Empty : "：" + string.Join(", ",
                    growth.Select(entry => $"{entry["subscription"]} {entry["handlers_before"]}→{entry["handlers_after"]}"))));
            return result;        }
        finally
        {
            Close(window);
        }
    }

    /// <summary>⑤ 界面偏好页（我负责的文件）：配色模态开关、逐字输入、Tab 循环的交互成本与订阅稳定性。</summary>
    private static Dictionary<string, object?> InterfaceSettingsInteractions()
    {
        const int cycles = 10;
        const int tabs = 12;
        var view = new MainView(new MemoryThemeStore());
        var window = Show(view);
        try
        {
            Navigate(view.Model, "interface");
            var page = view.GetVisualDescendants().OfType<InterfaceSettingsView>().Single();
            // 上游 usesPalettePreferences：配色偏好与它的弹窗只在简约/紧凑主题出现。
            // 默认主题下这一块不进可视树（ScrollViewer 的内容要等模板应用），所以先真切到简约。
            ((InterfaceSettingsViewModel)page.DataContext!).SelectedThemeId = UiThemes.ToId(UiTheme.Minimal);
            Pump();
            var open = Find<Button>(page, "AddPaletteButton");

            // 第一次打开含官方 ColorView 的模板应用成本，单独记；后面才是稳态。
            var firstOpen = Click(window, open);
            var overlay = Find<Border>(page, "PaletteDialogOverlay");
            Check(overlay.IsVisible, "a real pointer click opens the palette dialog");
            var primary = Find<ColorField>(page, "PalettePrimaryField");
            var secondary = Find<ColorField>(page, "PaletteSecondaryField");
            var firstClose = Press(window, Key.Escape, PhysicalKey.Escape);
            Check(!overlay.IsVisible, "Escape closes the palette dialog");

            var openTimes = new List<double>();
            var closeTimes = new List<double>();
            for (var i = 0; i < cycles; i++)
            {
                openTimes.Add(Click(window, open));
                Check(overlay.IsVisible, $"the palette dialog reopens (cycle {i})");
                closeTimes.Add(Press(window, Key.Escape, PhysicalKey.Escape));
                Check(!overlay.IsVisible, $"the palette dialog closes again (cycle {i})");
            }

            Click(window, open);
            var hex = primary.HexBox;
            var keystrokeTimes = new List<double>();
            long keystrokeAllocated = 0;
            for (var round = 0; round < 8; round++)
            {
                hex.Focus();
                Pump();
                hex.SelectAll();
                foreach (var character in "#1A2B3C")
                {
                    long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                    keystrokeTimes.Add(TypeText(window, character.ToString()));
                    keystrokeAllocated += GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                }
            }
            Check(!primary.IsInvalid && primary.ColorHex == "#1A2B3C",
                $"typing a valid hex keeps the field valid and syncs the colour (hex={primary.ColorHex}, invalid={primary.IsInvalid})");
            var tabTimes = new List<double>();
            for (var i = 0; i < tabs; i++) tabTimes.Add(Press(window, Key.Tab, PhysicalKey.Tab));
            Press(window, Key.Escape, PhysicalKey.Escape);
            Check(!overlay.IsVisible, "the dialog closes after the keyboard pass");

            var handlers = HandlerCounts(new (string, object)[]
            {
                ("ColorField.PalettePrimaryField", primary),
                ("ColorField.PaletteSecondaryField", secondary),
            });
            var fields = page.GetVisualDescendants().OfType<ColorField>().Count();

            // 归属：单独建一个界面偏好页（不经过整个外壳），看这一页在冷构造里占多少。
            var isolatedService = new ThemeService(new MemoryThemeStore());
            var isolatedModel = new InterfaceSettingsViewModel(isolatedService);
            long pageAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var pageWatch = Stopwatch.StartNew();
            var isolatedPage = new InterfaceSettingsView { DataContext = isolatedModel };
            var isolatedWindow = new Window { Width = WindowWidth, Height = WindowHeight, Content = isolatedPage };
            isolatedWindow.Show();
            Pump();
            pageWatch.Stop();
            long pageAllocated = GC.GetAllocatedBytesForCurrentThread() - pageAllocatedBefore;
            var isolatedControls = isolatedPage.GetVisualDescendants().Count();
            isolatedWindow.Close();
            Pump();

            return new Dictionary<string, object?>
            {
                ["id"] = "interface-settings-interactions",
                ["scenario"] = "界面偏好页：打开/关闭配色模态、逐字输入十六进制、Tab 焦点循环的交互成本（真实指针/键盘）",
                ["samples"] = cycles,
                ["first_open_ms"] = firstOpen,
                ["first_close_ms"] = firstClose,
                ["open"] = Stats("interface-palette-open", "ms", openTimes),
                ["close"] = Stats("interface-palette-close", "ms", closeTimes),
                ["keystroke"] = Stats("interface-palette-keystroke", "ms", keystrokeTimes),
                ["keystroke_allocated_bytes"] = keystrokeAllocated / keystrokeTimes.Count,
                ["tab"] = Stats("interface-palette-tab", "ms", tabTimes),
                ["color_fields_on_page"] = fields,
                ["handlers_after_10_cycles"] = handlers,
                ["isolated_page_construction_ms"] = Round(pageWatch.Elapsed.TotalMilliseconds),
                ["isolated_page_construction_allocated_bytes"] = pageAllocated,
                ["isolated_page_visual_descendants"] = isolatedControls,
                ["method"] = "页面切到「界面设置」（切到简约主题，配色偏好块才会进可视树）→ 真实指针点开模态 → Escape 关闭，重复 10 次；再逐字输入 #1A2B3C（每次击键一次泵）；再按 12 次 Tab；最后读两个取色控件的订阅数与页面上的控件实例数；末尾单独建一个同款页面量它在冷构造里的份额",
            };
        }
        finally
        {
            Close(window);
        }
    }

    /// <summary>
    /// ⑥ 外壳生命周期与单例订阅：按"逐步记账 + 确定性回归"量，而不是给一个聚合差值。
    /// 场景自己的构造/关闭动作与每个探针的增量都单独记一行，最后用真实窗口生命周期回归：
    /// 挂接恰好 +2、关闭回到基线、重新挂接再 +2、再关闭仍回基线。
    /// </summary>
    private static Dictionary<string, object?> ShellLifecycleRetention()
    {
        var singleton = DisconnectedInstanceSource.Instance;
        var steps = new List<Dictionary<string, object?>>();
        var baseline = SingletonHandlers(singleton);
        void Record(string what) => steps.Add(Step(what, baseline, SingletonHandlers(singleton)));
        Record("baseline");

        var beforePlain = SingletonHandlers(singleton);
        var plain = BuildAndClosePlainWindow();
        var plainDelta = SingletonHandlers(singleton) - beforePlain;
        Record("plain-window-closed (对照：不含产品界面)");

        var beforeModelOnly = SingletonHandlers(singleton);
        var modelOnly = BuildAndCloseModelOnlyWindow();
        var modelOnlyDelta = SingletonHandlers(singleton) - beforeModelOnly;
        Record("model-only-window-closed (只含 ShellViewModel，无 MainView)");

        var neverShown = BuildShellWithoutShowing();
        Record("shell-built-never-shown (构造但从未挂接)");

        var neverAttachedModel = BuildModelWithoutWindow();
        Record("model-built-without-window");

        var lifecycle = BuildAttachCloseReattach();
        steps.Add(Step("shell-constructed", baseline, lifecycle.AfterConstruct));
        steps.Add(Step("shell-attached (临时挂到宿主容器)", baseline, lifecycle.AfterAttach));
        steps.Add(Step("shell-detached (临时离树)", baseline, lifecycle.AfterDetach));
        steps.Add(Step("shell-reattached (同一个 view 再挂接)", baseline, lifecycle.AfterReattach));
        steps.Add(Step("shell-detached-again", baseline, lifecycle.AfterSecondDetach));
        steps.Add(Step("shell-attached-in-window (挂进窗口)", baseline, lifecycle.AfterAttachInWindow));
        steps.Add(Step("shell-window-closed", baseline, lifecycle.AfterClose));

        Collect();
        Settle();
        Collect();

        var attachedDelta = lifecycle.AfterAttach - lifecycle.Before;
        var detachedDelta = lifecycle.AfterDetach - lifecycle.Before;
        var reattachedDelta = lifecycle.AfterReattach - lifecycle.Before;
        var secondDetachedDelta = lifecycle.AfterSecondDetach - lifecycle.Before;
        var attachedInWindowDelta = lifecycle.AfterAttachInWindow - lifecycle.Before;
        var closedDelta = lifecycle.AfterClose - lifecycle.Before;

        Invariant("shell-attach-subscribes-exactly-two", attachedDelta == 2,
            $"挂接后单例 Changed 订阅增量 {attachedDelta}（期望 2：外壳 + 主页各一处）");
        Invariant("shell-detach-returns-subscriptions-to-baseline", detachedDelta == 0,
            $"临时离树后增量 {detachedDelta}（期望 0；VM 仍可复用）");
        Invariant("shell-reattach-restores-exactly-two", reattachedDelta == 2,
            $"同一个外壳重新挂接后增量 {reattachedDelta}（期望 2，且不累积）");
        Invariant("shell-second-detach-returns-to-baseline", secondDetachedDelta == 0,
            $"再次离树后增量 {secondDetachedDelta}（期望 0）");
        Invariant("shell-close-while-attached-returns-to-baseline",
            attachedInWindowDelta == 2 && closedDelta == 0,
            $"挂进窗口增量 {attachedInWindowDelta}、关闭后增量 {closedDelta}（期望 2 / 0）");
        Invariant("shell-unattached-construction-holds-no-subscription",
            plainDelta == 0 && modelOnlyDelta == 0 && neverShown.Delta == 0 && neverAttachedModel.Delta == 0,
            $"不含界面的空窗口 {plainDelta}、只含 VM 的窗口 {modelOnlyDelta}、构造未挂接的外壳 {neverShown.Delta}、只建 VM {neverAttachedModel.Delta}（期望全为 0）");
        Invariant("shell-released-is-collectable",
            !lifecycle.View.IsAlive && !lifecycle.Model.IsAlive,
            $"关闭并强制 GC 后外壳 IsAlive={lifecycle.View.IsAlive}、ShellViewModel IsAlive={lifecycle.Model.IsAlive}");

        return new Dictionary<string, object?>
        {
            ["id"] = "shell-lifecycle-retention",
            ["scenario"] = "外壳生命周期：真实窗口 Show/Close/再 Show/再 Close，逐步记录进程级单例的订阅数并做释放回归",
            ["baseline_handlers"] = baseline,
            ["steps"] = steps,
            ["probe_deltas"] = new Dictionary<string, object?>
            {
                ["plain_window"] = plainDelta,
                ["model_only_window"] = modelOnlyDelta,
                ["shell_never_shown"] = neverShown.Delta,
                ["model_without_window"] = neverAttachedModel.Delta,
            },
            ["controls"] = new Dictionary<string, object?>
            {
                ["plain_window_content_alive"] = plain.IsAlive,
                ["model_only_alive"] = modelOnly.IsAlive,
                ["never_shown_view_alive"] = neverShown.View.IsAlive,
                ["never_attached_model_alive"] = neverAttachedModel.View.IsAlive,
                ["lifecycle_view_alive"] = lifecycle.View.IsAlive,
                ["lifecycle_model_alive"] = lifecycle.Model.IsAlive,
                ["lifecycle_state_timer_enabled"] = lifecycle.TimerEnabled,
            },
            ["samples"] = 1,
            ["method"] = "每个探针都在单独方法里建立/显示/关闭，只带出 WeakReference 与各阶段订阅数；随后 Collect+Settle+Collect 再读可达性；"
                + "订阅数用事件后备字段的调用列表长度（反射），是确定值，可直接当判据；可达性只作参考（保守 GC 可能临时留住对象）。",
            ["root_cause"] = "ShellViewModel 构造期 `_backend.Changed += …` 与 HomeViewModel 构造期 `refreshable.Changed += …` 订阅到进程级单例 "
                + "DisconnectedInstanceSource.Instance（IAlasUiBackend : IRefreshableInstanceSource），且没有退订路径。"
                + "修好后订阅随挂接/离树生效：挂接 +2、关闭 -2、重新挂接 +2，不累积。",
        };
    }

    private static Dictionary<string, object?> Step(string what, int baseline, int handlers) => new()
    {
        ["step"] = what,
        ["handlers"] = handlers,
        ["delta_from_baseline"] = handlers - baseline,
    };

    private static int SingletonHandlers(object target) =>
        HandlerCounts(new (string Name, object Target)[] { ("DisconnectedInstanceSource.Instance", target) })
            .GetValueOrDefault("DisconnectedInstanceSource.Instance.Changed");

    // ── 被测界面的建立与驱动 ──────────────────────────────────────────────────────────────

    private static double ConstructShell(out int controls, out long allocated)
    {
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        var view = new MainView(new MemoryThemeStore());
        var window = new Window { Width = WindowWidth, Height = WindowHeight, Content = view };
        window.Show();
        Pump();
        watch.Stop();
        controls = view.GetVisualDescendants().Count();
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Close(window);
        return watch.Elapsed.TotalMilliseconds;
    }

    private static double Navigate(ShellViewModel model, string page)
    {
        var watch = Stopwatch.StartNew();
        model.SelectNavCommand.Execute(page);
        Pump();
        watch.Stop();
        Check(model.ActivePage == page, $"navigation to '{page}' must activate that page (ActivePage={model.ActivePage})");
        return watch.Elapsed.TotalMilliseconds;
    }

    private static OverviewViewModel OpenLogs(MainView view)
    {
        view.Model.SelectInstance("perf-fixture");
        Pump();
        view.Model.Overview.ShowLogsCommand.Execute(null);
        Pump();
        var overview = view.Model.Overview;
        Check(overview.IsLogsView, "the monitor panel must be on the logs view");
        return overview;
    }

    private static LogViewport LogList(MainView view)
    {
        var list = view.GetVisualDescendants().OfType<LogViewport>().FirstOrDefault(control => control.Name == "LogList");
        if (list is null) throw new Exception("the logs view did not realize its LogList viewport");
        return list;
    }

    /// <summary>
    /// 诊断相位（明确标注为诊断，不代表产品语义）：直接操作公开的 <c>VisibleLogs</c>，把
    /// "头部淘汰""尾部新增""两者组合""20 行批量淘汰"分开量，判断每行 8MB 到底出在哪一步。
    /// 每个样本后都把列表还原到 400 行，因此各相位可比。
    /// </summary>
    private static Dictionary<string, object?> SteadyOperationDiagnostics(MainView view, OverviewViewModel overview)
    {
        var list = LogList(view);
        var results = new List<Dictionary<string, object?>>();
        // 同一组操作分别在"视口在顶部"和"视口贴在底部"各测一遍：
        // 两者之差就是"贴底时的滚动锚定/可见页重建"成本，也是判断回收式面板能否解决问题的关键。
        foreach (var pinAtEnd in new[] { false, true })
        {
            var suffix = pinAtEnd ? "-at-bottom" : "-at-top";
            results.Add(OperatePhase(view, overview, "add-tail-only" + suffix,
                () => overview.VisibleLogs.Add(Index(9100)),
                () => overview.VisibleLogs.RemoveAt(overview.VisibleLogs.Count - 1), pinAtEnd));
            results.Add(OperatePhase(view, overview, "remove-head-only" + suffix,
                () => overview.VisibleLogs.RemoveAt(0),
                () => overview.VisibleLogs.Insert(0, Index(9000)), pinAtEnd));
            results.Add(OperatePhase(view, overview, "remove-head-and-add-tail" + suffix,
                () =>
                {
                    overview.VisibleLogs.RemoveAt(0);
                    overview.VisibleLogs.Add(Index(9200));
                },
                () =>
                {
                    overview.VisibleLogs.RemoveAt(overview.VisibleLogs.Count - 1);
                    overview.VisibleLogs.Insert(0, Index(9300));
                }, pinAtEnd));
        }
        return new Dictionary<string, object?>
        {
            ["id"] = "log-steady-operations",
            ["scenario"] = "诊断（直接操作 VisibleLogs，仅用于定位成本）：尾部新增 / 头部淘汰 / 两者组合，各自在列表顶部与贴底两种视口位置量",
            ["list_realized_controls"] = Realized(list),
            ["operations"] = results,
            ["samples"] = ConfidenceSamples,
        };
    }

    private static Dictionary<string, object?> OperatePhase(MainView view, OverviewViewModel overview, string name,
        Action operation, Action undo, bool pinAtEnd = false)
    {
        overview.IsFollowing = false;      // 只看布局成本，不掺滚动
        Pump();
        var scroller = LogScroll(view);
        var pumpTimes = new List<double>();
        var allocations = new List<long>();
        for (var i = 0; i < ConfidenceSamples; i++)
        {
            if (pinAtEnd)
            {
                scroller.Offset = new Vector(0, Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height));
                Pump();
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            operation();
            var watch = Stopwatch.StartNew();
            Pump();
            watch.Stop();
            allocations.Add(GC.GetAllocatedBytesForCurrentThread() - before);
            pumpTimes.Add(watch.Elapsed.TotalMilliseconds);
            undo();
            Pump();
        }
        return new Dictionary<string, object?>
        {
            ["operation"] = name,
            ["pinned_at_end"] = pinAtEnd,
            ["pump_after_operation"] = Stats($"op-{name}", "ms", pumpTimes),
            ["allocated_bytes_total"] = allocations.Sum() / ConfidenceSamples,
            ["realized_controls"] = Realized(LogList(view)),
        };
    }

    private static ScrollViewer LogScroll(MainView view)
    {
        var scroller = view.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(control => control.Name == "LogScroll");
        if (scroller is null) throw new Exception("the logs view did not realize its LogScroll ScrollViewer");
        return scroller;
    }

    /// <summary>日志列表里**实际存在**的控件数：虚拟化成不成，看的是这个数。</summary>
    private static int Realized(Control list) => list.GetVisualDescendants().Count();

    /// <summary>
    /// 被测对象：真实外壳走完整生命周期——构造 → 挂接 → 关闭 → **重新挂接同一个 view** → 再关闭，
    /// 每个阶段都读一次进程级单例的订阅数。整个方法不可内联，方法返回后局部引用即出栈，
    /// 调用方才能用 WeakReference 判定"释放后是否可回收"。
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ShellLifecycleProbe BuildAttachCloseReattach()
    {
        var singleton = DisconnectedInstanceSource.Instance;
        var before = SingletonHandlers(singleton);
        var view = new MainView(new MemoryThemeStore());
        var afterConstruct = SingletonHandlers(singleton);
        var model = view.Model;

        // 第一段：真实宿主容器里的"临时离树 / 重新挂接"（VM 不销毁，订阅要跟着回来）。
        var host = new Panel();
        var window = new Window { Width = WindowWidth, Height = WindowHeight, Content = host };
        window.Show();
        Pump();
        host.Children.Add(view);
        Pump();
        var afterAttach = SingletonHandlers(singleton);
        host.Children.Remove(view);
        Pump();
        var afterDetach = SingletonHandlers(singleton);
        host.Children.Add(view);
        Pump();
        var afterReattach = SingletonHandlers(singleton);
        host.Children.Remove(view);
        Pump();
        var afterSecondDetach = SingletonHandlers(singleton);

        // 第二段：生产里最常见的"关窗"路径 —— 外壳还挂在窗口里就关闭窗口。
        var second = new Window { Width = WindowWidth, Height = WindowHeight, Content = view };
        second.Show();
        Pump();
        var afterAttachInWindow = SingletonHandlers(singleton);
        second.Close();
        Pump();
        var afterClose = SingletonHandlers(singleton);

        var timer = typeof(MainView)
            .GetField("_stateTimer", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(view) as DispatcherTimer;
        return new ShellLifecycleProbe(new WeakReference(view), new WeakReference(model), timer?.IsEnabled ?? false,
            before, afterConstruct, afterAttach, afterDetach, afterReattach, afterSecondDetach,
            afterAttachInWindow, afterClose);
    }

    private sealed record ShellLifecycleProbe(
        WeakReference View, WeakReference Model, bool TimerEnabled,
        int Before, int AfterConstruct, int AfterAttach, int AfterDetach, int AfterReattach,
        int AfterSecondDetach, int AfterAttachInWindow, int AfterClose);

    /// <summary>构造探针：记录"构造这个对象本身"给单例加了几条订阅。</summary>
    private sealed record ConstructionProbe(WeakReference View, WeakReference Model, int Delta);

    /// <summary>对照组：不含任何产品界面的空窗口，走同一条建立/显示/关闭路径。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference BuildAndClosePlainWindow()
    {
        var content = new Border { Width = 120, Height = 80 };
        var window = new Window { Width = 320, Height = 240, Content = content };
        window.Show();
        Pump();
        window.Close();
        Pump();
        return new WeakReference(content);
    }

    /// <summary>对照组：窗口里只放一个 ShellViewModel（不带 MainView），看它是否被留住。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference BuildAndCloseModelOnlyWindow()
    {
        var model = new ShellViewModel(new MemoryThemeStore(), previewData: true);
        var window = new Window { Width = 800, Height = 600, Content = new Border { DataContext = model } };
        window.Show();
        Pump();
        window.Close();
        Pump();
        return new WeakReference(model);
    }

    /// <summary>对照组：建立 MainView 但从不显示，看"没上屏"时是否可回收、是否留下订阅。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ConstructionProbe BuildShellWithoutShowing()
    {
        var singleton = DisconnectedInstanceSource.Instance;
        var before = SingletonHandlers(singleton);
        var view = new MainView(new MemoryThemeStore());
        var delta = SingletonHandlers(singleton) - before;
        return new ConstructionProbe(new WeakReference(view), new WeakReference(view.Model), delta);
    }

    /// <summary>对照组：只建 ShellViewModel、连窗口都不建，隔离"构造是否就被单例事件留住"。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ConstructionProbe BuildModelWithoutWindow()
    {
        var singleton = DisconnectedInstanceSource.Instance;
        var before = SingletonHandlers(singleton);
        var model = new ShellViewModel(new MemoryThemeStore(), previewData: true);
        var delta = SingletonHandlers(singleton) - before;
        return new ConstructionProbe(new WeakReference(model), new WeakReference(model), delta);
    }

    // ── 事件订阅计数（读字段式事件的后备委托） ────────────────────────────────────────────

    private static Dictionary<string, int> HandlerCounts(IEnumerable<(string Name, object Target)> targets)
    {
        var counts = new Dictionary<string, int>();
        foreach (var (name, target) in targets)
        {
            var type = target.GetType();
            foreach (var info in type.GetEvents(BindingFlags.Instance | BindingFlags.Public))
            {
                var field = type.GetField(info.Name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field?.GetValue(target) is Delegate handler)
                    counts[$"{name}.{info.Name}"] = handler.GetInvocationList().Length;
            }
        }
        return counts;
    }

    // ── 统计与报告 ────────────────────────────────────────────────────────────────────────

    private static Dictionary<string, object?> Stats(string id, string unit, List<double> samples)
    {
        if (samples.Count == 0) throw new ArgumentException($"scenario '{id}' has no samples", nameof(samples));
        return new Dictionary<string, object?>
        {
            ["id"] = id,
            ["unit"] = unit,
            ["samples"] = samples.Count,
            ["median"] = Median(samples),
            ["p95"] = Percentile(samples, 0.95),
            ["min"] = Round(samples.Min()),
            ["max"] = Round(samples.Max()),
            ["mean"] = Round(samples.Average()),
        };
    }

    private static double Median(List<double> samples) => Percentile(samples, 0.5);

    /// <summary>nearest-rank：第 ceil(q·n) 个样本（1 起算），n=20 时 p95 即第 19 小值。</summary>
    private static double Percentile(List<double> samples, double q)
    {
        var sorted = samples.OrderBy(value => value).ToList();
        var rank = (int)Math.Ceiling(q * sorted.Count);
        return Round(sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)]);
    }

    private static double Round(double value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);

    private static Dictionary<string, object?> EnvironmentReport() => new()
    {
        ["os"] = RuntimeInformation.OSDescription,
        ["framework"] = RuntimeInformation.FrameworkDescription,
        ["process_architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
        ["processor_count"] = System.Environment.ProcessorCount,
        ["processor_identifier"] = System.Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "(unavailable)",
        ["server_gc"] = GCSettings.IsServerGC,
        ["latency_mode"] = GCSettings.LatencyMode.ToString(),
        ["build_configuration"] =
#if DEBUG
            "Debug",
#else
            "Release",
#endif
        ["stopwatch_frequency"] = Stopwatch.Frequency,
        ["window"] = $"{WindowWidth}x{WindowHeight} (native headless, Skia offscreen drawing)",
        ["command"] = System.Environment.CommandLine,
    };

    private static string Describe(Dictionary<string, object?> report)
    {
        var text = new StringBuilder();
        text.AppendLine($"ui-perf baseline — schema {report["schema"]} at {report["generated_at"]}");
        text.AppendLine();
        text.AppendLine("environment:");
        foreach (var (key, value) in (Dictionary<string, object?>)report["environment"]!) text.AppendLine($"  {key} = {value}");
        text.AppendLine();
        text.AppendLine("notes:");
        foreach (var note in (List<string>)report["notes"]!) text.AppendLine($"  - {note}");
        var invariants = (List<Dictionary<string, object?>>)report["invariants"]!;
        var failed = invariants.Where(entry => entry["pass"] is false).ToList();
        text.AppendLine();
        text.AppendLine($"invariants: {invariants.Count - failed.Count}/{invariants.Count} passed");
        foreach (var entry in invariants)
            text.AppendLine($"  [{(entry["pass"] is true ? "PASS" : "FAIL")}] {entry["id"]} — {entry["detail"]}");
        foreach (var scenario in (List<Dictionary<string, object?>>)report["scenarios"]!)
        {
            text.AppendLine();
            text.AppendLine($"— {scenario.GetValueOrDefault("scenario")}");
            foreach (var (key, value) in scenario)
                if (key != "scenario") text.AppendLine($"    {key} = {Flatten(value)}");
        }
        return text.ToString();
    }

    private static string Flatten(object? value) => value switch
    {
        null => "(null)",
        string text => text,
        IDictionary map => "{" + string.Join(", ", map.Keys.Cast<object?>().Select(key => $"{key}={Flatten(map[key!])}")) + "}",
        IEnumerable items => "[" + string.Join(", ", items.Cast<object?>().Select(Flatten)) + "]",
        _ => value.ToString() ?? "(null)",
    };

    // ── Headless 泵与断言 ────────────────────────────────────────────────────────────────

    /// <summary>按名字找控件；找不到就抛，避免"测了个不存在的场景"还报绿。</summary>
    private static T Find<T>(Control root, string name) where T : Control
    {
        var found = root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name);
        if (found is null) throw new Exception($"missing control: {name}");
        return found;
    }

    /// <summary>真实指针点击（先滚进视口），返回点击到泵完的耗时。</summary>
    private static double Click(Window window, Control control)
    {
        var name = control.Name ?? control.GetType().Name;
        control.BringIntoView();
        Pump();
        var centre = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
            ?? throw new Exception($"{name} is not attached to the window");
        Check(centre is { X: >= 0, Y: >= 0 } && centre.X < window.ClientSize.Width && centre.Y < window.ClientSize.Height,
            $"{name} must be inside the viewport (centre {centre}, viewport {window.ClientSize})");
        var watch = Stopwatch.StartNew();
        window.MouseMove(centre);
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        Pump();
        watch.Stop();
        return Round(watch.Elapsed.TotalMilliseconds);
    }

    /// <summary>真实按键（按下+抬起+泵），返回耗时。</summary>
    private static double Press(Window window, Key key, PhysicalKey physical)
    {
        var watch = Stopwatch.StartNew();
        window.KeyPress(key, RawInputModifiers.None, physical, null);
        window.KeyRelease(key, RawInputModifiers.None, physical, null);
        Pump();
        watch.Stop();
        return Round(watch.Elapsed.TotalMilliseconds);
    }

    /// <summary>真实文本输入（一次击键）+ 泵，返回耗时。</summary>
    private static double TypeText(Window window, string text)
    {
        var watch = Stopwatch.StartNew();
        window.KeyTextInput(text);
        Pump();
        watch.Stop();
        return Round(watch.Elapsed.TotalMilliseconds);
    }

    /// <summary>给尚未结束的异步续体与渲染队列留出时间，再判定可达性。</summary>
    private static void Settle()
    {
        for (var i = 0; i < 5; i++)
        {
            Thread.Sleep(40);
            Pump();
        }
    }

    private static Window Show(MainView view)
    {
        var window = new Window { Width = WindowWidth, Height = WindowHeight, Content = view };
        window.Show();
        Pump();
        return window;
    }

    private static void Close(Window window)
    {
        window.Close();
        Pump();
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Collect()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
    }
}
