using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Text.Json.Nodes;
using Alas.Contracts;
using Alas.UI.Statistics;
using Alas.UI.TaskEditor;
using Alas.UI.Overview;
using Avalonia;
using Avalonia.Media;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Alas.UI.Theming;

namespace Alas.UI.ViewModels;

/// <summary>
/// 共享外壳状态：主题、视口断点、抽屉、一级/任务导航、当前页面与右栏。
/// 预览构造函数使用上游风格的离线数据；生产构造函数通过能力接口读取 Alas.Core，
/// 页面本身不持有 HTTP、Python 或设备状态机。
/// </summary>
public sealed class ShellViewModel : INotifyPropertyChanged
{
    /// <summary>上游 layout.css 的主外壳断点：≤950px 时侧栏与右栏改为浮层抽屉。</summary>
    public const double NarrowBreakpoint = 950;

    /// <summary>上游 apple.css:339 的第二档断点：≤480px 时监控页签、工具栏与分段按钮继续压缩。</summary>
    public const double CompactBreakpoint = 480;

    private readonly Dictionary<(string Instance, string Task), TaskEditorViewModel> _editors = new();
    private Task<SchemaResponse>? _taskSchema;
    private long _editorLoadVersion;
    private bool _stateRefreshInFlight;
    private long _instanceVersion;
    private long _refreshVersion;
    private readonly IAlasUiBackend _backend;
    private bool _isNarrow;
    private bool _isDrawerOpen;
    private bool _isRailOpen;
    private string _activePage = "home";
    private string? _instanceName;
    private bool _hasInstance;
    private string _activeNavKey = "home";
    private string _activeTaskKey = string.Empty;
    private bool _isTaskSearchOpen;
    private string _taskSearchText = string.Empty;

    public ShellViewModel()
        : this(new MemoryThemeStore(), null, previewData: true)
    {
    }

    public ShellViewModel(IThemeStore themeStore, IAlasUiBackend? backend = null, bool previewData = false,
        Platform.IUiFiles? files = null, IResourceSelectionStore? resourceStore = null)
    {
        Theme = new ThemeService(themeStore);
        InterfaceSettings = new InterfaceSettingsViewModel(Theme);
        _backend = backend ?? DisconnectedInstanceSource.Instance;
        ConfigManagerBackend = new CoreConfigInstancesBackend(_backend);
        SettingsBackend = new CoreDeploySettingsBackend(_backend);
        Home = new HomeViewModel(_backend);
        Home.InstanceSelected += (_, instance) => SelectInstance(instance);
        Overview = new OverviewViewModel(previewData, previewData ? null : _backend, resourceStore);
        Statistics = new StatisticsViewModel(
            async (query, cancellationToken) => await _backend.ReadStatisticsAsync(ToStatisticsRequest(query), cancellationToken).ConfigureAwait(true),
            (instance, cancellationToken) => _backend.RefreshStatisticsLootAsync(instance, cancellationToken),
            files is null ? null : (export, cancellationToken) => files.SaveAsync(
                export.FileName, export.MediaType, () => Task.FromResult(export.Content), cancellationToken));
        TaskEditor = new TaskEditorViewModel { Backend = new CoreTaskEditorBackend(_backend) };
        MeowfficerBackend = new CoreMeowfficerReportBackend(_backend);
        Placeholder = new PlaceholderViewModel();
        Rail = new RailViewModel(Overview, previewData);
        Instances = new ObservableCollection<string>();
        ReloadBackendInstances();
        SelectNavCommand = new PreviewCommand(parameter => SelectNav(parameter as string));
        GoHomeCommand = new PreviewCommand(_ => GoHome());
        ToggleTaskSearchCommand = new PreviewCommand(_ => IsTaskSearchOpen = !IsTaskSearchOpen);
        OpenDrawerCommand = new PreviewCommand(_ => IsDrawerOpen = true);
        CloseDrawerCommand = new PreviewCommand(_ => { IsDrawerOpen = false; IsRailOpen = false; });
        ToggleRailCommand = new PreviewCommand(_ => IsRailOpen = !IsRailOpen);
        ToggleTaskGroupCommand = new PreviewCommand(parameter =>
        {
            if (parameter is TaskGroupEntry group) group.IsExpanded = !group.IsExpanded;
        });
        SelectTaskCommand = new PreviewCommand(parameter => SelectTask(parameter as TaskEntry));
        BuildNavigation();
        Theme.Changed += (_, _) =>
        {
            Notify(nameof(IsDark));
            Notify(nameof(CurrentTheme));
            Notify(nameof(IsLegacyLayout));
            Notify(nameof(IsExtremeLayout));
            NotifyLayout();
        };
        NotifyLayout();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _attached;

    /// <summary>
    /// 挂到可视树时订阅后端变更（幂等），并补读一次实例列表以覆盖离树期间错过的变化。
    /// 与 <see cref="Detach"/> 成对，由 <c>MainView</c> 在可视树生命周期里调用。
    /// </summary>
    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        _backend.Changed += OnBackendChanged;
        Home.Attach();
        ReloadBackendInstances();
    }

    /// <summary>
    /// 离树时退订：<c>_backend</c> 未注入时是**进程级单例** <c>DisconnectedInstanceSource.Instance</c>，
    /// 不退订会让关闭后的外壳（以及它持有的整棵视图）永久留在单例的调用列表里。
    /// 外壳本身不被销毁，重新挂接时 <see cref="Attach"/> 会恢复订阅，因此"临时离树"不会毁掉可复用的 VM。
    /// </summary>
    public void Detach()
    {
        if (!_attached) return;
        _attached = false;
        _backend.Changed -= OnBackendChanged;
        Home.Detach();
    }

    private void OnBackendChanged(object? sender, EventArgs args) => ReloadBackendInstances();

    public InterfaceSettingsViewModel InterfaceSettings { get; }
    public HomeViewModel Home { get; }
    public OverviewViewModel Overview { get; }
    public CoreConfigInstancesBackend ConfigManagerBackend { get; }
    public CoreDeploySettingsBackend SettingsBackend { get; }
    public bool IsBackendConnected => _backend.IsConnected;
    public bool IsUiOnly => _backend.IsSimulation;
    public string UiOnlyNotice => Simulation.SimulatedUiBackend.Notice;

    public async Task AppendSimulationLogsAsync()
    {
        if (_backend is not Simulation.SimulatedUiBackend simulation) return;
        if (!HasInstance)
        {
            string? first = simulation.Instances.FirstOrDefault()?.Name;
            if (first is null) return;
            SelectInstance(first);
        }
        SelectNav("overview");
        simulation.AppendLogs(InstanceName);
        await RefreshBackendStateAsync();
    }
    public StatisticsViewModel Statistics { get; }
    public TaskEditorViewModel TaskEditor { get; private set; }
    public IMeowfficerReportBackend MeowfficerBackend { get; }
    public RailViewModel Rail { get; }
    public PlaceholderViewModel Placeholder { get; }
    public ObservableCollection<NavEntry> PrimaryNav { get; } = new();
    public ObservableCollection<TaskGroupEntry> TaskGroups { get; } = new();
    public ObservableCollection<string> Instances { get; }
    public string InstanceName => _instanceName ?? "未选择实例";
    public string InstancesSummary => $"{Instances.Count} 个实例";

