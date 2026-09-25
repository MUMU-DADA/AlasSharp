using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Contracts;
using Alas.UI.Overview;
using Alas.UI.TaskEditor;
using Alas.UI.Theming;
using Alas.UI.ViewModels;
using Alas.UI.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Alas.UI.Headless;

/// <summary>
/// 任务设置页加载耗时的可复现测量（离屏、原生 Headless、**真实上游 schema**）。
///
/// 被测对象是生产路径本身：真实 <see cref="MainView"/> + <see cref="ShellViewModel"/> +
/// 真实 <c>TaskEditorView</c>，数据来自上游 <c>module/config/argument/{menu,args}.json</c>、
/// <c>i18n/zh-CN.json</c> 与一个真实实例配置，**不是**为测量另写的替代控件或模拟数据源。
///
/// 分三段计时，用来区分"读配置"和"建界面"各占多少：
/// M1 模型层：<c>TaskEditorViewModel.Load</c>（schema 已在手，只算建模与字段构造）；
/// M2 视图层：<c>TaskEditorView</c> 建组 + 挂窗口 + 布局 + 渲染一帧（schema 已在手）；
/// M3 端到端：真实外壳里来回切换任务，从发出选择到页面可用（含后端读取、首次完整建页及后续缓存切换）。
///
/// 口径与边界：
/// - 每个场景报样本数、median、p95、min、max 与托管分配量（percentile 用 nearest-rank）；
/// - 首次打开（含后端读取）与稳态切换（schema 与完整控件树均已缓存）分开记，不混成一个数；
/// - 只读上游文件与本地实例配置，不写回、不连设备、不启动 Python 宿主、不显示窗口；
/// - 本机离屏 Skia 的数字不能外推现场窗口/浏览器/GPU；找不到本地 engine 时如实标记 skipped。
///
/// 独立入口：必须在 Headless 会话的 UI 线程上调用
/// （<c>HeadlessUnitTestSession.Dispatch(() =&gt; TaskEditorLoadChecks.Run(output))</c>）。
/// </summary>
internal static class TaskEditorLoadChecks
{
    public const string SchemaId = "ui-task-load/1";

    private const int WindowWidth = 1280;
    private const int WindowHeight = 820;
    private const int Samples = 8;
    private const string Language = "zh-CN";

    /// <summary>侧栏里的「主线图-1Plus」与「活动图-1Plus」。</summary>
    private static readonly (string Task, string Label)[] Targets =
        [("Main", "主线图-1Plus"), ("Event", "活动图-1Plus")];

    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        string? engine = FindEngine();
        var report = NewReport();
        if (engine is null)
        {
            report["status"] = "skipped";
            report["reason"] = "找不到本地 .runtime/engine（需要上游 module/config/argument），未做任何测量。";
            Write(output, report, "task-load", Describe(report));
            Console.WriteLine("SKIP: task editor load measurement — local upstream engine not found; no numbers produced.");
            return;
        }

        string instance = FindInstance(engine)
            ?? throw new InvalidOperationException("engine/config 下没有可用的实例配置，无法测量真实的实例值加载。");
        using var backend = new UpstreamFileBackend(engine, instance);

        var model = NewSampleMap();
        var view = NewSampleMap();
        var viewCtor = NewSampleMap();
        var viewGroups = NewSampleMap();
        var viewRender = NewSampleMap();
        var fields = new Dictionary<string, int>(StringComparer.Ordinal);
        var controls = new Dictionary<string, int>(StringComparer.Ordinal);
        var firstOpen = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        var pages = new Dictionary<string, TaskEditorView>(StringComparer.Ordinal);
        var steady = NewSampleMap();
        var steadyAllocated = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        foreach (var (task, _) in Targets) steadyAllocated[task] = [];
        var inputChange = new List<double>();
        var inputAllocated = new List<long>();

