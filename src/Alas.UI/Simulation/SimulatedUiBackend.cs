using System.Text.Json.Nodes;
using Alas.Contracts;
using Alas.UI.TaskEditor;
using Alas.UI.ViewModels;

namespace Alas.UI.Simulation;

/// <summary>
/// Session-local display fixtures. No Core, transport, timers, files, Python or devices.
/// Requests exercise the same UI adapters, but only mutate these in-memory samples.
/// This deliberately does not model or validate the game's business rules.
/// </summary>
public sealed class SimulatedUiBackend : IAlasUiBackend
{
    public const string Notice = "UI 隔离模式 · 全部为模拟数据；操作仅在内存中生效，不连接后端或设备。";
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly Dictionary<string, Sample> _samples = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonObject> _imports = new(StringComparer.Ordinal);
    private readonly JsonObject _deploy = new();
    private readonly HashSet<string> _startup = new(StringComparer.Ordinal);
    private readonly SchemaResponse _schema = BuildSchema();
    private IReadOnlyList<InstanceCardViewModel> _cards = [];
    private JsonObject _queue = new();
    private string? _running;
    private string _task = "Reward";
    private bool _disposed;
    public bool IsConnected => !_disposed;
    public bool IsSimulation => true;
    public IReadOnlyList<InstanceCardViewModel> Instances => _cards;
    public event EventHandler? Changed;

    private sealed class Sample(JsonObject values)
    {
        public JsonObject Values { get; set; } = values;
        public int Revision { get; set; } = 1;
        public long Sequence { get; set; }
        public JsonArray Logs { get; } = [];
        public string Version => "ui-only-" + Revision;
    }

    public SimulatedUiBackend() => Reset();

    public void Reset()
    {
        Check();
        _samples.Clear(); _imports.Clear(); _startup.Clear(); _deploy.Clear();
        _queue = new(); _running = null; _task = "Reward";
        foreach (string name in new[] { "demo-main", "demo-event" })
        {
            _samples.Add(name, new Sample(DefaultValues()));
            AppendLogsCore(name, 40);
        }
        _deploy["Theme"] = "default";
        _deploy["CheckUpdate"] = false;
        _deploy["EnableRemoteAccess"] = false;
        _deploy["WebuiPort"] = 22267;
        Refresh();
    }

    public void AppendLogs(string instance, int count = 100)
    {
        Check();
        if (count is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(count));
        AppendLogsCore(instance, count);
        Refresh();
    }

    private void AppendLogsCore(string instance, int count)
    {
        var sample = Get(instance);
        for (int i = 0; i < count; i++)
        {
            long sequence = ++sample.Sequence;
            sample.Logs.Add(new JsonObject
            {
                ["id"] = sequence, ["time"] = Epoch.AddSeconds(sequence).ToString("O"),
                ["level"] = sequence % 10 == 0 ? "WARNING" : sequence % 3 == 0 ? "DEBUG" : "INFO",
                ["message"] = $"[模拟] 第 {sequence} 条界面性能样本：日志筛选、换行与滚动。"
                    + (sequence % 7 == 0 ? " 这是一条较长的模拟消息，用于检查窄屏和多行布局，不包含游戏运行结果。" : ""),
            });
            if (sample.Logs.Count > OverviewViewModel.LogCapacity) sample.Logs.RemoveAt(0);
        }
    }