    public ICommand SelectNavCommand { get; }
    public ICommand GoHomeCommand { get; }
    public ICommand ToggleTaskSearchCommand { get; }
    public ICommand OpenDrawerCommand { get; }
    public ICommand CloseDrawerCommand { get; }
    public ICommand ToggleRailCommand { get; }
    public ICommand ToggleTaskGroupCommand { get; }
    public ICommand SelectTaskCommand { get; }

    /// <summary>演示数据说明，用于无障碍读屏与工具提示，避免把模拟状态当成真实运行。</summary>
    public string DemoNotice => "离线演示数据：不连接服务或设备，按钮只改变本页模拟状态。";

    /// <summary>本阶段未接后端的入口统一禁用，并用这条提示说明原因，避免出现点了没反应的死按钮。</summary>
    public string PendingNotice => "第一阶段未接后端：该入口在后续切片实现，当前不可用。";

    /// <summary>六主题与配色偏好的唯一入口（界面设置页驱动；顶栏不再提供临时切换）。</summary>
    public ThemeService Theme { get; }

    public UiTheme CurrentTheme => Theme.Preference.Theme;

    /// <summary>旧版皮肤使用另一套外壳布局（无顶栏、内容带、圆角 8）。</summary>
    public bool IsLegacyLayout => UiThemes.SkinOf(CurrentTheme) == UiSkin.Legacy;

    /// <summary>紧凑皮肤不渲染页面标题、工具栏吸顶合并为一行。</summary>
    public bool IsExtremeLayout => CurrentTheme == UiTheme.Extreme;

    public bool IsClassicLayout => CurrentTheme is UiTheme.Light or UiTheme.Dark;
    public bool IsLegacyDesktop => IsLegacyLayout && !IsNarrow;
    public bool IsExtremeDesktop => IsExtremeLayout && !IsNarrow;
    public bool IsLegacyOverview => IsLegacyDesktop && IsOverviewActive;
    public double SidebarWidth => IsNarrow ? 232 : IsLegacyLayout ? 192 : IsExtremeLayout ? 178 : IsClassicLayout ? 232 : 240;
    public double ShellGap => IsNarrow || !IsClassicLayout ? 0 : 14;
    public double NavItemHeight => IsNarrow ? 44 : IsExtremeLayout ? 30 : CurrentTheme == UiTheme.Minimal ? 42 : 44;
    public double TaskGroupHeight => IsNarrow ? 44 : IsExtremeLayout ? 30 : IsLegacyLayout ? 44 : CurrentTheme == UiTheme.Minimal ? 42 : 43;
    public Thickness NavItemPadding => IsExtremeDesktop ? new Thickness(8, 4) : new Thickness(12, 6);
    public Thickness TaskGroupPadding => IsExtremeDesktop ? new Thickness(7, 4) : new Thickness(9, 10);
    public bool HidesOverviewTitle => IsLegacyLayout || IsExtremeDesktop;

    private void NotifyLayout()
    {
        foreach (string property in new[] { nameof(IsClassicLayout), nameof(IsLegacyDesktop), nameof(IsExtremeDesktop),
            nameof(IsLegacyOverview), nameof(SidebarWidth), nameof(ShellGap), nameof(NavItemHeight), nameof(TaskGroupHeight),
            nameof(HidesOverviewTitle), nameof(RailWidth), nameof(TopbarHeight), nameof(MainPadding), nameof(ContentMinHeight), nameof(IsRailVisible),
            nameof(NavItemPadding), nameof(TaskGroupPadding) })
            Notify(property);
        Overview.HideTitle = HidesOverviewTitle;
        Overview.ContentHeight = ContentMinHeight;
    }

    public bool IsTaskSearchOpen
    {
        get => _isTaskSearchOpen;
        set { if (SetField(ref _isTaskSearchOpen, value) && !value) TaskSearchText = string.Empty; }
    }

    public string TaskSearchText
    {
        get => _taskSearchText;
        set { if (SetField(ref _taskSearchText, value ?? string.Empty)) RebuildTaskGroups(); }
    }

    /// <summary>应用一条主题偏好（界面设置页调用）；systemDark 供 auto 模式解析。</summary>
    public void ApplyTheme(ThemePreference preference, bool systemDark = false)
    {
        Theme.Apply(preference, persist: true, systemDark);
        Notify(nameof(IsDark));
        Notify(nameof(CurrentTheme));
        Notify(nameof(IsLegacyLayout));
        Notify(nameof(IsExtremeLayout));
    }

    /// <summary>当前解析出的明暗；由主题服务决定，不再由外部赋值。</summary>
    public bool IsDark => Theme.IsDark;

    public bool IsNarrow
    {
        get => _isNarrow;
        private set
        {
            if (!SetField(ref _isNarrow, value)) return;
            Notify(nameof(IsWide));
            Notify(nameof(TopbarSpacing));
            Notify(nameof(BreadcrumbAlignment));
            Notify(nameof(TopbarHeight));
            Notify(nameof(MainPadding));
            // 主页内边距为 0、其它页为 32：切页会改变可用内容高度，必须一起重算。
            Overview.ContentHeight = ContentMinHeight;
            Notify(nameof(ContentMinHeight));
            Notify(nameof(ContentMinHeight));
            if (!value) { IsDrawerOpen = false; IsRailOpen = false; }
        }
    }

    public bool IsWide => !IsNarrow;
    public bool IsCompact => ViewportWidth <= CompactBreakpoint;
    public double RailWidth => IsNarrow ? 360 : IsLegacyLayout ? 280 : IsExtremeLayout ? 244
        : !IsClassicLayout ? 320 : ViewportWidth > 1562.5 ? 320 : 292;
    public double TopbarSpacing => IsNarrow ? 6 : 16;

    /// <summary>窄屏顶栏把面包屑贴右（上游 ≤950px 的顶栏布局），宽屏紧跟左侧开关。</summary>
    public HorizontalAlignment BreadcrumbAlignment => IsNarrow ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    public double ViewportWidth { get; private set; } = 1280;
    public double ViewportHeight { get; private set; } = 820;

    /// <summary>顶栏行高：宽屏取上游 41.5px，窄屏 56px（apple.css:306）。</summary>
    public double TopbarHeight => IsNarrow || IsLegacyLayout ? 56 : IsExtremeLayout ? 44 : IsClassicLayout ? 41.5 : 69;

    /// <summary>内容区内边距：宽屏 0 32 32、窄屏 28 18（apple.css:96 / 314，经典主题窄屏覆盖共享层）。</summary>
    public Thickness MainPadding => IsHomeActive
        ? new Thickness(0)
        : IsNarrow ? new Thickness(18, 28, 18, 28)
        : IsLegacyLayout ? new Thickness(IsLegacyOverview ? RailWidth + 42 : 28, 14, 28, 24)
        : IsExtremeLayout ? new Thickness(4)
        : IsClassicLayout ? new Thickness(32, 0, 32, 32) : new Thickness(28, 0, 28, 24);

