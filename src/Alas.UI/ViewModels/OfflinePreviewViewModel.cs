using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;

namespace Alas.UI.ViewModels;

/// <summary>
/// 旧原型的离线预览状态：工作区名双向绑定、任务队列 JSON 校验与上限 2,000 条的日志列表。
/// 外壳复刻（ee030f0）不再展示这一页，但能力与其无窗口回归保留下来，
/// 作为后续「配置管理」「日志」等上游页面的实现基础；不连接服务或设备，也不执行任务。
/// </summary>
public sealed class OfflinePreviewViewModel : INotifyPropertyChanged
{
    private const int LogLimit = 2000;
    private string _workspaceName = "演示工作区";
    private string _page = "overview";
    private bool _isDark;
    private long _logSequence;
    private string _runningText = "未运行 · 仅离线预览";
    private readonly List<string> _logLines = new();
    private string _queueJson = """
        {
          "tasks": [
            { "id": "preview-catalog", "kind": "task_catalog", "input": {} },
            { "id": "preview-state", "kind": "account_state", "input": {} }
          ]
        }
        """;
    private string _validationMessage = "仅检查 JSON 结构；模拟数据，未连接设备。";

    public OfflinePreviewViewModel()
    {
        ShowOverviewCommand = new PreviewCommand(SelectOverview);
        ShowSettingsCommand = new PreviewCommand(() => Page = "settings");
        ShowLogsCommand = new PreviewCommand(() => Page = "logs");
        ToggleThemeCommand = new PreviewCommand(ToggleTheme);
        ValidateQueueCommand = new PreviewCommand(ValidateQueue);
        AddLogsCommand = new PreviewCommand(() => AppendLogs(100));
        StartDryRunCommand = new PreviewCommand(StartDryRun);
        RequestStopCommand = new PreviewCommand(RequestStop);
        SelectOverviewCommand = new PreviewCommand(SelectOverview);
        SelectQueueCommand = new PreviewCommand(SelectQueue);
        SelectReportsCommand = new PreviewCommand(SelectReports);
        AppendLogs(200);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string WorkspaceName
    {
        get => _workspaceName;
        set => SetField(ref _workspaceName, value);
    }

    public string Page
    {
        get => _page;
        set
        {
            if (value is not ("overview" or "settings" or "logs" or "queue" or "reports"))
                throw new ArgumentOutOfRangeException(nameof(value), "未知的预览页面。");
            if (!SetField(ref _page, value)) return;
            Notify(nameof(IsOverview));
            Notify(nameof(IsSettings));
            Notify(nameof(IsLogs));
            Notify(nameof(IsQueue));
            Notify(nameof(IsReports));
            Notify(nameof(PageTitle));
        }
    }

    public bool IsOverview => Page == "overview";
    public bool IsSettings => Page == "settings";
    public bool IsLogs => Page == "logs";
    public bool IsQueue => Page == "queue";
    public bool IsReports => Page == "reports";
    public string PageTitle => Page switch
    {
        "settings" => "配置编辑",
        "logs" => "日志预览",
        "queue" => "任务队列",
        "reports" => "运行报告",
        _ => "工作区总览",
    };

    public bool IsDark
    {
        get => _isDark;
        set => SetField(ref _isDark, value);
    }

    public string QueueJson
    {
        get => _queueJson;
        set
        {
            if (SetField(ref _queueJson, value))
                ValidationMessage = "内容已修改，尚未检查 JSON 结构。";
        }
    }

    public string ValidationMessage
    {
        get => _validationMessage;
        private set => SetField(ref _validationMessage, value);
    }

    /// <summary>当前界面始终使用模拟数据，不代表服务或设备已连接。</summary>
    public string ConnectionText => "未连接服务或设备 · 离线预览";

    /// <summary>任务输入的静态摘要；不会因点击预览按钮而声称任务已执行。</summary>
    public string QueueSummary => $"{Tasks.Count} 项演示任务 · 仅供预览";

    public string RunningText
    {
        get => _runningText;
        private set => SetField(ref _runningText, value);
    }

    public ObservableCollection<PreviewLog> Logs { get; } = new();

    public IReadOnlyList<string> LogLines => _logLines;

    public IReadOnlyList<PreviewTask> Tasks { get; } = Array.AsReadOnly(new[]
    {
        new PreviewTask("上游任务目录", "task_catalog", "演示 · 未运行"),
        new PreviewTask("账号状态观察", "account_state", "演示 · 未运行"),
        new PreviewTask("战役队列", "campaign_batch", "演示 · 未运行"),
    });

    public ICommand ShowOverviewCommand { get; }
    public ICommand ShowSettingsCommand { get; }
    public ICommand ShowLogsCommand { get; }
    public ICommand ToggleThemeCommand { get; }
    public ICommand ValidateQueueCommand { get; }
    public ICommand AddLogsCommand { get; }
    public ICommand StartDryRunCommand { get; }
    public ICommand RequestStopCommand { get; }
    public ICommand SelectOverviewCommand { get; }
    public ICommand SelectQueueCommand { get; }
    public ICommand SelectReportsCommand { get; }

    public void ToggleTheme() => IsDark = !IsDark;

    /// <summary>仅更新预览状态，不调度、验证或执行任何任务。</summary>
    public void StartDryRun()
    {
        RunningText = "模拟预览已准备 · 未执行任务";
        ValidationMessage = "仅更新离线预览状态，不代表任务已运行。";
        AppendStatusLog("已准备离线模拟预览；没有连接服务或设备");
    }

    /// <summary>仅更新预览状态，不中断任何真实运行。</summary>
    public void RequestStop()
    {
        RunningText = "已请求停止 · 当前没有运行中的任务";
        ValidationMessage = "离线预览没有运行中的任务，无需发送停止请求。";
        AppendStatusLog("已记录停止请求；没有连接服务或设备");
    }

    public void SelectOverview() => Page = "overview";
    public void SelectQueue() => Page = "queue";
    public void SelectReports() => Page = "reports";

    private void ValidateQueue()
    {
        try
        {
            using var document = JsonDocument.Parse(QueueJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("tasks", out var tasks)
                || tasks.ValueKind != JsonValueKind.Array)
            {
                ValidationMessage = "JSON 结构无效：顶层必须是含 tasks 数组的对象。";
                return;
            }

            ValidationMessage = "JSON结构有效，不代表任务已运行；尚未进行服务端任务校验。";
        }
        catch (JsonException exception)
        {
            ValidationMessage = $"JSON 语法错误：第 {(exception.LineNumber ?? 0) + 1} 行，请检查编辑内容。";
        }
    }

    private void AppendLogs(int count)
    {
        for (var index = 0; index < count; index++)
        {
            var sequence = ++_logSequence;
            var time = TimeSpan.FromSeconds((12 * 3600 + sequence) % (24 * 3600));
            var log = new PreviewLog(
                time.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
                sequence % 10 == 0 ? "WARN" : "INFO",
                $"模拟日志 {sequence:D4} · {(sequence % 10 == 0 ? "演示提醒：未连接设备" : "仅用于滚动与中文排版预览")}。");
            Logs.Add(log);
            _logLines.Add($"{log.Time} [{log.Level}] {log.Message}");
            if (Logs.Count > LogLimit) Logs.RemoveAt(0);
            if (_logLines.Count > LogLimit) _logLines.RemoveAt(0);
        }
        Notify(nameof(LogLines));
    }

    private void AppendStatusLog(string message)
    {
        var sequence = ++_logSequence;
        var time = TimeSpan.FromSeconds((12 * 3600 + sequence) % (24 * 3600));
        var log = new PreviewLog(
            time.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
            "INFO",
            $"模拟日志 {sequence:D4} · {message}。");
        Logs.Add(log);
        _logLines.Add($"{log.Time} [{log.Level}] {log.Message}");
        while (Logs.Count > LogLimit) Logs.RemoveAt(0);
        while (_logLines.Count > LogLimit) _logLines.RemoveAt(0);
        Notify(nameof(LogLines));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(propertyName);
        return true;
    }

    private void Notify([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class PreviewCommand(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}

public sealed record PreviewLog(string Time, string Level, string Message);
public sealed record PreviewTask(string Name, string Kind, string State);
