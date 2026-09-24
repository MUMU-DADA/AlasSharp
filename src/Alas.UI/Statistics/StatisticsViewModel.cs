using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Windows.Input;

namespace Alas.UI.Statistics;

public abstract class StatisticsObservable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Changed(name); return true;
    }
}

public sealed record StatisticsOption(string Key, string Label)
{
    public override string ToString() => Label;
}

/// <summary>UI 只请求、筛选、展示上游报告。提供委托前绝不创建演示数据或访问设备。</summary>
public sealed class StatisticsViewModel : StatisticsObservable, IDisposable
{
    public static IReadOnlyList<StatisticsOption> CategoryList { get; } = [new("resources", "资源记录"), new("action", "行动力"),
        new("opsi", "大世界"), new("commission", "委托收益"), new("ships", "舰船练级"), new("loot", "战利品")];
    private readonly Func<JsonObject, CancellationToken, Task<JsonObject>>? _requestReport;
    private readonly Func<string, CancellationToken, Task>? _refreshLoot;
    private readonly Func<StatisticsExport, CancellationToken, Task>? _export;
    private CancellationTokenSource? _request;
    private readonly CancellationTokenSource _lifetime = new();
    private string _instance = "", _category = "resources", _month = DateTime.Now.ToString("yyyy-MM", CultureInfo.InvariantCulture), _period = "month";
    private string _error = "", _exportStatus = "";
    private int _days = 7, _revision;
    private bool _isLoading, _active, _disposed, _exporting;
    private StatisticsReport? _report;
    public IReadOnlyList<StatisticsOption> Categories => CategoryList;
    public IReadOnlyList<int> DayOptions { get; } = [1, 7, 30, 90, 365];
    public IReadOnlyList<string> PeriodOptions { get; } = ["day", "week", "month"];
    public ICommand RefreshCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand SelectCategoryCommand { get; }
    public bool IsResourceCategory => Category == "resources";
    public bool IsCommissionCategory => Category == "commission";
    public DateTimeOffset? MonthDate { get => DateTime.TryParseExact(Month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null; set { if (value is { } date) Month = date.ToString("yyyy-MM", CultureInfo.InvariantCulture); } }
    public bool HasReport => Report is not null;
    public bool HasNoReport => !IsLoading && Report is null;
    public string EmptyTitle => Error.Length == 0 ? "没有统计记录" : "统计报告加载失败";
    public string EmptyMessage => Error.Length == 0 ? "当前分类没有可显示的上游记录。" : Error;
    public StatisticsChartViewModel Chart => new(Report?.Series ?? []);
    public IReadOnlyList<StatisticsTableViewModel> Tables => Report?.Tables.Select(t => new StatisticsTableViewModel(t, export => _ = ExportAsync(export))).ToArray() ?? [];

    public StatisticsViewModel(Func<JsonObject, CancellationToken, Task<JsonObject>>? requestReport = null,
        Func<string, CancellationToken, Task>? refreshLoot = null,
        Func<StatisticsExport, CancellationToken, Task>? export = null)
    {
        _requestReport = requestReport; _refreshLoot = refreshLoot; _export = export;
        RefreshCommand = new AsyncCommand(() => RefreshAsync());
        ExportCommand = new AsyncCommand(ExportCategoryAsync);
        SelectCategoryCommand = new SyncCommand(value => Category = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "resources");
    }

    public string Instance { get => _instance; set { if (Set(ref _instance, value ?? "")) RequestChanged(); } }
    public string Category
    {
        get => _category;
        set
        {
            if (!CategoryList.Any(c => c.Key == value)) throw new ArgumentOutOfRangeException(nameof(value));
            if (Set(ref _category, value)) { Changed(nameof(CategoryLabel)); Changed(nameof(IsResourceCategory)); Changed(nameof(IsCommissionCategory)); Changed(nameof(HasNoReport)); RequestChanged(); }
        }
    }
    public string CategoryLabel => CategoryList.First(c => c.Key == Category).Label;
    public int Days { get => _days; set { if (value is not (1 or 7 or 30 or 90 or 365)) throw new ArgumentOutOfRangeException(nameof(value)); if (Set(ref _days, value)) RequestChanged(); } }
    public string Month { get => _month; set { if (Set(ref _month, value ?? "")) RequestChanged(); } }
    public string Period { get => _period; set { if (value is not ("day" or "week" or "month")) throw new ArgumentOutOfRangeException(nameof(value)); if (Set(ref _period, value)) RequestChanged(); } }
    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }
    public bool IsConnected => _requestReport is not null && Instance.Length > 0;
    public bool CanExport => _export is not null && Report is not null && !_exporting;
    public string Error { get => _error; private set => Set(ref _error, value); }
    public string ExportStatus { get => _exportStatus; private set => Set(ref _exportStatus, value); }
    public StatisticsReport? Report { get => _report; private set { if (Set(ref _report, value)) Changed(nameof(CanExport)); } }

