using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Nodes;
using Avalonia.Threading;

namespace Alas.UI.DeploySettings;

/// <summary>
/// 一条草稿输入的状态。取值对应上游 <c>EditQueue</c> 的 <c>Edit.status</c>：
/// queued（等待提交）/ saving（正在提交）/ saved（已确认）/ error（失败，另见 <see cref="DeployEdit"/>）。
/// </summary>
public enum DeployEditStatus
{
    Queued,
    Saving,
    Saved,
    Error,
}

/// <summary>
/// 一条草稿输入：用户看到的原文 <see cref="Value"/> 与真正提交的 <see cref="Payload"/> 分开存放。
/// </summary>
public sealed record DeployEdit(
    string Payload,
    string? Value,
    long Sequence,
    DeployEditStatus Status,
    string? Error = null,
    bool Retryable = false,
    long BackendGeneration = 0);

/// <summary>提交失败：<see cref="Retryable"/> 为 false 表示服务端明确拒绝，重试也一定失败。</summary>
public sealed class DeployTransportException(string message, bool retryable = true) : Exception(message)
{
    public bool Retryable { get; } = retryable;
}

/// <summary>
/// 提交通道：页面之外的队列通过它把一条改动交给后端。
///
/// <see cref="Generation"/> 标识当前后端实例：换后端（重连、切换实例）时必须改变它，
/// 队列据此丢弃旧后端的在途回执——旧回执只能确认它自己那一代、且序号未变的那条输入。
/// </summary>
public interface IDeployTransport
{
    string Identity { get; }

    long Generation { get; }

    bool Ready { get; }

    Task SendAsync(string key, string payload, CancellationToken cancellationToken = default);
}

/// <summary>草稿的本地持久化（上游用 sessionStorage）；实现由平台层提供，失败不抛出而是由队列报告。</summary>
public interface IDeployDraftStore
{
    string? Read(string key);

    void Write(string key, string content);

    void Remove(string key);
}

/// <summary>延迟接口：重试用它排队，离屏检查注入假时钟即可确定性推进。</summary>
public interface IDeployClock
{
    void Delay(TimeSpan delay, Action action);
}

/// <summary>按真实时间延迟（产品路径用）。</summary>
public sealed class SystemDeployClock : IDeployClock
{
    public static SystemDeployClock Instance { get; } = new();

    public void Delay(TimeSpan delay, Action action) =>
        _ = Task.Delay(delay).ContinueWith(_ => action(), TaskScheduler.Default);
}

/// <summary>
/// 部署设置草稿队列（上游 <c>config/EditQueue.ts</c> 的部署作用域）。
///
/// 契约来自上游，逐条保留：
/// ① 输入立即保留为草稿并按序号**串行**提交，不按到达顺序并发写；
/// ② 回执只确认**它自己那一次输入**（序号相同、后端同代），较新的输入或另一后端的草稿不被清掉；
/// ③ 可重试失败保留输入、延迟重试；永久失败保留原文供用户修正，但不再自动重试；
/// ④ 只有未确认的输入参与持久化，持久化失败单独报告，不影响内存中的草稿。
/// </summary>
public sealed class DeployEditQueue
{
    private readonly object _gate = new();
    private IDeployTransport _transport;
    private long _transportRevision;
    private readonly IDeployDraftStore? _store;
    private readonly IDeployClock _clock;
    private readonly string _storageKey;
    private readonly Dictionary<string, DeployEdit> _edits = new();
    private long _sequence;
    private TaskCompletionSource? _running;
    private bool _retryScheduled;
    private bool _persisted;
    private TimeSpan _retryDelay = TimeSpan.FromSeconds(1);

    public DeployEditQueue(string scope, IDeployTransport transport, IDeployDraftStore? store = null,
        IDeployClock? clock = null)
    {
        _transport = transport;
        _store = store;
        _clock = clock ?? SystemDeployClock.Instance;
        _storageKey = "deploy-edits." + scope;
        Restore();
    }

