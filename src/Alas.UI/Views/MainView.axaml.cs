using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Alas.UI.ViewModels;

namespace Alas.UI.Views;

/// <summary>
/// 共享外壳：侧栏 + 顶栏 + 内容区 + 右栏。
/// 上游用 CSS 媒体查询（≤950px）把侧栏与右栏换成浮层抽屉；这里用同一断点，
/// 在 <see cref="ApplyLayout"/> 里切换列宽、对齐与抽屉位移，保持单一视觉树。
/// </summary>
public partial class MainView : UserControl
{
    private static readonly TranslateTransform DrawerClosed = new(-250, 0);
    private static readonly TranslateTransform DrawerOpen = new(0, 0);
    private static readonly TranslateTransform None = new(0, 0);
    private readonly Avalonia.Threading.DispatcherTimer _stateTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private long _homeCreateVersion;

    public MainView()
        : this(new Theming.MemoryThemeStore())
    {
    }

    public MainView(Theming.IThemeStore themeStore, IAlasUiBackend? backend = null, Platform.IUiFiles? files = null)
    {
        InitializeComponent();
        files ??= new Platform.UiFiles(() => TopLevel.GetTopLevel(this)?.StorageProvider);
        Model = new ShellViewModel(themeStore, backend, previewData: backend is null, files: files);
        DataContext = Model;
        Model.PropertyChanged += OnModelChanged;
        var deploySession = new DeploySettings.DeploySettingsSession(
            new Settings.SettingsTransport(Model.SettingsBackend, 1), async cancellationToken =>
            {
                var schema = DeploySettings.DeploySchemaAdapter.ToSchema(await Model.SettingsBackend.ReadAsync(cancellationToken));
                return schema is null ? null : schema with
                {
                    Remote = new DeploySettings.DeployRemoteStatus(null, false, string.Empty,
                        "远程连接服务尚未接通；下方设置可保存，保存不会启动远程服务。"),
                };
            });
        SettingsPage = new Settings.SettingsView(Model.SettingsBackend, deploySession);
        RemotePage = new RemoteAccess.RemoteAccessView(
            RemoteAccess.DisconnectedRemoteAccessBackend.Instance, SettingsPage.Session, async address =>
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard is null) return false;
                await clipboard.SetTextAsync(address);
                return true;
            });
        SettingsHost.Children.Add(SettingsPage);
        RemoteHost.Children.Add(RemotePage);
        ConfigManagerPage.ModalHost = Root;
        DevToolsPage.ModalHost = Root;
        DevToolsPage.UiTheme = Model.CurrentTheme;
        ConfigManagerPage.Model.Connected = Model.IsBackendConnected;
        ConfigManagerPage.Backend = Model.ConfigManagerBackend;
        ConfigManagerPage.Model.OpenOverview = Model.SelectInstance;
        ConfigManagerPage.Model.ExportFileAsync = files.SaveTextAsync;
        ConfigManagerPage.Model.PickImportFileAsync = files.OpenJsonAsync;
        Model.Home.CreateInstance = async import =>
        {
            long request = ++_homeCreateVersion;
            bool ready = await ConfigManagerPage.Model.RefreshAsync();
            if (request != _homeCreateVersion || !Model.IsHomeActive) return;
            if (ready) ConfigManagerPage.Model.OpenCreateForm(import);
            else Model.Home.Report(ConfigManagerPage.Model.Error);
        };
        MeowfficerPage.Backend = Model.MeowfficerBackend;
        UpdateMeowfficerPage();
        _stateTimer.Tick += async (_, _) => await Model.RefreshBackendStateAsync();
        SizeChanged += (_, _) => Model.UpdateViewport(Bounds.Width, Bounds.Height);
        // 遮罩点击关闭当前浮层：窄屏抽屉与右栏浮层共用同一个收起命令。
        Scrim.PointerPressed += (_, args) =>
        {
            Model.CloseDrawerCommand.Execute(null);
            args.Handled = true;
        };
        MainScroll.Focusable = true;
        ConfigManagerHost.Focusable = true;
        DevToolsHost.Focusable = true;
        SkipLink.Click += (_, _) =>
            (Model.IsConfigManagerActive ? (Control)ConfigManagerHost : Model.IsDevToolsActive ? DevToolsHost : MainScroll).Focus();
    }

    public ShellViewModel Model { get; }
    public Settings.SettingsView SettingsPage { get; }
    public RemoteAccess.RemoteAccessView RemotePage { get; }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Bounds.Width > 0) Model.UpdateViewport(Bounds.Width, Bounds.Height);
        ApplyLayout();
        _stateTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _stateTimer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ShellViewModel.CurrentTheme)) DevToolsPage.UiTheme = Model.CurrentTheme;
        if (args.PropertyName == nameof(ShellViewModel.IsDevToolsActive) && !Model.IsDevToolsActive)
            DevToolsPage.CloseModal();
        if (args.PropertyName == nameof(ShellViewModel.IsHomeActive)) _homeCreateVersion++;
        if ((args.PropertyName == nameof(ShellViewModel.IsSettingsActive) && Model.IsSettingsActive) ||
            (args.PropertyName == nameof(ShellViewModel.IsRemoteActive) && Model.IsRemoteActive))
            _ = SettingsPage.Session.RefreshAsync();
        if (args.PropertyName == nameof(ShellViewModel.IsBackendConnected))
        {
            ConfigManagerPage.Model.Connected = Model.IsBackendConnected;
            if (Model.IsBackendConnected && Model.IsConfigManagerActive) _ = ConfigManagerPage.Model.RefreshAsync();
            if (Model.IsSettingsActive || Model.IsRemoteActive) _ = SettingsPage.Session.RefreshAsync();
        }
        if (args.PropertyName == nameof(ShellViewModel.IsConfigManagerActive))
        {
            if (Model.IsConfigManagerActive) _ = ConfigManagerPage.Model.RefreshAsync();
            else ConfigManagerPage.Model.CloseForm();
        }
        if (args.PropertyName is nameof(ShellViewModel.IsNarrow) or nameof(ShellViewModel.HasInstance)
            or nameof(ShellViewModel.IsDrawerOpen)
            or nameof(ShellViewModel.IsRailOpen) or nameof(ShellViewModel.IsRailVisible)
            or nameof(ShellViewModel.ViewportWidth) or nameof(ShellViewModel.ViewportHeight)
            or nameof(ShellViewModel.CurrentTheme) or nameof(ShellViewModel.IsLegacyOverview))
            ApplyLayout();
        if (args.PropertyName is nameof(ShellViewModel.InstanceName) or nameof(ShellViewModel.IsMeowfficerActive))
            UpdateMeowfficerPage();
    }

    private void UpdateMeowfficerPage()
    {
        MeowfficerPage.IsActive = Model.IsMeowfficerActive;
        MeowfficerPage.Instance = Model.HasInstance ? Model.InstanceName : "";
    }

    private void ApplyLayout()
    {
        bool narrow = Model.IsNarrow;
        bool legacy = Model.IsLegacyDesktop;
        bool inlineRail = Model.IsLegacyOverview;
        bool railColumn = Model.IsRailVisible && !legacy && !narrow;
        Layout.ColumnDefinitions = narrow ? new ColumnDefinitions("0,*,0")
            : railColumn ? new ColumnDefinitions($"{Model.SidebarWidth},*,{Model.RailWidth}")
            : new ColumnDefinitions($"{Model.SidebarWidth},*");
        Layout.ColumnSpacing = Model.ShellGap;
        Layout.RowDefinitions = legacy ? new RowDefinitions("56,42,*") : new RowDefinitions("Auto,*");
        int contentRow = legacy ? 2 : 1;
        Grid.SetRow(SidebarPanel, legacy ? 1 : 0);
        Grid.SetRowSpan(SidebarPanel, 2);
        Grid.SetColumn(SidebarPanel, 0);
        Grid.SetColumn(TopbarPanel, legacy ? 0 : 1);
        Grid.SetColumnSpan(TopbarPanel, legacy || railColumn ? 3 : 2);
        foreach (var page in new Control[] { MainScroll, ConfigManagerHost, DevToolsHost })
        {
            Grid.SetRow(page, contentRow);
            Grid.SetColumn(page, 1);
        }
        Grid.SetRow(RailPanel, contentRow);
        // Reassign a different column first: Avalonia otherwise keeps the old cell
        // after the column definitions change from two columns to three.
        Grid.SetColumn(RailPanel, inlineRail ? 2 : 1);
        Grid.SetColumn(RailPanel, inlineRail ? 1 : 2);
        Grid.SetRowSpan(Scrim, legacy ? 3 : 2);
        Topbar.SetBreadcrumbHost(legacy ? LegacyNavigationHost : null);
        Layout.InvalidateMeasure();
        Layout.InvalidateArrange();

        SidebarPanel.Width = Model.SidebarWidth;
        SidebarPanel.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        SidebarPanel.ZIndex = narrow ? 10 : 0;
        SidebarPanel.RenderTransform = narrow ? Model.IsDrawerOpen ? DrawerOpen : DrawerClosed : None;
        SidebarPanel.BoxShadow = Shadow(narrow ? "AlasDrawerShadow" : "AlasSidebarShadow");
        SidebarPanel.CornerRadius = Model.IsClassicLayout ? new CornerRadius(0, 26, 26, 0) : new CornerRadius(0);
        SidebarPanel.Padding = Model.IsExtremeDesktop ? new Thickness(8, 0, 8, 8)
            : new Thickness(14, legacy ? 14 : 0, 14, 14);
        Sidebar.ApplySkin();

        TopbarPanel.Height = Model.TopbarHeight;
        TopbarPanel.Margin = narrow ? new Thickness(12, 0, 0, 0)
            : Model.IsClassicLayout ? new Thickness(24, 0, 0, 0) : new Thickness(0);
        TopbarPanel.Padding = narrow ? new Thickness(10, 5.75)
            : Model.IsClassicLayout ? new Thickness(18, 5.75) : new Thickness(12, 0);
        TopbarPanel.CornerRadius = Model.IsClassicLayout ? new CornerRadius(26, 0, 0, 26) : new CornerRadius(0);

        RailPanel.Width = narrow ? Math.Min(360, Math.Max(280, Model.ViewportWidth - 12)) : Model.RailWidth;
        RailPanel.Margin = narrow ? new Thickness(0, Model.TopbarHeight, 0, 0)
            : inlineRail ? new Thickness(28, 14, 0, 24) : new Thickness(0);
        RailPanel.HorizontalAlignment = inlineRail ? Avalonia.Layout.HorizontalAlignment.Left : Avalonia.Layout.HorizontalAlignment.Right;
        RailPanel.ZIndex = narrow ? 10 : 0;
        RailPanel.BoxShadow = Shadow(narrow ? "AlasRailDrawerShadow" : "AlasRailShadow");
    }

    private static BoxShadows Shadow(string key)
    {
        if (Application.Current is { } application && application.TryFindResource(key, out var value)
            && value is BoxShadows shadows)
            return shadows;
        return default;
    }
}
