using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Alas.UI.Views;

/// <summary>界面设置页视图；数据上下文是 <see cref="ViewModels.InterfaceSettingsViewModel"/>。</summary>
public partial class InterfaceSettingsView : UserControl
{
    public InterfaceSettingsView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