    /// <summary>通知始终在 UI 线程发布；发送与持久化本身不依赖 UI 消息泵。</summary>
    public event Action? Changed;
    public string? StorageError { get; private set; }
    public bool Ready { get { lock (_gate) return _transport.Ready; } }

    /// <summary>替换实际发送通道；另一后端的草稿保留，旧连接的在途回执不确认新连接。</summary>
    public void UseTransport(IDeployTransport transport)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_transport, transport)) return;
            _transport = transport;
            _transportRevision++;
            foreach (var key in _edits.Keys.ToList())
            {
                var edit = _edits[key];
                if (edit.Status == DeployEditStatus.Saving)
                    _edits[key] = edit with { Status = DeployEditStatus.Queued };
            }
        }
        Publish();
    }

    public DeployEdit? Edit(string key)
    {
        lock (_gate) return _edits.TryGetValue(key, out var edit) ? edit : null;
    }

    public IReadOnlyDictionary<string, DeployEdit> Snapshot()
    {
        lock (_gate) return new Dictionary<string, DeployEdit>(_edits);
    }

    public bool HasPending(string key)
    {
        lock (_gate) return _edits.TryGetValue(key, out var edit) && edit.Status != DeployEditStatus.Saved;
    }

    public bool HasPendingFor(long generation)
    {
        lock (_gate) return _edits.Values.Any(edit =>
            edit.Status != DeployEditStatus.Saved && edit.BackendGeneration == generation);
    }

    public bool ShouldRetryOnReconnect()
    {
        lock (_gate) return _transport.Ready && _edits.Values.Any(edit =>
            edit.Status == DeployEditStatus.Error && edit.Retryable
            && edit.BackendGeneration == _transport.Generation);
    }

    public void Change(string key, string payload, string? value = null, string? error = null)
    {
        lock (_gate)
        {
            _edits[key] = new DeployEdit(payload, value, ++_sequence,
                error is null ? DeployEditStatus.Queued : DeployEditStatus.Error, error,
                Retryable: false, _transport.Generation);
        }
        Publish();
        if (error is null) _ = FlushAsync();
    }

    public void Retry()
    {
        long generation;
        lock (_gate) generation = _transport.Generation;
        ResumeAfterReconnect(generation);
    }

    public void ResumeAfterReconnect(long generation)
    {
        lock (_gate)
        {
            foreach (var key in _edits.Keys.ToList())
            {
                var edit = _edits[key];
                if (edit.Status == DeployEditStatus.Error && edit.Retryable
                    && edit.BackendGeneration == generation)
                    _edits[key] = edit with { Status = DeployEditStatus.Queued, Error = null, Retryable = false };
            }
        }
        Publish();
        _ = FlushAsync();
    }

    public void Reconcile(IReadOnlyDictionary<string, long> confirmed)
    {
        lock (_gate)
        {
            foreach (var (key, sequence) in confirmed)
                if (_edits.TryGetValue(key, out var edit) && edit.Status == DeployEditStatus.Saved
                    && edit.Sequence == sequence && edit.BackendGeneration == _transport.Generation)
                    _edits.Remove(key);
        }
        Publish();
    }

    public IReadOnlyDictionary<string, long> Confirmed()
    {
        lock (_gate) return _edits.Where(pair => pair.Value.Status == DeployEditStatus.Saved
            && pair.Value.BackendGeneration == _transport.Generation)
            .ToDictionary(pair => pair.Key, pair => pair.Value.Sequence);
    }

    /// <summary>锁内取得唯一排空者；同步完成和异步完成都共享同一完成句柄。</summary>
    public Task FlushAsync()
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_running is not null) return _running.Task;
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _running = completion;
        }
        _ = PumpAsync(completion);
        return completion.Task;
    }

    private async Task PumpAsync(TaskCompletionSource completion)
    {
        try
        {
            while (true)
            {
                string key;
                DeployEdit entry;
                IDeployTransport transport;
                long revision;
                lock (_gate)
                {
                    var next = NextQueuedLocked();
                    if (!_transport.Ready || next is null)
                    {
                        // 检查排空与释放所有权在同一把锁内，后来的 Change 会启动下一次泵。
                        _running = null;
                        completion.TrySetResult();
                        return;
                    }
                    (key, entry) = next.Value;
                    transport = _transport;
                    revision = _transportRevision;
                    _edits[key] = entry with { Status = DeployEditStatus.Saving };
                }
                Publish();
                Exception? failure = null;
                try { await transport.SendAsync(key, entry.Payload).ConfigureAwait(false); }
                catch (Exception error) { failure = error; }
                var retry = false;
                lock (_gate)
                {
                    if (_transportRevision == revision && ReferenceEquals(_transport, transport)
                        && _edits.TryGetValue(key, out var current)
                        && current.Sequence == entry.Sequence
                        && current.BackendGeneration == entry.BackendGeneration
                        && _transport.Generation == entry.BackendGeneration)
                    {
                        if (failure is null)
                        {
                            _edits[key] = entry with { Status = DeployEditStatus.Saved };
                            _retryDelay = TimeSpan.FromSeconds(1);
                        }
                        else
                        {
                            // 通道取消同样保留可重试草稿，不能永久卡在 Saving。
                            retry = failure is not DeployTransportException rejected || rejected.Retryable;
                            _edits[key] = entry with
                            {
                                Status = DeployEditStatus.Error,
                                Error = failure is OperationCanceledException ? "保存被取消，输入已保留。" : failure.Message,
                                Retryable = retry,
                            };
                        }
                    }
                }
                Publish();
                if (retry) ScheduleRetry();
            }
        }
        catch (Exception failure)
        {
            lock (_gate) _running = null;
            completion.TrySetException(failure);
        }
    }

    private (string Key, DeployEdit Entry)? NextQueuedLocked()
    {
        (string Key, DeployEdit Entry)? best = null;
        foreach (var (key, edit) in _edits)
        {
            if (edit.Status != DeployEditStatus.Queued || edit.BackendGeneration != _transport.Generation) continue;
            if (best is null || edit.Sequence < best.Value.Entry.Sequence) best = (key, edit);
        }
        return best;
    }

    private void ScheduleRetry()
    {
        TimeSpan delay;
        lock (_gate)
        {
            if (_retryScheduled) return;
            _retryScheduled = true;
            delay = _retryDelay;
            _retryDelay = TimeSpan.FromMilliseconds(Math.Min(15000, delay.TotalMilliseconds * 2));
        }
        _clock.Delay(delay, () =>
        {
            lock (_gate) _retryScheduled = false;
            Retry();
        });
    }

    private void Publish()
    {
        // 快照与存储操作一起串行化，旧快照不能在新快照之后覆盖本地草稿。
        lock (_gate)
        {
            try
            {
                if (_store is null) throw new InvalidOperationException("没有可用的草稿存储。");
                var pending = _edits.Where(pair => pair.Value.Status != DeployEditStatus.Saved)
                    .ToDictionary(pair => pair.Key, pair => pair.Value);
                if (pending.Count == 0)
                {
                    if (_persisted) { _store.Remove(_storageKey); StorageError = null; }
                }
                else
                {
                    _store.Write(_storageKey, Serialize(pending));
                    StorageError = null;
                }
                _persisted = pending.Count > 0;
            }
            catch (Exception failure)
            {
                StorageError = "草稿无法写入本地存储：" + failure.Message + "（输入仍保留在当前页面会话中。）";
            }
        }
        NotifyChanged();
    }

    private void NotifyChanged()
    {
        if (Dispatcher.UIThread.CheckAccess()) Changed?.Invoke();
        else Dispatcher.UIThread.Post(() => Changed?.Invoke());
    }

    private void Restore()
    {
        try
        {
            var content = _store?.Read(_storageKey);
            if (string.IsNullOrEmpty(content)) return;
            _persisted = true;
            var restored = content.TrimStart().StartsWith('{')
                ? Deserialize(content)
                : DeserializeLegacy(content).ToDictionary(pair => pair.Key, pair => pair.Value);
            foreach (var (key, edit) in restored)
            {
                if (edit.Status == DeployEditStatus.Error && !edit.Retryable && string.IsNullOrEmpty(edit.Value)) continue;
                _edits[key] = edit.Status == DeployEditStatus.Error && !edit.Retryable
                    ? edit : edit with { Status = DeployEditStatus.Queued };
                _sequence = Math.Max(_sequence, edit.Sequence);
            }
        }
        catch (Exception failure) { StorageError = "草稿无法从本地存储读取：" + failure.Message; }
    }

    private static string Serialize(IReadOnlyDictionary<string, DeployEdit> edits)
    {
        var root = new JsonObject();
        foreach (var (key, edit) in edits)
            root[key] = new JsonObject
            {
                [nameof(DeployEdit.Payload)] = edit.Payload,
                [nameof(DeployEdit.Value)] = edit.Value,
                [nameof(DeployEdit.Sequence)] = edit.Sequence,
                [nameof(DeployEdit.Status)] = (int)edit.Status,
                [nameof(DeployEdit.Error)] = edit.Error,
                [nameof(DeployEdit.Retryable)] = edit.Retryable,
                [nameof(DeployEdit.BackendGeneration)] = edit.BackendGeneration,
            };
        return root.ToJsonString();
    }

    private static Dictionary<string, DeployEdit> Deserialize(string content)
    {
        var result = new Dictionary<string, DeployEdit>();
        var root = JsonNode.Parse(content)?.AsObject()
            ?? throw new FormatException("草稿必须是 JSON 对象。");
        foreach (var (key, value) in root)
        {
            var edit = value?.AsObject() ?? throw new FormatException("草稿字段必须是对象。");
            result[key] = new DeployEdit(
                edit[nameof(DeployEdit.Payload)]?.GetValue<string>() ?? string.Empty,
                edit[nameof(DeployEdit.Value)]?.GetValue<string>(),
                edit[nameof(DeployEdit.Sequence)]?.GetValue<long>() ?? 0,
                (DeployEditStatus)(edit[nameof(DeployEdit.Status)]?.GetValue<int>() ?? 0),
                edit[nameof(DeployEdit.Error)]?.GetValue<string>(),
                edit[nameof(DeployEdit.Retryable)]?.GetValue<bool>() ?? false,
                edit[nameof(DeployEdit.BackendGeneration)]?.GetValue<long>() ?? 0);
        }
        return result;
    }

    // 兼容旧版单行草稿；新写入采用 JSON 保留多行 YAML、制表符和空值的区别。
    private static IEnumerable<KeyValuePair<string, DeployEdit>> DeserializeLegacy(string content)
    {
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 6) continue;
            var status = Enum.TryParse<DeployEditStatus>(parts[4], out var parsed) ? parsed : DeployEditStatus.Queued;
            yield return new KeyValuePair<string, DeployEdit>(parts[0], new DeployEdit(
                parts[1], parts[2], long.TryParse(parts[3], out var sequence) ? sequence : 0, status,
                parts.Length > 7 ? parts[7].Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\\\", "\\") : null,
                parts[5] == "1", parts.Length > 6 && long.TryParse(parts[6], out var generation) ? generation : 0));
        }
    }
}

