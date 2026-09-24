using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Alas.UI.ViewModels;

namespace Alas.UI.Views;

/// <summary>
/// 旧原型离线预览页的视图（工作区名双向绑定、JSON 校验、2,000 行日志列表）。
/// 由 <c>Alas.UI.Headless</c> 的无窗口回归驱动，后续迁移到对应上游页面时复用这些控件与模型。
/// </summary>
public partial class OfflinePreviewView : UserControl
{
    public OfflinePreviewView()
    {
        InitializeComponent();
        DataContext = Model;
        Model.PropertyChanged += OnModelChanged;
    }

    public OfflinePreviewViewModel Model { get; } = new();

    private void OnModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(OfflinePreviewViewModel.IsDark) || Application.Current is not { } application)
            return;
        application.RequestedThemeVariant = Model.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
    }
}
