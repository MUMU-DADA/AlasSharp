using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Alas.UI.ViewModels;

namespace Alas.UI.Views;

/// <summary>侧栏：品牌、一级导航与任务分组导航（上游 .sidebar / .primary-nav / .task-nav）。</summary>
public partial class SidebarView : UserControl
{
    public SidebarView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(ApplySkin, DispatcherPriority.Loaded);
    }

    public void ApplySkin()
    {
        if (DataContext is not ShellViewModel model) return;
        bool extreme = model.IsExtremeDesktop;
        BrandBlock.IsVisible = !model.IsLegacyDesktop;
        BrandBlock.Height = extreme ? 44 : 56;
        BrandBlock.Margin = new Thickness(extreme ? 6 : 10, 0, extreme ? 6 : 10, extreme ? 6 : 10);
        BrandLogo.Width = BrandLogo.Height = extreme ? 28 : 32;
        PrimaryNav.Margin = new Thickness(0, 0, 0, extreme ? 10 : 28);
        if (PrimaryNav.ItemsPanelRoot is StackPanel nav) nav.Spacing = extreme ? 1 : model.IsLegacyDesktop ? 4 : 5;
        if (TaskNav.ItemsPanelRoot is StackPanel tasks) tasks.Spacing = extreme ? 0 : 2;
    }

}