    /// <summary>
    /// 内容区至少可用的高度。上游 <c>main</c> 是 flex 列、总览的监控面板 <c>flex:1</c> 撑满剩余空间；
    /// Avalonia 的滚动容器按无限高测量，所以这里给出「视口 - 顶栏 - 内容内边距」作为最小高度，
    /// 让总览页的第三行（监控面板）能像上游一样填满，同时在内容更高时照常滚动。
    /// </summary>
    public double ContentMinHeight =>
        // 减 2px 吸收布局取整：内容正好等于最小高度时，round 后的 extent 会比 viewport 略大而弹出滚动条。
        Math.Max(200, ViewportHeight - TopbarHeight - (IsLegacyDesktop ? 42 : 0) - MainPadding.Top - MainPadding.Bottom - 2);


    public bool IsDrawerOpen
    {
        get => _isDrawerOpen;
        set => SetField(ref _isDrawerOpen, value);
    }

    public bool IsRailOpen
    {
        get => _isRailOpen;
        set => SetField(ref _isRailOpen, value);
    }

    public bool IsScrimVisible => IsNarrow && (IsDrawerOpen || IsRailOpen);
    public bool IsRailVisible => HasInstance && (IsWide || IsRailOpen) && (!IsLegacyDesktop || IsOverviewActive);
    public bool IsSidebarVisible => IsWide || IsDrawerOpen;

    /// <summary>有实例外壳还是无实例外壳（上游 App.tsx:242 按 instance 二分）。</summary>
    public bool HasInstance
    {
        get => _hasInstance;
        private set
        {
            if (!SetField(ref _hasInstance, value)) return;
            Notify(nameof(IsRailVisible));
            Notify(nameof(IsSidebarTaskNavVisible));
        }
    }

    /// <summary>任务分组导航只在有实例时出现（上游 {instance &amp;&amp; &lt;TaskNav/&gt;}）。</summary>
    public bool IsSidebarTaskNavVisible => HasInstance;

    /// <summary>页面路由：核心页面与任务编辑器走共享控件，其余入口显示明确空态。</summary>
    public string ActivePage
    {
        get => _activePage;
        private set
        {
            if (!SetField(ref _activePage, value)) return;
            Notify(nameof(IsHomeActive));
            Notify(nameof(IsOverviewActive));
            Notify(nameof(IsStatisticsActive));
            Notify(nameof(IsTaskEditorActive));
            Notify(nameof(IsMeowfficerActive));
            Notify(nameof(IsConfigManagerActive));
            Notify(nameof(IsDevToolsActive));
            Notify(nameof(IsSettingsActive));
            Notify(nameof(IsRemoteActive));
            Notify(nameof(IsUpdaterActive));
            Notify(nameof(IsLoginActive));
            Notify(nameof(UsesMainScroll));
            Notify(nameof(IsInterfaceSettingsActive));
            Notify(nameof(IsPlaceholderActive));
            Notify(nameof(MainPadding));
            // 主页内边距为 0、其它页为 32：切页会改变可用内容高度，必须一起重算。
            Overview.ContentHeight = ContentMinHeight;
            Notify(nameof(ContentMinHeight));
            Notify(nameof(BreadcrumbTail));
            NotifyLayout();
        }
    }

    public bool IsHomeActive => _activePage == "home";
    public bool IsOverviewActive => _activePage == "overview";
    public bool IsStatisticsActive => _activePage == "statistics";
    public bool IsTaskEditorActive => _activePage == "task";
    public bool IsMeowfficerActive => _activePage == "meowfficer";
    public bool IsConfigManagerActive => _activePage == "configs";
    public bool IsDevToolsActive => _activePage == "dev";
    public bool IsSettingsActive => _activePage == "settings";
    public bool IsRemoteActive => _activePage == "remote";
    public bool IsUpdaterActive => _activePage == "updater";
    public bool IsLoginActive => _activePage == "login";
    public bool UsesMainScroll => !IsConfigManagerActive && !IsDevToolsActive;

    /// <summary>未实现的入口显示占位页。</summary>
    public bool IsPlaceholderActive => !IsHomeActive && !IsOverviewActive && !IsStatisticsActive &&
        !IsInterfaceSettingsActive && !IsTaskEditorActive && !IsMeowfficerActive && !IsConfigManagerActive && !IsDevToolsActive &&
        !IsSettingsActive && !IsRemoteActive && !IsUpdaterActive && !IsLoginActive;

    /// <summary>界面设置页（上游 /interface）：六主题与本地首选项的唯一入口。</summary>
    public bool IsInterfaceSettingsActive => _activePage == "interface";

    public string ActiveNavKey
    {
        get => _activeNavKey;
        private set => SetField(ref _activeNavKey, value);
    }

    public string BreadcrumbHome => "主页";

    public string BreadcrumbTail => _activePage switch
    {
            "overview" => "运行总览",
        "statistics" => "资源统计",
        "interface" => "界面设置",
        "task" => TaskEditor.Title,
        "meowfficer" => "指挥喵评分",
        "configs" => "配置管理",
        "dev" => "开发者工具",
        "settings" => "系统设置",
        "remote" => "远程访问",
        "updater" => "更新器",
        "login" => "登录",
        "home" => string.Empty,
        _ => Placeholder.Title,
    };

    /// <summary>窄屏时隐藏面包屑（上游 apple.css:338 的 ≤300px 兜底与窄屏压缩）。</summary>
    public bool IsBreadcrumbVisible => ViewportWidth > 300;

    public void UpdateViewport(double width, double height)
    {
        if (Math.Abs(ViewportWidth - width) < 0.01 && Math.Abs(ViewportHeight - height) < 0.01) return;
        ViewportWidth = width;
        ViewportHeight = height > 0 ? height : ViewportHeight;
        IsNarrow = width <= NarrowBreakpoint;
        Notify(nameof(IsCompact));
        Overview.IsWideLayout = !IsNarrow;
        Overview.ContentHeight = ContentMinHeight;
        Notify(nameof(BreadcrumbAlignment));
        Notify(nameof(ViewportWidth));
        Notify(nameof(ViewportHeight));
        Notify(nameof(TopbarHeight));
        Notify(nameof(MainPadding));
        Notify(nameof(ContentMinHeight));
        Notify(nameof(RailWidth));
        Notify(nameof(IsBreadcrumbVisible));
        NotifyLayout();
    }

