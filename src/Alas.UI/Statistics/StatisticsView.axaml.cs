using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Markup.Xaml;

namespace Alas.UI.Statistics;

public partial class StatisticsView : UserControl
{
    public StatisticsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BuildCategories();
        BuildCategories();
    }
    public StatisticsView(StatisticsViewModel model) : this() => DataContext = model;
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
    private void BuildCategories()
    {
        if (this.FindControl<WrapPanel>("CategoryButtons") is not { } panel || DataContext is not StatisticsViewModel model) return;
        panel.Children.Clear();
        foreach (var option in StatisticsViewModel.CategoryList)
        {
            var button = new Button { Content = option.Label, Margin = new Avalonia.Thickness(2), Padding = new Avalonia.Thickness(12, 7), Tag = option.Key };
            button.Click += (_, _) => model.Category = option.Key;
            panel.Children.Add(button);
        }
    }
}
