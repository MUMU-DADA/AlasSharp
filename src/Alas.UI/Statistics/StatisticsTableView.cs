using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Alas.UI.Statistics;

public sealed class StatisticsTableView : UserControl
{
    private readonly StackPanel _rows = new() { Spacing = 3 };
    public StatisticsTableView()
    {
        var root = new StackPanel { Spacing = 8 };
        var title = new TextBlock { FontSize = 17, FontWeight = FontWeight.SemiBold };
        title.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding("Table.Title"));
        root.Children.Add(title);
        var note = new TextBlock { Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap };
        note.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding("Table.Note")); root.Children.Add(note);
        var search = new TextBox { PlaceholderText = "搜索此表" }; search.Bind(TextBox.TextProperty, new Avalonia.Data.Binding("Search") { Mode = Avalonia.Data.BindingMode.TwoWay }); root.Children.Add(search);
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        DataContextChanged += (_, _) =>
        {
            header.Children.Clear();
            if (DataContext is StatisticsTableViewModel model) foreach (var column in model.Table.Columns) header.Children.Add(new TextBlock { Text = column, Width = 140, FontWeight = FontWeight.SemiBold });
        };
        root.Children.Add(header);
        var scroll = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Content = _rows, MaxHeight = 330 }; root.Children.Add(scroll);
        var pager = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10 };
        var previous = new Button { Content = "上一页" }; previous.Bind(Button.CommandProperty, new Avalonia.Data.Binding("PreviousCommand"));
        var page = new TextBlock { VerticalAlignment = VerticalAlignment.Center }; page.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding("PageSummary"));
        var next = new Button { Content = "下一页" }; next.Bind(Button.CommandProperty, new Avalonia.Data.Binding("NextCommand")); pager.Children.Add(previous); pager.Children.Add(page); pager.Children.Add(next); root.Children.Add(pager);
        Content = root;
        DataContextChanged += (_, _) => Update();
    }
    private void Update()
    {
        _rows.Children.Clear();
        if (DataContext is not StatisticsTableViewModel model) return;
        foreach (var row in model.PageRows)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var value in row) line.Children.Add(new TextBlock { Text = value?.ToString() ?? "—", Width = 140, Margin = new Avalonia.Thickness(0, 3) });
            _rows.Children.Add(line);
        }
    }
}
