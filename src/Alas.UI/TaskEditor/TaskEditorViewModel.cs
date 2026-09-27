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

/// <summary>Engine queue editor. Runner input stays one JSON document owned by Alas.Engine.</summary>
public sealed class TaskEditorViewModel : EditorObservable
{
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
    public bool IsLoaded { get; private set; }
    public bool IsTool => false;
    public bool IsRunnable { get; private set; }
    public bool AutoSave { get; set; } = true;
    public bool IsBusy { get; private set; }
    public bool ConfirmRun { get; private set; }
    public string Error { get; private set; } = "";
    public string Message { get; private set; } = "";
    public bool HasChanges => Fields.Any(f => f.IsDirty);
    public bool HasConflicts => false;
    public int ChangedCount => Fields.Count(f => f.IsDirty);
    public int StateVersion => _stateVersion;
    public bool CanSave => IsLoaded && Backend is not null && !IsBusy && HasChanges &&
        Fields.All(f => f.Error.Length == 0);
    public bool CanRun => IsLoaded && IsRunnable && Backend is not null && !IsBusy &&
        Fields.All(f => f.Error.Length == 0);
    public string EditStatus => IsBusy ? "正在提交…" : HasChanges ? $"{ChangedCount} 项尚未保存" : "没有未保存的修改";
    public string Search
    {
        get => _search;
        set { if (_search == value) return; _search = value ?? ""; Notify(); }
    }
    public bool Matches(TaskFieldViewModel field) => field.IsVisible && (Search.Length == 0 ||
        $"{field.Label} {field.Group}.{field.Argument} {field.Help}".Contains(Search, StringComparison.OrdinalIgnoreCase));

    public void SetLoadError(string message)
    {
        Error = message ?? "Engine 任务加载失败";
        IsLoaded = false;
        Refresh();
    }

    public void LoadEngine(string instance, string kind, string title, JsonObject? input = null)
    {
        if (IsBusy || HasChanges) throw new InvalidOperationException("请先保存或放弃当前修改，再加载其他任务。");
        Instance = instance;
        TaskName = kind;
        Title = title;
        IsRunnable = true;
        Groups.Clear();
        var group = new TaskFieldGroup("Engine", "Engine 输入", "任务输入由 Engine runner 校验并记录到队列工件。");
        var value = input?.DeepClone() ?? new JsonObject();
        group.Fields.Add(new TaskFieldViewModel(kind, "Engine", "Input",
            new JsonObject { ["type"] = "json" }, value,
            (key, fallback) => key == "Engine.Input.name" ? "任务输入" : title,
            OnFieldChanged));
        Groups.Add(group);
        _search = "";
        Error = "";
        Message = "";
        ConfirmRun = false;
        IsLoaded = true;
        Notify(nameof(Groups));
        Refresh();
    }

    public void Discard()
    {
        if (IsBusy) return;
        _suppressAutoSave = true;
        try { foreach (var field in Fields) field.Restore(); }
        finally { _suppressAutoSave = false; }
        Error = "";
        Message = "已放弃未保存修改";
        ConfirmRun = false;
        Refresh();
    }

    public async Task<bool> SaveAsync(CancellationToken token = default)
    {
        if (!CanSave) return false;
        var input = Fields.SingleOrDefault(field => field.Argument == "Input")?.Value?.AsObject()
            ?? throw new InvalidDataException("Engine 输入必须是 JSON 对象");
        IsBusy = true; Error = ""; Message = ""; Refresh();
        try
        {
            var queue = await Backend!.SaveQueueAsync(Instance, TaskName, input, token).ConfigureAwait(false);
            foreach (var field in Fields) field.AcceptSaved(field.Value);
            Message = "Engine 队列已保存";
            return queue["tasks"] is JsonArray;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { Error = "队列保存请求已取消；请重新读取 Engine 状态确认。"; return false; }
        catch (Exception error) { Error = error.Message; return false; }
        finally { IsBusy = false; Refresh(); }
    }

    private void OnFieldChanged()
    {
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
            await Task.Delay(250, cancellation.Token).ConfigureAwait(false);
            if (!cancellation.IsCancellationRequested) await SaveAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    public void RequestRun() { if (CanRun) { ConfirmRun = true; Refresh(); } }
    public void CancelRun() { if (!IsBusy) { ConfirmRun = false; Refresh(); } }

    public async Task<bool> ConfirmRunAsync(CancellationToken token = default)
    {
        if (!ConfirmRun || _confirming || Backend is null) return false;
        _confirming = true;
        try
        {
            if (HasChanges && !await SaveAsync(token).ConfigureAwait(false)) return false;
            if (HasChanges) { Error = "仍有未保存的修改，运行请求尚未发送。"; Refresh(); return false; }
            var input = Fields.SingleOrDefault(field => field.Argument == "Input")?.Value?.AsObject()
                ?? throw new InvalidDataException("Engine 输入必须是 JSON 对象");
            IsBusy = true; Error = ""; Message = ""; Refresh();
            await Backend.RunQueueAsync(Instance, TaskName, input, token).ConfigureAwait(false);
            Message = "已提交 Engine 运行请求，请在任务日志中查看执行结果";
            ConfirmRun = false;
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { Error = "运行请求已取消；需查看 Engine 状态确认是否已启动。"; return false; }
        catch (Exception error) { Error = error.Message; return false; }
        finally { IsBusy = false; _confirming = false; Refresh(); }
    }

    private void Refresh()
    {
        _stateVersion++;
        Notify(nameof(StateVersion));
    }
}