    private void BuildNavigation()
    {
        PrimaryNav.Clear();
        if (HasInstance)
        {
            // 有实例：只有运行总览与资源统计（上游 App.tsx:242 的 instance 分支）。
            PrimaryNav.Add(new NavEntry("overview", "运行总览", "LayoutDashboard", _activeNavKey == "overview"));
            PrimaryNav.Add(new NavEntry("statistics", "资源统计", "ChartNoAxesCombined", _activeNavKey == "statistics"));
        }
        else
        {
            // 无实例：主页 + 七个全局入口，顺序与上游一致（最后一项是外链，本项目按未实现处理）。
            PrimaryNav.Add(new NavEntry("home", "主页", "House", _activeNavKey == "home"));
            PrimaryNav.Add(new NavEntry("updater", "更新器", "Download", _activeNavKey == "updater"));
            PrimaryNav.Add(new NavEntry("interface", "界面设置", "Palette", _activeNavKey == "interface"));
            PrimaryNav.Add(new NavEntry("remote", "远程访问", "Globe", _activeNavKey == "remote"));
            PrimaryNav.Add(new NavEntry("configs", "配置管理", "FileJson", _activeNavKey == "configs"));
            PrimaryNav.Add(new NavEntry("settings", "系统设置", "Settings2", _activeNavKey == "settings"));
            PrimaryNav.Add(new NavEntry("dev", "开发者工具", "Code2", _activeNavKey == "dev"));
            PrimaryNav.Add(new NavEntry("openSource", "开源项目", "ExternalLink", _activeNavKey == "openSource"));
        }

        RebuildTaskGroups();
    }

    private void RebuildTaskGroups()
    {
        var expanded = TaskGroups.Where(group => group.IsExpanded).Select(group => group.Key).ToHashSet();
        TaskGroups.Clear();
        if (!HasInstance) return;
        // 分组与任务来自上游静态目录 menu.json + zh-CN i18n，顺序原样保留。
        foreach (var (group, groupLabel, icon, tasks, labels) in TaskCatalog.Groups)
        {
            var entries = new List<TaskEntry>();
            for (var index = 0; index < tasks.Length; index++)
            {
                var entry = new TaskEntry(tasks[index], labels[index], groupLabel);
                if (entry.Matches(TaskSearchText)) entries.Add(entry);
            }
            if (entries.Count > 0)
                TaskGroups.Add(new TaskGroupEntry(group, groupLabel, icon, entries)
                { IsExpanded = expanded.Contains(group) || !string.IsNullOrWhiteSpace(TaskSearchText) });
        }
    }

    /// <summary>
    /// 进入某个实例的外壳（上游 /i/&lt;实例&gt;/overview）。主页卡片点击或外部导航都会走这里；
    /// 离线演示时由调用方显式指定实例名，不会在初始首页伪造已连接实例。
    /// </summary>
    public void SelectInstance(string instance)
    {
        if (string.IsNullOrWhiteSpace(instance)) return;
        _instanceVersion++;
        _editorLoadVersion++;
        _instanceName = instance;
        Notify(nameof(InstanceName));
        HasInstance = true;
        Overview.SetInstance(instance);
        Rail.SetInstance(instance);
        Statistics.Instance = instance;
        _ = RefreshBackendStateAsync();
        _activeNavKey = "overview";
        BuildNavigation();
        ActivePage = "overview";
        Notify(nameof(ActiveNavKey));
    }

    /// <summary>回到无实例外壳（上游点面包屑「主页」）。</summary>
    public void GoHome()
    {
        _instanceVersion++;
        _editorLoadVersion++;
        HasInstance = false;
        _activeNavKey = "home";
        BuildNavigation();
        ActivePage = "home";
        Notify(nameof(ActiveNavKey));
    }

    private void ReloadBackendInstances()
    {
        Instances.Clear();
        foreach (var item in _backend.Instances) Instances.Add(item.Name);
        Notify(nameof(InstancesSummary));
        Notify(nameof(IsBackendConnected));
        if (IsUiOnly) _ = RefreshBackendStateAsync();
    }

    public async Task RefreshBackendStateAsync()
    {
        if (!HasInstance || !_backend.IsConnected || (_stateRefreshInFlight && _refreshVersion == _instanceVersion)) return;
        long version = _instanceVersion;
        _refreshVersion = version;
        _stateRefreshInFlight = true;
        string instance = InstanceName;
        try
        {
            var state = await _backend.ReadInstanceStateAsync(instance).ConfigureAwait(true);
            if (HasInstance && _instanceVersion == version) Overview.ApplyState(state);
        }
        catch (Exception error)
        {
            if (HasInstance && _instanceVersion == version) Overview.ReportBackendError(error.Message);
        }
        finally { if (_refreshVersion == version) _stateRefreshInFlight = false; }
    }

    /// <summary>选择任务只读取配置；执行必须经过编辑器确认和 Core 队列。</summary>
    private void SelectTask(TaskEntry? task)
    {
        if (task is null || !HasInstance) return;
        long version = ++_editorLoadVersion;
        foreach (var entry in PrimaryNav) entry.IsActive = false;
        ActiveNavKey = $"task:{task.Key}";
        _activeTaskKey = task.Key;
        if (string.Equals(task.Key, "MeowfficerScore", StringComparison.OrdinalIgnoreCase))
        {
            ActivePage = "meowfficer";
            Notify(nameof(InstanceName));
        }
        else
        {
            var key = (InstanceName, task.Key);
            if (!_editors.TryGetValue(key, out var editor))
            {
                editor = new TaskEditorViewModel { Backend = new CoreTaskEditorBackend(_backend) };
                editor.PropertyChanged += (_, _) => { if (ReferenceEquals(TaskEditor, editor)) Notify(nameof(BreadcrumbTail)); };
                _editors.Add(key, editor);
            }
            TaskEditor = editor;
            Notify(nameof(TaskEditor));
            ActivePage = "task";
            if (!editor.IsLoaded) _ = LoadTaskEditorAsync(InstanceName, task, editor, version);
        }
        Notify(nameof(BreadcrumbTail));
        IsDrawerOpen = false;
    }

    private async Task LoadTaskEditorAsync(string instance, TaskEntry task, TaskEditorViewModel editor, long version)
    {
        try
        {
            var schema = await ReadTaskSchemaAsync().ConfigureAwait(true);
            var config = await _backend.ReadConfigAsync(instance).ConfigureAwait(true);
            if (_editorLoadVersion != version || !IsTaskEditorActive || InstanceName != instance) return;
            editor.Load(instance, task.Key, new JsonObject
            {
                ["args"] = schema.Args.DeepClone(),
                ["menu"] = schema.Menu.DeepClone(),
                ["translations"] = schema.Translations.DeepClone(),
            }, new JsonObject
            {
                ["instance"] = config.Instance,
                ["revision"] = config.Revision,
                ["values"] = config.Values.DeepClone(),
            });
        }
        catch (Exception error)
        {
            if (_editorLoadVersion == version) editor.SetLoadError(error.Message);
        }
    }

    private async Task<SchemaResponse> ReadTaskSchemaAsync()
    {
        // All task forms in one backend session use the same upstream argument schema.
        Task<SchemaResponse> task = _taskSchema ??= _backend.ReadSchemaAsync(cancellationToken: default);
        try
        {
            return await task.ConfigureAwait(true);
        }
        catch
        {
            if (ReferenceEquals(_taskSchema, task)) _taskSchema = null;
            throw;
        }
    }

