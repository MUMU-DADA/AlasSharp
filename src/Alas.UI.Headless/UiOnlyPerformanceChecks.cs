using System.Diagnostics;
using System.Text.Json;
using Alas.UI.Controls;
using Alas.UI.Simulation;
using Alas.UI.ViewModels;
using Alas.UI.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Alas.UI.Headless;

/// <summary>Measure the explicit isolation entry through its memory backend and normal state consumer.</summary>
internal static class UiOnlyPerformanceChecks
{
    public static void Run(string output)
    {
        const int samples = 20;
        var results = new List<object>();
        int liveFactories = 0;
        var options = UiLaunchOptions.Parse(["--ui-only"]);
        using var backend = options.CreateBackend(() => { liveFactories++; throw new Exception("Live backend constructed"); });
        var theme = options.CreateThemeStore(() => { liveFactories++; throw new Exception("Live theme store constructed"); });
        var resources = options.CreateResourceStore(() => { liveFactories++; throw new Exception("Live resource store constructed"); });
        var simulation = (SimulatedUiBackend)backend;
        var view = new MainView(theme, backend, resourceStore: resources);
        var window = new Window { Width = 1280, Height = 900, Content = view };
        window.Show();
        try
        {
            Pump();
            view.Model.SelectInstance("demo-main");
            simulation.AppendLogs("demo-main", 400);
            Pump();
            Check(view.Model.IsUiOnly && liveFactories == 0 && !view.IsBackendPolling, "explicit isolation composition");
            var logs = view.GetVisualDescendants().OfType<LogViewport>().Single(control => control.Name == "LogList");
            var scroll = view.GetVisualDescendants().OfType<ScrollViewer>().Single(control => control.Name == "LogScroll");
            var model = view.Model.Overview;
            foreach (int width in new[] { 1280, 390 })
            {
                window.Width = width;
                Pump();
                foreach (bool following in new[] { true, false })
                {
                    model.IsFollowing = following;
                    scroll.ScrollToEnd();
                    Pump();
                    // Warm the complete snapshot path at the current width, including multiline sample rows.
                    for (int i = 0; i < 20; i++) { simulation.AppendLogs("demo-main", 1); Pump(); }
                    var timings = new List<double>();
                    var allocations = new List<long>();
                    int maximumRows = 0;
                    int builtBefore = logs.RowsBuilt;
                    int rentedBefore = logs.RowsRented;
                    int measuredBefore = logs.RowsMeasured;
                    for (int i = 0; i < samples; i++)
                    {
                        long allocated = GC.GetAllocatedBytesForCurrentThread();
                        long started = Stopwatch.GetTimestamp();
                        simulation.AppendLogs("demo-main", 1);
                        Pump();
                        timings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                        allocations.Add(GC.GetAllocatedBytesForCurrentThread() - allocated);
                        maximumRows = Math.Max(maximumRows, logs.RealizedRowCount);
                        Check(model.CachedLogCount == OverviewViewModel.LogCapacity, "sample retention bound");
                        Check(logs.RealizedRowCount > 0 && logs.RealizedRowCount < model.VisibleLogs.Count,
                            "realization follows viewport rather than all retained rows");
                        if (following) Check(TailIntersects(logs, scroll, model),
                            $"newest row intersects viewport; width={width}, sample={i}, descending={model.IsDescending}, "
                            + $"offset={scroll.Offset.Y}, extent={scroll.Extent.Height}, viewport={scroll.Viewport.Height}, "
                            + $"scrollHeight={scroll.Bounds.Height}, realized={logs.FirstRealizedIndex}..{logs.LastRealizedIndex}");
                    }
                    timings.Sort(); allocations.Sort();
                    results.Add(new
                    {
                        width, following, samples,
                        milliseconds_median = timings[samples / 2 - 1],
                        milliseconds_p95 = timings[(int)Math.Ceiling(samples * .95) - 1],
                        allocated_bytes_median = allocations[samples / 2 - 1],
                        allocated_bytes_p95 = allocations[(int)Math.Ceiling(samples * .95) - 1],
                        maximum_realized_rows = maximumRows,
                        built = logs.RowsBuilt - builtBefore,
                        rented = logs.RowsRented - rentedBefore,
                        measured = logs.RowsMeasured - measuredBefore,
                    });
                }
            }
            // The same view must resume sample updates after temporary detachment without enabling a timer.
            window.Content = null; Pump();
            simulation.AppendLogs("demo-main", 1);
            window.Content = view; Pump();
            Check(!view.IsBackendPolling && liveFactories == 0, "reattachment retains isolation");
            Check(!AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name is "Alas.Core" or "Alas.Client"),
                "performance entry never loads automation or network adapters");
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "ui-only-performance.json"), JsonSerializer.Serialize(new
            {
                schema = "ui-only-perf/1",
                scope = "Headless + Skia; explicit UiLaunchOptions; memory snapshot, normal view model and shared view; UI thread allocations",
                limitation = "Includes memory JSON snapshots and state refresh; not comparable to direct AppendLog or native-window/browser/GPU measurements",
                live_factories = liveFactories,
                backend_polling = view.IsBackendPolling,
                scenarios = results,
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("PASS: explicit UI-only performance: 4 scenarios, 20 samples each; bounded rows, tail visibility, no live factories or polling");
        }
        finally { window.Close(); Pump(); }
    }

    private static bool TailIntersects(LogViewport logs, ScrollViewer scroll, OverviewViewModel model)
    {
        var row = logs.GetVisualChildren().OfType<Control>()
            .FirstOrDefault(control => ReferenceEquals(control.DataContext, model.VisibleLogs[^1]));
        if (row is null || row.TranslatePoint(default, scroll) is not { } origin) return false;
        return origin.Y < scroll.Bounds.Height - scroll.Padding.Bottom
            && origin.Y + row.Bounds.Height > scroll.Padding.Top;
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException("UI-only performance: " + message); }
}
