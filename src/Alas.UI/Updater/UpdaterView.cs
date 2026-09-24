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
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace Alas.UI.Updater;

/// <summary>
/// 更新器页（上游 <c>/updater</c>）需要的**页面局部能力**：读取版本/提交信息、获取更新、应用更新、取消。
/// 与配置管理、远程访问同样只依赖局部接口，不触碰共享外壳接线与后端实现。
/// </summary>
public interface IUpdaterBackend
{
    /// <summary>读取本地/上游 HEAD 与待更新提交；失败时返回带 Error 的结果而不抛异常。</summary>
    Task<UpdaterStatus> ReadStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>获取更新（上游 updater.fetch）。</summary>
    Task<UpdaterStatus> FetchAsync(CancellationToken cancellationToken = default);

    /// <summary>应用更新（上游 updater.update）。</summary>
    Task<UpdaterStatus> ApplyAsync(CancellationToken cancellationToken = default);

    /// <summary>取消更新（上游 updater.cancel）。</summary>
    Task<UpdaterStatus> CancelAsync(CancellationToken cancellationToken = default);

    /// <summary>按页读取提交记录（上游每页 50 条，offset 为起始下标）。</summary>
    Task<UpdaterStatus> ReadCommitsAsync(int offset, CancellationToken cancellationToken = default);
}

/// <summary>更新器状态：State 取上游取值（idle/available/checking/fetch/apply/start/wait/reload/failed/finish/cancel）。</summary>
public sealed record UpdaterStatus(
    string State,
    string LocalHead,
    string UpstreamHead,
    int AheadCount,
    IReadOnlyList<UpdaterCommit> Commits,
    string? Error = null,
    int CommitsTotal = 0,
    int CommitOffset = 0)
{
    /// <summary>上游提交记录每页 50 条（Updater.tsx 的 offset/50）。</summary>
    public const int PageSize = 50;

    /// <summary>上游分页文案：`{offset+1}–{min(offset+50,total)} / {total}`。</summary>
    public string PageLabel => CommitOffset + Commits.Count == 0
        ? "0"
        : $"{CommitOffset + 1}–{Math.Min(CommitOffset + PageSize, Math.Max(CommitsTotal, CommitOffset + Commits.Count))} / {Math.Max(CommitsTotal, CommitOffset + Commits.Count)}";

    public static UpdaterStatus Disconnected { get; } = new("failed", string.Empty, string.Empty, 0,
        Array.Empty<UpdaterCommit>(), DisconnectedUpdaterBackend.Notice);

    public bool HasVersion => !string.IsNullOrEmpty(LocalHead) || !string.IsNullOrEmpty(UpstreamHead);

    public bool IsBusy => State is "checking" or "fetch" or "apply" or "start" or "wait" or "reload" or "cancel";

    // 上游 Updater.tsx：「获取更新」只受 disabled（未连接/忙）限制，与具体状态无关；
    // 「更新」由 data.canApply 控制，「取消更新」只在 canCancel 时渲染。
    public bool CanFetch => !IsBusy;

    public bool CanApply => State == "available";

    public bool CanCancel => State is "checking" or "fetch" or "start" or "wait" or "apply";

    /// <summary>关系文案：上游 updater.localAhead / localBehind / aheadCommits。</summary>
    public string Relation => AheadCount switch
    {
        > 0 => $"本地领先 {AheadCount} 个提交",
        < 0 => "本地落后",
        _ => "本地领先",
    };
}

/// <summary>一条待更新提交（上游 updater.commits）。</summary>
public sealed record UpdaterCommit(string Hash, string Subject);

/// <summary>未接能力时的默认实现：一切操作如实报告不可用，不伪造“已是最新”。</summary>
public sealed class DisconnectedUpdaterBackend : IUpdaterBackend
{
    public static DisconnectedUpdaterBackend Instance { get; } = new();

    public const string Notice = "未连接 Alas.Core：版本与更新由 Core 提供，当前不可用。";

    public Task<UpdaterStatus> ReadStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(UpdaterStatus.Disconnected);

    public Task<UpdaterStatus> FetchAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(UpdaterStatus.Disconnected);