    private void SelectNav(string? key)
    {
        if (key is null) return;
        _editorLoadVersion++;
        foreach (var entry in PrimaryNav) entry.IsActive = entry.Key == key;
        ActiveNavKey = key;
        // 面包屑「主页」= 回到无实例外壳（上游 / 路由），导航集合随之切回八项。
        if (key == "home" && HasInstance)
        {
            GoHome();
            IsDrawerOpen = false;
            return;
        }
        // 已实现的页面走真实视图；其余入口如实显示未实现，不伪造页面内容。
        ActivePage = key switch
        {
            "home" => "home",
            "overview" => "overview",
            "statistics" => "statistics",
            "interface" => "interface",
            "task" => "task",
            "meowfficer" => "meowfficer",
            _ => key,
        };
        if (IsPlaceholderActive) Placeholder.Load(key);
        if (IsStatisticsActive) _ = Statistics.ActivateAsync();
        IsDrawerOpen = false;
    }

    private static StatisticsRequest ToStatisticsRequest(JsonObject query) => new()
    {
        Instance = query["instance"]?.GetValue<string>() ?? string.Empty,
        Category = query["category"]?.GetValue<string>() ?? "resources",
        Days = query["days"]?.GetValue<int>() ?? 7,
        Month = query["month"]?.GetValue<string>(),
        Period = query["period"]?.GetValue<string>() ?? "month",
    };

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(propertyName);
        return true;
    }

    private void Notify([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName is nameof(IsNarrow) or nameof(IsDrawerOpen) or nameof(IsRailOpen))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsScrimVisible)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRailVisible)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSidebarVisible)));
        }
    }
}

public sealed class NavEntry : INotifyPropertyChanged
{
    private bool _isActive;

    public NavEntry(string key, string label, string iconKey, bool isActive)
    {
        Key = key;
        Label = label;
        Icon = IconLookup.Resolve(iconKey);
        _isActive = isActive;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Key { get; }
    public string Label { get; }
    public Geometry? Icon { get; }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
        }
    }
}

public sealed class TaskGroupEntry : INotifyPropertyChanged
{
    private bool _isExpanded;

    public TaskGroupEntry(string key, string title, string iconKey, IReadOnlyList<TaskEntry> tasks)
    {
        Key = key;
        Title = title;
        Tasks = tasks;
        Icon = IconLookup.Resolve(iconKey);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Key { get; }
    public string Title { get; }
    public IReadOnlyList<TaskEntry> Tasks { get; }
    public Geometry? Icon { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }
}

/// <summary>侧栏任务分组下的一个任务（名称来自上游 Task.<key>.name）。</summary>
public sealed record TaskEntry(string Key, string Label, string GroupTitle)
{
    public bool Matches(string query) => string.IsNullOrWhiteSpace(query)
        || Label.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)
        || GroupTitle.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>运行总览页：资源卡与运行监控（对齐上游 Overview.tsx + ResourceCards + MonitorPanel）。</summary>
public sealed class OverviewViewModel : INotifyPropertyChanged
{
    private string _monitorView = "logs";
    private bool _isFilterOpen;
    private bool _isFollowing = true;
    private bool _isDescending;
    private bool _isSchedulerRunning;
    private bool _isWideLayout = true;
    private string _searchText = string.Empty;
    private string _logLevel = "ALL";
    private double _contentHeight = 745.5;
    private readonly List<LogLineViewModel> _allLogs = new();
    private readonly bool _previewData;
    private readonly IAlasControlBackend? _backend;
    private bool _schedulerBusy;
    private bool _stateKnown;
    private bool _otherInstanceRunning;
    private bool _stopRequested;
    private string _schedulerPhase = "idle";
    private string? _logStream;
    private long _instanceGeneration;
    private long _nativeLogCursor;
    private long _coreLogCursor;
    private JsonObject? _observation;
    public IReadOnlyList<RailTaskViewModel> NativeTasks { get; private set; } = [];
    public event EventHandler? ObservationChanged;

    public OverviewViewModel(bool previewData = false, IAlasControlBackend? backend = null, IResourceSelectionStore? resourceStore = null)
    {
        _previewData = previewData;
        _backend = backend;
        Selection = new ResourceSelection(resourceStore ?? new MemoryResourceSelectionStore());
        ToggleFilterCommand = new PreviewCommand(_ => IsFilterOpen = !IsFilterOpen);
        ToggleFollowCommand = new PreviewCommand(_ => IsFollowing = !IsFollowing);
        ToggleOrderCommand = new PreviewCommand(_ => IsDescending = !IsDescending);
        ClearCommand = new PreviewCommand(_ => ClearLogs());
        ShowLogsCommand = new PreviewCommand(_ => MonitorView = "logs");
        ShowPreviewCommand = new PreviewCommand(_ => MonitorView = "preview");
        ToggleSchedulerCommand = new PreviewCommand(async _ => await ToggleSchedulerAsync());
        Resources = previewData
            ? new ObservableCollection<ResourceCardViewModel>
            {
                new("石油", "Resources/oil", "14,200", "/ 25,000", "记录于 09-24 01:13:44", 0),
                new("物资", "Resources/gold", "186,420", "/ 600,000", "记录于 09-24 01:13:44", 1),
                new("钻石", "Resources/diamond", "2,468", null, "记录于 09-24 01:13:44", 2),
                new("心智魔方", "Resources/cube", "384", null, "记录于 09-24 01:13:44", 3),
            }
            : new ObservableCollection<ResourceCardViewModel>();
        Selection.PropertyChanged += (_, args) =>
        {
            if (_previewData || args.PropertyName is not (null or nameof(ResourceSelection.Selected))) return;
            Resources.Clear();
            foreach (var choice in Selection.Selected) Resources.Add(choice.Card);
        };
        if (previewData) AppendLog("测试实例已就绪，所有操作均为模拟。");
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private string _instanceName = "未选择实例";
    public string InstanceName => _instanceName;
    public ObservableCollection<ResourceCardViewModel> Resources { get; }
    public ResourceSelection Selection { get; }

    /// <summary>日志缓存上限：上游 LogPanel 按 id 合并后只保留最近 400 条。</summary>
    public const int LogCapacity = 400;

    /// <summary>按当前搜索词、级别与排序过滤后的可见行（上游 .log-content 的渲染来源）。</summary>
    public ObservableCollection<LogLineViewModel> VisibleLogs { get; } = new();

    /// <summary>级别下拉的取值，顺序与上游 Select 一致。</summary>
    public IReadOnlyList<string> Levels { get; } = new[] { "ALL", "DEBUG", "INFO", "WARNING", "ERROR", "CRITICAL" };

    /// <summary>可见行变化后触发，由视图决定是否跟随滚动到底部。</summary>
    public event EventHandler? VisibleLogsChanged;

    public int CachedLogCount => _allLogs.Count;

    public ICommand ToggleFilterCommand { get; }
    public ICommand ToggleFollowCommand { get; }
    public ICommand ToggleOrderCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand ShowLogsCommand { get; }
    public ICommand ShowPreviewCommand { get; }
    public ICommand ToggleSchedulerCommand { get; }

    public string MonitorView
    {
        get => _monitorView;
        set
        {
            if (_monitorView == value) return;
            _monitorView = value;
            Notify(nameof(MonitorView));
            Notify(nameof(IsLogsView));
            Notify(nameof(IsPreviewView));
        }
    }

    public bool IsLogsView => _monitorView == "logs";
    public bool IsPreviewView => _monitorView == "preview";

    public bool IsFilterOpen
    {
        get => _isFilterOpen;
        set => SetField(ref _isFilterOpen, value);
    }

    public bool IsFollowing
    {
        get => _isFollowing;
        set => SetField(ref _isFollowing, value);
    }

    public bool IsDescending
    {
        get => _isDescending;
        set
        {
            if (!SetField(ref _isDescending, value)) return;
            RebuildVisibleLogs();
        }
    }

    public bool IsSchedulerRunning
    {
        get => _isSchedulerRunning;
        private set
        {
            if (!SetField(ref _isSchedulerRunning, value)) return;
            Notify(nameof(SchedulerButtonText));
            Notify(nameof(SchedulerStatusText));
        }
    }

    public string SchedulerButtonText => _stopRequested ? "正在停止…" : IsSchedulerRunning ? "停止运行" : "启动调度器";
    public string SchedulerStatusText => _stopRequested ? "等待任务边界停止" :
        IsSchedulerRunning ? _schedulerPhase == "waiting" ? "等待中" : "运行中" :
        _schedulerPhase is "failed" or "error" ? "运行失败" : "已停止";
    public bool IsSchedulerControlEnabled => _previewData ||
        (_backend is not null && _stateKnown && !_schedulerBusy && !_otherInstanceRunning && !_stopRequested);

    /// <summary>本阶段未接后端的入口统一禁用，并用这条提示说明原因。</summary>
    public string PendingNotice => "第一阶段未接后端：该入口在后续切片实现，当前不可用。";

    /// <summary>上游筛选行的「最近 {count} 条」显示的是缓存条数，不是可见条数。</summary>
    public string RecentCountText => $"最近 {CachedLogCount} 条";

    public bool HasVisibleLogs => VisibleLogs.Count > 0;
    public string EmptyLogTitle => CachedLogCount == 0 ? "日志通道已就绪" : "没有匹配的日志";
    public string EmptyLogText => CachedLogCount == 0 ? "启动任务后，日志将在这里显示。" : "尝试调整筛选条件。";

    /// <summary>日志搜索词：直接过滤可见行（上游 log.search）。</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetField(ref _searchText, value)) return;
            RebuildVisibleLogs();
        }
    }