    public void Refresh()
    {
        if (_disposed) return;
        _cards = _samples.Keys.Select(name => InstanceCardViewModel.Create(name,
            _running == name ? "running" : "stopped", "模拟环境", "", _running == name ? _task : null,
            isDemo: true)).ToArray();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Check(CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(_disposed, this); }

    private Sample Get(string instance)
    {
        Check();
        return _samples.TryGetValue(instance, out var sample) ? sample
            : throw new InvalidOperationException("模拟实例不存在，请重置样本。");
    }

    private Task<T> Read<T>(Func<T> read, CancellationToken token)
    { Check(token); return Task.FromResult(read()); }

    private Task Write(Action change, CancellationToken token)
    { Check(token); change(); Refresh(); return Task.CompletedTask; }

    private ConfigResponse Config(string instance)
    {
        var sample = Get(instance);
        return new() { Instance = instance, Revision = sample.Version, Values = (JsonObject)sample.Values.DeepClone() };
    }

    public Task<InstanceListResponse> ReadInstancesAsync(CancellationToken cancellationToken = default)
        => Read(() => new InstanceListResponse { Instances = _samples.Select(item => new InstanceSummary
        {
            Instance = item.Key, Revision = item.Value.Version, Server = "模拟环境", Serial = "",
            Status = item.Key == _running ? "running" : "stopped", CurrentTask = item.Key == _running ? _task : null,
        }).ToArray() }, cancellationToken);

    public Task<SchemaResponse> ReadSchemaAsync(string language = "zh-CN", CancellationToken cancellationToken = default)
        => Read(() => new SchemaResponse { Menu = (JsonObject)_schema.Menu.DeepClone(),
            Args = (JsonObject)_schema.Args.DeepClone(), Translations = (JsonObject)_schema.Translations.DeepClone() }, cancellationToken);

    public Task<ConfigResponse> ReadConfigAsync(string instance, CancellationToken cancellationToken = default)
        => Read(() => Config(instance), cancellationToken);

    public Task<ConfigResponse> PatchConfigAsync(ConfigPatchRequest request, CancellationToken cancellationToken = default)
        => Read(() =>
        {
            var sample = Get(request.Instance);
            CheckRevision(request.Instance, request.Revision);
            var updated = (JsonObject)sample.Values.DeepClone();
            foreach (var change in request.Changes)
            {
                string[] path = change.Path.Split('.');
                if (path.Length != 3 || updated[path[0]]?[path[1]] is not JsonObject group || !group.ContainsKey(path[2]))
                    throw new ArgumentException("未知的模拟字段");
                group[path[2]] = change.Value?.DeepClone();
            }
            sample.Values = updated; sample.Revision++;
            Refresh();
            return Config(request.Instance);
        }, cancellationToken);

    private void CheckRevision(string instance, string? revision)
    {
        if (revision != Get(instance).Version)
        {
            var config = Config(instance);
            throw new TaskEditorConflictException(new JsonObject { ["instance"] = instance,
                ["revision"] = config.Revision, ["values"] = config.Values });
        }
    }

    public Task<ConfigResponse> CreateInstanceAsync(InstanceCreateRequest request, CancellationToken cancellationToken = default)
        => Read(() =>
        {
            string name = request.Instance.Trim();
            if (name.Length is < 1 or > 64 || _samples.ContainsKey(name))
                throw new ArgumentException("模拟实例名为空、过长或已存在");
            JsonObject values = request.ImportFile is { } file ? _imports[file]
                : request.Source is { Length: > 0 } source && source != "template" ? Get(source).Values : DefaultValues();
            _samples.Add(name, new Sample((JsonObject)values.DeepClone()));
            AppendLogsCore(name, 1); Refresh();
            return Config(name);
        }, cancellationToken);

    public Task DeleteInstanceAsync(InstanceDeleteRequest request, CancellationToken cancellationToken = default)
        => Write(() =>
        {
            CheckRevision(request.Instance, request.Revision);
            if (_running == request.Instance) throw new InvalidOperationException("请先停止模拟运行");
            _samples.Remove(request.Instance); _startup.Remove(request.Instance);
        }, cancellationToken);

    public Task<InstanceImportSource> ImportInstanceAsync(InstanceImportRequest request, CancellationToken cancellationToken = default)
        => Read(() =>
        {
            _imports[request.Name] = JsonNode.Parse(request.Content) as JsonObject
                ?? throw new ArgumentException("模拟配置必须是 JSON 对象");
            return new InstanceImportSource { Name = request.Name, ModifiedAt = Epoch };
        }, cancellationToken);

    public Task<InstanceImportListResponse> ReadInstanceImportsAsync(CancellationToken cancellationToken = default)
        => Read(() => new InstanceImportListResponse { Sources = _imports.Keys.Select(name => new InstanceImportSource
            { Name = name, ModifiedAt = Epoch }).ToArray() }, cancellationToken);

    public Task SaveQueueAsync(JsonObject queue, CancellationToken cancellationToken = default)
        => Write(() => _queue = (JsonObject)queue.DeepClone(), cancellationToken);
    public Task StartRunAsync(ControlRunRequest request, CancellationToken cancellationToken = default)
        => Write(() => { _queue = (JsonObject)request.Queue.DeepClone(); Start(_samples.Keys.First(), "模拟队列"); }, cancellationToken);
    public Task StartTaskAsync(InstanceTaskRunRequest request, CancellationToken cancellationToken = default)
        => Write(() => Start(request.Instance, request.Task), cancellationToken);
    public Task StartSchedulerAsync(InstanceSchedulerRunRequest request, CancellationToken cancellationToken = default)
        => Write(() => Start(request.Instance, "Reward"), cancellationToken);
    private void Start(string instance, string task)
    { Get(instance); _running = instance; _task = task; AppendLogsCore(instance, 1); }
    public Task<bool> RequestStopAsync(CancellationToken cancellationToken = default)
        => Read(() => { bool stopped = _running is not null; _running = null; Refresh(); return stopped; }, cancellationToken);

    public Task<JsonObject> ReadStateAsync(CancellationToken cancellationToken = default)
        => Read(() => Snapshot(_running ?? _samples.Keys.FirstOrDefault()), cancellationToken);
    public Task<JsonObject> ReadInstanceStateAsync(string instance, CancellationToken cancellationToken = default)
        => Read(() => { Get(instance); return Snapshot(instance); }, cancellationToken);
    public Task<JsonObject?> ReadReportAsync(string stamp, CancellationToken cancellationToken = default)
        => Read<JsonObject?>(() => null, cancellationToken);

    private JsonObject Snapshot(string? instance)
    {
        var observation = new JsonObject { ["instance"] = instance, ["phase"] = "running", ["task"] = _task,
            ["pending"] = new JsonArray(new JsonObject { ["name"] = "Commission", ["next_run"] = "模拟等待" }),
            ["waiting"] = new JsonArray(new JsonObject { ["name"] = "Dorm", ["next_run"] = "模拟等待" }),
            ["resources"] = new JsonArray(new[] { ("Oil", 14200), ("Coin", 186420), ("Gem", 2468), ("Cube", 384) }
                .Select(item => (JsonNode)new JsonObject { ["name"] = item.Item1, ["value"] = (double)item.Item2,
                    ["record"] = Epoch.ToString("O") }).ToArray()) };
        return new JsonObject { ["simulation"] = true, ["queue"] = _queue.DeepClone(),
            ["active"] = new JsonObject { ["instance"] = _running ?? instance, ["status"] = _running is null ? "idle" : "running",
                ["kind"] = "simulation", ["started_at"] = "ui-only-session", ["scheduler"] = observation.DeepClone() },
            ["overview"] = observation,
            ["recent_logs"] = instance is not null ? Get(instance).Logs.DeepClone() : new JsonArray() };
    }

    public Task<StartupRunResponse> ReadStartupRunAsync(string instance, CancellationToken cancellationToken = default)
        => Read(() => { Get(instance); return Startup(instance); }, cancellationToken);
    public Task<StartupRunResponse> SetStartupRunAsync(StartupRunRequest request, CancellationToken cancellationToken = default)
        => Read(() => { Get(request.Instance); if (request.Enabled) _startup.Add(request.Instance); else _startup.Remove(request.Instance);
            return Startup(request.Instance); }, cancellationToken);
    private StartupRunResponse Startup(string instance) => new() { Instance = instance,
        Enabled = _startup.Contains(instance), Run = _startup.Order(StringComparer.Ordinal).ToArray() };

    public Task<DeploySettingsResponse> ReadDeploySettingsAsync(string language = "zh-CN", CancellationToken cancellationToken = default)
        => Read(() => new DeploySettingsResponse { Demo = true, Notice = Notice, Groups = new JsonArray(
            DeployGroup("Gui", "界面测试设置", ("Theme", "样本主题", "text"), ("CheckUpdate", "模拟更新检查", "bool")),
            DeployGroup("RemoteAccess", "远程访问模拟", ("EnableRemoteAccess", "模拟开关", "bool")),
            DeployGroup("Webui", "网页模拟", ("WebuiPort", "模拟端口", "int"))) }, cancellationToken);
    private JsonObject DeployGroup(string key, string label, params (string Key, string Label, string Type)[] fields)
        => new() { ["key"] = key, ["label"] = label, ["fields"] = new JsonArray(fields.Select(field => (JsonNode)new JsonObject
            { ["key"] = field.Key, ["label"] = field.Label, ["type"] = field.Type, ["value"] = _deploy[field.Key]?.DeepClone(),
                ["help"] = "仅修改本次 UI 模拟数据" }).ToArray()) };
    public Task<DeploySettingsPatchResponse> PatchDeploySettingsAsync(DeploySettingsPatchRequest request, CancellationToken cancellationToken = default)
        => Read(() =>
        {
            if (request.Values.Any(pair => !_deploy.ContainsKey(pair.Key))) throw new ArgumentException("未知模拟设置");
            foreach (var pair in request.Values) _deploy[pair.Key] = pair.Value?.DeepClone();
            return new DeploySettingsPatchResponse { Updated = request.Values.Select(pair => pair.Key).ToArray() };
        }, cancellationToken);

    public Task<JsonObject> ReadStatisticsAsync(StatisticsRequest request, CancellationToken cancellationToken = default)
        => Read(() =>
        {
            Get(request.Instance);
            string month = string.IsNullOrWhiteSpace(request.Month) ? "2026-01" : request.Month;
            var points = new JsonArray(Enumerable.Range(0, 48).Select(i => (JsonNode)new JsonObject
                { ["time"] = $"{month}-01 {i / 2:00}:{(i % 2) * 30:00}:00", ["value"] = 10000 + i * 90 - (i % 5) * 200,
                    ["source"] = "UI 模拟样本" }).ToArray());
            return new JsonObject { ["instance"] = request.Instance, ["category"] = request.Category, ["month"] = month,
                ["metrics"] = new JsonArray(new JsonObject { ["label"] = "模拟样本", ["value"] = 48, ["unit"] = "条" }),
                ["series"] = new JsonArray(new JsonObject { ["key"] = "sample", ["label"] = "模拟曲线", ["points"] = points }),
                ["tables"] = new JsonArray(new JsonObject { ["title"] = "模拟明细", ["columns"] = new JsonArray("序号", "状态"),
                    ["rows"] = new JsonArray(Enumerable.Range(1, 80).Select(i => (JsonNode)new JsonArray(i, "模拟数据")).ToArray()) }),
                ["notes"] = new JsonArray(Notice) };
        }, cancellationToken);
    public Task<JsonObject> RefreshStatisticsLootAsync(string instance, CancellationToken cancellationToken = default)
        => Read(() => { Get(instance); return new JsonObject { ["simulation"] = true, ["notice"] = Notice }; }, cancellationToken);
    public Task<JsonObject> ReadMeowfficerAsync(MeowfficerRequest request, CancellationToken cancellationToken = default)
        => Read(() => { Get(request.Instance); return new JsonObject { ["instance"] = request.Instance,
            ["generatedAt"] = Epoch.ToString("O"), ["count"] = 0, ["cats"] = new JsonArray() }; }, cancellationToken);
    public Task<JsonObject> ClearMeowfficerAsync(string instance, CancellationToken cancellationToken = default)
        => Read(() => { Get(instance); return new JsonObject { ["cleared"] = true, ["simulation"] = true }; }, cancellationToken);
    public Task<JsonObject> ValidateShopStrategyAsync(string script, CancellationToken cancellationToken = default)
        => Read(() => new JsonObject { ["valid"] = false, ["diagnostics"] = new JsonArray(new JsonObject
            { ["code"] = "ui_only", ["message"] = "UI 隔离模式不执行后端脚本校验。" }) }, cancellationToken);

    private static SchemaResponse BuildSchema()
    {
        var args = new JsonObject(); var menu = new JsonObject(); var translations = new JsonObject();
        foreach (var (group, label, _, tasks, labels) in TaskCatalog.Groups)
        {
            menu[group] = new JsonObject { ["page"] = group == "Tool" ? "tool" : "setting",
                ["tasks"] = new JsonArray(tasks.Select(task => (JsonNode?)JsonValue.Create(task)).ToArray()) };
            for (int i = 0; i < tasks.Length; i++)
            {
                args[tasks[i]] = JsonNode.Parse("""
                    {"Scheduler":{"Enable":{"type":"checkbox","value":true},"Command":{"type":"input","value":"","display":"hide"}},
                     "Sample":{"Count":{"type":"input","value":3,"validate":[1,100]},
                               "Mode":{"type":"select","value":"normal","option":["normal","fast"]},
                               "Note":{"type":"textarea","value":"UI 隔离样本；此处不是实际游戏配置。"}}}
                    """);
                args[tasks[i]]!["Scheduler"]!["Command"]!["value"] = tasks[i];
                translations[$"Task.{tasks[i]}.name"] = labels[i] + "（模拟）";
            }
        }
        translations["Sample._info.name"] = "模拟字段";
        translations["Sample.Count.name"] = "模拟数量";
        translations["Sample.Mode.name"] = "模拟选项";
        translations["Sample.Note.name"] = "模拟备注";
        return new() { Menu = menu, Args = args, Translations = translations };
    }

    private JsonObject DefaultValues()
    {
        var values = new JsonObject();
        foreach (var (task, groups) in _schema.Args)
        {
            var taskValues = new JsonObject(); values[task] = taskValues;
            foreach (var (group, fields) in groups!.AsObject())
            {
                var groupValues = new JsonObject(); taskValues[group] = groupValues;
                foreach (var (key, definition) in fields!.AsObject()) groupValues[key] = definition?["value"]?.DeepClone();
            }
        }
        return values;
    }

    public void Dispose()
    { _disposed = true; Changed = null; _samples.Clear(); _imports.Clear(); _cards = []; _deploy.Clear(); _startup.Clear(); }
}