        // ── M3 先跑：这是进程内第一次建任务页，JIT 与字体都还没热，最接近"冷启动" ────────
        var shell = new MainView(new MemoryThemeStore(), backend, resourceStore: new MemoryResourceSelectionStore());
        var window = new Window { Width = WindowWidth, Height = WindowHeight, Content = shell };
        window.Show();
        Pump();
        try
        {
            shell.Model.SelectInstance(instance);
            Pump();
            var entries = shell.Model.TaskGroups.SelectMany(group => group.Tasks)
                .GroupBy(entry => entry.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            foreach (var (task, label) in Targets)
                if (!entries.ContainsKey(task))
                    throw new InvalidOperationException($"侧栏里找不到任务 {task}（{label}），无法测量端到端切换。");

            // 首次打开：schema 与实例配置都还没被这个 (实例, 任务) 读过。
            foreach (var (task, _) in Targets)
            {
                backend.ResetCounters();
                var watch = Stopwatch.StartNew();
                shell.Model.SelectTaskCommand.Execute(entries[task]);
                WaitFor(() => shell.Model.TaskEditor.IsLoaded && shell.Model.TaskEditor.TaskName == task);
                window.UpdateLayout();
                Pump();
                var page = ActiveTaskPage(shell);
                var inputs = page.GetVisualDescendants().OfType<Control>()
                    .Where(control => control.Name?.StartsWith("Field_", StringComparison.Ordinal) == true)
                    .ToDictionary(control => control.Name!, StringComparer.Ordinal);
                var taskFields = shell.Model.TaskEditor.Fields.ToArray();
                if (inputs.Count != taskFields.Length || taskFields.Any(field =>
                    !inputs.TryGetValue("Field_" + field.Path, out var input) || input.IsEffectivelyVisible != field.IsVisible))
                    throw new InvalidOperationException($"{task} 字段树不完整或可见性与上游 schema 不一致。");
                pages.Add(task, page);
                watch.Stop();
                var reads = backend.Reads;
                firstOpen[task] = new Dictionary<string, object?>
                {
                    ["ms"] = Round(watch.Elapsed.TotalMilliseconds),
                    ["backend_schema_reads"] = reads.Schema,
                    ["backend_config_reads"] = reads.Config,
                    ["backend_read_ms"] = Round(reads.Milliseconds),
                };
            }

            // 稳态：两个任务的完整控件树都留在宿主中，来回切换不重建控件、不再读 schema。
            for (var index = 0; index < Samples; index++)
                foreach (var (task, _) in Targets)
                {
                    backend.ResetCounters();
                    Pump();
                    long allocated = GC.GetAllocatedBytesForCurrentThread();
                    var watch = Stopwatch.StartNew();
                    shell.Model.SelectTaskCommand.Execute(entries[task]);
                    window.UpdateLayout();
                    Pump();
                    watch.Stop();
                    long switchAllocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                    if (!ReferenceEquals(ActiveTaskPage(shell), pages[task]))
                        throw new InvalidOperationException($"稳态切换 {task} 重建了任务页，缓存断言失败。");
                    var reads = backend.Reads;
                    if (reads.Schema != 0 || reads.Config != 0)
                        throw new InvalidOperationException(
                            $"稳态切换 {task} 仍向后端读了 schema={reads.Schema}/config={reads.Config} 次；"
                            + "计数口径不成立，稳态结论无效。");
                    steady[task].Add(watch.Elapsed.TotalMilliseconds);
                    steadyAllocated[task].Add(switchAllocated);
                }

            shell.Model.SelectTaskCommand.Execute(entries["Main"]);
            window.UpdateLayout();
            Pump();
            var inputEditor = shell.Model.TaskEditor;
            inputEditor.AutoSave = false;
            var inputField = inputEditor.Fields.First(field => !field.ReadOnly &&
                field.Kind is TaskFieldKind.Text or TaskFieldKind.Number);
            for (var index = 0; index < 20; index++)
            {
                Pump();
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                var watch = Stopwatch.StartNew();
                inputField.SetText(index % 2 == 0 ? "7" : "8");
                window.UpdateLayout();
                Pump();
                watch.Stop();
                inputChange.Add(watch.Elapsed.TotalMilliseconds);
                inputAllocated.Add(GC.GetAllocatedBytesForCurrentThread() - allocated);
            }
            if (ActiveTaskPage(shell).GetVisualDescendants().OfType<Control>()
                    .Count(control => control.Name?.StartsWith("Field_", StringComparison.Ordinal) == true)
                != inputEditor.Fields.Count())
                throw new InvalidOperationException("输入性能场景丢失了字段控件。");

            var mainPage = pages["Main"];
            var mainEditor = mainPage.Model;
            mainEditor.Search = mainEditor.Fields.First(field => field.IsVisible).Label;
            foreach (var entry in entries.Values.Where(entry => entry.Key is not ("Main" or "Event")).Take(3))
            {
                shell.Model.SelectTaskCommand.Execute(entry);
                WaitFor(() => shell.Model.TaskEditor.IsLoaded && shell.Model.TaskEditor.TaskName == entry.Key);
                window.UpdateLayout();
                Pump();
                if (TaskPageHost(shell).Children.Count > 3)
                    throw new InvalidOperationException("任务页缓存超过三页上限。");
            }
            if (TaskPageHost(shell).Children.Contains(mainPage))
                throw new InvalidOperationException("最久未使用的任务页未被淘汰。");
            shell.Model.SelectTaskCommand.Execute(entries["Main"]);
            window.UpdateLayout();
            Pump();
            if (ReferenceEquals(ActiveTaskPage(shell), mainPage) ||
                !ReferenceEquals(shell.Model.TaskEditor, mainEditor) ||
                shell.Model.TaskEditor.Search != mainEditor.Search)
                throw new InvalidOperationException("缓存淘汰后未正确恢复任务模型和筛选状态。");
        }
        finally { window.Close(); Pump(); }

        // ── M1 / M2：schema 预先读好并预热，隔离"建模"与"建控件" ───────────────────────
        var schemaResponse = backend.ReadSchemaAsync(Language).GetAwaiter().GetResult();
        var configResponse = backend.ReadConfigAsync(instance).GetAwaiter().GetResult();
        var schemaNode = new JsonObject
        {
            ["args"] = schemaResponse.Args.DeepClone(),
            ["menu"] = schemaResponse.Menu.DeepClone(),
            ["translations"] = schemaResponse.Translations.DeepClone(),
        };
        var configNode = new JsonObject
        {
            ["instance"] = configResponse.Instance,
            ["revision"] = configResponse.Revision,
            ["values"] = configResponse.Values.DeepClone(),
        };

        var host = new Window { Width = WindowWidth, Height = WindowHeight };
        host.Show();
        try
        {
            foreach (var (task, _) in Targets)
            {
                var warmed = MeasureModel(instance, task, schemaNode, configNode);
                fields[task] = warmed.Fields;
                var first = MeasureView(host, instance, task, schemaNode, configNode);
                controls[task] = first.Controls;
                for (var index = 0; index < Samples; index++)
                {
                    model[task].Add(MeasureModel(instance, task, schemaNode, configNode).Milliseconds);
                    var sample = MeasureView(host, instance, task, schemaNode, configNode);
                    view[task].Add(sample.Milliseconds);
                    viewCtor[task].Add(sample.Ctor);
                    viewGroups[task].Add(sample.Groups);
                    viewRender[task].Add(sample.Render);
                }
            }
        }
        finally { host.Close(); }

        report["status"] = "measured";
        report["engine_configuration"] = engine;
        report["instance"] = instance;
        report["window"] = $"{WindowWidth}x{WindowHeight} (native headless, Skia offscreen)";
        report["comparison"] = new Dictionary<string, object?>
        {
            ["model_load"] = Stats("M1_model_load", model, "ms"),
            ["view_build"] = Stats("M2_view_build", view, "ms"),
            ["view_ctor"] = Stats("M2a_view_ctor", viewCtor, "ms"),
            ["view_groups"] = Stats("M2b_build_groups", viewGroups, "ms"),
            ["view_render"] = Stats("M2c_attach_layout_render", viewRender, "ms"),
            ["first_open_end_to_end"] = new Dictionary<string, object?>
            {
                ["unit"] = "ms",
                ["note"] = "含后端读取；每个 (实例, 任务) 只有一次，样本数 1，不做统计外推。",
                ["per_task"] = firstOpen,
            },
            ["steady_switch_end_to_end"] = Stats("M3_steady_switch", steady, "ms"),
            ["input_change_end_to_end"] = new Dictionary<string, object?>
            {
                ["samples"] = inputChange.Count,
                ["median_ms"] = Round(Median(inputChange)),
                ["p95_ms"] = Round(Percentile(inputChange, 0.95)),
                ["median_allocated_bytes"] = Round(Median([.. inputAllocated.Select(value => (double)value)])),
                ["field"] = "Main 中首个可编辑文本或数字字段",
            },
            ["steady_switch_allocated"] = new Dictionary<string, object?>
            {
                ["unit"] = "bytes",
                ["note"] = "每次切换的 UI 线程托管分配；两个已打开任务页的完整控件树保持附着。",
                ["per_task"] = Targets.ToDictionary(
                    target => target.Task,
                    target => (object?)new Dictionary<string, object?>
                    {
                        ["samples"] = steadyAllocated[target.Task].Count,
                        ["median"] = Round(Median([.. steadyAllocated[target.Task].Select(value => (double)value)])),
                        ["max"] = steadyAllocated[target.Task].Max(),
                    },
                    StringComparer.Ordinal),
            },
            ["realised_controls"] = controls,
            ["field_counts"] = fields,
        };
        Write(output, report, "task-load", Describe(report));
        Console.WriteLine(Describe(report));
    }

