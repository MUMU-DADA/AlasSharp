using System.Text.Json.Nodes;
using Alas.Contracts;
using Alas.UI.Overview;
using Alas.UI.Settings;
using Alas.UI.Simulation;
using Alas.UI.Statistics;
using Alas.UI.TaskEditor;
using Alas.UI.Theming;
using Alas.UI.ViewModels;
using Alas.UI.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Alas.UI.Headless;

internal static class UiOnlyChecks
{
    public static async Task Verify()
    {
        int liveFactories = 0;
        var options = UiLaunchOptions.Parse(["--ui-only"]);
        using var backend = options.CreateBackend(() => { liveFactories++; throw new Exception("Live backend constructed"); });
        var theme = options.CreateThemeStore(() => { liveFactories++; throw new Exception("Live theme store constructed"); });
        var resources = options.CreateResourceStore(() => { liveFactories++; throw new Exception("Live resource store constructed"); });
        Check(liveFactories == 0 && backend is SimulatedUiBackend && backend.IsSimulation
            && theme is MemoryThemeStore && resources is MemoryResourceSelectionStore, "isolation precedes all live factories");
        var live = UiLaunchOptions.Parse([]).CreateBackend(() => { liveFactories++; return DisconnectedInstanceSource.Instance; });
        Check(liveFactories == 1 && ReferenceEquals(live, DisconnectedInstanceSource.Instance) && !live.IsSimulation,
            "normal launch remains live/disconnected and never silently becomes a simulation");
        Check(!UiLaunchOptions.Parse(["--ui-only=false"]).UiOnly, "only explicit UI-only flag selects simulation");

        var simulation = (SimulatedUiBackend)backend;
        Check(simulation.Instances.Count == 2 && simulation.Instances.All(card => card.IsDemo), "all fixture instances are labelled");
        var schema = await backend.ReadSchemaAsync();
        var config = await backend.ReadConfigAsync("demo-main");
        var editor = new TaskEditorViewModel { Backend = new CoreTaskEditorBackend(backend), AutoSave = false };
        editor.Load(config.Instance, "Main", new JsonObject { ["args"] = schema.Args, ["menu"] = schema.Menu,
            ["translations"] = schema.Translations }, new JsonObject { ["instance"] = config.Instance,
            ["revision"] = config.Revision, ["values"] = config.Values });
        editor.Fields.Single(field => field.Argument == "Count").SetText("9");
        Check(await editor.SaveAsync() && !editor.HasChanges, "real task adapter saves to memory");
        Check((await backend.ReadConfigAsync("demo-main")).Values["Main"]!["Sample"]!["Count"]!.GetValue<double>() == 9,
            "local config edit retained");
        Check((await backend.ReadConfigAsync("demo-event")).Values["Main"]!["Sample"]!["Count"]!.GetValue<int>() == 3,
            "instance samples are isolated");
        editor.RequestRun();
        Check(await editor.ConfirmRunAsync(), "confirmed task exercises UI state without automation");
        Check((await backend.ReadStateAsync())["active"]!["kind"]!.GetValue<string>() == "simulation", "run is explicitly synthetic");
        Check(await backend.RequestStopAsync(), "simulated task can stop");
        await backend.StartSchedulerAsync(new() { Instance = "demo-main", ConfirmActions = true });
        Check(await backend.RequestStopAsync(), "scheduler operation stays in sample state");
        await backend.SaveQueueAsync(new JsonObject { ["tasks"] = new JsonArray() });
        await backend.StartRunAsync(new() { Queue = new JsonObject { ["tasks"] = new JsonArray() } });
        Check(await backend.RequestStopAsync(), "queue intent remains in memory");

        var settings = new CoreDeploySettingsBackend(backend);
        var settingsSnapshot = await settings.ReadAsync();
        Check(settingsSnapshot!.Groups.SelectMany(group => group.Fields).All(field => field.Editable), "simulation settings allow edits");
        await settings.SaveAsync(new("CheckUpdate", "true"));
        Check((await settings.ReadAsync())!.Groups.SelectMany(group => group.Fields).Single(field => field.Key == "CheckUpdate").Value == "true",
            "settings adapter changes only sample values");
        Check((await backend.SetStartupRunAsync(new() { Instance = "demo-main", Enabled = true })).Enabled
            && (await backend.ReadStartupRunAsync("demo-main")).Enabled, "startup preference is a memory fixture");
        await backend.ImportInstanceAsync(new() { Name = "memory-upload", Content = "{\"sample\":true}" });
        Check((await backend.ReadInstanceImportsAsync()).Sources.Single().Name == "memory-upload", "imports remain in memory");
        var created = await backend.CreateInstanceAsync(new() { Instance = "demo-new", ImportFile = "memory-upload" });
        await backend.DeleteInstanceAsync(new() { Instance = created.Instance, Revision = created.Revision });
        Check((await backend.ReadInstancesAsync()).Instances.Count == 2, "create and delete never reach saved instances");
        foreach (string category in new[] { "resource", "combat", "commission", "research", "drop" })
            Check(StatisticsReport.Parse(await backend.ReadStatisticsAsync(new() { Instance = "demo-main", Category = category })).Series.Count > 0,
                "statistics sample uses the real report parser");
        await backend.RefreshStatisticsLootAsync("demo-main");
        Check((await new CoreMeowfficerReportBackend(backend).LoadAsync("demo-main", default))!.Count == 0, "empty report is explicit");
        await backend.ClearMeowfficerAsync("demo-main");
        Check((await backend.ValidateShopStrategyAsync("return true"))["valid"]!.GetValue<bool>() == false, "simulation does not fake script validation");
        Check(await backend.ReadReportAsync("missing") is null, "no fabricated completed-run evidence");

        simulation.AppendLogs("demo-main", 1000);
        var state = await backend.ReadInstanceStateAsync("demo-main");
        Check(state["recent_logs"]!.AsArray().Count == OverviewViewModel.LogCapacity, "sample load is bounded");
        state["recent_logs"]!.AsArray().Clear();
        config.Values.Clear();
        Check((await backend.ReadInstanceStateAsync("demo-main"))["recent_logs"]!.AsArray().Count == OverviewViewModel.LogCapacity
            && (await backend.ReadConfigAsync("demo-main")).Values.Count > 0, "returned snapshots cannot mutate shared sample state");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await backend.DeleteInstanceAsync(new() { Instance = "demo-main", Revision = "wrong" }, cancelled.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { }
        using var second = new SimulatedUiBackend();
        Check((await second.ReadConfigAsync("demo-main")).Values["Main"]!["Sample"]!["Count"]!.GetValue<int>() == 3,
            "new launch resets all sample writes");
        simulation.Dispose();
        Check(!simulation.IsConnected && simulation.Instances.Count == 0, "dispose clears the data source");
        AssertNoAutomationAssemblies();
        Console.WriteLine("PASS: UI-only factory isolation, all capability methods, memory edits, cancellation and bounded sample data");
    }

    public static void VerifyControls(string output)
    {
        using var backend = new SimulatedUiBackend();
        var view = new MainView(new MemoryThemeStore(), backend, resourceStore: new MemoryResourceSelectionStore());
        var window = new Window { Width = 1280, Height = 820, Content = view };
        window.Show(); Pump();
        try
        {
            Check(view.Model.IsUiOnly && view.FindControl<Border>("SimulationBanner")!.IsEffectivelyVisible, "persistent simulation banner");
            Check(!view.IsBackendPolling, "no periodic backend polling in isolated mode");
            var card = view.GetVisualDescendants().OfType<Button>().First(button => button.DataContext is InstanceCardViewModel);
            Click(window, card);
            Check(view.Model.HasInstance && view.Model.Overview.Resources.Count == 4 && view.Model.Overview.CachedLogCount == 40,
                "real home click opens sample overview");
            Check(view.Model.Overview.Resources.All(resource => resource.Value != "—"), "numeric resource samples render as values");
            var skip = view.FindControl<Button>("SkipLink")!;
            Check(!skip.IsFocused && skip.Bounds.Bottom <= 0, "unfocused skip link does not cover simulation notice");
            Click(window, view.FindControl<Button>("SimulationLogsButton")!);
            Check(view.Model.Overview.CachedLogCount == 140, "real toolbar click adds local render workload");
            var toggle = view.GetVisualDescendants().OfType<Button>().Single(button => ReferenceEquals(button.Command, view.Model.Overview.ToggleSchedulerCommand));
            Click(window, toggle);
            Check(view.Model.Overview.IsSchedulerRunning, "real start click only changes simulated status");
            Click(window, toggle);
            Check(!view.Model.Overview.IsSchedulerRunning, "real stop click returns to simulated idle");
            Capture(window, output, "ui-only-wide.png");
            view.Model.Overview.SearchText = "第 100 条";
            Check(view.Model.Overview.VisibleLogs.Count == 1, "sample logs use normal filtering");
            view.Model.Overview.SearchText = "";
            foreach (var task in view.Model.TaskGroups.SelectMany(group => group.Tasks).Where(task => task.Key != "MeowfficerScore").ToArray())
            {
                view.Model.SelectTaskCommand.Execute(task);
                Check(view.Model.TaskEditor.IsLoaded && view.Model.TaskEditor.Fields.Any(), "every task navigation has simulated fields");
                view.Model.TaskEditor.AutoSave = false;
            }
            Pump();
            var field = view.GetVisualDescendants().OfType<TextBox>().First(box => box.Name?.EndsWith(".Sample.Note", StringComparison.Ordinal) == true);
            field.BringIntoView(); Pump(); field.Focus(); field.SelectAll(); window.KeyTextInput("隔离输入"); Pump();
            Check(view.Model.TaskEditor.Fields.Single(item => item.Argument == "Note").Text == "隔离输入", "real keyboard updates local draft");
            Check(view.Model.TaskEditor.SaveAsync().GetAwaiter().GetResult(), "real editor save uses local capability");
            view.Model.GoHome();
            foreach (string page in new[] { "settings", "remote", "configs", "interface", "dev", "updater", "home" })
            { view.Model.SelectNavCommand.Execute(page); Pump(); }
            view.Model.SelectInstance("demo-main");
            window.Width = 390; window.Height = 844; Pump();
            var banner = view.FindControl<Border>("SimulationBanner")!;
            var button = view.FindControl<Button>("SimulationLogsButton")!;
            Check(banner.Bounds.Width <= window.ClientSize.Width && button.TranslatePoint(default, window) is { } point
                && point.X >= 0 && point.X + button.Bounds.Width <= window.ClientSize.Width + 1, "narrow banner and controls fit");
            Check(!view.IsBackendPolling, "navigation and resizing do not enable polling");
            window.Content = null; Pump(); window.Content = view; Pump();
            Check(!view.IsBackendPolling, "reattachment retains isolation");
            Capture(window, output, "ui-only-narrow.png");
            AssertNoAutomationAssemblies();
            Console.WriteLine("PASS: UI-only Headless mouse/keyboard, all task routes, simulated start/stop, narrow layout and no polling");
        }
        finally { window.Close(); Pump(); }
    }

    private static void AssertNoAutomationAssemblies()
        => Check(!AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name is "Alas.Core" or "Alas.Client"),
            "UI-only tests do not load Core or network adapters");
    private static void Click(Window window, Control target)
    {
        var origin = target.TranslatePoint(default, window) ?? throw new Exception("Detached click target");
        var point = origin + new Vector(target.Bounds.Width / 2, target.Bounds.Height / 2);
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Pump();
    }
    private static void Pump() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static void Capture(Window window, string output, string name)
    {
        window.MouseMove(new Point(2, 2)); Pump();
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No isolated UI frame");
        frame.Save(Path.Combine(output, name), PngBitmapEncoderOptions.Default);
    }
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException("UI-only: " + message); }
}
