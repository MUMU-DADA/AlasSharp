using System.Collections.ObjectModel;
using System.Text.Json.Nodes;

namespace Alas.UI.TaskEditor;

public sealed class TaskFieldGroup(string key, string label, string help)
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    public string Help { get; } = help;
    public ObservableCollection<TaskFieldViewModel> Fields { get; } = [];
}

/// <summary>
/// A schema-driven editor for upstream config values. Keep one model per open instance/task when
/// navigating to preserve unsaved drafts. Load refuses to replace a dirty model; Discard is explicit.
/// </summary>
public sealed class TaskEditorViewModel : EditorObservable
{
    private JsonObject _translations = [];
    private JsonObject _values = [];
    private string _search = "";
    private ITaskEditorBackend? _backend;
    private bool _confirming;
    private CancellationTokenSource? _autoSaveCancellation;
    private bool _suppressAutoSave;
    private int _stateVersion;
    public ObservableCollection<TaskFieldGroup> Groups { get; } = [];
    public IEnumerable<TaskFieldViewModel> Fields => Groups.SelectMany(g => g.Fields);
    public ITaskEditorBackend? Backend
    {
        get => _backend;
        set { _backend = value; Refresh(); }
    }
    public string Instance { get; private set; } = "";
    public string TaskName { get; private set; } = "";
    public string Title { get; private set; } = "任务设置";
    public string Revision { get; private set; } = "";
    public bool IsLoaded { get; private set; }
    public bool IsTool { get; private set; }
    public bool IsRunnable { get; private set; }
    /// <summary>Matches the upstream EditQueue: ordinary fields save after a short debounce.</summary>
    public bool AutoSave { get; set; } = true;
    public bool IsBusy { get; private set; }
    public bool ConfirmRun { get; private set; }
    public string Error { get; private set; } = "";
    public string Message { get; private set; } = "";
    public bool HasChanges => Fields.Any(f => f.IsDirty);
    public bool HasConflicts => Fields.Any(f => f.HasConflict);
    public int ChangedCount => Fields.Count(f => f.IsDirty);
    /// <summary>Monotonic aggregate state signal for the view chrome.</summary>
    public int StateVersion => _stateVersion;
    public bool CanSave => IsLoaded && Backend is not null && !IsBusy &&
        Fields.Any(f => f.IsDirty && f.Kind != TaskFieldKind.Lua) && !HasConflicts &&
        Fields.All(f => !f.IsDirty || f.Kind == TaskFieldKind.Lua || f.Error.Length == 0);
    public bool CanRun => IsLoaded && IsRunnable && Backend is not null && !IsBusy && !HasConflicts &&
        // Restricted Lua drafts are a separate check/apply editor. A successful check alone must
        // never make the run button start the old server-side script.
        Fields.All(f => !f.IsDirty || (f.Kind != TaskFieldKind.Lua && f.Error.Length == 0));
    public string EditStatus => IsBusy ? "正在提交…" : HasConflicts ? "配置存在冲突，请逐项选择" : HasChanges ? $"{ChangedCount} 项尚未保存" : "没有未保存的修改";
    public string Search
    {
        get => _search;
        set { if (_search == value) return; _search = value ?? ""; Notify(); }
    }
    public bool Matches(TaskFieldViewModel field) => field.IsVisible && (Search.Length == 0 ||
        $"{field.Label} {field.Group}.{field.Argument} {field.Help}".Contains(Search, StringComparison.OrdinalIgnoreCase));

    /// <summary>Keep a failed schema/config load visible in the shared editor.</summary>
    public void SetLoadError(string message)
    {
        Error = message ?? "任务配置加载失败";
        IsLoaded = false;
        Refresh();
    }

