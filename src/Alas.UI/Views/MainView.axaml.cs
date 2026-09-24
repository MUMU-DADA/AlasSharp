using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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

    public MainView()
        : this(new Theming.MemoryThemeStore())
    {
    }

    public MainView(Theming.IThemeStore themeStore, IAlasUiBackend? backend = null)
    {
        InitializeComponent();
        Model = new ShellViewModel(themeStore, backend, previewData: backend is null);
        DataContext = Model;
        Model.PropertyChanged += OnModelChanged;
        SizeChanged += (_, _) => Model.UpdateViewport(Bounds.Width, Bounds.Height);
        // 遮罩点击关闭当前浮层：窄屏抽屉与右栏浮层共用同一个收起命令。
        Scrim.PointerPressed += (_, args) =>
        {
            Model.CloseDrawerCommand.Execute(null);
            args.Handled = true;
        };
    }

    public ShellViewModel Model { get; }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Bounds.Width > 0) Model.UpdateViewport(Bounds.Width, Bounds.Height);
        ApplyLayout();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ShellViewModel.IsNarrow) or nameof(ShellViewModel.HasInstance)
            or nameof(ShellViewModel.IsDrawerOpen)
            or nameof(ShellViewModel.IsRailOpen) or nameof(ShellViewModel.IsRailVisible)
            or nameof(ShellViewModel.ViewportWidth) or nameof(ShellViewModel.ViewportHeight))
            ApplyLayout();
    }

    private void ApplyLayout()
    {
        var narrow = Model.IsNarrow;
        Layout.ColumnDefinitions = narrow
            ? new ColumnDefinitions("0,*,0")
            // 无实例时上游只有两列（232 + 内容），右栏列不占位：多一列会多算一次列间距。
            : Model.HasInstance
                ? new ColumnDefinitions($"232,*,{Model.RailWidth}")
                : new ColumnDefinitions("232,*");
        Layout.ColumnSpacing = narrow ? 0 : 14;
        // 整份替换 ColumnDefinitions 后，子元素会保留上一次排布算出的「单元格编号」
        // （实测：无实例两列 → 有实例三列时，右栏仍按「被夹到最后一列」的位置摆放，
        //  停在内容列右缘 682 而不是 988）。重新写一遍单元格编号并让网格失效重排。
        Grid.SetColumn(RailPanel, 1);
        Grid.SetColumn(RailPanel, 2);
        Grid.SetColumn(MainScroll, 1);
        Grid.SetColumn(SidebarPanel, 0);
        Layout.InvalidateMeasure();
        Layout.InvalidateArrange();
        RailPanel.InvalidateMeasure();
        RailPanel.InvalidateArrange();

        SidebarPanel.Width = 232;
        SidebarPanel.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        SidebarPanel.ZIndex = narrow ? 10 : 0;
        SidebarPanel.RenderTransform = narrow
            ? Model.IsDrawerOpen ? DrawerOpen : DrawerClosed
            : None;
        SidebarPanel.BoxShadow = Shadow(narrow ? "AlasDrawerShadow" : "AlasSidebarShadow");

        TopbarPanel.Height = Model.TopbarHeight;
        TopbarPanel.Margin = new Thickness(narrow ? 12 : 24, 0, 0, 0);
        TopbarPanel.Padding = narrow ? new Thickness(10, 5.75, 10, 5.75) : new Thickness(18, 5.75, 18, 5.75);

        RailPanel.Width = narrow ? Math.Min(360, Math.Max(280, Model.ViewportWidth - 12)) : Model.RailWidth;
        RailPanel.Margin = narrow ? new Thickness(0, Model.TopbarHeight, 0, 0) : new Thickness(0);
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
