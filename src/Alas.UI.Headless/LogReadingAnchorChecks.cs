using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Alas.UI.Controls;
using Alas.UI.ViewModels;
using Alas.UI.Views;

namespace Alas.UI.Headless;

/// <summary>读取实际行的视口坐标，验证追加日志后保留所阅内容；原生离屏，不接后端。</summary>
internal static class LogReadingAnchorChecks
{
    public static void Run(string output)
    {
        var reports = new List<Dictionary<string, object?>>();
        void Record(string name, Fixture fixture, (object Item, double Y, Control Control) anchor, bool reuse = true)
        {
            var after = fixture.Position(anchor.Item);
            var sameControl = fixture.Viewport.Children.Any(control => ReferenceEquals(control, anchor.Control)
                && ReferenceEquals(control.DataContext, anchor.Item));
            var intersects = fixture.Intersects(anchor.Item);
            var pass = after is { } y && Math.Abs(y - anchor.Y) <= 1 && intersects && (!reuse || sameControl);
            reports.Add(new() { ["scenario"] = name, ["before_y"] = anchor.Y, ["after_y"] = after,
                ["same_control"] = sameControl, ["intersects_viewport"] = intersects, ["pass"] = pass });
            Console.WriteLine($"Log reading anchor: {name}, row {anchor.Y:F2} → {after:F2}, visible={intersects}, reused={sameControl}, pass={pass}");
        }
        foreach (var descending in new[] { false, true })
        foreach (var mixed in new[] { false, true })
        foreach (var atCapacity in new[] { false, true })
        {
            var label = $"{(descending ? "descending" : "ascending")}-{(mixed ? "mixed" : "short")}-{(atCapacity ? "capped" : "growing")}";
            using var fixture = new Fixture(descending, atCapacity ? OverviewViewModel.LogCapacity : 300, mixed);
            fixture.Middle();
            var anchor = fixture.Capture();
            for (var i = 0; i < 7; i++) fixture.Model.AppendLog($"批量新增 {i}: " + new string('宽', mixed ? 160 : 8));
            Pump();
            Record(label + "-paused-batch", fixture, anchor);

            // 跟随开启但用户翻看历史，仍保留同一阅读行；显式恢复则回最新端。
            fixture.Model.IsFollowing = true;
            Pump();
            fixture.Middle();
            anchor = fixture.Capture();
            fixture.Model.AppendLog("用户翻看历史期间新增");
            Pump();
            Record(label + "-scrolled-away", fixture, anchor);

            fixture.Model.IsFollowing = false;
            fixture.Model.IsFollowing = true;
            Pump();
            for (var i = 0; i < 4; i++) fixture.Model.AppendLog($"跟随新增 {i}: " + new string('长', i % 2 == 0 ? 120 : 8));
            Pump();
            var newest = descending ? fixture.Model.VisibleLogs[0] : fixture.Model.VisibleLogs[^1];
            var newestVisible = fixture.Intersects(newest);
            reports.Add(new() { ["scenario"] = label + "-follow-newest", ["pass"] = newestVisible });
            Console.WriteLine($"Log reading anchor: {label}-follow-newest, pass={newestVisible}");

            fixture.Model.IsFollowing = false;
            fixture.Middle();
            anchor = fixture.Capture();
            fixture.Window.Content = null;
            Pump();
            for (var i = 0; i < 3; i++) fixture.Model.AppendLog($"离树新增 {i}");
            fixture.Window.Content = fixture.View;
            Pump();
            Record(label + "-reattach", fixture, anchor, reuse: false);

            fixture.Middle();
            // 首条部分可见行是锚点；宽度变化后只要求此行的位置，不要求下一条换行后仍原位。
            anchor = fixture.Capture(firstIntersecting: true);
            fixture.Window.Width = 640;
            Pump();
            Record(label + "-width-reflow", fixture, anchor);
        }
        foreach (var descending in new[] { false, true })
        {
            using var fixture = new Fixture(descending, 400, mixed: true);
            fixture.Scroll.ScrollToHome();
            Pump();
            var anchor = fixture.Capture(skip: descending ? 0 : 1);
            fixture.Model.AppendLog("暂停在顶部，淘汰最旧记录");
            Pump();
            // 正序在不可负滚动的顶部淘汰首行，原像素位置不可保持；必须正常显露第一条尚存记录。
            if (descending) Record("descending-paused-at-top", fixture, anchor);
            else
            {
                var pass = fixture.Intersects(fixture.Model.VisibleLogs[0]) && fixture.Scroll.Offset.Y == 0;
                reports.Add(new() { ["scenario"] = "ascending-evicted-at-top", ["pass"] = pass });
            }

            fixture.Middle();
            anchor = fixture.Capture(skip: 1);
            var first = fixture.Capture(firstIntersecting: true);
            fixture.Model.VisibleLogs.Remove((LogLineViewModel)first.Item);
            Pump();
            Record($"{descending}-removed-reading-row-keeps-next", fixture, anchor);

            fixture.Middle();
            anchor = fixture.Capture(firstIntersecting: true);
            // 已实现的屏外行变高会改变锚点上方的实测高度与其它行的估计值。
            var beforeAnchor = fixture.Viewport.Children.Where(control =>
                (control.TranslatePoint(default, fixture.Scroll)?.Y ?? 0) < anchor.Y).Last();
            beforeAnchor.MinHeight = beforeAnchor.Bounds.Height + 80;
            Pump();
            Record($"{descending}-measured-row-height-change", fixture, anchor);

            fixture.Middle();
            fixture.Model.AppendLog("排队后用户主动滚动");
            fixture.Scroll.ScrollToHome();
            Pump();
            var userTarget = fixture.Model.VisibleLogs[0];
            var userScrollPass = fixture.Intersects(userTarget) && fixture.Scroll.Offset.Y <= 1;
            reports.Add(new() { ["scenario"] = $"{descending}-user-scroll-cancels-pending-anchor", ["pass"] = userScrollPass });

            fixture.Middle();
            fixture.Model.AppendLog("排队后恢复跟随");
            fixture.Model.IsFollowing = true;
            Pump();
            var followTarget = descending ? fixture.Model.VisibleLogs[0] : fixture.Model.VisibleLogs[^1];
            reports.Add(new() { ["scenario"] = $"{descending}-resume-cancels-pending-anchor", ["pass"] = fixture.Intersects(followTarget) });

            fixture.Model.IsFollowing = false;
            fixture.Middle();
            fixture.Model.AppendLog("旧上下文待补偿");
            var replacement = new OverviewViewModel(previewData: true) { IsFollowing = false };
            replacement.ClearCommand.Execute(null);
            replacement.AppendLog("新上下文唯一日志");
            fixture.View.DataContext = replacement;
            Pump();
            reports.Add(new() { ["scenario"] = $"{descending}-context-switch-invalidates-anchor",
                ["pass"] = fixture.Intersects(replacement.VisibleLogs[0]) && fixture.Viewport.RealizedRowCount == 1 });
        }
        using (var fixture = new Fixture(descending: true, count: 200, mixed: false))
        {
            fixture.Model.VisibleLogs.Insert(0, new LogLineViewModel("2026-01-01", "00:00:00", "INFO", "[anchor]",
                string.Concat(Enumerable.Repeat("这一条日志超过整个视口高度。", 100))));
            fixture.Scroll.ScrollToHome();
            Pump();
            fixture.Scroll.Offset = new Vector(0, 100);
            Pump();
            var anchor = fixture.Capture(firstIntersecting: true);
            fixture.Model.AppendLog("高于视口的阅读行上方新增");
            Pump();
            Record("taller-than-viewport-prepend", fixture, anchor);
            reports.Add(new() { ["scenario"] = "taller-than-viewport-fixture-is-tall",
                ["pass"] = anchor.Control.Bounds.Height > fixture.Scroll.Viewport.Height });
        }
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "log-reading-anchors.json"),
            JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
        var failed = reports.Where(report => report["pass"] is false).ToList();
        if (failed.Count > 0) throw new Exception("FAIL: log reading anchors: " + string.Join(", ", failed.Select(report => report["scenario"])));
        Console.WriteLine($"PASS: {reports.Count} log reading anchor checks use actual visible row identity and viewport geometry");
    }

    private sealed class Fixture : IDisposable
    {
        public OverviewViewModel Model { get; }
        public OverviewView View { get; }
        public Window Window { get; }
        public LogViewport Viewport { get; }
        public ScrollViewer Scroll { get; }

        public Fixture(bool descending, int count, bool mixed)
        {
            Model = new OverviewViewModel(previewData: true) { IsDescending = descending, IsFollowing = false };
            Model.ClearCommand.Execute(null);
            for (var i = 0; i < count; i++)
                Model.AppendLog($"日志 {i:D4} " + (mixed && i % 5 == 0 ? string.Concat(Enumerable.Repeat("长日志用于验证换行测量。", 8)) : "固定短行"));
            View = new OverviewView { DataContext = Model };
            Window = new Window { Width = 900, Height = 900, Content = View };
            Window.Show();
            Pump();
            Viewport = View.GetVisualDescendants().OfType<LogViewport>().Single();
            Scroll = View.GetVisualDescendants().OfType<ScrollViewer>().Single(control => control.Name == "LogScroll");
        }

        public void Middle()
        {
            Scroll.Offset = new Vector(0, (Scroll.Extent.Height - Scroll.Viewport.Height) / 3);
            Pump();
        }

        public (object Item, double Y, Control Control) Capture(bool firstIntersecting = false, int skip = 0)
        {
            var row = Viewport.Children.OrderBy(control => control.TranslatePoint(default, Scroll)?.Y ?? double.NaN)
                .Where(control =>
                {
                    var y = control.TranslatePoint(default, Scroll)?.Y ?? double.NaN;
                    return (firstIntersecting ? y + control.Bounds.Height > Scroll.Padding.Top : y >= Scroll.Padding.Top)
                        && y < Scroll.Viewport.Height / 2;
                }).Skip(skip).First();
            return (row.DataContext!, row.TranslatePoint(default, Scroll)!.Value.Y, row);
        }

        public double? Position(object item) => Viewport.Children.FirstOrDefault(control => ReferenceEquals(control.DataContext, item))
            ?.TranslatePoint(default, Scroll)?.Y;

        public bool Intersects(object item)
        {
            var row = Viewport.Children.FirstOrDefault(control => ReferenceEquals(control.DataContext, item));
            return row?.TranslatePoint(default, Scroll) is { } point
                && point.Y < Scroll.Bounds.Height - Scroll.Padding.Bottom && point.Y + row.Bounds.Height > Scroll.Padding.Top;
        }

        public void Dispose() { Window.Close(); Pump(); }
    }

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
