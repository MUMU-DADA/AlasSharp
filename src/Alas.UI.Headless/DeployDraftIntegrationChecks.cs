using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Contracts;
using Alas.UI.DeploySettings;
using Alas.UI.Simulation;
using Alas.UI.Theming;
using Alas.UI.Views;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Alas.UI.Headless;

internal static class DeployDraftIntegrationChecks
{
    private const string DraftKey = "deploy-edits.deploy";

    public static void Run()
    {
        var store = new MemoryDeployDraftStore();
        var rejected = Backend("master", "22267", _ =>
            Task.FromException<DeploySettingsPatchResponse>(new ArgumentException("字段被拒绝")));
        var (first, firstWindow) = Show(rejected, store);
        try
        {
            Open(first, "settings");
            var branch = Input(first.SettingsPage, "Branch");
            branch.Text = "release";
            WaitFor(() => first.SettingsPage.Session.Edits.Edit("Branch")?.Status == DeployEditStatus.Error);
            Check(ReferenceEquals(first.SettingsPage.Session, first.RemotePage.Session),
                "settings and remote share the same persisted draft queue");
            Check(store.Read(DraftKey)?.Contains("release", StringComparison.Ordinal) == true,
                "a rejected real input remains in session storage");
        }
        finally { firstWindow.Close(); }

        string savedBranch = "master";
        string savedPort = "22267";
        var accepted = Backend(savedBranch, savedPort, request =>
        {
            if (request.Values["Branch"] is { } branch) savedBranch = branch.GetValue<string>();
            if (request.Values["WebuiPort"] is { } port) savedPort = port.GetValue<string>();
            return Task.FromResult(new DeploySettingsPatchResponse
            {
                Updated = request.Values.Select(item => item.Key).ToArray(),
            });
        });
        accepted.DeployRead = () => Task.FromResult(Response(savedBranch, savedPort));
        var (restored, restoredWindow) = Show(accepted, store);
        try
        {
            Open(restored, "settings");
            var branch = Input(restored.SettingsPage, "Branch");
            Check(branch.Text == "release" &&
                restored.SettingsPage.Session.Edits.Edit("Branch")?.Status == DeployEditStatus.Error,
                "rebuilding MainView restores the unconfirmed input and its error state");
            Check(ReferenceEquals(restored.SettingsPage.Session, restored.RemotePage.Session),
                "both rebuilt pages use the one restored session");
            branch.Text = "release2";
            WaitFor(() => restored.SettingsPage.Session.Edits.Edit("Branch")?.Status == DeployEditStatus.Saved);
            var refresh = restored.SettingsPage.Session.RefreshAsync();
            WaitFor(() => refresh.IsCompleted);
            refresh.GetAwaiter().GetResult();
            Check(restored.SettingsPage.Session.Edits.Edit("Branch") is null && store.Read(DraftKey) is null,
                "the acknowledged edit is reconciled and removed from session storage");

            Open(restored, "remote");
            var port = Input(restored.RemotePage, "WebuiPort");
            port.Text = "9090";
            WaitFor(() => restored.RemotePage.Session.Edits.Edit("WebuiPort")?.Status == DeployEditStatus.Saved);
            Check(restored.SettingsPage.Session.Edits.Edit("WebuiPort")?.Value == "9090",
                "remote input is visible through the settings page's shared queue");
            Check(accepted.DeployPatch?.Values["WebuiPort"]?.GetValue<string>() == "9090",
                "the shared draft submits through the real settings capability");
        }
        finally { restoredWindow.Close(); }

        var failedStore = new ThrowingStore();
        var (failed, failedWindow) = Show(accepted, failedStore);
        try
        {
            Open(failed, "settings");
            Check(failed.SettingsPage.Session.HasStorageError && failedStore.Reads > 0,
                "restore failure is visible on the storage-error channel immediately");
            Check(failed.SettingsPage.GetVisualDescendants().OfType<TextBlock>()
                .Single(block => block.Name == "SettingsStorageErrorBox").IsVisible,
                "the storage error is rendered separately from the settings read error");
            Check(!failed.SettingsPage.Model.HasError, "storage failure does not replace schema data");
            Input(failed.SettingsPage, "Branch").Text = "blocked";
            WaitFor(() => accepted.DeployPatch?.Values["Branch"]?.GetValue<string>() == "blocked");
            Check(failedStore.Writes > 0 && failed.SettingsPage.Session.HasStorageError,
                "write failure is reported while the edit still reaches the backend");
        }
        finally { failedWindow.Close(); }

        using var simulation = new SimulatedUiBackend();
        var forbiddenStore = new ThrowingStore();
        var isolated = new MainView(new MemoryThemeStore(), simulation, deployDraftStore: forbiddenStore);
        isolated.SettingsPage.Session.Edits.Change("CheckUpdate", "true", "true");
        Check(forbiddenStore.Reads == 0 && forbiddenStore.Writes == 0 &&
            !isolated.SettingsPage.Session.HasStorageError,
            "UI-only MainView never reads or writes an injected live draft store");
        Console.WriteLine("PASS: deploy drafts restore across views, share pages, clear on confirmation and isolate storage errors");
    }

