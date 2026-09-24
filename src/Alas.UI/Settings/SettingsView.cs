using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Alas.UI.DeploySettings;

namespace Alas.UI.Settings;

/// <summary>
/// 系统设置页（上游 <c>/settings</c>）需要的**页面局部能力**：读取部署设置分组、提交改动。
/// 上游该页由 useDeploySettings 提供 data / error / edits.storageError / queue 四个状态，
/// 页面按这四种状态如实渲染（含两条独立的错误通道）。只依赖局部接口，不触碰共享外壳接线。
/// </summary>
public interface ISettingsBackend
{
    /// <summary>读取部署设置；返回 null 表示尚未取得数据（上游显示 Loading）。</summary>
    Task<SettingsSnapshot?> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>提交一条改动；失败必须抛出（可重试的连接类错误与不可重试的校验类错误都如实抛出）。</summary>
    Task SaveAsync(SettingsChange change, CancellationToken cancellationToken = default);
}

/// <summary>部署设置快照：分组 + 当前值 + 读取错误 + 本地暂存错误（上游 error 与 edits.storageError 两条通道）。</summary>
public sealed record SettingsSnapshot(
    IReadOnlyList<SettingsGroup> Groups,
    string? Error = null,
    string? StorageError = null,
    int QueuedChanges = 0,
    IReadOnlyDictionary<string, string?>? Values = null)
{
    public bool HasGroups => Groups.Count > 0;

    public string? Value(string key) => Values is not null && Values.TryGetValue(key, out var value) ? value : null;
}

/// <summary>
/// 一个设置分组（上游 DeployGroups 渲染的单元）。
/// Key 是上游用于归属过滤与 i18n 的分组键（<c>Gui.DeploySetting.Group{key}</c>），Title 是渲染出来的标题；
/// 上游按 **key** 过滤，因此标题被翻译成中文也不影响归属判断。缺省 key 时按标题处理。
/// </summary>
public sealed record SettingsGroup(string Title, IReadOnlyList<SettingsField> Fields, string? Key = null)
{
    /// <summary>用于归属判断的分组键。</summary>
    public string GroupKey => string.IsNullOrEmpty(Key) ? Title : Key;
}

/// <summary>被「远程访问」页认领的分组键（上游 app/settingsGroups.ts 的 REMOTE_ACCESS_GROUPS）。</summary>
public static class RemoteAccessGroups
{
    /// <summary>本页不渲染的分组键；其余分组即使后端新增也会照常显示，不会静默消失。</summary>
    public static IReadOnlyList<string> Keys { get; } = new[] { "RemoteAccess", "Webui" };
}

/// <summary>
/// 一条设置项。Kind 对应上游 FieldInput 的分支：text / number / int / checkbox(布尔开关) / select(有 options) /
/// multiselect / textarea / password / datetime / yaml；Options 供 select 与 multiselect 使用。
/// Help 对应上游 DeployGroups 在标签下渲染的字段说明（上游会剥掉其中的 HTML 标签）。
/// </summary>
public sealed record SettingsField(
    string Key,
    string Label,
    string Value,
    bool Editable,
    string Kind = "text",
    IReadOnlyList<string>? Options = null,
    bool IsSelected = false,
    string Help = "",
    string Error = "",
    bool Retryable = false,
    string Status = "",
    bool IsInteger = false,
    double? Min = null,
    double? Max = null)
{
    public bool IsBoolean => Kind is "checkbox" or "bool";

    public bool HasOptions => Options is { Count: > 0 };
}

/// <summary>一条待提交改动。</summary>
public sealed record SettingsChange(string Key, string Value);

/// <summary>
/// 把 <see cref="ISettingsBackend"/> 适配成部署设置提交通道。每次注入后端代数 +1：
/// 队列据此丢弃旧后端的在途回执，不会把旧连接的结果算到新连接头上。
/// </summary>
public sealed class SettingsTransport(ISettingsBackend backend, long generation) : IDeployTransport
{
    public string Identity => backend.GetType().Name;

    public long Generation => generation;

    public bool Ready => backend is not DisconnectedSettingsBackend;

    public Task SendAsync(string key, string payload, CancellationToken cancellationToken = default) =>
        backend.SaveAsync(new SettingsChange(key, payload), cancellationToken);
}

/// <summary>未接能力时的默认实现：读取返回固定的"不可用"说明，提交抛出不可重试错误，不伪造成功。</summary>
public sealed class DisconnectedSettingsBackend : ISettingsBackend
{
    public static DisconnectedSettingsBackend Instance { get; } = new();

    public const string Notice = "未连接 Alas.Core：部署设置由 Core 提供，当前不可用。";

    public Task<SettingsSnapshot?> ReadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<SettingsSnapshot?>(new SettingsSnapshot(Array.Empty<SettingsGroup>(), Notice));

    public Task SaveAsync(SettingsChange change, CancellationToken cancellationToken = default) =>
        throw new DeployTransportException(Notice, retryable: false);
}

