using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace Alas.UI.Statistics;

public sealed class StatisticsTableView : UserControl
{
    private readonly StackPanel _rows = new() { Spacing = 0 }, _headers = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _title = new() { FontSize = 17, FontWeight = Avalonia.Media.FontWeight.SemiBold, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBlock _note = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap }, _count = new() { VerticalAlignment = VerticalAlignment.Center }, _page = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _search = new() { Name = "TableSearch", PlaceholderText = "搜索此表", MinWidth = 100 };
    private readonly Button _export = new() { Name = "TableExport", Content = "导出明细" }, _previous = new() { Name = "TablePrevious", Content = "上一页" }, _next = new() { Name = "TableNext", Content = "下一页" };
    private StatisticsTableViewModel? _model; private bool _updating;
    public StatisticsTableView()
    {
        var root = new StackPanel { Spacing = 10 }; var heading = new WrapPanel(); heading.Children.Add(_title); heading.Children.Add(_export); root.Children.Add(heading); root.Children.Add(_note);
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 }; Grid.SetColumn(_count, 1); toolbar.Children.Add(_search); toolbar.Children.Add(_count); root.Children.Add(toolbar);
        var table = new StackPanel(); table.Children.Add(_headers); table.Children.Add(_rows); root.Children.Add(new ScrollViewer { Name = "TableScroll", HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Content = table });
        var pager = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; pager.Children.Add(_previous); pager.Children.Add(_page); pager.Children.Add(_next); root.Children.Add(pager); Content = root;
        _note.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AlasMutedBrush")); _count.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AlasMutedBrush"));
        _search.TextChanged += (_, _) => { if (!_updating && _model is not null) _model.Search = _search.Text ?? ""; }; _previous.Click += (_, _) => _model?.MovePage(-1); _next.Click += (_, _) => _model?.MovePage(1); _export.Click += (_, _) => _model?.ExportCommand.Execute(null);
        DataContextChanged += (_, _) => SetModel(DataContext as StatisticsTableViewModel); AttachedToVisualTree += (_, _) => SetModel(DataContext as StatisticsTableViewModel); DetachedFromVisualTree += (_, _) => SetModel(null);
    }
    private void SetModel(StatisticsTableViewModel? model)
    {
        if (_model is not null) _model.PropertyChanged -= Changed; _model = model; if (model is not null) model.PropertyChanged += Changed; _headers.Children.Clear();
        if (model is not null) for (var i = 0; i < model.Table.Columns.Count; i++) { var index = i; var button = new Button { Name = $"TableSort{index}", Width = 160, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8), FontWeight = Avalonia.Media.FontWeight.SemiBold }; button.Click += (_, _) => _model?.Sort(index); _headers.Children.Add(button); }
        Update();
    }
    private void Changed(object? sender, PropertyChangedEventArgs args) { if (args.PropertyName is nameof(StatisticsTableViewModel.PageRows) or nameof(StatisticsTableViewModel.SortIndex) or nameof(StatisticsTableViewModel.Descending)) Update(); }
    private void Update()
    {
        _rows.Children.Clear(); if (_model is not { } model) return; _updating = true;
        try
        {
            _title.Text = model.Table.Title; _note.Text = model.Table.Note; _note.IsVisible = model.HasNote; _search.Text = model.Search; AutomationProperties.SetName(_search, $"搜索{model.Table.Title}"); _count.Text = model.RecordSummary; _page.Text = model.PageSummary; _previous.IsEnabled = model.CanPrevious; _next.IsEnabled = model.CanNext; _export.IsEnabled = model.CanExport;
            for (var i = 0; i < _headers.Children.Count; i++) { var text = model.Table.Columns[i] + (model.SortIndex == i ? model.Descending ? " ↓" : " ↑" : ""); ((Button)_headers.Children[i]).Content = text; AutomationProperties.SetName(_headers.Children[i], text); }
            foreach (var row in model.PageRows) { var line = new StackPanel { Orientation = Orientation.Horizontal }; foreach (var value in row) { var text = value switch { null => "—", double number => number.ToString("N4", CultureInfo.CurrentCulture).TrimEnd('0').TrimEnd(CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator.ToCharArray()), bool flag => flag ? "true" : "false", _ => value.ToString() ?? "" }; line.Children.Add(new TextBlock { Text = text, Width = 160, Padding = new Thickness(8) }); } _rows.Children.Add(line); }
            if (model.PageRows.Count == 0) _rows.Children.Add(new TextBlock { Text = "没有匹配的记录", Margin = new Thickness(8) });
        }
        finally { _updating = false; }
    }
}