    public Task<UpdaterStatus> ApplyAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(UpdaterStatus.Disconnected);

    public Task<UpdaterStatus> CancelAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(UpdaterStatus.Disconnected);

    public Task<UpdaterStatus> ReadCommitsAsync(int offset, CancellationToken cancellationToken = default) =>
        Task.FromResult(UpdaterStatus.Disconnected);
}

/// <summary>
/// 更新器页：标题「更新器」+ 状态、本地/上游 HEAD、关系、提交记录、三个动作。
/// 未连接时显示真实错误态与原因，**不显示「已是最新」**（那会伪造版本结论）。
/// </summary>
public sealed class UpdaterView : UserControl
{
    private readonly UpdaterViewModel _model;

    public UpdaterView()
        : this(DisconnectedUpdaterBackend.Instance)
    {
    }

    public UpdaterView(IUpdaterBackend backend)
    {
        _model = new UpdaterViewModel(backend);
        DataContext = _model;
        Content = Build();
        _ = _model.RefreshAsync();
    }

    public UpdaterViewModel Model => _model;

    /// <summary>
    /// 外壳注入点（按 master 的承载约定）：设置后由 VM 归零分页游标并按新后端刷新。
    /// 未注入时保持"未连接"的真实状态，不伪造版本信息。
    /// </summary>
    public IUpdaterBackend Backend
    {
        get => _model.Backend;
        set => _model.Backend = value;
    }

    /// <summary>HEAD 行文案：拿不到 SHA 时用上游 common.notFetched（未获取），不留空值。</summary>
    private static string HeadText(string label, string sha) =>
        string.IsNullOrEmpty(sha) ? label + "未获取" : label + sha;

