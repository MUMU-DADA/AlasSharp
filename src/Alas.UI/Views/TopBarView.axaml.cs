using Avalonia.Controls;

namespace Alas.UI.Views;

/// <summary>顶栏：窄屏开关、可展开的快捷开关、面包屑与实例切换（上游 .topbar / .breadcrumb）。</summary>
public partial class TopBarView : UserControl
{
    public TopBarView() => InitializeComponent();

    public void SetBreadcrumbHost(Panel? host)
    {
        var target = host ?? TopbarRoot;
        if (ReferenceEquals(Breadcrumb.Parent, target)) return;
        if (Breadcrumb.Parent is Panel previous) previous.Children.Remove(Breadcrumb);
        target.Children.Add(Breadcrumb);
    }

}