    // ── 冷建页分解：进程内第一次建任务页，逐阶段计时 ────────────────────────────────────

    /// <summary>
    /// 单独一个入口，因为"第一次"只能在一个进程里出现一次：这里不做任何预热，
    /// 建页之前只把 schema/配置读进内存（约 10 ms 的 JSON 解析，会在报告里单列）。
    /// </summary>
    public static void RunCold(string output)
    {
        Directory.CreateDirectory(output);
        var report = NewReport();
        report["schema"] = SchemaId + "+cold";
        string? engine = FindEngine();
        if (engine is null)
        {
            report["status"] = "skipped";
            report["reason"] = "找不到本地 .runtime/engine，未做任何测量。";
            Write(output, report, "task-load-cold", DescribeCold(report));
            Console.WriteLine("SKIP: cold task editor breakdown — local upstream engine not found.");
            return;
        }

        string instance = FindInstance(engine)
            ?? throw new InvalidOperationException("engine/config 下没有可用的实例配置。");
        using var backend = new UpstreamFileBackend(engine, instance);
        var schemaResponse = backend.ReadSchemaAsync(Language).GetAwaiter().GetResult();
        var configResponse = backend.ReadConfigAsync(instance).GetAwaiter().GetResult();
        var schemaNode = new JsonObject
        {
            ["args"] = schemaResponse.Args.DeepClone(),
            ["menu"] = schemaResponse.Menu.DeepClone(),
            ["translations"] = schemaResponse.Translations.DeepClone(),
        };
        var configNode = new JsonObject
        {
            ["instance"] = configResponse.Instance,
            ["revision"] = configResponse.Revision,
            ["values"] = configResponse.Values.DeepClone(),
        };

        var phases = new Dictionary<string, object?>(StringComparer.Ordinal);
        var host = new Window { Width = WindowWidth, Height = WindowHeight };
        host.Show();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            foreach (var (task, label) in Targets)
            {
                var editor = new TaskEditorViewModel { AutoSave = false };
                var watch = Stopwatch.StartNew();
                editor.Load(instance, task, (JsonObject)schemaNode.DeepClone(), (JsonObject)configNode.DeepClone());
                double model = watch.Elapsed.TotalMilliseconds;
                var page = new TaskEditorView();
                double ctor = watch.Elapsed.TotalMilliseconds;
                page.Model = editor;
                double groups = watch.Elapsed.TotalMilliseconds;
                host.Content = page;
                double attach = watch.Elapsed.TotalMilliseconds;
                host.UpdateLayout();
                double layout = watch.Elapsed.TotalMilliseconds;
                Pump();
                double render = watch.Elapsed.TotalMilliseconds;
                host.Content = null;
                phases[task] = new Dictionary<string, object?>
                {
                    ["label"] = label,
                    ["model_load_ms"] = Round(model),
                    ["view_ctor_ms"] = Round(ctor - model),
                    ["build_groups_ms"] = Round(groups - ctor),
                    ["attach_ms"] = Round(attach - groups),
                    ["layout_ms"] = Round(layout - attach),
                    ["render_ms"] = Round(render - layout),
                    ["attach_layout_render_ms"] = Round(render - groups),
                    ["total_ms"] = Round(render),
                    ["realised_controls"] = page.GetVisualDescendants().Count(),
                };
            }
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        }
        finally { host.Close(); }