    public Task ActivateAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _active = true;
        return RefreshAsync();
    }
    public void Deactivate() { _active = false; ++_revision; _request?.Cancel(); IsLoading = false; }

    private void RequestChanged()
    {
        Changed(nameof(IsConnected));
        Report = null;
        if (_active) _ = RefreshAsync();
    }

    public async Task RefreshAsync(bool refreshSource = false)
    {
        if (_disposed) return;
        var revision = ++_revision;
        _request?.Cancel();
        var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _request = request;
        Report = null; Error = ""; ExportStatus = ""; IsLoading = true; Changed(nameof(HasReport)); Changed(nameof(HasNoReport));
        try
        {
            if (!IsConnected) { Error = "尚未连接统计服务，请选择可用实例。"; return; }
            if (!DateTime.TryParseExact(Month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month) || month.Year is < 2020 or > 9998)
            { Error = "月份格式应为 YYYY-MM，范围 2020-01 至 9998-12。"; return; }
            var instance = Instance;
            var category = Category;
            var query = new JsonObject { ["instance"] = instance, ["category"] = category, ["days"] = Days, ["month"] = Month, ["period"] = Period };
            if (refreshSource && category == "loot")
            {
                if (_refreshLoot is null) throw new InvalidOperationException("统计服务未提供战利品刷新接口。");
                await _refreshLoot(instance, request.Token);
            }
            var json = await _requestReport!(query, request.Token);
            request.Token.ThrowIfCancellationRequested();
            var report = StatisticsReport.Parse(json);
            if (report.Instance != instance || report.Category != category) throw new FormatException("统计报告的实例或分类与请求不一致。");
            if (revision == _revision) { Report = report; Changed(nameof(HasReport)); Changed(nameof(HasNoReport)); Changed(nameof(Chart)); Changed(nameof(Tables)); }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error) { if (revision == _revision) Error = error.Message; }
        finally
        {
            if (revision == _revision) { IsLoading = false; _request = null; Changed(nameof(HasNoReport)); }
            request.Dispose();
        }
    }

    public Task ExportCategoryAsync()
    {
        if (Report is not { } report) return Task.CompletedTask;
        var rows = new List<IEnumerable<object?>> { new object?[] { "指标", "数值", "单位" } };
        rows.AddRange(report.Metrics.Select(m => new object?[] { m.Label, m.Value, m.Unit }));
        foreach (var table in report.Tables) { rows.Add([table.Title]); rows.Add(table.Columns); rows.AddRange(table.Rows); rows.Add([]); }
        foreach (var series in report.Series)
        {
            rows.Add([series.Label]); rows.Add(["时间", "数值", "来源"]);
            rows.AddRange(series.Points.Select(p => new object?[] { p.Time, p.Value, p.Source })); rows.Add([]);
        }
        if (report.Notes.Count > 0) { rows.Add(["说明"]); rows.AddRange(report.Notes.Select(n => new object?[] { n })); }
        return ExportAsync(StatisticsData.Csv($"{report.Instance}-{CategoryLabel}-{report.Month}", rows));
    }

    public async Task ExportAsync(StatisticsExport export)
    {
        if (_exporting || _disposed) return;
        if (_export is null) { ExportStatus = "当前平台未提供文件保存接口。"; return; }
        _exporting = true; Changed(nameof(CanExport)); ExportStatus = "正在导出…";
        try { await _export(export, _lifetime.Token); ExportStatus = $"已导出 {export.FileName}"; }
        catch (OperationCanceledException) { ExportStatus = "已取消导出。"; }
        catch (Exception error) { ExportStatus = $"导出失败：{error.Message}"; }
        finally { _exporting = false; Changed(nameof(CanExport)); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; Deactivate(); _lifetime.Cancel(); _lifetime.Dispose();
    }
}