/// <summary>
/// 系统设置页：标题「系统设置」+ 读取错误框 + 本地暂存错误框 +（加载中 / 设置分组）。
/// 分组渲染由两组页面共享的 <see cref="DeploySettingsSession"/> 提供：本页负责
/// 「除远程访问/WebUI 之外的全部」分组，按**分组键**归属，后端新增分组不会静默消失。
/// 无数据时停在加载态并显示原因，**不伪造任何设置项**。
/// </summary>
public sealed class SettingsView : UserControl
{
    private readonly SettingsViewModel _model;
    private readonly DeploySettingsSession _session;
    private long _generation;
    private readonly bool _ownsSession;
    private ISettingsBackend _backend;

    public SettingsView()
        : this(DisconnectedSettingsBackend.Instance)
    {
    }

    public SettingsView(ISettingsBackend backend)
        : this(backend, null)
    {
    }

    /// <summary>
    /// 用**已有的**部署草稿会话构造：系统设置页与远程访问页共用同一个会话实例，
    /// 因此来回导航不会丢未确认的输入，也不会各自建一条保存队列。
    /// </summary>
    public SettingsView(ISettingsBackend backend, DeploySettingsSession? session)
    {
        _backend = backend;
        _generation = 1;
        _ownsSession = session is null;
        _session = session ?? CreateSession(backend);

        _model = new SettingsViewModel(_session);
        DataContext = _model;
        Content = Build();
        // 构造时就把首次读取落定：页面首帧不能停在"还在加载"。
        _session.AttachBackend();
    }

    public SettingsViewModel Model => _model;

    /// <summary>本页与远程访问页共享的部署设置会话（草稿队列就在里面）。</summary>
    public DeploySettingsSession Session => _session;

    /// <summary>
    /// 外壳注入点（按 master 的承载约定）：设置后按新后端重新读取。
    /// 未确认的草稿**保留**（上游队列按作用域取单例），但它们只会提交给自己那一代的后端。
    /// </summary>
    public ISettingsBackend Backend
    {
        get => _backend;
        set
        {
            _backend = value;
            _generation++;
            if (_ownsSession)
            {
                _session.Reader = DeploySchemaAdapter.Read(ReadAsync);
                _session.AttachBackend(new SettingsTransport(value, _generation));
            }
        }
    }

    private DeploySettingsSession CreateSession(ISettingsBackend backend) =>
        new(new SettingsTransport(backend, _generation), DeploySchemaAdapter.Read(ReadAsync));

    /// <summary>读取后端快照；翻译交给共享适配器（分组键沿用后端的归属键）。</summary>
    private Task<SettingsSnapshot?> ReadAsync(CancellationToken cancellationToken) =>
        _backend.ReadAsync(cancellationToken);

    private Control Build()
    {
        var title = new TextBlock
        {
            Name = "SettingsTitle", Text = "系统设置",
            // 外壳的页面标题样式（32/窄屏 40），不写死字号。
            Classes = { "page-title" },
            Margin = new Thickness(0, 28, 0, 14),
        };
        var error = new TextBlock
        {
            Name = "SettingsErrorBox", FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false,
        };
        var storageError = new TextBlock
        {
            Name = "SettingsStorageErrorBox", FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false,
        };
        var loading = new TextBlock { Name = "SettingsLoading", Text = "正在加载部署设置…", FontSize = 13 };
        var empty = new TextBlock
        {
            Name = "SettingsEmptyState", Text = "没有可显示的部署设置。", FontSize = 12, IsVisible = false,
        };
        var groups = new StackPanel { Name = "SettingsGroups", Spacing = 0 };
        // 页面级的重试入口：与每个字段行上的「重试保存」是同一个命令，失败时按失败条数说明。
        // 输入即提交，页面终态因此完全由队列决定——没有待重试项时按钮禁用，不做假的"应用改动"。
        var retryLabel = new TextBlock { Name = "SettingsRetryLabel", FontSize = 12, IsVisible = false };
        var retryAll = new Button
        {
            Name = "SettingsRetryButton", Content = "重试保存", Padding = new Thickness(12, 7),
            HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = false,
            Command = _model.RetryFailedCommand,
        };
        var retryRow = new StackPanel
        {
            Name = "SettingsRetryRow", Orientation = Orientation.Horizontal, Spacing = 10,
            Children = { retryAll, retryLabel },
        };

        void Refresh()
        {
            error.Text = _session.Error;
            error.IsVisible = _session.HasError;
            storageError.Text = _session.StorageError ?? string.Empty;
            storageError.IsVisible = _session.HasStorageError;
            loading.IsVisible = _session.IsLoading;
            empty.IsVisible = _session.HasData && !_session.HasError && _session.SystemGroups.Count == 0;
            groups.IsVisible = _session.SystemGroups.Count > 0;
            var failures = _session.Edits.Snapshot().Values
                .Count(edit => edit.Status == DeployEditStatus.Error && edit.Retryable);
            retryAll.IsEnabled = failures > 0;
            retryLabel.IsVisible = failures > 0;
            retryLabel.Text = failures > 0
                ? $"有 {failures} 项改动保存失败，可重试；具体原因见对应字段。"
                : string.Empty;
            DeployGroupsView.Render(groups, _session.SystemGroups, _session);
        }

        void SessionChanged(object? sender, PropertyChangedEventArgs args) => Refresh();
        AttachedToVisualTree += (_, _) =>
        {
            _model.Connect();
            _session.PropertyChanged += SessionChanged;
            _session.Edits.Changed += Refresh;
            Refresh();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _model.Disconnect();
            _session.PropertyChanged -= SessionChanged;
            _session.Edits.Changed -= Refresh;
        };

        Refresh();
        return new StackPanel
        {
            Name = "SettingsPage", Spacing = 10,
            Children = { title, error, storageError, loading, empty, retryRow, groups },
        };
    }
}

