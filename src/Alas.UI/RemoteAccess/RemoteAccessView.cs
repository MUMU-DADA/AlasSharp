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

namespace Alas.UI.RemoteAccess;

/// <summary>异步剪贴板委托：成功返回 true。实现由平台层提供，界面只消费结果。</summary>
public delegate Task<bool> DeployClipboard(string text);

/// <summary>
/// 远程访问页（上游 <c>/remote</c>）需要的**页面局部能力**：读取远程访问状态与地址。
/// 与配置管理页同样只依赖局部接口，不触碰共享外壳接线与后端实现。
/// </summary>
public interface IRemoteAccessBackend
{
    /// <summary>读取当前远程访问状态与它负责的分组（远程访问 / WebUI）；失败时返回带 Error 的结果而不抛异常。</summary>
    Task<RemoteAccessSchema> ReadStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>启用/停用远程访问。未接能力时必须返回带 Error 的结果。</summary>
    Task<RemoteAccessStatus> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
}

/// <summary>
/// 远程访问页的一次读取结果：远程连接的**原始状态串**（上游 provider 的 get_connection_state 取值）
/// 与地址，以及本页负责渲染的分组（按**分组键**归属：RemoteAccess / Webui，与系统设置页互补）。
/// 四档归并由 <see cref="DeployRemoteStatus"/> 按上游 remoteStatus() 完成，页面不自己推断。
/// </summary>
public sealed record RemoteAccessSchema(
    string State,
    string Address,
    IReadOnlyList<Settings.SettingsGroup>? Groups = null,
    string? Error = null);

/// <summary>远程访问状态：State 取 upstream 的四种（disabled/starting/ready/failed）。</summary>
public sealed record RemoteAccessStatus(string State, string Address, string? Error = null)
{
    public bool IsDisabled => State == "disabled";
    public bool IsStarting => State == "starting";
    public bool IsReady => State == "ready";
    public bool IsFailed => State == "failed";

    /// <summary>状态文案与上游 i18n 一致（remote.stateDisabled / Starting / Ready / Failed）。</summary>
    public string StateLabel => State switch
    {
        "starting" => "启动中",
        "ready" => "已连接",
        "failed" => "连接失败",
        _ => "未启用",
    };

    /// <summary>上游 remoteStatus() 用的原始连接状态串（四档之外的取值按失败处理）。</summary>
    public string? RawState => State switch
    {
        "ready" => "direct_p2p",
        "starting" => "starting",
        "failed" => "failed",
        _ => null,
    };
}

/// <summary>未接能力时的默认实现：一切操作如实报告不可用，不伪造已连接。</summary>
public sealed class DisconnectedRemoteAccessBackend : IRemoteAccessBackend
{
    public static DisconnectedRemoteAccessBackend Instance { get; } = new();

    public const string Notice = "未连接 Alas.Core：远程访问状态由 Core 提供，当前不可用。";

    public Task<RemoteAccessSchema> ReadStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new RemoteAccessSchema("disabled", string.Empty, Error: Notice));

    public Task<RemoteAccessStatus> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
        Task.FromResult(new RemoteAccessStatus("disabled", string.Empty, Notice));
}

/// <summary>
/// 把 <see cref="IRemoteAccessBackend"/> 适配成共享会话的提交通道。
/// 远程访问页与系统设置页共用同一份会话，因此这里只承担"这一代后端是谁"，
/// 具体提交仍由系统设置页通道承担（远程页只负责渲染远程访问/WebUI 两组设置）。
/// </summary>
internal sealed class RemoteAccessTransport(IRemoteAccessBackend backend, long generation) : IDeployTransport
{
    public string Identity => backend.GetType().Name;

    public long Generation => generation;

    public bool Ready => backend is not DisconnectedRemoteAccessBackend;

    public Task SendAsync(string key, string payload, CancellationToken cancellationToken = default) =>
        throw new DeployTransportException(
            "远程访问页负责展示远程连接与 WebUI 分组；提交由系统设置页的通道承担。", retryable: false);
}