    private Control Build()
    {
        var title = new TextBlock
        {
            Name = "UpdaterTitle", Text = "更新器",
            // 外壳的页面标题样式（32/窄屏 40），不写死字号。
            Classes = { "page-title" },
            Margin = new Thickness(0, 28, 0, 14),
        };
        var state = new TextBlock { Name = "UpdaterStateLabel", Text = _model.StateLabel, FontSize = 13 };
        var relation = new TextBlock { Name = "UpdaterRelation", Text = _model.Relation, FontSize = 12, IsVisible = _model.HasVersion };
        // 上游是每项一个 div.panel.head-card：本地/上游 HEAD 各一张小卡。
        // 上游 updater.localHead/upstreamHead + common.notFetched：拿不到 SHA 时显示「未获取」，
        // 而不是留一个空值（空值看起来像"加载中"或"没有这个信息"）。
        var localHeadText = new TextBlock
        {
            Name = "UpdaterLocalHead", FontSize = 12,
            Text = HeadText("本地 HEAD ", _model.LocalHead),
        };
        var upstreamHeadText = new TextBlock
        {
            Name = "UpdaterUpstreamHead", FontSize = 12,
            Text = HeadText("上游 HEAD ", _model.UpstreamHead),
        };
        var heads = new StackPanel
        {
            Name = "UpdaterHeads", Orientation = Orientation.Horizontal, Spacing = 8, IsVisible = _model.HasVersion,
            Children =
            {
                new Border { Name = "UpdaterLocalHeadCard", Classes = { "panel" }, Padding = new Thickness(12, 8), Child = localHeadText },
                new Border { Name = "UpdaterUpstreamHeadCard", Classes = { "panel" }, Padding = new Thickness(12, 8), Child = upstreamHeadText },
            },
        };
        var notice = new TextBlock
        {
            Name = "UpdaterNotice", Text = _model.Notice, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            IsVisible = _model.HasNotice,
        };
        var commitsTitle = new TextBlock { Name = "UpdaterCommitsTitle", Text = "提交记录", FontSize = 13, FontWeight = FontWeight.SemiBold, IsVisible = _model.HasVersion };
        var commits = new ItemsControl
        {
            Name = "UpdaterCommitList", ItemsSource = _model.Commits, IsVisible = _model.HasVersion,
            ItemTemplate = new FuncDataTemplate<UpdaterCommit>((commit, _) => new TextBlock
            {
                Text = commit.Hash + " " + commit.Subject, FontSize = 12, Margin = new Thickness(0, 4, 0, 4),
            }, supportsRecycling: true),
        };
        var noCommits = new TextBlock { Name = "UpdaterNoCommits", Text = "暂无提交记录", FontSize = 12, IsVisible = _model.HasVersion && _model.Commits.Count == 0 };
        // 上游分页行：{offset+1}–{min(offset+50,total)} / total + 上一页/下一页（common.previous / common.next）。
        var pageLabel = new TextBlock { Name = "UpdaterPageLabel", Text = _model.PageLabel, FontSize = 12 };
        var previousPage = new Button
        {
            Name = "UpdaterPreviousButton", Content = "上一页", Padding = new Thickness(10, 6),
            IsEnabled = _model.HasPreviousPage, Command = _model.PreviousPageCommand,
        };
        var nextPage = new Button
        {
            Name = "UpdaterNextButton", Content = "下一页", Padding = new Thickness(10, 6),
            IsEnabled = _model.HasNextPage, Command = _model.NextPageCommand,
        };
        var pager = new StackPanel
        {
            Name = "UpdaterPager", Orientation = Orientation.Horizontal, Spacing = 8,
            IsVisible = _model.HasVersion,
            Children = { pageLabel, previousPage, nextPage },
        };
        var fetch = new Button { Name = "UpdaterFetchButton", Content = "获取更新", Padding = new Thickness(14, 9), IsEnabled = _model.CanFetch, Command = _model.FetchCommand };
        var apply = new Button { Name = "UpdaterApplyButton", Content = "更新", Padding = new Thickness(14, 9), IsEnabled = _model.CanApply, Command = _model.ApplyCommand };
        var cancel = new Button { Name = "UpdaterCancelButton", Content = "取消更新", Padding = new Thickness(14, 9), IsEnabled = _model.CanCancel, Command = _model.CancelCommand };

        _model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(UpdaterViewModel.StateLabel)) state.Text = _model.StateLabel;
            if (args.PropertyName is nameof(UpdaterViewModel.Relation)) relation.Text = _model.Relation;
            if (args.PropertyName is nameof(UpdaterViewModel.LocalHead))
                localHeadText.Text = HeadText("本地 HEAD ", _model.LocalHead);
            if (args.PropertyName is nameof(UpdaterViewModel.UpstreamHead))
                upstreamHeadText.Text = HeadText("上游 HEAD ", _model.UpstreamHead);
            if (args.PropertyName is nameof(UpdaterViewModel.HasVersion))
            {
                heads.IsVisible = _model.HasVersion;
                relation.IsVisible = _model.HasVersion;
                commitsTitle.IsVisible = _model.HasVersion;
                commits.IsVisible = _model.HasVersion;
                noCommits.IsVisible = _model.HasVersion && _model.Commits.Count == 0;
            }
            if (args.PropertyName is nameof(UpdaterViewModel.Notice))
            {
                notice.Text = _model.Notice;
                notice.IsVisible = _model.HasNotice;
            }
            if (args.PropertyName is nameof(UpdaterViewModel.CanFetch)) fetch.IsEnabled = _model.CanFetch;
            if (args.PropertyName is nameof(UpdaterViewModel.CanApply)) apply.IsEnabled = _model.CanApply;
            if (args.PropertyName is nameof(UpdaterViewModel.CanCancel)) cancel.IsEnabled = _model.CanCancel;
            if (args.PropertyName is nameof(UpdaterViewModel.HasVersion))
            {
                pager.IsVisible = _model.HasVersion;
                noCommits.IsVisible = _model.HasVersion && _model.Commits.Count == 0;
            }
            if (args.PropertyName is nameof(UpdaterViewModel.PageLabel)) pageLabel.Text = _model.PageLabel;
            if (args.PropertyName is nameof(UpdaterViewModel.HasPreviousPage)) previousPage.IsEnabled = _model.HasPreviousPage;
            if (args.PropertyName is nameof(UpdaterViewModel.HasNextPage)) nextPage.IsEnabled = _model.HasNextPage;
        };

        return new StackPanel
        {
            Name = "UpdaterPage", Spacing = 10,
            Children =
            {
                title,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { state, relation } },
                heads, notice,
                // 上游是 section.panel.commit-panel：提交记录整块套面板（外观取自皮肤）。
                new Border
                {
                    Name = "UpdaterCommitPanel",
                    Classes = { "panel" },
                    Padding = new Thickness(16, 12),
                    Child = new StackPanel { Spacing = 6, Children = { commitsTitle, commits, noCommits, pager } },
                },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { fetch, apply, cancel } },
            },
        };
    }
}

