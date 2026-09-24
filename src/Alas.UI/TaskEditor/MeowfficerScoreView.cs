using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;

namespace Alas.UI.TaskEditor;

/// <summary>Native rendering of the upstream Meowfficer score report. Results come from the host backend.</summary>
public sealed class MeowfficerScoreView : UserControl
{
    private IMeowfficerReportBackend? _backend;
    private string _instance = "";
    private readonly StackPanel _body = new() { Spacing = 12 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _refresh = ActionButton("刷新报告", "MeowRefresh");
    private readonly Button _clear = ActionButton("清空报告", "MeowClear");
    private readonly Button _open = ActionButton("打开报告", "MeowOpenReport");
    private bool _busy;
    private bool _confirmClear;

    public MeowfficerScoreView()
    {
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var title = Text("指挥喵评分", 18, FontWeight.SemiBold);
        heading.Children.Add(new StackPanel { Spacing = 3, Children = { title, _status } });
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Children = { _open, _clear, _refresh } };
        Grid.SetColumn(actions, 1); heading.Children.Add(actions);
        _refresh.Click += async (_, _) => await LoadAsync();
        _clear.Click += (_, _) => { _confirmClear = true; RenderConfirm(); };
        _open.Click += async (_, _) => { if (OpenReportAsync is not null) await OpenReportAsync(Instance); };
        var panel = Card(new StackPanel { Spacing = 14, Children = { heading, _body } });
        Content = panel;
        Loaded += async (_, _) => await LoadAsync();
        RenderEmpty("尚未读取评分报告");
    }

    public IMeowfficerReportBackend? Backend { get => _backend; set { _backend = value; _ = LoadAsync(); } }
    /// <summary>The host decides how to open its same report endpoint (browser, panel, or no-op).</summary>
    public Func<string, Task>? OpenReportAsync { get; set; }
    public string Instance { get => _instance; set { if (_instance == value) return; _instance = value ?? ""; _ = LoadAsync(); } }

    private async Task LoadAsync()
    {
        if (_busy || _backend is null || string.IsNullOrWhiteSpace(Instance)) return;
        _busy = true; _refresh.IsEnabled = _clear.IsEnabled = _open.IsEnabled = false; _status.Text = "正在读取报告…";
        try
        {
            var report = await _backend.LoadAsync(Instance, CancellationToken.None);
            if (report is null || report.Cats.Count == 0) RenderEmpty("尚未生成指挥喵评分报告");
            else RenderReport(report);
        }
        catch (Exception error) { RenderEmpty("读取评分报告失败：" + error.Message); }
        finally { _busy = false; _refresh.IsEnabled = _clear.IsEnabled = _backend is not null; }
    }

    private async Task ClearAsync()
    {
        if (_busy || !_confirmClear || _backend is null) return;
        _busy = true; _confirmClear = false; _refresh.IsEnabled = _clear.IsEnabled = _open.IsEnabled = false; _status.Text = "正在清空报告…";
        try { await _backend.ClearAsync(Instance, CancellationToken.None); RenderEmpty("评分报告已清空"); }
        catch (Exception error) { RenderEmpty("清空评分报告失败：" + error.Message); }
        finally { _busy = false; _refresh.IsEnabled = _clear.IsEnabled = _backend is not null; }
    }

