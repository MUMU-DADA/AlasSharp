using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Alas.UI.ViewModels;

/// <summary>
/// 主页（上游 <c>src/pages/Home.tsx</c>）的实例数据来源。
/// 只暴露「是否真的连着服务」与「读到哪些实例」两件事：
/// 页面据此显示真实状态，不把演示实例当成已连接/运行中的实例。
/// </summary>
public interface IInstanceSource
{
    /// <summary>是否真的连着 Alas 服务；false 时主页显示断线态且不渲染任何实例卡。</summary>
    bool IsConnected { get; }

    /// <summary>服务返回的实例列表；未连接时必须是空列表。</summary>
    IReadOnlyList<InstanceCardViewModel> Instances { get; }
}

/// <summary>
/// 可选能力：数据源能主动通知变化，并支持「重试连接」。
/// 主页的断线态重试按钮只在这个接口可用时才真的发起刷新，否则如实说明数据源不支持。
/// </summary>
public interface IRefreshableInstanceSource : IInstanceSource
{
    event EventHandler? Changed;

    void Refresh();
}

/// <summary>
/// 没有接服务时的默认实现：永远报告未连接、实例列表为空。
/// 主页因此显示真实的断线空态，而不是任何演示实例。
/// </summary>
public sealed class DisconnectedInstanceSource : IInstanceSource
{
    public static DisconnectedInstanceSource Instance { get; } = new();

    private DisconnectedInstanceSource()
    {
    }

    public bool IsConnected => false;

    public IReadOnlyList<InstanceCardViewModel> Instances => Array.Empty<InstanceCardViewModel>();
}

/// <summary>
/// 一张实例卡（上游 Home.tsx 的 <c>a.instance-card</c>）。
/// 文案与状态词表都对上游 i18n：status.running/stopped/error/updating 与 home.notRunning/home.waitingSchedule。
/// </summary>
public sealed class InstanceCardViewModel
{
    private InstanceCardViewModel(
        string name, string status, string deviceLabel, string serial, string footerText, bool isDemo)
    {
        Name = name;
        Status = status;
        StatusLabel = LabelOf(status);
        DeviceLabel = deviceLabel;
        Serial = serial;
        FooterText = footerText;
        IsDemo = isDemo;
    }

    /// <summary>
    /// 按上游 Home.tsx 的规则拼一张卡：状态词表固定，底部文案随状态变化
    /// （running → 当前任务名或「等待调度」；error → 「需要处理」；updating → 「更新中」；其余 → 「未运行」）。
    /// </summary>
    /// <param name="isDemo">true 表示这是演示数据；卡片上会显示「演示」徽标，绝不冒充真实实例。</param>
    public static InstanceCardViewModel Create(
        string name,
        string status,
        string? deviceLabel = null,
        string serial = "",
        string? currentTask = null,
        bool isDemo = false)
    {
        var footer = status switch
        {
            "running" => string.IsNullOrWhiteSpace(currentTask) ? "等待调度" : currentTask,
            "error" => "需要处理",
            "updating" => "更新中",
            _ => "未运行",
        };
        return new InstanceCardViewModel(name, status, deviceLabel ?? string.Empty, serial, footer, isDemo);
    }

    /// <summary>上游 i18n 的 status.* 词表；未知状态归到 stopped，不发明新词。</summary>
    public static string LabelOf(string status) => status switch
    {
        "running" => "运行中",
        "error" => "需要处理",
        "updating" => "更新中",
        _ => "待命中",
    };

    public string Name { get; }

    /// <summary>running / stopped / error / updating（上游 <c>Status</c>）。</summary>
    public string Status { get; }

    public string StatusLabel { get; }

    public bool IsRunning => Status == "running";
    public bool IsStopped => !IsRunning && Status is not ("error" or "updating");
    public bool IsErrored => Status == "error";
    public bool IsUpdating => Status == "updating";

    /// <summary>服务器名（上游 Emulator.ServerName.*）；空表示禁用服务器，不显示这一段。</summary>
    public string DeviceLabel { get; }

    public bool HasDeviceLabel => !string.IsNullOrEmpty(DeviceLabel);

    /// <summary>模拟器串号，例如 127.0.0.1:5555。</summary>
    public string Serial { get; }

    /// <summary>卡片底部状态文案。</summary>
    public string FooterText { get; }

    /// <summary>演示数据标记：为 true 时卡片显示「演示」徽标，不得伪装成真实实例。</summary>
    public bool IsDemo { get; }
}