/// <summary>
/// 部署设置会话（上游 <c>useDeploySettings</c> + <c>editor('deploy')</c> 的合并体）：
/// **同一份数据与同一条草稿队列**由系统设置页与远程访问页共享，来回导航不丢未确认的输入。
/// </summary>
public sealed class DeploySettingsSession : INotifyPropertyChanged
{
    private const string RemoteAccessKey = "RemoteAccess";
    private const string WebuiKey = "Webui";

    private IDeployTransport _transport;
    private Func<CancellationToken, Task<DeploySchema?>> _read;
    private long _generation;
    private long _readSequence;
    private DeploySchema? _schema;
    private string _error = string.Empty;
    private bool _loading = true;
    private string? _storageError;

    public DeploySettingsSession(IDeployTransport transport, Func<CancellationToken, Task<DeploySchema?>> read,
        IDeployDraftStore? store = null, IDeployClock? clock = null)
    {
        _transport = transport;
        _read = read;
        Edits = new DeployEditQueue("deploy", transport, store, clock);
        RetryFailedCommand = new DeployCommand(_ => Edits.Retry());
        Edits.Changed += () =>
        {
            StorageError = Edits.StorageError;
            Notify(nameof(Edits));
        };
        StorageError = Edits.StorageError;
    }

    /// <summary>切换读取函数（外壳换后端时与提交通道一起更新）。</summary>
    public Func<CancellationToken, Task<DeploySchema?>> Reader
    {
        get => _read;
        set => _read = value;
    }