/// <summary>页面状态：直接转发共享会话的分组、两条错误通道与加载态，页面不自己保存任何草稿。</summary>
public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly DeploySettingsSession _session;
    private bool _subscribed;

    public SettingsViewModel(DeploySettingsSession session)
    {
        _session = session;
        Connect();
    }

    /// <summary>本页与远程访问页共享的草稿会话。</summary>
    public DeploySettingsSession Session => _session;

    /// <summary>本页负责渲染的分组（除远程访问/WebUI 之外的全部）。</summary>
    public IReadOnlyList<DeployGroup> Groups => _session.SystemGroups;

    /// <summary>
    /// 草稿队列的重试入口（上游 EditStatus 的「重试保存」）：把可重试的失败输入重新排队并立即冲刷。
    /// 每个字段行上的「重试保存」按钮都指向它。
    /// </summary>
    public ICommand RetryFailedCommand => _session.RetryFailedCommand;

    /// <summary>本页是否已取得数据（分组为空也可能是"连上了但没有设置项"）。</summary>
    public bool HasGroups => _session.SystemGroups.Count > 0;

    public string Error => _session.Error;
    public string? StorageError => _session.StorageError;
    public bool HasError => _session.HasError;
    public bool HasStorageError => _session.HasStorageError;
    public bool IsLoading => _session.IsLoading;
    public bool IsEmpty => _session.HasData && !_session.HasError && _session.SystemGroups.Count == 0;

    /// <summary>草稿队列里未确认的输入条数（上游 queue 语义：应用完成后归零）。</summary>
    public int QueuedChanges => _session.Edits.Snapshot().Values.Count(edit => edit.Status != DeployEditStatus.Saved);

    public bool HasQueuedChanges => QueuedChanges > 0;

    /// <summary>待应用改动文案；没有待应用项时为空（页面上不显示该行）。</summary>
    public string QueueLabel => QueuedChanges == 0 ? string.Empty : $"待应用 {QueuedChanges} 项改动";

    /// <summary>用户在输入框里改过的值（键 → 原文）；未改过的键不出现。上游 <c>edits.edits[key]?.value</c>。</summary>
    public IReadOnlyDictionary<string, string?> Edits => _session.Edits.Snapshot()
        .Where(pair => pair.Value.Status != DeployEditStatus.Saved)
        .ToDictionary(pair => pair.Key, pair => pair.Value.Value);

    /// <summary>该字段当前的本地校验错误（无则 null）；与后端下发的错误分开。</summary>
    public string? ValidationError(string key) =>
        _session.Edits.Edit(key) is { Status: DeployEditStatus.Error } edit && !edit.Retryable ? edit.Error : null;

    public bool HasValidationError => _session.Edits.Snapshot().Values
        .Any(edit => edit.Status == DeployEditStatus.Error && !edit.Retryable);

    /// <summary>直接取值（测试与诊断用）。</summary>
    public string? EditedValue(string key) => _session.Edits.Edit(key)?.Value;

    /// <summary>冲刷草稿队列（等待所有未确认输入提交完成）。</summary>
    public Task ApplyAsync(CancellationToken cancellationToken = default) => _session.FlushAsync();

    internal void Connect()
    {
        if (_subscribed) return;
        _subscribed = true;
        _session.PropertyChanged += SessionChanged;
    }

    internal void Disconnect()
    {
        if (!_subscribed) return;
        _subscribed = false;
        _session.PropertyChanged -= SessionChanged;
    }

    private void SessionChanged(object? sender, PropertyChangedEventArgs args) =>
        Notify(args.PropertyName ?? string.Empty);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        // 派生属性跟着一起通知，避免界面只更新一半。
        foreach (var derived in new[]
                 {
                     nameof(Groups), nameof(HasGroups), nameof(Error), nameof(StorageError), nameof(HasError),
                     nameof(HasStorageError), nameof(IsLoading), nameof(IsEmpty), nameof(QueuedChanges),
                     nameof(HasQueuedChanges), nameof(QueueLabel), nameof(Edits), nameof(HasValidationError),
                 })
        {
            if (derived != propertyName) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(derived));
        }
    }
}

internal sealed class SettingsCommand(Action<object?> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute(parameter);
}