/// <summary>
/// 主页视图模型（上游 <c>Home.tsx</c> + <c>styles/home.css</c>）。
/// 结构：左侧指挥台（eyebrow / 问候语 / 副标题 / 统计 / 链接）+ 右侧实例区（标题行 + 卡片网格）。
/// 数据全部来自注入的 <see cref="IInstanceSource"/>：未接服务时是真实断线态，界面不伪造实例。
/// </summary>
public sealed class HomeViewModel : INotifyPropertyChanged
{
    /// <summary>未连接时统计值显示为「—」，而不是把 0 当成「没有实例」。</summary>
    public const string UnknownValue = "—";

    /// <summary>本项目把主题切换统一到「界面设置」页，主页的旧版快捷开关默认不生效。</summary>
    public const string PendingFeatureNotice = "第一阶段未接后端：该入口在后续切片实现，当前不可用。";

    public const string NotConnectedNotice = "未连接 Alas 服务：连接服务后该操作才可用。";

    private readonly IInstanceSource _source;
    private bool _canToggleLegacyUi;
    private bool _isLegacyUi;
    private string _statusMessage = string.Empty;

    public HomeViewModel()
        : this(DisconnectedInstanceSource.Instance)
    {
    }

    public HomeViewModel(IInstanceSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        RetryCommand = new HomeCommand(_ => Retry());
        SelectInstanceCommand = new HomeCommand(parameter =>
        {
            if (parameter is InstanceCardViewModel card) SelectInstance(card);
        });
        ToggleLegacyUiCommand = new HomeCommand(_ => RequestLegacyUiToggle());
        if (source is IRefreshableInstanceSource refreshable)
            refreshable.Changed += (_, _) => Reload();
        Reload();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>选中一张实例卡：把实例名交给外壳切路由（外壳负责真正的导航）。</summary>
    public event EventHandler<string>? InstanceSelected;

    /// <summary>主页的「切换旧版界面」被点击；外壳订阅后可接自己的主题服务，未订阅时该按钮保持禁用。</summary>
    public event EventHandler? LegacyUiToggleRequested;

    public ICommand RetryCommand { get; }
    public ICommand SelectInstanceCommand { get; }
    public ICommand ToggleLegacyUiCommand { get; }

    /// <summary>实例卡列表（上游 <c>.home-instance-grid</c> 的渲染来源）。</summary>
    public ObservableCollection<InstanceCardViewModel> Instances { get; } = new();

    public bool IsConnected => _source.IsConnected;

    /// <summary>断线态：显示「未连接 Alas 服务」+ 重试，且不渲染任何实例卡。</summary>
    public bool ShowDisconnected => !IsConnected;

    /// <summary>已连接但没有实例：上游 <c>.home-instance-empty</c> 的「创建第一个实例」。</summary>
    public bool ShowEmptyState => IsConnected && Instances.Count == 0;

    public bool HasInstances => Instances.Count > 0 && IsConnected;

    // ---- 左侧指挥台文案（上游 i18n home.*） ----

    public string CommandCenter => "你的指挥中心";

    /// <summary>按本地时间取问候语，与上游 getGreeting 的三段一致（5-12 / 12-18 / 其余）。</summary>
    public string Greeting => GreetingFor(DateTime.Now);

    public string Subtitle => "每一次出航，都井然有序。所有实例与任务，尽在掌握。";

    public string StatsLabel => "实例状态摘要";

    public string AllInstancesLabel => "全部实例";
    public string RunningLabel => "运行中";
    public string ErrorLabel => "需要处理";

    public string TotalText => IsConnected ? Instances.Count.ToString(CultureInfo.InvariantCulture) : UnknownValue;

    public string RunningText => IsConnected ? Count("running") : UnknownValue;

    public string ErrorText => IsConnected ? Count("error") : UnknownValue;

    public string OpenSourceLabel => "开源项目";

    /// <summary>上游 Home.tsx 的外链地址。</summary>
    public string OpenSourceUrl => "https://github.com/wess09/AzurPilot";

    public string LegacyUiLabel => _isLegacyUi ? "回到新版界面" : "切换旧版界面";

    /// <summary>外壳是否接管了旧版快捷开关；默认 false，按钮据此禁用并说明原因。</summary>
    public bool CanToggleLegacyUi
    {
        get => _canToggleLegacyUi;
        set => SetField(ref _canToggleLegacyUi, value);
    }

    /// <summary>当前是否处于旧版皮肤（由外壳在切换后回写，用于切换按钮文案）。</summary>
    public bool IsLegacyUi
    {
        get => _isLegacyUi;
        set
        {
            if (!SetField(ref _isLegacyUi, value)) return;
            Notify(nameof(LegacyUiLabel));
        }
    }

    public string LegacyUiNotice => CanToggleLegacyUi
        ? "在旧版与新版界面之间切换。"
        : "主题切换已统一到「界面设置」页：主页的旧版快捷开关由外壳接管后才可用。";

    // ---- 右侧实例区文案 ----

    public string InstancesTitle => "实例";
    public string NewInstanceLabel => "新建实例";
    public string ImportInstanceLabel => "导入实例";
    public string DeleteInstanceLabel => "删除实例";
    public string SwitchInstanceLabel => "切换实例";
    public string InstanceSettingsLabel => "实例设置";
    public string CreateFirstInstanceLabel => "创建第一个实例";

    public string DisconnectedTitle => "未连接 Alas 服务";
    public string DisconnectedText =>
        "主页的实例列表来自 Alas 服务。当前没有可用连接，因此这里不显示任何实例，也不会把演示数据当成运行中的实例。";
    public string RetryLabel => "重试连接";

    // ---- 动作可用性：本切片未接后端，全部禁用并给出原因（不留点了没反应的死按钮） ----

    public bool CanCreateInstance => false;
    public bool CanImportInstance => false;
    public bool CanDeleteInstance => false;
    public bool CanSwitchInstance => false;
    public bool CanOpenInstanceSettings => false;

    public string CreateInstanceNotice => IsConnected ? PendingFeatureNotice : NotConnectedNotice;
    public string ImportInstanceNotice => IsConnected ? PendingFeatureNotice : NotConnectedNotice;
    public string DeleteInstanceNotice => PendingFeatureNotice;
    public string SwitchInstanceNotice => PendingFeatureNotice;
    public string InstanceSettingsNotice => PendingFeatureNotice;

    /// <summary>断线态与交互的如实说明（重试结果等）。空串表示没有需要播报的内容。</summary>
    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (!SetField(ref _statusMessage, value)) return;
            Notify(nameof(HasStatusMessage));
        }
    }

    public bool HasStatusMessage => !string.IsNullOrEmpty(_statusMessage);

    /// <summary>写入一条如实说明（重试结果、外部链接结果等），供视图播报。</summary>
    public void Report(string message) => StatusMessage = message;

    /// <summary>重新读取数据源；断线态下重试按钮调用它（并转发给可刷新的数据源）。</summary>
    public void Reload()
    {
        Instances.Clear();
        if (_source.IsConnected)
        {
            foreach (var card in _source.Instances) Instances.Add(card);
        }

        Notify(nameof(IsConnected));
        Notify(nameof(ShowDisconnected));
        Notify(nameof(HasInstances));
        Notify(nameof(ShowEmptyState));
        Notify(nameof(TotalText));
        Notify(nameof(RunningText));
        Notify(nameof(ErrorText));
        Notify(nameof(CreateInstanceNotice));
        Notify(nameof(ImportInstanceNotice));
    }

    private string Count(string status)
    {
        var total = 0;
        foreach (var card in Instances)
        {
            if (string.Equals(card.Status, status, StringComparison.Ordinal)) total++;
        }
        return total.ToString(CultureInfo.InvariantCulture);
    }

    private void Retry()
    {
        if (_source is IRefreshableInstanceSource refreshable)
        {
            refreshable.Refresh();
            Reload();
            StatusMessage = IsConnected
                ? "已重新连接 Alas 服务。"
                : "仍然未连接 Alas 服务：请确认服务已启动并监听正确端口。";
            return;
        }

        Reload();
        StatusMessage = IsConnected
            ? "已重新读取实例列表。"
            : "当前数据源不支持重试：仍然未连接 Alas 服务，请确认服务已启动并监听正确端口。";
    }

    /// <summary>把选中的实例名交给外壳（真正切路由由外壳完成）。</summary>
    public void SelectInstance(InstanceCardViewModel card)
    {
        StatusMessage = $"已选择实例 {card.Name}：由外壳切换到该实例的总览。";
        InstanceSelected?.Invoke(this, card.Name);
    }

    private void RequestLegacyUiToggle()
    {
        if (!CanToggleLegacyUi)
        {
            StatusMessage = LegacyUiNotice;
            return;
        }

        IsLegacyUi = !IsLegacyUi;
        LegacyUiToggleRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>上游 getGreeting：5-12 上午、12-18 下午、其余晚上。</summary>
    public static string GreetingFor(DateTime now) => now.Hour switch
    {
        >= 5 and < 12 => "上午好，指挥官！",
        >= 12 and < 18 => "下午好，指挥官！",
        _ => "晚上好，指挥官！",
    };

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(propertyName);
        return true;
    }

    private void Notify([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>主页命令：只做本地状态与回调，不调度任务、不访问设备。</summary>
internal sealed class HomeCommand(Action<object?> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute(parameter);
}