    /// <summary>
    /// 只换提交通道、不重新读取：同一个会话已经挂到另一个页面上时，
    /// 后挂的页面只改变"草稿提交到哪一代后端"，不改变已读到的数据由谁提供。
    /// </summary>
    public void UseTransport(IDeployTransport? transport)
    {
        if (transport is not null)
        {
            _transport = transport;
            Edits.UseTransport(transport);
        }
    }

    /// <summary>源数据或草稿发生变化时触发。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>两个页面共享的草稿队列。</summary>
    public DeployEditQueue Edits { get; }

    /// <summary>「重试保存」入口：把可重试的失败输入重新排队并立即冲刷。</summary>
    public System.Windows.Input.ICommand RetryFailedCommand { get; }

    /// <summary>读取到的分组（系统设置页与远程访问页各取所需，按**分组键**归属，不看翻译后的标题）。</summary>
    public IReadOnlyList<DeployGroup> Groups => _schema?.Groups ?? Array.Empty<DeployGroup>();

    /// <summary>远程访问的原始状态（未启用/启动中/已连接/连接失败的归并前形态）与地址。</summary>
    public DeployRemoteStatus RemoteRaw => _schema?.Remote ?? DeployRemoteStatus.Disabled;

    /// <summary>读取失败的真实消息（上游 error 通道）。</summary>
    public string Error { get => _error; private set => SetField(ref _error, value); }

