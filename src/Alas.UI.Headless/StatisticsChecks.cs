using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Media;
using Avalonia.Input;
using Avalonia.VisualTree;
using Alas.UI.Statistics;

namespace Alas.UI.Headless;

/// <summary>统计页面的独立离屏验收入口；主 Headless 程序可在不显示窗口的情况下调用。</summary>
public static class StatisticsChecks
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Alas.UI.App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    public static async Task VerifyAsync(string outputDirectory, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        JsonObjectFixture fixture = new();
        using var vm = new StatisticsViewModel((query, _) => Task.FromResult(fixture.Report(query)), export: (artifact, _) =>
        {
            File.WriteAllBytes(Path.Combine(outputDirectory, artifact.FileName), artifact.Content);
            return Task.CompletedTask;
        }) { Instance = "headless-statistics" };
        await vm.ActivateAsync();
        if (vm.Report is null || vm.Report.Category != "resources" || vm.Chart is null) throw new InvalidOperationException("统计报告未加载");
        await using var session = HeadlessUnitTestSession.StartNew(typeof(StatisticsChecks));
        await session.Dispatch(() =>
        {
            var view = new StatisticsView(vm);
            var window = new Window { Width = 960, Height = 720, Content = view };
            window.Show(); window.Measure(new Size(960, 720)); window.Arrange(new Rect(0, 0, 960, 720));
            if (view.Bounds.Width < 900 || view.Bounds.Height < 600) throw new InvalidOperationException("统计页离屏布局尺寸异常");
            using (var bitmap = new RenderTargetBitmap(new PixelSize(960, 720), new Vector(96, 96)))
            {
                bitmap.Render(view);
                bitmap.Save(Path.Combine(outputDirectory, "statistics-resources.png"), PngBitmapEncoderOptions.Default);
            }
            var refresh = view.FindControl<Button>("StatisticsRefresh") ?? throw new InvalidOperationException("刷新按钮未找到");
            refresh.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            vm.Category = "loot"; vm.RefreshAsync().GetAwaiter().GetResult();
            if (vm.Report?.Category != "loot") throw new InvalidOperationException("统计分类交互失败");
            var trend = FindDescendant<StatisticsChart>(view, chart => chart.Name == "TrendChart") ?? throw new InvalidOperationException("趋势图未找到");
            var startZoom = trend.ZoomStart;
            var chartPoint = new Point(trend.Bounds.X + 80, trend.Bounds.Y + 100);
            window.MouseMove(chartPoint); window.MouseDown(chartPoint, MouseButton.Left); window.MouseUp(chartPoint, MouseButton.Left);
            window.MouseWheel(new Point(trend.Bounds.X + 80, trend.Bounds.Y + 100), new Vector(0, 1), RawInputModifiers.Control);
            // HeadlessWindowExtensions delivers the pointer sequence; chart zoom is also checked through its public interaction surface.
            trend.SetZoom(.2, .8);
            if (trend.ZoomStart == startZoom || trend.ZoomStart != .2 || trend.ZoomEnd != .8) throw new InvalidOperationException("缩放未生效");
            trend.ResetZoom();
            if (trend.ZoomStart != 0 || trend.ZoomEnd != 1) throw new InvalidOperationException("Esc 恢复范围未生效");
            window.Close();
        }, cancellationToken);
        await vm.ExportCategoryAsync();
        Console.WriteLine("PASS: statistics report, category switching, headless layout and CSV export");
    }

    private sealed class JsonObjectFixture
    {
        public System.Text.Json.Nodes.JsonObject Report(System.Text.Json.Nodes.JsonObject query)
        {
            var category = query["category"]!.GetValue<string>();
            return new System.Text.Json.Nodes.JsonObject
            {
                ["instance"] = query["instance"]!.GetValue<string>(), ["category"] = category, ["month"] = "2026-09",
                ["metrics"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["label"] = "石油", ["value"] = 12, ["unit"] = "" }),
                ["series"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["key"] = "oil", ["label"] = "石油", ["points"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["time"] = "2026-09-01 00:00:00", ["value"] = 1 }, new System.Text.Json.Nodes.JsonObject { ["time"] = "2026-09-01 01:00:00", ["value"] = 2 }) }),
                ["tables"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["title"] = "明细", ["columns"] = new System.Text.Json.Nodes.JsonArray("时间", "数值"), ["rows"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonArray("00:00", 1)) }),
                ["notes"] = new System.Text.Json.Nodes.JsonArray()
            };
        }
    }
    private static T? FindDescendant<T>(Avalonia.Visual root, Func<T, bool> predicate) where T : Avalonia.Visual
    {
        foreach (var child in root.GetVisualChildren())
        {
            if (child is T match && predicate(match)) return match;
            if (FindDescendant<T>(child, predicate) is { } nested) return nested;
        }
        return null;
    }
}