    public static void RunPerformance(string output)
    {
        var source = FindDeployRules();
        var rules = JsonNode.Parse(File.ReadAllText(source))!.AsObject();
        var groups = (JsonArray)rules["groups"]!.DeepClone();
        foreach (var group in groups)
            foreach (var fieldNode in group!["fields"]!.AsArray())
            {
                var field = fieldNode!.AsObject();
                field["label"] = field["key"]!.GetValue<string>();
                field["help"] = field["help"]?.GetValue<string>() ?? string.Empty;
                field["value"] = field["type"]?.GetValue<string>() switch
                {
                    "bool" => JsonValue.Create(false),
                    "int" => JsonValue.Create(1),
                    _ => JsonValue.Create(string.Empty),
                };
            }
        int expectedFields = groups.SelectMany(group => group!["fields"]!.AsArray()).Count();
        int expectedSystemFields = groups.Where(group => group!["key"]!.GetValue<string>()
                is not ("RemoteAccess" or "Webui"))
            .Sum(group => group!["fields"]!.AsArray().Count);
        var backend = new CoreUiBackendChecks.FixtureBackend
        {
            DeployRead = () => Task.FromResult(new DeploySettingsResponse
            {
                Groups = (JsonArray)groups.DeepClone(), Notice = "offline fixture", Demo = false,
            }),
        };
        var watch = Stopwatch.StartNew();
        var (view, window) = Show(backend, new MemoryDeployDraftStore());
        try
        {
            Open(view, "settings");
            window.UpdateLayout();
            Pump();
            double coldMs = watch.Elapsed.TotalMilliseconds;
            int systemRows = view.SettingsPage.GetVisualDescendants().OfType<Control>()
                .Count(control => control.Name?.StartsWith("DeployFieldRow", StringComparison.Ordinal) == true);
            Check(systemRows == expectedSystemFields,
                $"all {expectedSystemFields} system fields must be built on first entry (got {systemRows})");
            Open(view, "remote");
            int remoteRows = view.RemotePage.GetVisualDescendants().OfType<Control>()
                .Count(control => control.Name?.StartsWith("DeployFieldRow", StringComparison.Ordinal) == true);
            Check(systemRows + remoteRows == expectedFields,
                $"both pages must preserve all {expectedFields} upstream fields");
            var switches = new List<FrameSample>();
            for (int index = 0; index < 20; index++)
            {
                var page = index % 2 == 0 ? "remote" : "settings";
                switches.Add(MeasureFrame(window, () => view.Model.SelectNavCommand.Execute(page)));
            }
            Open(view, "settings");
            var branch = Input(view.SettingsPage, "Branch");
            var idle = new List<FrameSample>();
            for (int index = 0; index < 20; index++)
                idle.Add(MeasureFrame(window, () => { }));
            var edits = new List<FrameSample>();
            for (int index = 0; index < 20; index++)
            {
                var value = index % 2 == 0 ? "release-a" : "release-b";
                edits.Add(MeasureFrame(window, () => branch.Text = value));
            }
            Check(systemRows + remoteRows == expectedFields && branch.IsEffectivelyVisible,
                "performance input keeps the complete field tree visible");
            var report = new
            {
                schema = "ui-deploy-drafts-perf/1",
                field_count = expectedFields,
                cold_entry_ms = Math.Round(coldMs, 3),
                @switch = Stats(switches),
                idle = Stats(idle),
                input = Stats(edits),
                environment = "Release Avalonia Headless, offscreen Skia; exported field declarations with neutral values",
            };
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "deploy-drafts-perf.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"deploy drafts: fields={report.field_count} cold={report.cold_entry_ms} ms "
                + $"switch={report.@switch.MedianMs} ms input={report.input.MedianMs} ms "
                + $"idle={report.idle.MedianMs} ms "
                + $"input action/layout/pump={report.input.ActionMs}/{report.input.LayoutMs}/{report.input.PumpMs} ms");
        }
        finally { window.Close(); }
    }

    private static string FindDeployRules()
    {
        for (string? current = Directory.GetCurrentDirectory(); current is not null;
             current = Directory.GetParent(current)?.FullName)
        {
            var path = Path.Combine(current, "src", "Alas.Core", "Runtime", "Resources", "deploy-settings.json");
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("找不到上游部署字段声明，性能测量不能使用缩减样本。");
    }

    private readonly record struct FrameSample(double ActionMs, double LayoutMs, double PumpMs, long AllocatedBytes)
    {
        public double TotalMs => ActionMs + LayoutMs + PumpMs;
    }

    private static FrameSample MeasureFrame(Window window, Action action)
    {
        Pump();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        action();
        double actionMs = watch.Elapsed.TotalMilliseconds;
        window.UpdateLayout();
        double layoutMs = watch.Elapsed.TotalMilliseconds - actionMs;
        Pump();
        return new FrameSample(actionMs, layoutMs, watch.Elapsed.TotalMilliseconds - actionMs - layoutMs,
            GC.GetAllocatedBytesForCurrentThread() - allocated);
    }

    private sealed record FrameStats(double MedianMs, double P95Ms, double ActionMs, double LayoutMs,
        double PumpMs, double AllocatedBytes);

    private static FrameStats Stats(List<FrameSample> samples) =>
        new(Median(samples.Select(sample => sample.TotalMs).ToList()),
         Percentile(samples.Select(sample => sample.TotalMs).ToList(), 0.95),
         Median(samples.Select(sample => sample.ActionMs).ToList()),
         Median(samples.Select(sample => sample.LayoutMs).ToList()),
         Median(samples.Select(sample => sample.PumpMs).ToList()),
         Median(samples.Select(sample => (double)sample.AllocatedBytes).ToList()));

    private static double Median(List<double> samples) => Percentile(samples, 0.5);

    private static double Percentile(List<double> samples, double rank)
    {
        var sorted = samples.Order().ToArray();
        return Math.Round(sorted[(int)Math.Ceiling(rank * sorted.Length) - 1], 3);
    }

    private static CoreUiBackendChecks.FixtureBackend Backend(string branch, string port,
        Func<DeploySettingsPatchRequest, Task<DeploySettingsPatchResponse>> patch) => new()
    {
        DeployRead = () => Task.FromResult(Response(branch, port)),
        DeployPatchHandler = patch,
    };

    private static DeploySettingsResponse Response(string branch, string port)
    {
        var groups = JsonNode.Parse("""
            [{"key":"Git","label":"版本","fields":[{"key":"Branch","label":"分支","type":"string","value":"master","help":"分支说明","options":[]}]},
             {"key":"Webui","label":"网页","fields":[{"key":"WebuiPort","label":"端口","type":"int","value":22267,"help":"","options":[]}]}]
            """)!.AsArray();
        groups[0]!["fields"]![0]!["value"] = branch;
        groups[1]!["fields"]![0]!["value"] = int.Parse(port);
        return new DeploySettingsResponse { Groups = groups, Notice = "fixture", Demo = false };
    }

    private static (MainView View, Window Window) Show(CoreUiBackendChecks.FixtureBackend backend,
        IDeployDraftStore store)
    {
        var view = new MainView(new MemoryThemeStore(), backend, deployDraftStore: store);
        var window = new Window { Width = 1280, Height = 820, Content = view };
        window.Show();
        Pump();
        return (view, window);
    }

    private static void Open(MainView view, string page)
    {
        view.Model.SelectNavCommand.Execute(page);
        WaitFor(() => view.SettingsPage.Session.HasData && view.Model.ActivePage == page);
    }

    private static TextBox Input(Control page, string key) => page.GetVisualDescendants()
        .OfType<Control>().Single(control => control.Name == "DeployFieldRow" + key)
        .GetVisualDescendants().OfType<TextBox>().First();

    private static void WaitFor(Func<bool> ready)
    {
        var watch = Stopwatch.StartNew();
        while (!ready())
        {
            if (watch.ElapsedMilliseconds > 5000) throw new TimeoutException("部署草稿界面检查超时。");
            Pump();
            Thread.Sleep(1);
        }
        Pump();
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ThrowingStore : IDeployDraftStore
    {
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public string? Read(string key) { Reads++; throw new InvalidOperationException("存储被禁用"); }
        public void Write(string key, string content) { Writes++; throw new InvalidOperationException("存储被禁用"); }
        public void Remove(string key) => throw new InvalidOperationException("存储被禁用");
    }
}