    /// <summary>日志级别筛选：ALL 或 DEBUG/INFO/WARNING/ERROR/CRITICAL。</summary>
    public string LogLevel
    {
        get => _logLevel;
        set
        {
            if (!SetField(ref _logLevel, value)) return;
            RebuildVisibleLogs();
        }
    }

    /// <summary>宽屏时日志工具栏显示「导出」文字；窄屏只留图标（上游窄屏工具栏压缩）。</summary>
    public bool IsWideLayout
    {
        get => _isWideLayout;
        set
        {
            if (!SetField(ref _isWideLayout, value)) return;
            Notify(nameof(PanelHeight));
            Notify(nameof(ResourceColumns));
        }
    }

    public int ResourceColumns => IsWideLayout ? 4 : 2;
    private bool _hideTitle;
    public bool HideTitle
    {
        get => _hideTitle;
        set { if (SetField(ref _hideTitle, value)) Notify(nameof(PanelHeight)); }
    }

    /// <summary>总览页标题区高度：48 上边距 + 50 标题行 + 14 下边距（上游实测）。</summary>
    private const double OverviewTitleBlockHeight = 112;

    /// <summary>资源卡区高度：95 卡高 + 10 下边距（上游实测）。</summary>
    private const double OverviewCardsBlockHeight = 106;

    /// <summary>
    /// 监控面板高度。宽屏取「内容区高度 - 标题区 - 资源卡区」，等于上游 <c>.monitor-panel{flex:1}</c> 的结果；
    /// 这里给确定值而不是 NaN：面板内的日志区需要被约束住才能自己滚动，
    /// 否则 Avalonia 的滚动容器按无限高测量，日志会把整块面板顶高、页面反而出现滚动条。
    /// 窄屏按上游 apple.css:331 的 ≤950px 规则固定 520px。
    /// </summary>
    public double PanelHeight => IsWideLayout
        ? Math.Max(320, ContentHeight - (HideTitle ? 0 : OverviewTitleBlockHeight) - OverviewCardsBlockHeight)
        : 520;

    /// <summary>内容区可用高度，由外壳在视口变化时写入（上游 main 的高度预算）。</summary>
    public double ContentHeight
    {
        get => _contentHeight;
        set
        {
            if (!SetField(ref _contentHeight, value)) return;
            Notify(nameof(PanelHeight));
        }
    }

    /// <summary>通过 Core 请求启停；界面状态以运行时回报为准，停止须等上游边界确认。</summary>
    public async Task ToggleSchedulerAsync()
    {
        if (_previewData)
        {
            IsSchedulerRunning = !IsSchedulerRunning;
            AppendLog(IsSchedulerRunning ? "模拟调度器已启动。" : "模拟调度器已停止。");
            return;
        }
        if (!IsSchedulerControlEnabled || _backend is null) return;
        _schedulerBusy = true;
        string instance = InstanceName;
        long generation = _instanceGeneration;
        Notify(nameof(IsSchedulerControlEnabled));
        try
        {
            if (IsSchedulerRunning)
            {
                if (!await _backend.RequestStopAsync())
                    throw new InvalidOperationException("当前没有可停止的运行任务");
            }
            else
                await _backend.StartSchedulerAsync(new InstanceSchedulerRunRequest
                    { Instance = instance, ConfirmActions = true });
            var state = await _backend.ReadInstanceStateAsync(instance);
            if (_instanceGeneration == generation) ApplyState(state);
        }
        catch (Exception error)
        {
            if (_instanceGeneration == generation) ReportBackendError(error.Message);
        }
        finally
        {
            _schedulerBusy = false;
            Notify(nameof(IsSchedulerControlEnabled));
        }
    }

    public void SetInstance(string instance)
    {
        if (string.IsNullOrWhiteSpace(instance) || _instanceName == instance) return;
        _instanceGeneration++;
        _instanceName = instance;
        Selection.SetInstance(instance);
        _stateKnown = false;
        _otherInstanceRunning = false;
        _stopRequested = false;
        _schedulerPhase = "idle";
        IsSchedulerRunning = false;
        if (!_previewData)
        {
            _logStream = null;
            _nativeLogCursor = _coreLogCursor = 0;
            _observation = null;
            NativeTasks = [];
            ClearLogs();
            ObservationChanged?.Invoke(this, EventArgs.Empty);
        }
        Notify(nameof(InstanceName));
        NotifyScheduler();
    }