/// <summary>
/// 把 <see cref="IRemoteAccessBackend"/> 的读取包成共享会话的读取函数：远程访问页与系统设置页
/// 共用同一份会话与同一条草稿队列，但远程四状态仍来自本后端——状态串由后端给出，页面不自己推断。
/// </summary>
public static class RemoteAccessSchemaReader
{
    public static async Task<DeploySchema?> Read(IRemoteAccessBackend backend, CancellationToken cancellationToken)
    {
        var schema = await backend.ReadStatusAsync(cancellationToken);
        var groups = (schema.Groups ?? Array.Empty<Settings.SettingsGroup>())
            .Select(group => new DeployGroup(
                group.GroupKey, group.Title,
                group.Fields.Select(field => new DeployFieldSpec(
                    field.Key, field.Label, field.Kind, field.Help, field.Options,
                    PreserveEmpty: false, field.Min, field.Max, field.IsInteger || field.Kind == "int",
                    ReadOnly: !field.Editable)).ToList()))
            .ToList();
        var values = new Dictionary<string, string?>();
        foreach (var field in (schema.Groups ?? Array.Empty<Settings.SettingsGroup>())
                 .SelectMany(group => group.Fields))
        {
            values[field.Key] = field.Value;
        }
        var remote = new DeployRemoteStatus(
            schema.State,
            // 已启用＝已经拿到连接状态（上游 remote.enabled）：未启用的 provider 不报连接状态。
            !string.IsNullOrEmpty(schema.State) && schema.State != "disabled",
            schema.Address, schema.Error ?? string.Empty);
        return new DeploySchema(groups, values, remote, schema.Error);
    }
}

/// <summary>
/// 远程访问页：标题「远程访问地址」+ 远程卡片（地址、四状态、错误、复制、启用/停用）
/// + 远程访问与 WebUI 两组设置（上游 DeployGroups only={REMOTE_ACCESS_GROUPS}）。
///
/// 「只交地址卡」是不合格的：分包与 WebUI 设置必须一起渲染，且分组按 **key** 归属。
/// 未连接时显示真实空/错误态与原因，不显示任何伪造地址或“已连接”。
/// </summary>
public sealed class RemoteAccessView : UserControl
{
    private readonly RemoteAccessViewModel _model;
    private readonly DeploySettingsSession _session;
    private IRemoteAccessBackend _backend;
    private long _generation;
    private readonly bool _ownsSession;

    public RemoteAccessView()
        : this(DisconnectedRemoteAccessBackend.Instance)
    {
    }

    public RemoteAccessView(IRemoteAccessBackend backend)
        : this(backend, null, null)
    {
    }

    /// <summary>
    /// 用**已有的**部署草稿会话构造：与系统设置页共享同一份会话与同一条保存队列，
    /// 因此来回导航不丢输入，也不会各自建独立保存队列。
    /// <paramref name="clipboard"/> 是注入的异步剪贴板；为空时「复制」如实报告不可用，不假装已复制。
    /// </summary>
    public RemoteAccessView(IRemoteAccessBackend backend, DeploySettingsSession? session, DeployClipboard? clipboard)
    {
        _backend = backend;
        _generation = 1;
        _ownsSession = session is null;
        _session = session ?? new DeploySettingsSession(
            new RemoteAccessTransport(backend, _generation), ReadSchemaAsync);

        _model = new RemoteAccessViewModel(_session, clipboard)
        {
            ToggleBackend = backend is DisconnectedRemoteAccessBackend ? null : backend,
        };
        DataContext = _model;
        Content = Build();
        _session.AttachBackend();
    }

    public RemoteAccessViewModel Model => _model;

    /// <summary>本页与系统设置页共享的部署设置会话（草稿队列就在里面）。</summary>
    public DeploySettingsSession Session => _session;

    /// <summary>
    /// 外壳注入点：XAML 里声明的视图无法传构造参数，所以按 master 的承载约定暴露一个可写属性
    /// （设置后立即按新后端刷新）。未确认的草稿保留，它们只提交给自己那一代的后端。
    /// </summary>
    public IRemoteAccessBackend Backend
    {
        get => _backend;
        set
        {
            _backend = value;
            _generation++;
            _model.ToggleBackend = value is DisconnectedRemoteAccessBackend ? null : value;
            if (_ownsSession)
            {
                _session.Reader = ReadSchemaAsync;
                _session.AttachBackend(new RemoteAccessTransport(value, _generation));
            }
        }
    }

    /// <summary>读取远程状态，并把远程访问/WebUI 两组设置交给共享会话渲染。</summary>
    private Task<DeploySchema?> ReadSchemaAsync(CancellationToken cancellationToken) =>
        RemoteAccessSchemaReader.Read(_backend, cancellationToken);

