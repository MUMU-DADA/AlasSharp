using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Media;
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
            vm.Category = "loot"; vm.RefreshAsync().GetAwaiter().GetResult();
            if (vm.Report?.Category != "loot") throw new InvalidOperationException("统计分类交互失败");
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
}