        report["status"] = "measured";
        report["engine_configuration"] = engine;
        report["instance"] = instance;
        report["window"] = $"{WindowWidth}x{WindowHeight} (native headless, Skia offscreen)";
        report["comparison"] = new Dictionary<string, object?>
        {
            ["cold_page_build"] = new Dictionary<string, object?>
            {
                ["unit"] = "ms",
                ["note"] = "进程内第一次建任务页；顺序按 Targets，第一个才是真冷启动，第二个已被预热。",
                ["per_task"] = phases,
            },
            ["cold_page_build_allocated_bytes"] = allocated,
        };
        Write(output, report, "task-load-cold", DescribeCold(report));
        Console.WriteLine(DescribeCold(report));
    }

    private static string DescribeCold(Dictionary<string, object?> report)
    {
        var text = new StringBuilder();
        text.AppendLine($"cold task-page build — schema {report["schema"]} at {report["generated_at"]}");
        if (report["status"] is not "measured")
        {
            text.AppendLine($"status: {report["status"]} — {report["reason"]}");
            return text.ToString();
        }
        var comparison = (Dictionary<string, object?>)report["comparison"]!;
        var cold = (Dictionary<string, object?>)comparison["cold_page_build"]!;
        foreach (var (task, value) in (Dictionary<string, object?>)cold["per_task"]!)
        {
            var row = (Dictionary<string, object?>)value!;
            text.AppendLine($"  {task,-6} ({row["label"]}) total={row["total_ms"],-9} "
                + $"model={row["model_load_ms"],-7} ctor={row["view_ctor_ms"],-7} "
                + $"build_groups={row["build_groups_ms"],-7} attach={row["attach_ms"],-9} "
                + $"layout={row["layout_ms"],-9} render={row["render_ms"],-9} "
                + $"controls={row["realised_controls"]}");
        }
        text.AppendLine($"  allocated={comparison["cold_page_build_allocated_bytes"]} bytes");
        return text.ToString();
    }

    // ── 三段测量的具体动作 ────────────────────────────────────────────────────────────

    private static (double Milliseconds, int Fields) MeasureModel(
        string instance, string task, JsonObject schema, JsonObject config)
    {
        var editor = new TaskEditorViewModel { AutoSave = false };
        var watch = Stopwatch.StartNew();
        editor.Load(instance, task, (JsonObject)schema.DeepClone(), (JsonObject)config.DeepClone());
        watch.Stop();
        if (editor.Groups.Count == 0) throw new InvalidOperationException($"schema 里没有 {task} 的组，测量无效。");
        return (watch.Elapsed.TotalMilliseconds, editor.Fields.Count());
    }

    private static (double Milliseconds, double Ctor, double Groups, double Render, int Controls) MeasureView(
        Window host, string instance, string task, JsonObject schema, JsonObject config)
    {
        var editor = new TaskEditorViewModel { AutoSave = false };
        editor.Load(instance, task, (JsonObject)schema.DeepClone(), (JsonObject)config.DeepClone());
        var watch = Stopwatch.StartNew();
        var page = new TaskEditorView();
        double ctor = watch.Elapsed.TotalMilliseconds;
        page.Model = editor;                 // BuildGroups：把每个字段的整行控件都建出来
        double groups = watch.Elapsed.TotalMilliseconds;
        host.Content = page;
        host.UpdateLayout();
        Pump();
        double render = watch.Elapsed.TotalMilliseconds;
        int realised = page.GetVisualDescendants().Count();
        host.Content = null;
        return (render, ctor, groups - ctor, render - groups, realised);
    }

    // ── 上游文件后端：与 ConfigWorkspace 同形，但只读、无事务、无 Python 宿主 ──────────────

    private sealed class UpstreamFileBackend(string engine, string instance) : IAlasUiBackend
    {
        private readonly string _argument = Path.Combine(engine, "module", "config", "argument");
        private readonly string _i18n = Path.Combine(engine, "module", "config", "i18n");
        private readonly string _config = Path.Combine(engine, "config");

        /// <summary>累计的后端读取次数与耗时，用来证明稳态切换没有再读 schema。</summary>
        public (int Schema, int Config, double Milliseconds) Reads { get; private set; }
        public void ResetCounters() => Reads = (0, 0, 0);

        public bool IsConnected => true;
        public bool IsSimulation => false;
        public IReadOnlyList<InstanceCardViewModel> Instances =>
            [InstanceCardViewModel.Create(instance, "stopped", "上游本地实例", "", null)];
        public event EventHandler? Changed;
        public void Refresh() => Changed?.Invoke(this, EventArgs.Empty);

        public Task<SchemaResponse> ReadSchemaAsync(string language = "zh-CN", CancellationToken cancellationToken = default)
            => Task.Run(() =>
            {
                var watch = Stopwatch.StartNew();
                var response = new SchemaResponse
                {
                    Menu = Parse(Path.Combine(_argument, "menu.json")),
                    Args = Parse(Path.Combine(_argument, "args.json")),
                    Translations = Parse(Path.Combine(_i18n, language + ".json")),
                };
                watch.Stop();
                Reads = (Reads.Schema + 1, Reads.Config, Reads.Milliseconds + watch.Elapsed.TotalMilliseconds);
                return response;
            }, cancellationToken);

        public Task<ConfigResponse> ReadConfigAsync(string instanceName, CancellationToken cancellationToken = default)
            => Task.Run(() =>
            {
                var watch = Stopwatch.StartNew();
                string path = Path.Combine(_config, instanceName + ".json");
                var values = MergeObjects(Parse(Path.Combine(_config, "template.json")), Parse(path));
                string revision = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
                watch.Stop();
                Reads = (Reads.Schema, Reads.Config + 1, Reads.Milliseconds + watch.Elapsed.TotalMilliseconds);
                return new ConfigResponse { Instance = instanceName, Revision = revision, Values = values };
            }, cancellationToken);

        public Task<InstanceListResponse> ReadInstancesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new InstanceListResponse
            {
                Instances =
                [
                    new InstanceSummary
                    {
                        Instance = instance, Revision = "measurement", Server = "上游本地实例",
                        Serial = "", Status = "stopped",
                    },
                ],
            });

        public Task<JsonObject> ReadInstanceStateAsync(string instanceName, CancellationToken cancellationToken = default)
            => Task.FromResult(new JsonObject { ["instance"] = instanceName });
        public Task<JsonObject> ReadStateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new JsonObject { ["instance"] = instance });
        public Task<StartupRunResponse> ReadStartupRunAsync(string instanceName, CancellationToken cancellationToken = default)
            => Task.FromResult(new StartupRunResponse { Instance = instanceName, Enabled = false, Run = [] });
        public Task<DeploySettingsResponse> ReadDeploySettingsAsync(string language = "zh-CN", CancellationToken cancellationToken = default)
            => Task.FromResult(new DeploySettingsResponse { Demo = false, Notice = "", Groups = [] });
        public Task SaveQueueAsync(JsonObject queue, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> RequestStopAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

        // 测量只走读取路径；写入与设备动作显式失败，避免把"测到了别的东西"当成结果。
        public Task<JsonObject?> ReadReportAsync(string stamp, CancellationToken cancellationToken = default) => Unsupported<JsonObject?>(nameof(ReadReportAsync));
        public Task<ConfigResponse> PatchConfigAsync(ConfigPatchRequest request, CancellationToken cancellationToken = default) => Unsupported<ConfigResponse>(nameof(PatchConfigAsync));
        public Task<ConfigResponse> CreateInstanceAsync(InstanceCreateRequest request, CancellationToken cancellationToken = default) => Unsupported<ConfigResponse>(nameof(CreateInstanceAsync));
        public Task DeleteInstanceAsync(InstanceDeleteRequest request, CancellationToken cancellationToken = default) => Task.FromException(new NotSupportedException("测量后端不实现 " + nameof(DeleteInstanceAsync)));
        public Task<InstanceImportSource> ImportInstanceAsync(InstanceImportRequest request, CancellationToken cancellationToken = default) => Unsupported<InstanceImportSource>(nameof(ImportInstanceAsync));
        public Task<InstanceImportListResponse> ReadInstanceImportsAsync(CancellationToken cancellationToken = default) => Unsupported<InstanceImportListResponse>(nameof(ReadInstanceImportsAsync));
        public Task<DeploySettingsPatchResponse> PatchDeploySettingsAsync(DeploySettingsPatchRequest request, CancellationToken cancellationToken = default) => Unsupported<DeploySettingsPatchResponse>(nameof(PatchDeploySettingsAsync));
        public Task<StartupRunResponse> SetStartupRunAsync(StartupRunRequest request, CancellationToken cancellationToken = default) => Unsupported<StartupRunResponse>(nameof(SetStartupRunAsync));
        public Task StartRunAsync(ControlRunRequest request, CancellationToken cancellationToken = default) => Task.FromException(new NotSupportedException("测量后端不实现 " + nameof(StartRunAsync)));
        public Task StartTaskAsync(InstanceTaskRunRequest request, CancellationToken cancellationToken = default) => Task.FromException(new NotSupportedException("测量后端不实现 " + nameof(StartTaskAsync)));
        public Task StartSchedulerAsync(InstanceSchedulerRunRequest request, CancellationToken cancellationToken = default) => Task.FromException(new NotSupportedException("测量后端不实现 " + nameof(StartSchedulerAsync)));
        public Task<JsonObject> ReadStatisticsAsync(StatisticsRequest request, CancellationToken cancellationToken = default) => Unsupported<JsonObject>(nameof(ReadStatisticsAsync));
        public Task<JsonObject> RefreshStatisticsLootAsync(string instanceName, CancellationToken cancellationToken = default) => Unsupported<JsonObject>(nameof(RefreshStatisticsLootAsync));
        public Task<JsonObject> ReadMeowfficerAsync(MeowfficerRequest request, CancellationToken cancellationToken = default) => Unsupported<JsonObject>(nameof(ReadMeowfficerAsync));
        public Task<JsonObject> ClearMeowfficerAsync(string instanceName, CancellationToken cancellationToken = default) => Unsupported<JsonObject>(nameof(ClearMeowfficerAsync));
        public Task<JsonObject> ValidateShopStrategyAsync(string script, CancellationToken cancellationToken = default) => Unsupported<JsonObject>(nameof(ValidateShopStrategyAsync));
        public void Dispose() => Changed = null;

        private static Task<T> Unsupported<T>(string name)
            => Task.FromException<T>(new NotSupportedException("测量后端不实现 " + name));

        private static JsonObject Parse(string path)
            => JsonNode.Parse(File.ReadAllBytes(path)) as JsonObject
               ?? throw new InvalidOperationException("不是 JSON 对象：" + Path.GetFileName(path));

        /// <summary>与 ConfigWorkspace.MergeObjects 同形：模板打底，实例覆盖。</summary>
        private static JsonObject MergeObjects(JsonObject defaults, JsonObject overrides)
        {
            var result = (JsonObject)defaults.DeepClone();
            foreach (var pair in overrides)
            {
                if (pair.Value is JsonObject child && result[pair.Key] is JsonObject defaultChild)
                    result[pair.Key] = MergeObjects(defaultChild, child);
                else
                    result[pair.Key] = pair.Value?.DeepClone();
            }
            return result;
        }
    }

    // ── 统计与报告 ────────────────────────────────────────────────────────────────────

    private static Dictionary<string, object?> NewReport() => new()
    {
        ["schema"] = SchemaId,
        ["generated_at"] = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
        ["environment"] = EnvironmentReport(),
        ["notes"] = new List<string>
        {
            "真实上游 schema：module/config/argument/{menu,args}.json + i18n/zh-CN.json，每次读取都重新读盘并解析（与 ConfigWorkspace.Schema 同形）。",
            "实例配置与 ConfigWorkspace.Get 同形：读实例 JSON 并与 config/template.json 递归合并。",
            "M1/M2 的 schema 预先读好，只测建模与建界面；M3 包含后端读取、首次完整建页与后续缓存切换。",
            "每个任务的全部可见字段默认构建；打开过的任务页在宿主中保留完整控件树，最近三页按模型复用。",
            "本机离屏 Skia 单轮数字，不作为现场窗口/浏览器/GPU 结论。",
        },
    };

    private static Dictionary<string, List<double>> NewSampleMap()
        => Targets.ToDictionary(target => target.Task, _ => new List<double>(), StringComparer.Ordinal);

    private static TaskEditorView ActiveTaskPage(MainView shell)
    {
        return TaskPageHost(shell).Children.OfType<TaskEditorView>().Single(page => page.IsVisible);
    }

    private static Panel TaskPageHost(MainView shell) => shell.GetVisualDescendants().OfType<Panel>()
        .First(control => control.Name == "TaskEditorHost");

    private static Dictionary<string, object?> Stats(string id, Dictionary<string, List<double>> samples, string unit)
        => new()
        {
            ["id"] = id,
            ["unit"] = unit,
            ["note"] = "每个任务各自取样；percentile 用 nearest-rank。",
            ["per_task"] = samples.ToDictionary(
                pair => pair.Key,
                pair => (object?)new Dictionary<string, object?>
                {
                    ["samples"] = pair.Value.Count,
                    ["median"] = Median(pair.Value),
                    ["p95"] = Percentile(pair.Value, 0.95),
                    ["min"] = Round(pair.Value.Min()),
                    ["max"] = Round(pair.Value.Max()),
                },
                StringComparer.Ordinal),
        };

    private static double Median(List<double> samples) => Percentile(samples, 0.5);

    /// <summary>nearest-rank：第 ceil(q·n) 个样本（1 起算）。</summary>
    private static double Percentile(List<double> samples, double q)
    {
        var sorted = samples.OrderBy(value => value).ToList();
        var rank = (int)Math.Ceiling(q * sorted.Count);
        return Round(sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)]);
    }

    private static double Round(double value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);

    private static Dictionary<string, object?> EnvironmentReport() => new()
    {
        ["os"] = RuntimeInformation.OSDescription,
        ["framework"] = RuntimeInformation.FrameworkDescription,
        ["processor_count"] = System.Environment.ProcessorCount,
        ["build_configuration"] =
#if DEBUG
            "Debug",
#else
            "Release",
#endif
        ["command"] = System.Environment.CommandLine,
    };

    private static string Describe(Dictionary<string, object?> report)
    {
        var text = new StringBuilder();
        text.AppendLine($"task-editor load measurement — schema {report["schema"]} at {report["generated_at"]}");
        if (report["status"] is not "measured")
        {
            text.AppendLine($"status: {report["status"]} — {report["reason"]}");
            return text.ToString();
        }
        text.AppendLine($"engine: {report["engine_configuration"]}   instance: {report["instance"]}");
        text.AppendLine($"window: {report["window"]}");
        text.AppendLine();
        var comparison = (Dictionary<string, object?>)report["comparison"]!;
        foreach (string key in new[]
                 {
                     "model_load", "view_build", "view_ctor", "view_groups", "view_render",
                     "steady_switch_end_to_end",
                 })
        {
            var stats = (Dictionary<string, object?>)comparison[key]!;
            text.AppendLine($"{stats["id"]} ({stats["unit"]}):");
            foreach (var (task, value) in (Dictionary<string, object?>)stats["per_task"]!)
            {
                var row = (Dictionary<string, object?>)value!;
                text.AppendLine($"  {task,-6} n={row["samples"],-3} median={row["median"],-9} p95={row["p95"],-9} "
                    + $"min={row["min"],-9} max={row["max"]}");
            }
        }
        var firstOpen = (Dictionary<string, object?>)comparison["first_open_end_to_end"]!;
        text.AppendLine("first_open_end_to_end (ms, 含后端读取):");
        foreach (var (task, row) in (Dictionary<string, Dictionary<string, object?>>)firstOpen["per_task"]!)
        {
            text.AppendLine($"  {task,-6} total={row["ms"],-9} backend={row["backend_read_ms"],-9} "
                + $"schema_reads={row["backend_schema_reads"]} config_reads={row["backend_config_reads"]}");
        }
        var allocated = (Dictionary<string, object?>)comparison["steady_switch_allocated"]!;
        text.AppendLine("steady_switch_allocated (bytes/次):");
        foreach (var (task, value) in (Dictionary<string, object?>)allocated["per_task"]!)
        {
            var row = (Dictionary<string, object?>)value!;
            text.AppendLine($"  {task,-6} median={row["median"],-12} max={row["max"]}");
        }
        var controls = (Dictionary<string, int>)comparison["realised_controls"]!;
        var counts = (Dictionary<string, int>)comparison["field_counts"]!;
        text.AppendLine("scale: " + string.Join(", ",
            controls.Select(pair => $"{pair.Key}: {counts[pair.Key]} 字段 / {pair.Value} 可视控件")));
        return text.ToString();
    }

    private static void Write(string output, Dictionary<string, object?> report, string name, string description)
    {
        File.WriteAllText(Path.Combine(output, name + ".json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(output, name + ".txt"),
            description + Environment.NewLine, new UTF8Encoding(false));
    }

    // ── 环境定位与驱动 ────────────────────────────────────────────────────────────────

    private static string? FindEngine()
    {
        string? configured = System.Environment.GetEnvironmentVariable("ALAS_REPO");
        if (!string.IsNullOrWhiteSpace(configured) && IsEngine(configured)) return Path.GetFullPath(configured);
        for (string? current = AppContext.BaseDirectory; current is not null; current = Directory.GetParent(current)?.FullName)
        {
            string candidate = Path.Combine(current, ".runtime", "engine");
            if (IsEngine(candidate)) return candidate;
        }
        string direct = Path.Combine(Directory.GetCurrentDirectory(), ".runtime", "engine");
        return IsEngine(direct) ? direct : null;
    }

    private static bool IsEngine(string path)
        => File.Exists(Path.Combine(path, "module", "config", "argument", "args.json"));

    private static string? FindInstance(string engine)
        => Directory.EnumerateFiles(Path.Combine(engine, "config"), "*.json", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name is not null && !name.StartsWith("template", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .FirstOrDefault();

    private static void WaitFor(Func<bool> ready)
    {
        var watch = Stopwatch.StartNew();
        while (!ready())
        {
            if (watch.ElapsedMilliseconds > 15_000) throw new TimeoutException("任务设置页 15 秒内未加载完成。");
            Pump();
            Thread.Sleep(1);
        }
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}