    public void Load(string instance, string task, JsonObject schema, JsonObject config)
    {
        if (IsBusy || HasChanges) throw new InvalidOperationException("请先保存或放弃当前修改，再加载其他配置。");
        ValidateConfig(instance, config);
        if (schema["args"] is not JsonObject arguments) throw new ArgumentException("Schema 缺少 args 对象。", nameof(schema));
        Instance = instance; TaskName = task;
        _translations = schema["translations"] is JsonObject t ? (JsonObject)t.DeepClone() : [];
        _values = (JsonObject)config["values"]!.DeepClone();
        Revision = TaskFieldViewModel.String(config["revision"]);
        Title = Translate($"Task.{task}.name", task);
        IsTool = schema["menu"] is JsonObject menu && menu.Any(pair => pair.Value is JsonObject group &&
            TaskFieldViewModel.String(group["page"]) == "tool" && group["tasks"] is JsonArray tasks &&
            tasks.Any(item => TaskFieldViewModel.String(item) == task));
        // The upstream menu supplies the tool category; Core validates against
        // get_available_func() again when submitting. Never keep a tool-name table here.
        IsRunnable = arguments[task] is JsonObject && (IsTool ||
            !string.IsNullOrWhiteSpace(TaskFieldViewModel.String(arguments[task]?["Scheduler"]?["Command"]?["value"])));
        Groups.Clear();
        if (arguments[task] is JsonObject groups)
            foreach (var (groupName, groupNode) in groups)
            {
                if (groupNode is not JsonObject definitions) continue;
                var group = new TaskFieldGroup(groupName, Translate($"{groupName}._info.name", groupName), Translate($"{groupName}._info.help", ""));
                foreach (var (argument, node) in definitions)
                {
                    if (node is not JsonObject definition || argument == "_info") continue;
                    var value = FindValue(_values, task, groupName, argument, definition["value"]);
                    group.Fields.Add(new TaskFieldViewModel(task, groupName, argument, definition, value, Translate, OnFieldChanged));
                }
                Groups.Add(group);
            }
        _search = ""; Error = ""; Message = ""; ConfirmRun = false; IsLoaded = true;
        Notify(nameof(Groups)); Refresh();
    }

    public void Discard()
    {
        if (IsBusy) return;
        _suppressAutoSave = true;
        try { foreach (var field in Fields) field.Restore(); }
        finally { _suppressAutoSave = false; }
        Error = ""; Message = "已放弃未保存修改"; ConfirmRun = false; Refresh();
    }

