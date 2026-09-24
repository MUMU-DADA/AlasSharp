using System.Text.Json.Nodes;
using Alas.Contracts;
using Alas.UI.Settings;
using Alas.UI.Theming;
using Alas.UI.ViewModels;
using Alas.UI.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Alas.UI.Headless;

internal static class AgentIntegrationChecks
{
    public static void Run()
    {
        var backend = new CoreUiBackendChecks.FixtureBackend
        {
            DeployRead = () => Task.FromResult(new DeploySettingsResponse
            {
                Groups = JsonNode.Parse("""
                    [{"key":"Git","label":"版本","fields":[{"key":"Branch","label":"分支","type":"string","value":"master","help":"<b>分支说明</b>","options":[]},{"key":"SSLVerify","label":"验证证书","type":"bool","value":true,"help":"","options":[]}]},
                     {"key":"Webui","label":"网页","fields":[{"key":"WebuiPort","label":"端口","type":"int","value":22267,"help":"","options":[]}]},
                     {"key":"RemoteAccess","label":"远程","fields":[{"key":"Password","label":"密码","type":"password","value":"","help":"","options":[]}]}]
                    """)!.AsArray(), Notice = "fixture", Demo = false,
            }),
        };
        var adapter = new CoreDeploySettingsBackend(backend);
        var snapshot = adapter.ReadAsync().GetAwaiter().GetResult()!;
        Check(snapshot.Groups[0].Fields[0].Help == "分支说明", "Core help strips markup without changing the label");
        adapter.SaveAsync(new SettingsChange("SSLVerify", "false")).GetAwaiter().GetResult();
        Check(backend.DeployPatch!.Values["SSLVerify"]!.GetValue<bool>() == false, "boolean changes retain JSON boolean type");
        adapter.SaveAsync(new SettingsChange("Branch", "123")).GetAwaiter().GetResult();
        Check(backend.DeployPatch!.Values["Branch"]!.GetValue<string>() == "123", "numeric-looking strings stay strings");
        backend.DeployPatch = null;
        foreach (var width in new[] { 1280, 390 })
        {
            var view = new MainView(new MemoryThemeStore(), backend);
            var window = new Window { Width = width, Height = 844, Content = view };
            window.Show();
            try
            {
                Pump();
                foreach (var (key, host) in new[] { ("settings", "SettingsHost"), ("remote", "RemoteHost"), ("updater", "UpdaterHost") })
                {
                    if (width == 390) { view.Model.IsDrawerOpen = true; Pump(); }
                    var button = view.GetVisualDescendants().OfType<Button>()
                        .Single(b => b.DataContext is NavEntry entry && entry.Key == key);
                    Click(window, button);
                    Check(view.Model.ActivePage == key && !view.Model.IsPlaceholderActive,
                        $"{key} navigation reaches the integrated page at {width}");
                    Check(view.GetVisualDescendants().OfType<Panel>().Single(p => p.Name == host).IsVisible, "route host visible");
                }
                Check(ReferenceEquals(view.SettingsPage.Session, view.RemotePage.Session), "both pages share one draft queue");
                Check(view.SettingsPage.Session.SystemGroups.Single().Key == "Git" &&
                    view.SettingsPage.Session.RemoteGroups.Count == 2, "Core groups split without loss");
                Check(!view.RemotePage.Model.CanToggle && !view.RemotePage.Model.HasAddress,
                    "unimplemented remote service does not invent an address or enabled action");
                view.Model.SelectNavCommand.Execute("login");
                Pump();
                Check(view.Model.IsLoginActive && !view.Model.IsPlaceholderActive, "login component route is available without enabling authentication");
                view.Model.SelectNavCommand.Execute("settings");
                Pump();
                var input = view.SettingsPage.GetVisualDescendants().OfType<TextBox>().Single(t => t.Text == "master");
                Click(window, input); input.SelectAll();
                foreach (char c in "release") { window.KeyTextInput(c.ToString()); Pump(); }
                var deadline = DateTime.UtcNow.AddSeconds(3);
                while (backend.DeployPatch is null && DateTime.UtcNow < deadline) { Pump(); Thread.Sleep(5); }
                Check(backend.DeployPatch?.Values["Branch"]?.GetValue<string>() == "release", "real settings input invokes the shared Core capability");
                Check(input.IsFocused && input.Text == "release", "save notification preserves the active editor");
                var skip = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SkipLink");
                skip.Focus(); Pump();
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); Pump();
                Check(view.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "MainScroll").IsFocused,
                    "keyboard skip link actually transfers focus to page content");
                view.Model.SelectInstance("fixture"); Pump();
                var cards = view.GetVisualDescendants().OfType<OverviewView>().Single()
                    .GetVisualDescendants().OfType<Button>().Single(b => b.Name == "InstanceSettingsButton");
                Click(window, cards);
                Check(cards.Flyout is Flyout { IsOpen: true, Content: Overview.ResourceCardSettingsPanel },
                    "instance settings opens the delivered resource selection component");
                cards.Flyout!.Hide();
                backend.DeployPatch = null;
            }
            finally { window.Close(); }
        }
        Console.WriteLine("PASS: integrated agent routes, shared Core deployment settings and narrow-screen pointer input");
    }

    private static void Click(Window window, Control control)
    {
        control.BringIntoView(); Pump();
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        Check(point.X >= 0 && point.Y >= 0 && point.X < window.Width && point.Y < window.Height, "navigation stays inside the viewport");
        window.MouseMove(point); window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Pump();
    }
    private static void Pump() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static void Check(bool result, string label) { if (!result) throw new Exception("FAIL: " + label); }
}
