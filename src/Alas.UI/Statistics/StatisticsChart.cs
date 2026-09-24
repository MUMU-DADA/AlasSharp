using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Input;
using Avalonia.Media;

namespace Alas.UI.Statistics;

public sealed record StatisticsChartSeries(StatisticsSeries Series, IReadOnlyList<StatisticsPoint> Points, IReadOnlyList<StatisticsBucket> Buckets)
{
    public string Summary => Points.Count == 0 ? "暂无记录" : $"最新 {Points[^1].Value:N2}　变化 {Points[^1].Value - Points[0].Value:+0.##;-0.##;0}　最高 {Points.Max(p => p.Value):N2}　最低 {Points.Min(p => p.Value):N2}　原始 {Points.Count} 条";
}

public sealed class StatisticsChartViewModel : StatisticsObservable
{
    private readonly List<string> _selected = [];
    private string _mode = "line", _axis = "separate", _from = "", _to = "";
    private int _bucket;
    private bool _expanded;
    private readonly Func<StatisticsExport, Task>? _export;
    public StatisticsChartViewModel(IReadOnlyList<StatisticsSeries> series, Func<StatisticsExport, Task>? export = null)
    {
        Series = series; _export = export;
        var first = series.FirstOrDefault(s => s.Points.Count > 0) ?? series.FirstOrDefault();
        if (first is not null) _selected.Add(first.Key);
    }
    public IReadOnlyList<StatisticsSeries> Series { get; }
    public IReadOnlyList<string> SelectedKeys => _selected;
    public string Mode { get => _mode; set { if (value is not ("line" or "candlestick")) throw new ArgumentOutOfRangeException(nameof(value)); if (Set(ref _mode, value)) { if (value == "candlestick" && _bucket == 0) _bucket = 60; Refresh(); } } }
    public string Axis { get => _axis; set { if (value is not ("separate" or "unified")) throw new ArgumentOutOfRangeException(nameof(value)); if (Set(ref _axis, value)) Refresh(); } }
    public int Bucket { get => _bucket; set { if (value is not (0 or 5 or 60 or 1440)) throw new ArgumentOutOfRangeException(nameof(value)); if (Set(ref _bucket, value)) Refresh(); } }
    public string From { get => _from; set { if (Set(ref _from, value ?? "")) Refresh(); } }
    public string To { get => _to; set { if (Set(ref _to, value ?? "")) Refresh(); } }
    public bool Expanded { get => _expanded; set => Set(ref _expanded, value); }
    public string RangeError
    {
        get
        {
            if (new[] { From, To }.Any(value => value.Length > 0 && !DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))) return "时间格式应为 YYYY-MM-DD HH:mm。";
            return From.Length > 0 && To.Length > 0 && DateTime.Parse(From, CultureInfo.InvariantCulture) > DateTime.Parse(To, CultureInfo.InvariantCulture) ? "开始时间不能晚于结束时间。" : "";
        }
    }
    public IReadOnlyList<StatisticsSeries> SelectedSeries => _selected.Select(key => Series.First(s => s.Key == key)).ToArray();
    public IReadOnlyList<StatisticsChartSeries> Data => RangeError.Length > 0 ? [] : SelectedSeries.Select(series =>
    {
        var points = StatisticsData.Filter(series, From, To);
        return new StatisticsChartSeries(series, points, StatisticsData.Aggregate(points, Mode == "candlestick" && Bucket == 0 ? 60 : Bucket));
    }).ToArray();
    public StatisticsTable RawTable => StatisticsData.RawTable(SelectedSeries, From, To);
    public bool CanExportPng => _export is not null;
    public Task ExportPngAsync(byte[] png)
    {
        if (_export is null) return Task.CompletedTask;
        return _export(new StatisticsExport(StatisticsData.SafeFileName("statistics-trend") + ".png", "image/png", png));
    }
    public Task ExportDataAsync(StatisticsExport export) => _export is null ? Task.CompletedTask : _export(export);
    public void Toggle(string key)
    {
        if (!Series.Any(s => s.Key == key && s.Points.Count > 0)) return;
        if (_selected.Contains(key)) { if (_selected.Count == 1) return; _selected.Remove(key); }
        else _selected.Add(key);
        Refresh();
    }
    public void SelectOnly(string key)
    {
        if (!Series.Any(s => s.Key == key && s.Points.Count > 0)) return;
        _selected.Clear(); _selected.Add(key); Refresh();
    }
    public void Primary(string key)
    {
        if (!_selected.Remove(key)) return;
        _selected.Insert(0, key); Refresh();
    }
    public void ResetRange() { _from = _to = ""; Changed(nameof(From)); Changed(nameof(To)); Refresh(); }
    private void Refresh() => Changed(nameof(Data));
}