    private Control Build()
    {
        var title = new TextBlock
        {
            Name = "RemoteAccessTitle",
            Text = "远程访问地址",
            // 外壳的页面标题样式（32/窄屏 40，字重与字距随皮肤），不写死字号。
            Classes = { "page-title" },
            Margin = new Thickness(0, 28, 0, 14),
        };

        var state = new TextBlock { Name = "RemoteStateLabel", Text = _model.StateLabel, FontSize = 12 };
        var address = new SelectableTextBlock
        {
            Name = "RemoteAddressText",
            Text = _model.Address,
            FontSize = 15,
            Margin = new Thickness(0, 6, 0, 0),
            IsVisible = _model.HasAddress,
        };
        var copy = new Button
        {
            Name = "RemoteCopyButton",
            Content = "复制",
            Padding = new Thickness(12, 7),
            HorizontalAlignment = HorizontalAlignment.Left,
            // 保持可见、按地址可用性禁用：整个藏起来时 Avalonia 不会把控件放进可视树
            // （不可见的控件不参与渲染），"没有地址时复制不可用"就既看不到也无从核对。
            IsEnabled = _model.HasAddress,
            Command = _model.CopyCommand,
        };
        var notice = new TextBlock
        {
            Name = "RemoteNotice",
            Text = _model.Notice,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = _model.HasNotice,
        };
        var copyError = new TextBlock { Name = "RemoteCopyError", FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        var error = new TextBlock
        {
            Name = "RemoteError", FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false,
        };
        var passwordHint = new TextBlock
        {
            Name = "RemotePasswordHint", Text = "远程访问密码在下方「远程访问」分组里设置。",
            FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap,
        };
        var toggle = new Button
        {
            Name = "RemoteToggleButton", Content = "启用", Padding = new Thickness(12, 7),
            HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = _model.CanToggle,
            Command = _model.ToggleCommand,
        };

        var card = new StackPanel
        {
            Name = "RemoteAccessCard",
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "远程访问", FontWeight = FontWeight.SemiBold }, state, address, notice, error,
                passwordHint, copyError,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { copy, toggle } },
            },
        };

        var remoteGroups = new StackPanel { Name = "RemoteAccessGroups", Spacing = 0 };

        void Refresh()
        {
            state.Text = _model.StateLabel;
            address.Text = _model.Address;
            address.IsVisible = _model.HasAddress;
            copy.IsEnabled = _model.HasAddress;
            copy.Content = _model.CopyLabel;
            copyError.Text = _model.CopyError;
            copyError.IsVisible = _model.HasCopyError;
            notice.Text = _model.Notice;
            notice.IsVisible = _model.HasNotice;
            error.Text = _model.Error ?? string.Empty;
            error.IsVisible = _model.HasError;
            toggle.Content = _model.IsEnabled ? "停用" : "启用";
            toggle.IsEnabled = _model.CanToggle;
            DeployGroupsView.Render(remoteGroups, _session.RemoteGroups, _session);
        }

        void SessionChanged(object? sender, PropertyChangedEventArgs args) => Refresh();
        AttachedToVisualTree += (_, _) =>
        {
            _model.PropertyChanged += SessionChanged;
            _model.Connect();
            _session.PropertyChanged += SessionChanged;
            _session.Edits.Changed += Refresh;
            Refresh();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _model.PropertyChanged -= SessionChanged;
            _model.Disconnect();
            _session.PropertyChanged -= SessionChanged;
            _session.Edits.Changed -= Refresh;
        };
        Refresh();

        return new StackPanel
        {
            Name = "RemoteAccessPage",
            Spacing = 10,
            Children =
            {
                title,
                // 上游是 section.panel.config-group：卡片外观取自皮肤的 .panel 样式。
                new Border
                {
                    Name = "RemoteAccessPanel",
                    Classes = { "panel" },
                    Padding = new Thickness(16, 14),
                    Child = card,
                },
                remoteGroups,
            },
        };
    }
}

/// <summary>
/// 页面状态：远程卡片的四状态/地址/错误来自共享会话，复制走注入的异步剪贴板。
/// 未注入剪贴板时「复制」如实报告不可用，**不显示已复制**。
/// </summary>
public sealed class RemoteAccessViewModel : INotifyPropertyChanged
{
    private readonly DeploySettingsSession _session;
    private bool _subscribed;
    private readonly DeployClipboard? _clipboard;
    private IRemoteAccessBackend? _backend;
    private bool _copied;
    private string? _copyError;
    private string? _toggleError;