    public async Task<bool> SaveAsync(CancellationToken token = default)
    {
        if (!CanSave) return false;
        var backend = Backend!;
        var changes = Fields.Where(f => f.IsDirty && f.Kind != TaskFieldKind.Lua)
            .Select(f => new TaskFieldChange(f.Path, f.Value)).ToArray();
        var submitted = changes.ToDictionary(c => c.Path, c => c.Value);
        IsBusy = true; Error = ""; Message = ""; Refresh();
        try
        {
            var config = await backend.SaveAsync(Instance, Revision, changes, token);
            ValidateConfig(Instance, config);
            var values = (JsonObject)config["values"]!;
            foreach (var field in Fields)
            {
                var saved = FindValue(values, TaskName, field.Group, field.Argument, field.Value);
                if (submitted.TryGetValue(field.Path, out var sent)) field.AcceptSaved(saved, sent);
                else field.ReconcileRemote(saved);
            }
            _values = (JsonObject)values.DeepClone();
            Revision = TaskFieldViewModel.String(config["revision"]);
            Message = "配置已保存";
            return true;
        }
        catch (TaskEditorConflictException conflict)
        {
            ValidateConfig(Instance, conflict.CurrentConfig);
            Reconcile(conflict.CurrentConfig);
            Error = HasConflicts ? conflict.Message : "配置版本已更新，已保留本地修改；请检查后再次保存。";
            return false;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { Error = "保存请求已取消，草稿仍保留；服务端是否已写入需重新读取确认。"; return false; }
        catch (Exception error) { Error = error.Message; return false; }
        finally { IsBusy = false; Refresh(); }
    }

    private void OnFieldChanged()
    {
        // The field raises its own notification for input controls. The aggregate notification
        // only updates the action/status chrome; the view must not rescan every row for each key.
        Notify(nameof(HasChanges));
        if (_suppressAutoSave || !AutoSave || Backend is null || IsBusy || !HasChanges) return;
        _autoSaveCancellation?.Cancel();
        var cancellation = _autoSaveCancellation = new CancellationTokenSource();
        _ = DebouncedSaveAsync(cancellation);
    }
    private async Task DebouncedSaveAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(250, cancellation.Token);
            if (!cancellation.IsCancellationRequested) await SaveAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    /// <summary>Merge an external refresh without losing drafts; conflicting fields require explicit choice.</summary>
    public void Reconcile(JsonObject config)
    {
        ValidateConfig(Instance, config);
        var values = (JsonObject)config["values"]!;
        foreach (var field in Fields)
            field.ReconcileRemote(FindValue(values, TaskName, field.Group, field.Argument, field.Value));
        _values = (JsonObject)values.DeepClone();
        Revision = TaskFieldViewModel.String(config["revision"]);
        Refresh();
    }

    public Task CheckScriptAsync(TaskFieldViewModel field, CancellationToken token = default)
    {
        if (Backend is null || !Fields.Contains(field) || IsBusy) return Task.CompletedTask;
        return field.CheckScriptAsync((script, ct) => Backend.ValidateScriptAsync(Instance, TaskName, script, ct), token);
    }
    public async Task<bool> ApplyScriptAsync(TaskFieldViewModel field, CancellationToken token = default)
    {
        if (Backend is null || !Fields.Contains(field) || field.Kind != TaskFieldKind.Lua || field.ReadOnly ||
            !field.ScriptValidated || IsBusy) return false;
        IsBusy = true; Error = ""; Message = ""; Refresh();
        try
        {
            // Applying a checked Lua draft is a single-field config.patch. Do not include unrelated
            // ordinary drafts: upstream keeps restricted Lua's check/apply transaction independent.
            var config = await Backend.SaveAsync(Instance, Revision,
                [new TaskFieldChange(field.Path, field.Value)], token);
            ValidateConfig(Instance, config);
            var values = (JsonObject)config["values"]!;
            field.AcceptScript(FindValue(values, TaskName, field.Group, field.Argument, field.Value));
            _values = (JsonObject)values.DeepClone();
            Revision = TaskFieldViewModel.String(config["revision"]);
            Message = "脚本已应用";
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { Error = "脚本应用请求已取消，草稿仍保留；服务端状态需重新读取确认。"; return false; }
        catch (Exception error) { Error = error.Message; return false; }
        finally { IsBusy = false; Refresh(); }
    }
    public void RequestRun() { if (CanRun) { ConfirmRun = true; Refresh(); } }
    public void CancelRun() { if (!IsBusy) { ConfirmRun = false; Refresh(); } }
    public async Task<bool> ConfirmRunAsync(CancellationToken token = default)
    {
        if (!ConfirmRun || !CanRun || _confirming) return false;
        _confirming = true;
        try
        {
            if (HasChanges && !await SaveAsync(token)) return false;
            if (HasChanges || HasConflicts) { Error = "仍有未保存的修改，运行请求尚未发送。"; Refresh(); return false; }
            IsBusy = true; Error = ""; Message = ""; Refresh();
            await Backend!.RunAsync(Instance, TaskName, token);
            Message = "已提交运行请求，请在任务日志中查看执行结果";
            ConfirmRun = false;
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { Error = "运行请求已取消；需查看服务状态确认是否已启动。"; return false; }
        catch (Exception error) { Error = error.Message; return false; }
        finally { IsBusy = false; _confirming = false; Refresh(); }
    }

    private string Translate(string key, string fallback)
    {
        if (_translations[key] is JsonValue flat && flat.TryGetValue<string>(out var direct)) return direct;
        JsonNode? value = _translations;
        foreach (var part in key.Split('.')) value = value is JsonObject obj ? obj[part] : null;
        return value is JsonValue j && j.TryGetValue<string>(out var text) ? text : fallback;
    }
    private void Refresh()
    {
        _stateVersion++;
        Notify(nameof(StateVersion));
    }
    private static JsonNode? FindValue(JsonObject values, string task, string group, string argument, JsonNode? fallback) =>
        values[task] is JsonObject taskValues && taskValues[group] is JsonObject groupValues && groupValues.TryGetPropertyValue(argument, out var value)
            ? value : fallback;
    private static void ValidateConfig(string instance, JsonObject config)
    {
        if (TaskFieldViewModel.String(config["instance"]) != instance || string.IsNullOrWhiteSpace(TaskFieldViewModel.String(config["revision"])) || config["values"] is not JsonObject)
            throw new ArgumentException("配置响应必须包含对应 instance、非空 revision 和 values 对象。");
    }
}