    private void RenderReport(MeowfficerScoreReport report)
    {
        _confirmClear = false; _status.Text = $"{report.Cats.Count} 只 · {report.GeneratedAt}";
        _open.IsEnabled = report.Cats.Count > 0 && OpenReportAsync is not null;
        _clear.IsEnabled = report.Cats.Count > 0 && _backend is not null;
        _body.Children.Clear();
        foreach (var cat in report.Cats) _body.Children.Add(CatCard(cat));
    }
    private void RenderEmpty(string message)
    {
        _body.Children.Clear();
        _status.Text = message;
        _body.Children.Add(Text(message, 14));
        _clear.IsEnabled = _open.IsEnabled = false;
    }
    private void RenderConfirm()
    {
        _body.Children.Clear();
        _body.Children.Add(Text("将由服务端删除该实例的评分产物，是否继续？", 14));
        var yes = ActionButton("确认清空", "MeowClearConfirm", true);
        var no = ActionButton("取消", "MeowClearCancel");
        yes.Click += async (_, _) => await ClearAsync();
        no.Click += (_, _) => { _confirmClear = false; _ = LoadAsync(); };
        _body.Children.Add(new WrapPanel { Children = { yes, no } });
    }
    private static Control CatCard(MeowfficerCat cat)
    {
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var tags = string.Join(" · ", cat.Tags ?? []);
        var name = Text(cat.Cat + (string.IsNullOrWhiteSpace(tags) ? "" : "  " + tags), 16, FontWeight.SemiBold);
        head.Children.Add(new StackPanel { Spacing = 3, Children = { name, Text((cat.Level is { } level ? $"Lv{level}" : "") + (cat.Maxed ? " · 已满级" : "") + (cat.Fixed ? " · 固定" : ""), 12) } });
        var primary = cat.Rubrics?.FirstOrDefault(r => r.Primary) ?? cat.Rubrics?.FirstOrDefault();
        var score = primary is null ? "" : (primary.Tier ?? "") + (primary.Score is { } value ? $"  {value:0.#}" : "");
        var scoreBlock = Text(score, 14, FontWeight.SemiBold); Grid.SetColumn(scoreBlock, 1); head.Children.Add(scoreBlock);
        var content = new StackPanel { Spacing = 8, Children = { head } };
        if (!string.IsNullOrWhiteSpace(cat.Note)) content.Children.Add(Text(cat.Note!, 12));
        if (cat.Talents is { Count: > 0 }) content.Children.Add(Text("天赋：" + string.Join("、", cat.Talents.Select(t => t.Name + (t.Level is { } level ? $" Lv{level}" : ""))), 12));
        if (primary is not null) content.Children.Add(Rubric(primary));
        if (cat.Advice is { } advice) content.Children.Add(Card(new StackPanel { Spacing = 4, Children = { Text(advice.Headline, 13, FontWeight.SemiBold), Text(advice.Reason, 12), Text(advice.CostText ?? "", 12) } }));
        if (cat.Rubrics is { Count: > 1 })
        {
            var others = new StackPanel { Spacing = 8 };
            foreach (var rubric in cat.Rubrics.Where(r => r != primary)) others.Children.Add(Rubric(rubric));
            content.Children.Add(new Expander { Header = $"其他评分口径（{cat.Rubrics.Count - 1}）", Content = others });
        }
        if (!string.IsNullOrWhiteSpace(cat.Source) || cat.PointsSpent is not null) content.Children.Add(Text((cat.Source is null ? "" : "来源：" + cat.Source) + (cat.PointsSpent is { } points ? $"  已投入 {points} 点" : ""), 11));
        return Card(content);
    }
    private static Control Rubric(MeowfficerRubric rubric)
    {
        var panel = new StackPanel { Spacing = 4, Children = { Text(rubric.Label + (rubric.X is { } x && rubric.Y is { } y ? $"  x + y = {x:0} + {y:0.#}" : ""), 13, FontWeight.SemiBold) } };
        if (rubric.XHits is { Count: > 0 }) panel.Children.Add(Text((rubric.XLabel ?? "X") + "：" + string.Join("、", rubric.XHits), 12));
        if (rubric.YHits is { Count: > 0 }) panel.Children.Add(Text((rubric.YLabel ?? "Y") + "：" + string.Join("、", rubric.YHits), 12));
        if (rubric.Notes is { Count: > 0 }) panel.Children.Add(Text(string.Join(Environment.NewLine, rubric.Notes), 12));
        if (!string.IsNullOrWhiteSpace(rubric.Source)) panel.Children.Add(Text("出处：" + rubric.Source, 11));
        return panel;
    }
    private static TextBlock Text(string text, double size, FontWeight? weight = null) => new() { Text = text, FontSize = size, FontWeight = weight ?? FontWeight.Normal, TextWrapping = TextWrapping.Wrap };
    private static Button ActionButton(string text, string name, bool primary = false) { var button = new Button { Content = text, Name = name, Padding = new Thickness(12, 7), MinHeight = 34 }; Resource(button, Button.ThemeProperty, primary ? "PrimaryButtonTheme" : "GhostButtonTheme"); AutomationProperties.SetName(button, text); return button; }
    private static Border Card(Control child) { var card = new Border { Child = child, Padding = new Thickness(16), BorderThickness = new Thickness(1) }; Resource(card, Border.BackgroundProperty, "AlasSurfaceBrush"); Resource(card, Border.BorderBrushProperty, "AlasBorderBrush"); Resource(card, Border.CornerRadiusProperty, "AlasPanelRadius"); return card; }
    private static void Resource(AvaloniaObject target, AvaloniaProperty property, string key) => target.Bind(property, new DynamicResourceExtension(key));
}