    public void ApplyState(JsonObject state)
    {
        if (state["active"] is not JsonObject active) return;
        string status = active["status"]?.GetValue<string>() ?? "idle";
        bool selected = active["instance"]?.GetValue<string>() == InstanceName;
        _stateKnown = true;
        _otherInstanceRunning = status == "running" && !selected;
        IsSchedulerRunning = status == "running" && selected;
        _stopRequested = IsSchedulerRunning && active["stop_requested"]?.GetValue<bool>() == true;
        _schedulerPhase = selected ? active["scheduler"]?["phase"]?.GetValue<string>() ?? status : "idle";
        var configured = state["overview"] as JsonObject;
        if (configured?["instance"]?.GetValue<string>() != InstanceName) configured = null;
        _observation = IsSchedulerRunning && active["scheduler"] is JsonObject native ? native : configured;
        NativeTasks = SchedulerObservation.Tasks(_observation, IsSchedulerRunning);
        if (!_previewData)
        {
            Selection.Observe(_observation);
        }
        if (selected) ApplyLogSnapshot(state, active);
        ObservationChanged?.Invoke(this, EventArgs.Empty);
        NotifyScheduler();
    }

    private void ApplyLogSnapshot(JsonObject state, JsonObject active)
    {
        string stream = active["started_at"]?.GetValue<string>() ?? "idle";
        if (_logStream != stream)
        {
            _logStream = stream;
            _nativeLogCursor = _coreLogCursor = 0;
            ClearLogs();
        }
        var additions = new List<JsonObject>();
        void Collect(JsonArray? entries, ref long cursor)
        {
            foreach (var entry in entries?.OfType<JsonObject>() ?? [])
                if (entry["id"] is JsonValue id && id.TryGetValue<long>(out long sequence) && sequence > cursor)
                {
                    additions.Add(entry);
                    cursor = sequence;
                }
        }
        Collect(state["recent_logs"] as JsonArray, ref _coreLogCursor);
        Collect(active["scheduler"]?["logs"]?["entries"] as JsonArray, ref _nativeLogCursor);
        foreach (var entry in additions.OrderBy(entry => DateTimeOffset.TryParse(entry["time"]?.GetValue<string>(),
                     CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp) ? timestamp : DateTimeOffset.MinValue))
        {
            DateTimeOffset.TryParse(entry["time"]?.GetValue<string>(), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var timestamp);
            string level = entry["level"]?.GetValue<string>() ?? "INFO";
            AppendLog(entry["message"]?.GetValue<string>() ?? "", level == "WARN" ? "WARNING" : level,
                timestamp == default ? DateTime.Now : timestamp.LocalDateTime);
        }
    }

    private void NotifyScheduler()
    {
        Notify(nameof(SchedulerButtonText));
        Notify(nameof(SchedulerStatusText));
        Notify(nameof(IsSchedulerControlEnabled));
    }

    public void ReportBackendError(string message)
    {
        _stateKnown = false;
        NotifyScheduler();
        AppendLog(message, "ERROR");
    }

    /// <summary>
    /// 追加一条日志：超出上限时丢弃最旧一条。逐条增量维护可见行（等价于上游按 id 合并增量），
    /// 只有搜索/级别/排序变化时才整体重建，避免每次追加都重建整个列表。
    /// </summary>
    public void AppendLog(string message, string level = "INFO", DateTime? timestamp = null)
    {
        var time = timestamp ?? DateTime.Now;
        var line = new LogLineViewModel(time.ToString("yyyy-MM-dd"), time.ToString("HH:mm:ss"), level, $"[{InstanceName}]", message);
        _allLogs.Add(line);
        while (_allLogs.Count > LogCapacity)
        {
            var dropped = _allLogs[0];
            _allLogs.RemoveAt(0);
            // 被淘汰的是缓存里最旧的一条，它在可见列表里的位置取决于排序与筛选：
            // 正序在头部、倒序在尾部、被筛掉时根本不在列表里，因此按对象移除，不能只删 VisibleLogs[0]。
            VisibleLogs.Remove(dropped);
        }
        if (MatchesFilter(line))
        {
            if (IsDescending) VisibleLogs.Insert(0, line);
            else VisibleLogs.Add(line);
        }
        NotifyLogCounters();
        VisibleLogsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>清空当前视图：上游把 floor 设为最后一条，这里等价于丢弃已缓存内容。</summary>
    private void ClearLogs()
    {
        _allLogs.Clear();
        RebuildVisibleLogs();
    }

    /// <summary>按搜索词与级别判断某一行是否可见。</summary>
    private bool MatchesFilter(LogLineViewModel line)
    {
        var search = SearchText?.Trim();
        if (!string.IsNullOrEmpty(search)
            && !line.Message.Contains(search, StringComparison.OrdinalIgnoreCase)
            && !line.Scope.Contains(search, StringComparison.OrdinalIgnoreCase))
            return false;
        return string.Equals(LogLevel, "ALL", StringComparison.Ordinal)
            || string.Equals(line.Level, LogLevel, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>按搜索词、级别与排序重建可见行；排序只改渲染顺序，不改缓存。</summary>
    private void RebuildVisibleLogs()
    {
        var rows = _allLogs.Where(MatchesFilter).ToList();
        if (IsDescending) rows.Reverse();
        VisibleLogs.Clear();
        foreach (var row in rows) VisibleLogs.Add(row);
        NotifyLogCounters();
        VisibleLogsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyLogCounters()
    {
        Notify(nameof(RecentCountText));
        Notify(nameof(HasVisibleLogs));
        Notify(nameof(EmptyLogTitle));
        Notify(nameof(EmptyLogText));
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
}

/// <summary>资源卡：底色由显示下标 %4 决定，数值与上限文案按上游 ResourceCards 规则拼接。</summary>
public sealed class ResourceCardViewModel
{
    public ResourceCardViewModel(string name, string? imagePath, string value, string? limit, string foot, int tintIndex)
    {
        Name = name;
        Value = value;
        Limit = limit;
        Foot = foot;
        TintIndex = tintIndex % 4;
        Image = imagePath is null ? null : UIAssets.TryLoadBitmap($"avares://Alas.UI/Assets/{imagePath}.webp");
    }

    public string Name { get; }
    public string Value { get; }
    public string? Limit { get; }
    public bool HasLimit => !string.IsNullOrEmpty(Limit);
    public string Foot { get; }
    public int TintIndex { get; }
    public bool IsTint0 => TintIndex == 0;
    public bool IsTint1 => TintIndex == 1;
    public bool IsTint2 => TintIndex == 2;
    public bool IsTint3 => TintIndex == 3;
    public Bitmap? Image { get; }
    public bool HasImage => Image is not null;
    public bool HasFallback => Image is null;
}

public sealed record LogLineViewModel(string Date, string Time, string Level, string Scope, string Message);

/// <summary>右栏：调度器卡 + 任务计划三组（running/pending/waiting 固定顺序）。</summary>
public sealed class RailViewModel : INotifyPropertyChanged
{
    private readonly bool _previewData;
    public RailViewModel(OverviewViewModel overview, bool previewData = false)
    {
        _previewData = previewData;
        Overview = overview;
        Overview.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is not (nameof(OverviewViewModel.IsSchedulerRunning))) return;
            Notify(nameof(RunningCount));
            Notify(nameof(IsStopped));
        };
        Overview.ObservationChanged += (_, _) =>
        {
            if (previewData) return;
            foreach (var group in Groups!)
            {
                group.Tasks.Clear();
                foreach (var task in Overview.NativeTasks.Where(task => task.State == group.State)) group.Tasks.Add(task);
                group.Refresh();
            }
            Notify(nameof(PlanCountText));
            Notify(nameof(RunningCount));
            Notify(nameof(PendingCount));
            Notify(nameof(WaitingCount));
        };
        Groups = previewData
            ? new ObservableCollection<RailGroupViewModel>
            {
                new("正在运行", "running", "CirclePlay", "当前没有正在运行的任务", Array.Empty<RailTaskViewModel>()),
                new("待运行", "pending", "ListTodo", "当前没有待运行任务", new[]
                {
                    new RailTaskViewModel("重启设置", "2020-01-01 00:00:00", "pending", "待运行"),
                    new RailTaskViewModel("委托", "2026-09-24 00:43:44", "pending", "待运行"),
                    new RailTaskViewModel("科研", "2026-09-24 01:13:44", "pending", "待运行"),
                    new RailTaskViewModel("收获", "2020-01-01 00:00:00", "pending", "待运行"),
                }),
                new("等待中", "waiting", "Hourglass", "当前没有等待中的任务", new[]
                {
                    new RailTaskViewModel("主线图-1Plus", "2026-09-24 02:13:44", "waiting", "等待中"),
                    new RailTaskViewModel("后宅", "2026-09-24 01:43:44", "waiting", "等待中"),
                }),
            }
            : new ObservableCollection<RailGroupViewModel>
            {
                new("正在运行", "running", "CirclePlay", "当前没有正在运行的任务", Array.Empty<RailTaskViewModel>()),
                new("待运行", "pending", "ListTodo", "当前没有待运行任务", Array.Empty<RailTaskViewModel>()),
                new("等待中", "waiting", "Hourglass", "当前没有等待中的任务", Array.Empty<RailTaskViewModel>()),
            };
    }

    public OverviewViewModel Overview { get; }
    public string InstanceName => _instanceName;
    public string PlanCountText => Groups.Sum(group => group.Tasks.Count).ToString(CultureInfo.InvariantCulture);
    public string RunningCount => _previewData ? Overview.IsSchedulerRunning ? "1" : "0"
        : Groups.First(group => group.State == "running").Tasks.Count.ToString(CultureInfo.InvariantCulture);
    public string PendingCount => Groups.First(group => group.State == "pending").Tasks.Count.ToString(CultureInfo.InvariantCulture);
    public string WaitingCount => Groups.First(group => group.State == "waiting").Tasks.Count.ToString(CultureInfo.InvariantCulture);
    public bool IsStopped => !Overview.IsSchedulerRunning;
    public ObservableCollection<RailGroupViewModel> Groups { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetInstance(string instance)
    {
        if (string.IsNullOrWhiteSpace(instance) || InstanceName == instance) return;
        _instanceName = instance;
        Notify(nameof(InstanceName));
    }

    private string _instanceName = "未选择实例";
    private void Notify(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class RailGroupViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEmpty)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountText)));
    }
    public RailGroupViewModel(string title, string state, string iconKey, string emptyText, IReadOnlyList<RailTaskViewModel> tasks)
    {
        Title = title;
        State = state;
        Icon = IconLookup.Resolve(iconKey);
        EmptyText = emptyText;
        Tasks = new ObservableCollection<RailTaskViewModel>(tasks);
    }