/// <summary>原生离屏可绘制的时间序列控件；色板、OHLC 和多指标顺序沿上游 StatisticsChart。</summary>
public sealed class StatisticsChart : Control
{
    public static readonly StyledProperty<StatisticsChartViewModel?> ModelProperty = AvaloniaProperty.Register<StatisticsChart, StatisticsChartViewModel?>(nameof(Model));
    public static readonly StyledProperty<IBrush?> TextBrushProperty = AvaloniaProperty.Register<StatisticsChart, IBrush?>(nameof(TextBrush), Brushes.Gray);
    public static readonly StyledProperty<IBrush?> GridBrushProperty = AvaloniaProperty.Register<StatisticsChart, IBrush?>(nameof(GridBrush), Brushes.LightGray);
    public static readonly StyledProperty<IBrush?> AccentBrushProperty = AvaloniaProperty.Register<StatisticsChart, IBrush?>(nameof(AccentBrush), Brushes.Teal);
    public static readonly StyledProperty<IBrush?> NegativeBrushProperty = AvaloniaProperty.Register<StatisticsChart, IBrush?>(nameof(NegativeBrush), Brushes.Coral);
    private static readonly string[] Palette = ["#159b88", "#f59e0b", "#0ea5e9", "#ec4899", "#8b5cf6", "#10b981", "#f97316", "#6366f1", "#14b8a6"];
    private static readonly IReadOnlyDictionary<string, string> ResourcePalette = new Dictionary<string, string>
    { ["oil"]="#10b981",["coin"]="#f59e0b",["cube"]="#0ea5e9",["gem"]="#f43f5e",["pt"]="#8b5cf6",["core"]="#06b6d4",["medal"]="#e11d48",["merit"]="#d97706",["guild_coin"]="#64748b",["ap"]="#3b82f6",["asset"]="#6366f1",["distance"]="#14b8a6",["yellow_coins"]="#eab308",["purple_coins"]="#a855f7" };
    private StatisticsChartViewModel? _model;
    private double _zoomStart, _zoomEnd = 1;
    private Point? _hover;
    public IBrush? TextBrush { get => GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public IBrush? GridBrush { get => GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public IBrush? NegativeBrush { get => GetValue(NegativeBrushProperty); set => SetValue(NegativeBrushProperty, value); }
    public StatisticsChartViewModel? Model
    {
        get => GetValue(ModelProperty);
        set
        {
            if (_model is not null) _model.PropertyChanged -= Changed;
            _model = value;
            SetValue(ModelProperty, value);
            if (value is not null) value.PropertyChanged += Changed;
            ResetZoom();
        }
    }
    public double ZoomStart => _zoomStart;
    public double ZoomEnd => _zoomEnd;
    public event EventHandler? ZoomChanged;
    public StatisticsChart()
    {
        MinHeight = 260; Height = 360; Focusable = true; ClipToBounds = true;
        AutomationProperties.SetName(this, "统计趋势图，Ctrl 加滚轮缩放，Esc 恢复范围");
    }
    static StatisticsChart() => AffectsRender<StatisticsChart>(TextBrushProperty, GridBrushProperty, AccentBrushProperty, NegativeBrushProperty);
    private void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs args) => InvalidateVisual();
    public static IBrush SeriesBrush(string key, int index) => Brush.Parse(ResourcePalette.TryGetValue(key, out var color) ? color : Palette[index % Palette.Length]);
    public void ResetZoom() => SetZoom(0, 1);
    public void SetZoom(double start, double end)
    {
        _zoomStart = Math.Clamp(start, 0, .99); _zoomEnd = Math.Clamp(end, _zoomStart + .01, 1);
        InvalidateVisual(); ZoomChanged?.Invoke(this, EventArgs.Empty);
    }
    private Rect Plot => new(58, 24, Math.Max(1, Bounds.Width - (Model?.SelectedKeys.Count > 1 && Model.Axis == "separate" ? 116 : 80)), Math.Max(1, Bounds.Height - 70));
    private static double Time(string value) => DateTime.TryParse(value.Replace('T', ' '), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed.Ticks / (double)TimeSpan.TicksPerSecond : double.NaN;
    private static (double Min, double Max) Range(IEnumerable<double> values)
    {
        var valid = values.Where(double.IsFinite).ToArray();
        if (valid.Length == 0) return (0, 1);
        double min = valid.Min(), max = valid.Max();
        if (min == max) { min -= Math.Max(1, Math.Abs(min) * .02); max += Math.Max(1, Math.Abs(max) * .02); }
        return (min, max);
    }
    private void Label(DrawingContext context, string text, Point location, IBrush? brush = null)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("avares://Alas.UI/Assets/Fonts/NotoSansCJKsc-Regular.otf#Noto Sans CJK SC"), 11, brush ?? TextBrush ?? Brushes.Gray);
        context.DrawText(formatted, location);
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var model = Model;
        var data = model?.Data ?? [];
        if (!data.Any(s => s.Buckets.Count > 0)) { Label(context, model?.RangeError is { Length: > 0 } error ? error : "所选时间范围暂无记录", new(18, 80)); return; }
        var plot = Plot;
        var times = Range(data.SelectMany(s => s.Buckets.Select(b => Time(b.Time))));
        var totalSpan = times.Max - times.Min;
        var leftTime = times.Min + totalSpan * _zoomStart;
        var rightTime = times.Min + totalSpan * _zoomEnd;
        var unified = Range(data.SelectMany(s => s.Buckets.SelectMany(b => new[] { b.Low, b.High })));
        var first = Range(data[0].Buckets.SelectMany(b => new[] { b.Low, b.High }));
        var leftRange = model!.Axis == "unified" ? unified : first;
        for (var tick = 0; tick <= 4; tick++)
        {
            var y = plot.Bottom - plot.Height * tick / 4;
            context.DrawLine(new Pen(GridBrush, 1), new(plot.Left, y), new(plot.Right, y));
            Label(context, (leftRange.Min + (leftRange.Max - leftRange.Min) * tick / 4).ToString("0.##", CultureInfo.CurrentCulture), new(2, y - 9));
            if (model.Axis == "separate" && data.Count > 1)
            {
                var second = Range(data[1].Buckets.SelectMany(b => new[] { b.Low, b.High }));
                Label(context, (second.Min + (second.Max - second.Min) * tick / 4).ToString("0.##", CultureInfo.CurrentCulture), new(plot.Right + 5, y - 9), SeriesBrush(data[1].Series.Key, 1));
            }
        }
        for (var tick = 0; tick <= 2; tick++)
        {
            var time = leftTime + (rightTime - leftTime) * tick / 2;
            Label(context, new DateTime((long)(time * TimeSpan.TicksPerSecond)).ToString("MM-dd HH:mm", CultureInfo.InvariantCulture), new(plot.Left + plot.Width * tick / 2 - (tick == 2 ? 72 : 0), plot.Bottom + 10));
        }
        using (context.PushClip(plot))
        {
            for (var i = 0; i < data.Count; i++)
            {
                var item = data[i];
                var range = model.Axis == "unified" ? unified : Range(item.Buckets.SelectMany(b => new[] { b.Low, b.High }));
                var brush = data.Count == 1 ? AccentBrush : SeriesBrush(item.Series.Key, i);
                double X(StatisticsBucket b) => plot.Left + (Time(b.Time) - leftTime) / (rightTime - leftTime) * plot.Width;
                double Y(double value) => plot.Bottom - (value - range.Min) / (range.Max - range.Min) * plot.Height;
                if (model.Mode == "candlestick" && i == 0)
                {
                    var width = Math.Clamp(plot.Width / Math.Max(1, item.Buckets.Count) / (_zoomEnd - _zoomStart) * .55, 2, 18);
                    foreach (var bucket in item.Buckets)
                    {
                        var x = X(bucket);
                        var color = bucket.Close >= bucket.Open ? AccentBrush : NegativeBrush;
                        context.DrawLine(new Pen(color, 1), new(x, Y(bucket.Low)), new(x, Y(bucket.High)));
                        context.DrawRectangle(color, null, new Rect(x - width / 2, Math.Min(Y(bucket.Open), Y(bucket.Close)), width, Math.Max(1, Math.Abs(Y(bucket.Open) - Y(bucket.Close)))));
                    }
                }
                else
                {
                    Point? previous = null;
                    foreach (var bucket in item.Buckets)
                    {
                        var point = new Point(X(bucket), Y(bucket.Close));
                        if (previous is { } p) context.DrawLine(new Pen(brush, 2), p, point);
                        if (item.Buckets.Count < 80) context.DrawEllipse(brush, null, point, 2.5, 2.5);
                        previous = point;
                    }
                }
            }
            if (_hover is { } hover && plot.Contains(hover)) context.DrawLine(new Pen(TextBrush, 1, dashStyle: DashStyle.Dash), new(hover.X, plot.Top), new(hover.X, plot.Bottom));
        }
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e); _hover = e.GetPosition(this);
        var data = Model?.Data ?? [];
        if (Plot.Contains(_hover.Value) && data.Any(s => s.Buckets.Count > 0))
        {
            var times = Range(data.SelectMany(s => s.Buckets.Select(b => Time(b.Time))));
            var target = times.Min + (times.Max - times.Min) * (_zoomStart + (_zoomEnd - _zoomStart) * (_hover.Value.X - Plot.Left) / Plot.Width);
            var lines = data.Where(s => s.Buckets.Count > 0).Select(s =>
            {
                var bucket = s.Buckets.MinBy(b => Math.Abs(Time(b.Time) - target))!;
                return Model!.Mode == "candlestick" ? $"{s.Series.Label} {bucket.Time}\n开 {bucket.Open:N2} 收 {bucket.Close:N2} 低 {bucket.Low:N2} 高 {bucket.High:N2}" : $"{s.Series.Label} {bucket.Time}: {bucket.Close:N2}";
            });
            ToolTip.SetTip(this, string.Join('\n', lines));
        }
        InvalidateVisual();
    }
    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); _hover = null; InvalidateVisual(); }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        var scale = e.Delta.Y > 0 ? .8 : 1.25;
        var center = (_zoomStart + _zoomEnd) / 2;
        var span = Math.Clamp((_zoomEnd - _zoomStart) * scale, .01, 1);
        SetZoom(Math.Clamp(center - span / 2, 0, 1 - span), Math.Clamp(center + span / 2, span, 1)); e.Handled = true;
    }
    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.Key == Key.Escape) { ResetZoom(); e.Handled = true; } }
}