    /// <summary>草稿持久化失败的真实消息（上游 edits.storageError 通道），与读取错误互不覆盖。</summary>
    public string? StorageError { get => _storageError; private set => SetField(ref _storageError, value); }

    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool HasStorageError => !string.IsNullOrEmpty(StorageError);

    /// <summary>未取得数据且连接就绪（上游 !data 时显示 Loading；未连接时显示真实原因而不是一直转圈）。</summary>
    public bool IsLoading => _loading && _schema is null;

    public bool HasData => _schema is not null;

    /// <summary>系统设置页负责的分组：除远程访问/WebUI 之外的全部（后端新增分组不会静默消失）。</summary>
    public IReadOnlyList<DeployGroup> SystemGroups => Groups
        .Where(group => !IsRemoteGroup(group)).ToList();

    /// <summary>远程访问页负责的分组（上游 REMOTE_ACCESS_GROUPS）。</summary>
    public IReadOnlyList<DeployGroup> RemoteGroups => Groups.Where(IsRemoteGroup).ToList();

    /// <summary>上游 app/settingsGroups.ts：被远程访问页认领的分组键。</summary>
    public static bool IsRemoteGroup(DeployGroup group) =>
        group.Key is RemoteAccessKey or WebuiKey;

    /// <summary>上游 app/remoteStatus.ts 的四档归并结果。</summary>
    public DeployRemoteState RemoteKind => DeployRemoteStatus.ToState(RemoteRaw);