/// <summary>页面状态：状态文案、HEAD、关系、提交记录与三个动作的可用性，全部来自注入的局部接口。</summary>
public sealed class UpdaterViewModel : INotifyPropertyChanged
{
    private IUpdaterBackend _backend;
    private long _requestVersion;
    private string? _operation;
    private UpdaterStatus _status = UpdaterStatus.Disconnected;

    /// <summary>
    /// 可替换的后端：外壳按 master 的承载约定注入（XAML 传不了构造参数，所以不能只读）。
    /// 换后端时把分页游标归零再刷新——新后端的提交列表与旧游标无关，沿用旧偏移会读到错误的一页。
    /// </summary>
    public IUpdaterBackend Backend
    {
        get => _backend;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(_backend, value)) return;
            ++_requestVersion;
            _backend = value;
            _operation = null;
            Apply(UpdaterStatus.Disconnected with { Error = null });
            _ = RefreshAsync();
        }
    }
    private string _stateLabel = "更新失败";
    private string _localHead = string.Empty;
    private string _upstreamHead = string.Empty;
    private string _relation = string.Empty;
    private string _notice = DisconnectedUpdaterBackend.Notice;
    private bool _hasVersion;
    private int _offset;
    private int _total;
    private bool _hasPreviousPage;
    private bool _hasNextPage;
    private string _pageLabel = "0";

    public UpdaterViewModel(IUpdaterBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        FetchCommand = new UpdaterCommand(_ => _ = Run(_backend.FetchAsync, "fetch"), () => CanFetch);
        ApplyCommand = new UpdaterCommand(_ => _ = Run(_backend.ApplyAsync, "apply"), () => CanApply);
        CancelCommand = new UpdaterCommand(_ => _ = Run(_backend.CancelAsync, "cancel"), () => CanCancel);
        PreviousPageCommand = new UpdaterCommand(_ => _ = LoadPageAsync(_offset - UpdaterStatus.PageSize), () => HasPreviousPage);
        NextPageCommand = new UpdaterCommand(_ => _ = LoadPageAsync(_offset + UpdaterStatus.PageSize), () => HasNextPage);
    }

    public ObservableCollection<UpdaterCommit> Commits { get; } = new();

    public ICommand FetchCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand PreviousPageCommand { get; }
    public ICommand NextPageCommand { get; }

    public string StateLabel { get => _stateLabel; private set => SetField(ref _stateLabel, value); }
    public string LocalHead { get => _localHead; private set => SetField(ref _localHead, value); }
    public string UpstreamHead { get => _upstreamHead; private set => SetField(ref _upstreamHead, value); }
    public string Relation { get => _relation; private set => SetField(ref _relation, value); }
    public string Notice { get => _notice; private set => SetField(ref _notice, value); }
    public bool HasVersion { get => _hasVersion; private set => SetField(ref _hasVersion, value); }
    public bool HasNotice => !string.IsNullOrEmpty(Notice);
    public bool CanFetch { get; private set; }
    public bool CanApply { get; private set; }
    public bool CanCancel { get; private set; }

    /// <summary>分页指示（上游 `{offset+1}–{min(offset+50,total)} / total`）。</summary>
    public string PageLabel { get => _pageLabel; private set => SetField(ref _pageLabel, value); }

    public bool HasPreviousPage { get => _hasPreviousPage; private set => SetField(ref _hasPreviousPage, value); }

    public bool HasNextPage { get => _hasNextPage; private set => SetField(ref _hasNextPage, value); }

    public Task RefreshAsync(CancellationToken cancellationToken = default) => _operation is null
        ? Run(_backend.ReadStatusAsync, "checking", cancellationToken) : Task.CompletedTask;

    private async Task Run(Func<CancellationToken, Task<UpdaterStatus>> operation, string kind, CancellationToken cancellationToken = default)
    {
        var request = ++_requestVersion;
        _operation = kind;
        if (kind != "page") StateLabel = Label(kind);
        Notice = string.Empty;
        Notify(nameof(HasNotice));
        UpdateAvailability();
        try
        {
            var status = await operation(cancellationToken);
            if (request == _requestVersion) Apply(status);
        }
        catch (OperationCanceledException)
        {
            if (request == _requestVersion)
                Apply(_status with { Error = "操作已取消。" });
        }
        catch (Exception error)
        {
            if (request == _requestVersion)
                Apply(_status with { State = "failed", Error = error.Message });
        }
        finally
        {
            if (request == _requestVersion)
            {
                _operation = null;
                UpdateAvailability();
            }
        }
    }

    /// <summary>翻页（上游 offset ± 50）；请求中阻止重复翻页及更新动作。</summary>
    public Task LoadPageAsync(int offset, CancellationToken cancellationToken = default)
    {
        if (_operation is not null || _status.IsBusy || !HasVersion || offset < 0 || offset >= _total)
            return Task.CompletedTask;
        var backend = _backend;
        return Run(token => backend.ReadCommitsAsync(offset, token), "page", cancellationToken);
    }

    private void Apply(UpdaterStatus status)
    {
        _status = status;
        StateLabel = !status.HasVersion && status.State is "idle" or "available" or "finish"
            ? "尚未获取版本信息" : Label(status.State);
        LocalHead = status.LocalHead;
        UpstreamHead = status.UpstreamHead;
        Relation = status.HasVersion ? status.Relation : string.Empty;
        Notice = status.Error ?? string.Empty;
        Commits.Clear();
        foreach (var commit in status.Commits) Commits.Add(commit);
        // 分页：offset 与 total 由后端给出（上游每页 50 条）；没有 total 时退化为当前页行数。
        _offset = status.CommitOffset;
        _total = status.CommitsTotal == 0 ? status.Commits.Count : status.CommitsTotal;
        PageLabel = status.CommitsTotal == 0 && status.Commits.Count == 0 ? "0" : status.PageLabel;
        // 没有版本信息时不得显示任何“已是最新/新版本可用”结论。
        HasVersion = status.HasVersion;
        Notify(nameof(HasNotice));
        UpdateAvailability();
    }

    private void UpdateAvailability()
    {
        var available = _operation is null && string.IsNullOrEmpty(_status.Error);
        // 已读取版本的后端发生暂时错误后仍能重新获取；未连接能力继续禁用。
        CanFetch = _operation is null && _status.CanFetch && (HasVersion || string.IsNullOrEmpty(_status.Error));
        CanApply = available && _status.CanApply;
        // 取消可以终结正在等待的获取/更新请求；其响应也使先前请求失效。
        CanCancel = _operation is "fetch" or "apply" || available && _status.CanCancel;
        HasPreviousPage = available && !_status.IsBusy && HasVersion && _offset > 0;
        HasNextPage = available && !_status.IsBusy && HasVersion && _total > _offset + Commits.Count;
        Notify(nameof(CanFetch));
        Notify(nameof(CanApply));
        Notify(nameof(CanCancel));
        foreach (var command in new[] { FetchCommand, ApplyCommand, CancelCommand, PreviousPageCommand, NextPageCommand })
            ((UpdaterCommand)command).Invalidate();
    }

    /// <summary>状态文案与上游 i18n 一致（updater.state.*）。</summary>
    private static string Label(string state) => state switch
    {
        "idle" => "已是最新",
        "available" => "新版本可用",
        "fetch" => "正在获取更新",
        "checking" => "正在检查更新",
        "apply" => "正在更新",
        "start" => "准备更新",
        "wait" => "等待任务结束",
        "reload" => "正在重启",
        "finish" => "更新完成",
        "cancel" => "正在取消",
        _ => "更新失败",
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(propertyName!);
        return true;
    }
}

internal sealed class UpdaterCommand(Action<object?> execute, Func<bool> canExecute) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute();

    public void Execute(object? parameter) { if (canExecute()) execute(parameter); }

    public void Invalidate() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
