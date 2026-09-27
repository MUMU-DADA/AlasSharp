using System.Diagnostics;
using System.Text.Json;
using Alas.UI.Simulation;
using Alas.UI.Theming;
using Alas.UI.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;

namespace Alas.UI.Headless;

/// <summary>
/// Lightweight Engine queue editor measurement. The production task page is
/// runner based, so this check does not load upstream menu/args files or run
/// the retired Config task form.
/// </summary>
internal static class TaskEditorLoadChecks
{
    public static void Run(string output) => RunCore(output, "task-load");
    public static void RunCold(string output) => RunCore(output, "task-load-cold");

    private static void RunCore(string output, string mode)
    {
        Directory.CreateDirectory(output);
        using var backend = new SimulatedUiBackend();
        var view = new MainView(new MemoryThemeStore(), backend);
        var window = new Window { Width = 1280, Height = 820, Content = view };
        window.Show();
        Pump();
        var watch = Stopwatch.StartNew();
        view.Model.SelectInstance("demo-main");
        Pump();
        var task = view.Model.TaskGroups.SelectMany(group => group.Tasks).First();
        view.Model.SelectTaskCommand.Execute(task);
        Pump();
        watch.Stop();
        if (!view.Model.TaskEditor.IsLoaded || view.Model.TaskEditor.TaskName != task.Key || !view.Model.TaskEditor.Fields.Any())
            throw new InvalidOperationException("Engine queue editor did not load a runner input field");
        File.WriteAllText(Path.Combine(output, mode + ".json"), JsonSerializer.Serialize(new
        {
            schema = "engine-task-editor/1", task = task.Key, fields = view.Model.TaskEditor.Fields.Count(),
            elapsed_ms = Math.Round(watch.Elapsed.TotalMilliseconds, 3), source = "EngineTaskCatalog"
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS: {mode} loads Engine runner input without upstream task schema");
        window.Close();
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}