public sealed class StatisticsTableViewModel : StatisticsObservable
{
    private string _search = "";
    private int _page, _sortIndex;
    private bool _descending;
    private readonly Action<StatisticsExport>? _export;
    public StatisticsTableViewModel(StatisticsTable table, Action<StatisticsExport>? export = null) { Table = table; _export = export; _sortIndex = table.SortIndex ?? -1; _descending = table.Descending; PreviousCommand = new SyncCommand(_ => MovePage(-1)); NextCommand = new SyncCommand(_ => MovePage(1)); ExportCommand = new SyncCommand(_ => _export?.Invoke(Export())); }
    public StatisticsTable Table { get; }
    public string Search { get => _search; set { if (Set(ref _search, value ?? "")) { _page = 0; Refresh(); } } }
    public int Page => Math.Clamp(_page, 0, PageCount - 1);
    public int PageCount => Math.Max(1, (FilteredRows.Count + 24) / 25);
    public int SortIndex => _sortIndex;
    public bool Descending => _descending;
    public bool HasNote => !string.IsNullOrWhiteSpace(Table.Note);
    public string RecordSummary => $"{FilteredRows.Count} 条记录";
    public string PageSummary => $"第 {Page + 1} / {PageCount} 页";
    public ICommand PreviousCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand ExportCommand { get; }
    public IReadOnlyList<object?[]> FilteredRows
    {
        get
        {
            var rows = Table.Rows.Where(row => row.Any(value => (Convert.ToString(value, CultureInfo.CurrentCulture) ?? "").Contains(Search, StringComparison.CurrentCultureIgnoreCase))).ToList();
            if (_sortIndex >= 0) rows = rows.Select((row, i) => (row, i)).OrderBy(pair => pair.row[_sortIndex], new CellComparer(_descending)).ThenBy(pair => pair.i).Select(pair => pair.row).ToList();
            return rows;
        }
    }
    public IReadOnlyList<object?[]> PageRows => FilteredRows.Skip(Page * 25).Take(25).ToArray();
    public void Sort(int index)
    {
        if (index < 0 || index >= Table.Columns.Count) throw new ArgumentOutOfRangeException(nameof(index));
        _descending = _sortIndex == index && !_descending; _sortIndex = index; _page = 0; Refresh();
    }
    public void MovePage(int delta) { _page = Math.Clamp(Page + delta, 0, PageCount - 1); Refresh(); }
    public StatisticsExport Export() => StatisticsData.Csv(Table.Title, new[] { Table.Columns.Cast<object?>() }.Concat(FilteredRows));
    private void Refresh() { Changed(nameof(PageRows)); Changed(nameof(PageSummary)); Changed(nameof(RecordSummary)); }

    private sealed class CellComparer(bool descending) : IComparer<object?>
    {
        public int Compare(object? a, object? b)
        {
            var order = a is double an && b is double bn ? an.CompareTo(bn) : CultureInfo.CurrentCulture.CompareInfo.Compare(
                Convert.ToString(a, CultureInfo.CurrentCulture) ?? "", Convert.ToString(b, CultureInfo.CurrentCulture) ?? "", CompareOptions.NumericOrdering);
            return descending ? -Math.Sign(order) : Math.Sign(order);
        }
    }
}

public sealed class SyncCommand(Action<object?> action) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => action(parameter);
}
public sealed class AsyncCommand(Func<Task> action) : ICommand
{
    private bool _running;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_running;
    public async void Execute(object? parameter)
    {
        if (_running) return;
        _running = true; CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await action(); } finally { _running = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
    }
}