    /// <summary>某字段当前生效的配置值（后端下发；缺失返回 null）。</summary>
    public string? CurrentValue(string key) => _schema?.Value(key);

    /// <summary>
    /// 绑定/切换后端（外壳注入新连接）：代数 +1，清掉读取错误与加载态并按新后端重新读取。
    /// 草稿**不清**——上游队列按作用域取单例，未确认的输入跨后端切换照旧保留；
    /// 它们只会提交给它自己那一代的后端（见 <see cref="DeployEditQueue"/>）。
    /// <paramref name="transport"/> 为空表示沿用当前通道，只按新代数重新读取。
    /// </summary>
    public void AttachBackend(IDeployTransport? transport = null)
    {
        if (transport is not null)
        {
            _transport = transport;
            Edits.UseTransport(transport);
        }
        _generation = _transport.Generation;
        Edits.ResumeAfterReconnect(_generation);
        _ = RefreshAsync();
    }

    /// <summary>
    /// 绑定后端并等待本次读取落定（页面构造用）：返回时数据、错误与加载态都已就位，
    /// 首次渲染不会停在"还在加载"。
    /// </summary>
    public Task AttachAndReadAsync(IDeployTransport? transport = null,
        CancellationToken cancellationToken = default)
    {
        if (transport is not null)
        {
            _transport = transport;
            Edits.UseTransport(transport);
        }
        _generation = _transport.Generation;
        Edits.ResumeAfterReconnect(_generation);
        return RefreshAsync(cancellationToken);
    }

    /// <summary>连接恢复：重试该后端上可重试的失败输入，并把队列里属于它的草稿继续冲刷。</summary>
    public void NotifyConnected()
    {
        Edits.ResumeAfterReconnect(_transport.Generation);
        _ = FlushAsync();
    }

    public Task FlushAsync() => Edits.FlushAsync();

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var request = Interlocked.Increment(ref _readSequence);
        var transport = _transport;
        var reader = _read;
        var generation = transport.Generation;
        Loading(true);
        var confirmed = Edits.Confirmed();
        bool IsCurrent() => request == Interlocked.Read(ref _readSequence)
            && ReferenceEquals(transport, _transport) && generation == _transport.Generation;
        try
        {
            var schema = await reader(cancellationToken).ConfigureAwait(false);
            await OnUiAsync(() =>
            {
                if (!IsCurrent()) return;
                cancellationToken.ThrowIfCancellationRequested();
                if (schema is null) { Fail(DisconnectedNotice); return; }
                _schema = schema;
                Error = schema.Error ?? string.Empty;
                _loading = false;
                Edits.Reconcile(confirmed);
                Publish();
            });
        }
        catch (OperationCanceledException)
        {
            await OnUiAsync(() => { if (IsCurrent()) Loading(false); });
            throw;
        }
        catch (Exception failure)
        {
            await OnUiAsync(() => { if (IsCurrent()) Fail(failure.Message); });
        }
    }

    private static Task OnUiAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) { action(); return Task.CompletedTask; }
        return Dispatcher.UIThread.InvokeAsync(action).GetTask();
    }

    /// <summary>等待一次读取落定（页面构造后调用，保证首次渲染前数据与错误都已就位）。</summary>
    public Task SettleAsync(CancellationToken cancellationToken = default) => RefreshAsync(cancellationToken);

    /// <summary>未接能力时的固定说明（不伪造设置项，也不假装是空配置）。</summary>
    public const string DisconnectedNotice = "未连接 Alas.Core：部署设置由 Core 提供，当前不可用。";

    private void Loading(bool value)
    {
        _loading = value;
        Notify(nameof(IsLoading));
    }

    private void Fail(string message)
    {
        _loading = false;
        Error = message;
        Notify(nameof(IsLoading));
        Notify(nameof(HasError));
        Publish();
    }

    private void Publish()
    {
        Notify(nameof(Groups));
        Notify(nameof(SystemGroups));
        Notify(nameof(RemoteGroups));
        Notify(nameof(RemoteRaw));
        Notify(nameof(RemoteKind));
        Notify(nameof(HasData));
        Notify(nameof(IsLoading));
        Notify(nameof(HasError));
    }

    private void Notify(string propertyName)
    {
        if (Dispatcher.UIThread.CheckAccess())
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        else Dispatcher.UIThread.Post(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName)));
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Notify(propertyName!);
    }
}