    public RemoteAccessViewModel(DeploySettingsSession session, DeployClipboard? clipboard = null)
    {
        _session = session;
        _clipboard = clipboard;
        CopyCommand = new RemoteCommand(_ => _ = CopyAsync());
        ToggleCommand = new RemoteCommand(_ => _ = ToggleAsync());
        Connect();
    }

    public ICommand CopyCommand { get; }

    /// <summary>启用/停用远程访问（需外壳注入后端；未注入时不改变任何显示状态）。</summary>
    public ICommand ToggleCommand { get; }

    /// <summary>启用/停用所需的后端能力（外壳注入；为空时动作不可用）。</summary>
    public IRemoteAccessBackend? ToggleBackend
    {
        get => _backend;
        set
        {
            _backend = value;
            Notify(nameof(CanToggle));
        }
    }

    public DeploySettingsSession Session => _session;

    /// <summary>四种状态文案（上游 remote.stateDisabled / Starting / Ready / Failed）。</summary>
    public string StateLabel => DeployRemoteStatus.Label(_session.RemoteKind);

    public string Address => _session.RemoteRaw.Address;

    /// <summary>远程连接错误原文（上游 data.remote.error）。</summary>
    public string? Error => _toggleError ?? (string.IsNullOrEmpty(_session.RemoteRaw.Error) ? null : _session.RemoteRaw.Error);

    public bool HasError => Error is not null;

    /// <summary>未接能力时的原因说明（未连接才显示，不伪造地址）。</summary>
    public string Notice => _session.HasError ? _session.Error
        : _backend is null ? DisconnectedRemoteAccessBackend.Notice : string.Empty;

    public bool HasNotice => !string.IsNullOrEmpty(Notice);

    public bool HasAddress => !string.IsNullOrEmpty(Address);

    /// <summary>启用状态取后端标志；启动中和连接失败仍允许停用。</summary>
    public bool IsEnabled => _session.RemoteRaw.Enabled;

    /// <summary>已取得远程状态且有操作通道时允许启用/停用，连接失败不阻止停用。</summary>
    public bool CanToggle => _backend is not null && _session.HasData;

    /// <summary>「复制」按钮文案：复制成功后才显示「已复制」。</summary>
    public string CopyLabel => _copied ? "已复制" : "复制";

    /// <summary>复制失败的真实原因（未注入剪贴板能力也走这条通道）。</summary>
    public string? CopyError => _copyError;

    public bool HasCopyError => !string.IsNullOrEmpty(_copyError);

    /// <summary>
    /// 复制地址：调用注入的异步剪贴板，**成功才显示已复制**；失败保留可选择地址并给出真实原因。
    /// </summary>
    public async Task CopyAsync(CancellationToken cancellationToken = default)
    {
        if (!HasAddress) return;
        if (_clipboard is null)
        {
            _copied = false;
            _copyError = "当前环境没有可用的剪贴板能力，请手动选择地址复制。";
            Notify(nameof(CopyLabel));
            Notify(nameof(CopyError));
            Notify(nameof(HasCopyError));
            return;
        }
        try
        {
            var ok = await _clipboard(Address);
            _copied = ok;
            _copyError = ok ? null : "剪贴板调用未成功，请手动选择地址复制。";
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            _copied = false;
            _copyError = "剪贴板不可用：" + failure.Message + " 请手动选择地址复制。";
        }
        Notify(nameof(CopyLabel));
        Notify(nameof(CopyError));
        Notify(nameof(HasCopyError));
    }

    /// <summary>读一次远程状态（页面创建时与外壳重新注入后端时调用）。</summary>
    public Task RefreshAsync(CancellationToken cancellationToken = default) => _session.RefreshAsync(cancellationToken);

    /// <summary>启用/停用远程访问：调用注入的后端能力，失败时刷新回真实状态，不伪造成功。</summary>
    public async Task ToggleAsync(CancellationToken cancellationToken = default)
    {
        if (_backend is null) return;
        try
        {
            var result = await _backend.SetEnabledAsync(!IsEnabled, cancellationToken);
            _toggleError = result.Error;
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            _toggleError = failure.Message;
        }
        await _session.RefreshAsync(cancellationToken);
        Notify(nameof(Error));
    }

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
        foreach (var derived in new[]
                 {
                     nameof(StateLabel), nameof(Address), nameof(HasAddress), nameof(IsEnabled), nameof(CanToggle),
                     nameof(Notice), nameof(HasNotice), nameof(Error), nameof(HasError),
                 })
        {
            if (derived != propertyName) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(derived));
        }
    }
}

internal sealed class RemoteCommand(Action<object?> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute(parameter);
}
