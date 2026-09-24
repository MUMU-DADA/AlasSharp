using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Alas.UI.Statistics;

public partial class StatisticsView : UserControl
{
    public StatisticsView() => InitializeComponent();
    public StatisticsView(StatisticsViewModel model) : this() => DataContext = model;
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