/// <summary>远程访问的四种展示状态（上游 app/remoteStatus.ts）。</summary>
public enum DeployRemoteState
{
    Disabled,
    Starting,
    Ready,
    Failed,
}

/// <summary>后端给出的原始远程访问状态串。</summary>
public sealed record DeployRemoteStatus(string? State, bool Enabled, string Address = "", string Error = "")
{
    public static DeployRemoteStatus Disabled { get; } = new(null, false);

    /// <summary>
    /// 上游 remoteStatus()：未启用 → 未启用；已可用地址（等待连接/直连/中继/SSH 转发）→ 已连接；
    /// 仍在连接或重连 → 启动中；**未知取值一律按失败处理**，避免界面显示成正常。
    /// </summary>
    public static DeployRemoteState ToState(DeployRemoteStatus status)
    {
        if (!status.Enabled) return DeployRemoteState.Disabled;
        return status.State switch
        {
            "waiting_peer" or "direct_p2p" or "turn_relay" or "ssh_forward" => DeployRemoteState.Ready,
            "starting" or "signaling" or "reconnecting" => DeployRemoteState.Starting,
            _ => DeployRemoteState.Failed,
        };
    }

    /// <summary>状态文案（上游 remote.stateDisabled / Starting / Ready / Failed）。</summary>
    public static string Label(DeployRemoteState state) => state switch
    {
        DeployRemoteState.Starting => "启动中",
        DeployRemoteState.Ready => "已连接",
        DeployRemoteState.Failed => "连接失败",
        _ => "未启用",
    };
}

/// <summary>一条部署设置字段（键、类型、标签、说明、校验与选项；值与草稿分开存放）。</summary>
public sealed record DeployFieldSpec(
    string Key,
    string Label,
    string Kind,
    string Help = "",
    IReadOnlyList<string>? Options = null,
    bool PreserveEmpty = false,
    double? Min = null,
    double? Max = null,
    bool Integer = false,
    bool ReadOnly = false);

/// <summary>一个部署设置分组；<see cref="Key"/> 是归属判断依据（不看翻译后的标题）。</summary>
public sealed record DeployGroup(string Key, string Title, IReadOnlyList<DeployFieldSpec> Fields);

/// <summary>一次读取的完整结果：分组、当前值、远程访问状态。值缺失表示该字段尚未取到。</summary>
public sealed record DeploySchema(
    IReadOnlyList<DeployGroup> Groups,
    IReadOnlyDictionary<string, string?> Values,
    DeployRemoteStatus Remote,
    string? Error = null)
{
    public string? Value(string key) => Values.TryGetValue(key, out var value) ? value : null;
}

/// <summary>
/// 未接能力时的默认会话：读取返回固定的"不可用"说明，提交一律失败，不伪造设置项或成功。
/// </summary>
public sealed class DisconnectedDeployTransport : IDeployTransport
{
    public static DisconnectedDeployTransport Instance { get; } = new();

    public string Identity => "disconnected";

    public long Generation => 0;

    public bool Ready => false;

    public Task SendAsync(string key, string payload, CancellationToken cancellationToken = default) =>
        throw new DeployTransportException(DeploySettingsSession.DisconnectedNotice, retryable: false);
}

/// <summary>部署设置里的简单命令（只转发一次动作，不维护可用状态）。</summary>
internal sealed class DeployCommand(Action<object?> execute) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute(parameter);
}