    public string Title { get; }
    public string State { get; }
    public Geometry? Icon { get; }
    public string EmptyText { get; }
    public bool IsEmpty => Tasks.Count == 0;
    public bool IsRunning => State == "running";
    public bool IsWaiting => State == "waiting";
    public string CountText => Tasks.Count.ToString(CultureInfo.InvariantCulture);
    public ObservableCollection<RailTaskViewModel> Tasks { get; }
}

public sealed class RailTaskViewModel
{
    public RailTaskViewModel(string name, string time, string state, string stateLabel)
    {
        Name = name;
        Time = time;
        State = state;
        StateLabel = stateLabel;
        Icon = IconLookup.Resolve(state switch
        {
            "running" => "CirclePlay",
            "pending" => "ListTodo",
            _ => "Hourglass",
        });
    }

    public string Name { get; }
    public string Time { get; }
    public string State { get; }
    public string StateLabel { get; }
    public bool IsWaiting => State == "waiting";
    public Geometry? Icon { get; }
}

/// <summary>第一阶段未实现的页面：只显示入口名与说明，不伪造内容。</summary>
public sealed class PlaceholderViewModel : INotifyPropertyChanged
{
    private string _title = "资源统计";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title
    {
        get => _title;
        private set
        {
            if (_title == value) return;
            _title = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
        }
    }

    public string Hint => string.IsNullOrEmpty(Group)
        ? "该页面属于后续阶段：外壳与运行总览完成并复核后再按同一基准逐页实现。"
        : $"任务配置页（{Group} → {Title}）属于后续阶段：本切片只做经典外壳与运行总览，不连接服务、不执行任务。";

    private string Group { get; set; } = string.Empty;

    /// <summary>选中侧栏任务后切到该任务的未实现页；只改导航状态，不触发任何运行。</summary>
    public void LoadTask(string group, string task)
    {
        Group = group;
        Title = task;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Hint)));
    }

    public void Load(string key)
    {
        Group = string.Empty;
        Title = key switch
        {
            "home" => "主页",
            "statistics" => "资源统计",
            "updater" => "更新器",
            "interface" => "界面设置",
            "remote" => "远程访问",
            "configs" => "配置管理",
            "settings" => "系统设置",
            "dev" => "开发者工具",
            _ => "页面",
        };
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Hint)));
    }
}

/// <summary>从应用资源字典按图标名取几何；缺失时返回 null，由控件跳过绘制。</summary>
internal static class IconLookup
{
    public static Geometry? Resolve(string key)
    {
        if (Application.Current is { } application
            && application.TryGetResource($"Icon{key}", null, out var value)
            && value is Geometry geometry)
            return geometry;
        return null;
    }
}

internal static class UIAssets
{
    private static readonly Dictionary<string, Bitmap> Bitmaps = new(StringComparer.Ordinal);
    public static Bitmap? TryLoadBitmap(string uri)
    {
        lock (Bitmaps)
        {
            if (Bitmaps.TryGetValue(uri, out var cached)) return cached;
            try
            {
                using var stream = Avalonia.Platform.AssetLoader.Open(new Uri(uri));
                var bitmap = new Bitmap(stream);
                Bitmaps.Add(uri, bitmap);
                return bitmap;
            }
            catch (Exception)
            {
                // Headless checks may run before the renderer is initialized.
                // Do not cache failures: a later attached view can load the asset.
                return null;
            }
        }
    }
}

/// <summary>演示用命令：只更新本地预览状态，不调度任务、不访问服务。</summary>
internal sealed class PreviewCommand(Action<object?> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute(parameter);
}
