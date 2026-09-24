using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Alas.UI.Statistics;

public sealed class StatisticsChartPanel : UserControl
{
    private readonly StatisticsChart _chart = new() { Name = "TrendChart", Height = 360 };
    private readonly WrapPanel _series = new(), _summaries = new();
    private readonly ComboBox _mode = new() { Name = "ChartMode", Width = 122, ItemsSource = new[] { new StatisticsOption("line", "折线图"), new StatisticsOption("candlestick", "K线图") } };
    private readonly ComboBox _axis = new() { Name = "ChartAxis", Width = 122, ItemsSource = new[] { new StatisticsOption("separate", "独立坐标轴"), new StatisticsOption("unified", "统一坐标轴") } };
    private readonly ComboBox _bucket = new() { Name = "ChartBucket", Width = 122, ItemsSource = new[] { new StatisticsOption("0", "每条记录"), new StatisticsOption("5", "每5分钟"), new StatisticsOption("60", "每小时"), new StatisticsOption("1440", "每天") } };
    private readonly TextBox _from = new() { Name = "ChartFrom", Width = 172, PlaceholderText = "YYYY-MM-DD HH:mm" }, _to = new() { Name = "ChartTo", Width = 172, PlaceholderText = "YYYY-MM-DD HH:mm" };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap }, _exportError = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _png = new() { Name = "ChartPng", Content = "保存图像" }, _expand = new() { Name = "ChartExpand", Content = "展开图表" };
    private readonly Slider _start = new() { Name = "ChartZoomStart", Minimum = 0, Maximum = 99, Width = 150 }, _end = new() { Name = "ChartZoomEnd", Minimum = 1, Maximum = 100, Value = 100, Width = 150 };
    private readonly StatisticsTableView _raw = new() { Name = "ChartRawTable" };
    private StatisticsChartViewModel? _model; private bool _updating;
    public StatisticsChartPanel()
    {
        var root = new StackPanel { Spacing = 10 }; var heading = new WrapPanel(); heading.Children.Add(new TextBlock { Text = "趋势与明细", FontSize = 17, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 18, 6) }); heading.Children.Add(_expand); heading.Children.Add(_png); root.Children.Add(heading); root.Children.Add(_series);
        var controls = new WrapPanel(); controls.Children.Add(Labeled("图表", _mode)); controls.Children.Add(Labeled("坐标轴", _axis)); controls.Children.Add(Labeled("聚合", _bucket)); controls.Children.Add(Labeled("开始时间", _from)); controls.Children.Add(Labeled("结束时间", _to)); var all = new Button { Name = "ChartAllTime", Content = "全部时间", Margin = new Thickness(0, 22, 0, 5) }; all.Click += (_, _) => _model?.ResetRange(); controls.Children.Add(all); root.Children.Add(controls); root.Children.Add(_error); root.Children.Add(_summaries); root.Children.Add(_chart);
        var zoom = new WrapPanel(); zoom.Children.Add(Labeled("缩放开始", _start)); zoom.Children.Add(Labeled("缩放结束", _end)); var reset = new Button { Name = "ChartRestore", Content = "恢复范围", Margin = new Thickness(0, 22, 0, 5) }; reset.Click += (_, _) => _chart.ResetZoom(); zoom.Children.Add(reset); root.Children.Add(zoom);
        root.Children.Add(new TextBlock { Text = "选择多个指标进行对照；双击指标仅看该项。Ctrl + 滚轮缩放，Esc 恢复范围。", TextWrapping = TextWrapping.Wrap }); root.Children.Add(_exportError); root.Children.Add(_raw); Content = root;
        _chart.Bind(StatisticsChart.TextBrushProperty, new DynamicResourceExtension("AlasMutedBrush")); _chart.Bind(StatisticsChart.GridBrushProperty, new DynamicResourceExtension("AlasBorderBrush")); _chart.Bind(StatisticsChart.AccentBrushProperty, new DynamicResourceExtension("AlasAccentBrush")); _chart.Bind(StatisticsChart.NegativeBrushProperty, new DynamicResourceExtension("AlasDangerBrush")); _error.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AlasDangerBrush"));
        _mode.SelectionChanged += (_, _) => { if (!_updating && _model is not null && _mode.SelectedItem is StatisticsOption o) _model.Mode = o.Key; };
        _axis.SelectionChanged += (_, _) => { if (!_updating && _model is not null && _axis.SelectedItem is StatisticsOption o) _model.Axis = o.Key; };
        _bucket.SelectionChanged += (_, _) => { if (!_updating && _model is not null && _bucket.SelectedItem is StatisticsOption o) _model.Bucket = int.Parse(o.Key); };
        _from.TextChanged += (_, _) => { if (!_updating && _model is not null) _model.From = _from.Text ?? ""; }; _to.TextChanged += (_, _) => { if (!_updating && _model is not null) _model.To = _to.Text ?? ""; };
        _expand.Click += (_, _) => { if (_model is not null) _model.Expanded = !_model.Expanded; };
        _png.Click += async (_, _) => { if (_model is null) return; try { using var bitmap = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)_chart.Bounds.Width * 2), Math.Max(1, (int)_chart.Bounds.Height * 2)), new Vector(192, 192)); bitmap.Render(_chart); using var stream = new MemoryStream(); bitmap.Save(stream, PngBitmapEncoderOptions.Default); await _model.ExportPngAsync(stream.ToArray()); _exportError.Text = ""; } catch (Exception error) { _exportError.Text = $"图像导出失败：{error.Message}"; } };
        _start.ValueChanged += (_, _) => { if (!_updating) _chart.SetZoom(_start.Value / 100, _end.Value / 100); }; _end.ValueChanged += (_, _) => { if (!_updating) _chart.SetZoom(_start.Value / 100, _end.Value / 100); }; _chart.ZoomChanged += (_, _) => { _updating = true; _start.Value = _chart.ZoomStart * 100; _end.Value = _chart.ZoomEnd * 100; _updating = false; };
        DataContextChanged += (_, _) => SetModel(DataContext as StatisticsChartViewModel); AttachedToVisualTree += (_, _) => SetModel(DataContext as StatisticsChartViewModel); DetachedFromVisualTree += (_, _) => SetModel(null);
    }
    private static StackPanel Labeled(string title, Control input) { var stack = new StackPanel { Margin = new Thickness(0, 0, 12, 6), Spacing = 4 }; stack.Children.Add(new TextBlock { Text = title }); stack.Children.Add(input); return stack; }
    private void SetModel(StatisticsChartViewModel? model) { if (_model is not null) _model.PropertyChanged -= Changed; _model = model; if (model is not null) model.PropertyChanged += Changed; _chart.Model = model; Update(); }
    private void Changed(object? sender, PropertyChangedEventArgs args) { if (args.PropertyName is nameof(StatisticsChartViewModel.Data) or nameof(StatisticsChartViewModel.Expanded)) Update(); }
    private void Update()
    {
        if (_model is not { } model) return; _updating = true;
        try
        {
            _mode.SelectedItem = _mode.ItemsSource!.Cast<StatisticsOption>().First(o => o.Key == model.Mode); _axis.SelectedItem = _axis.ItemsSource!.Cast<StatisticsOption>().First(o => o.Key == model.Axis); _bucket.SelectedItem = _bucket.ItemsSource!.Cast<StatisticsOption>().First(o => o.Key == model.Bucket.ToString()); _from.Text = model.From; _to.Text = model.To; _error.Text = model.RangeError; _error.IsVisible = model.RangeError.Length > 0; _png.IsEnabled = model.CanExportPng && model.Data.Any(s => s.Points.Count > 0); _chart.Height = model.Expanded ? 620 : 360; _expand.Content = model.Expanded ? "收起图表" : "展开图表";
            _series.Children.Clear(); foreach (var item in model.Series) { var selected = model.SelectedKeys.Contains(item.Key); var button = new Button { Name = "Series_" + item.Key, Content = (selected ? "● " : "○ ") + item.Label, IsEnabled = item.Points.Count > 0, Margin = new Thickness(0, 0, 5, 5) }; button.Click += (_, _) => model.Toggle(item.Key); button.DoubleTapped += (_, _) => model.SelectOnly(item.Key); _series.Children.Add(button); if (selected && model.Mode == "candlestick" && model.SelectedKeys.Count > 1) { var primary = new Button { Name = "Primary_" + item.Key, Content = model.SelectedKeys[0] == item.Key ? "主K线" : "设为主K线", Margin = new Thickness(0, 0, 8, 5), IsEnabled = model.SelectedKeys[0] != item.Key }; primary.Click += (_, _) => model.Primary(item.Key); _series.Children.Add(primary); } }
            _summaries.Children.Clear(); foreach (var item in model.Data) _summaries.Children.Add(new TextBlock { Text = item.Series.Label + "　" + item.Summary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 10, 6), MaxWidth = 500 });
            _raw.DataContext = new StatisticsTableViewModel(model.RawTable, model.CanExportPng ? export => _ = model.ExportDataAsync(export) : null);
        }
        finally { _updating = false; }
    }
}
