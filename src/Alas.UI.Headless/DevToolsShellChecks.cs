using Alas.UI.DevTools;
using Alas.UI.Theming;
using Alas.UI.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Alas.UI.Headless;

internal static class DevToolsShellChecks
{
    public static void Run(string output)
    {
        foreach (int width in new[] { 1280, 390 })
        {
            var view = new MainView(new MemoryThemeStore(), new CoreUiBackendChecks.FixtureBackend());
            var window = new Window { Width = width, Height = 844, Content = view };
            window.Show();
            Pump();
            try
            {
                if (width < 950) { view.Model.IsDrawerOpen = true; Pump(); }
                var sidebar = view.FindControl<SidebarView>("Sidebar")!;
                var navigation = sidebar.GetVisualDescendants().OfType<Button>().Single(button => button.CommandParameter as string == "dev");
                Click(window, navigation, scroll: true);
                Check(view.Model.IsDevToolsActive && !view.Model.IsPlaceholderActive, "dev navigation reaches the page");
                var page = Find<DevToolsView>(view, "DevToolsPage");
                Check(page.IsEffectivelyVisible, "routed page is visible");
                Check(!Find<ScrollViewer>(view, "MainScroll").IsVisible, "page owns its bounded scroll viewport");
                foreach (UiTheme theme in Enum.GetValues<UiTheme>())
                {
                    view.Model.ApplyTheme(view.Model.Theme.Preference with { Theme = theme });
                    Pump();
                    Check(page.UiTheme == theme, "theme reaches mounted developer page");
                }
                view.Model.ApplyTheme(ThemePreference.Default);
                Pump();
                var opener = Find<Button>(view, "DevToolsOpenModalButton");
                Click(window, opener, scroll: true);
                var overlay = Find<Border>(view, "DevToolsModalOverlay");
                Check(overlay.IsVisible, "pointer opens modal");
                Check(ReferenceEquals(overlay.Parent, view.FindControl<Grid>("Root")), "modal is attached to the whole shell");
                Check(Math.Abs(overlay.Bounds.Width - window.ClientSize.Width) <= 1 &&
                    Math.Abs(overlay.Bounds.Height - window.ClientSize.Height) <= 1, "modal covers shell viewport");
                var home = sidebar.GetVisualDescendants().OfType<Button>().Single(button => button.CommandParameter as string == "home");
                if (width > 950) { Click(window, home, scroll: false); Check(view.Model.IsDevToolsActive, "modal blocks sidebar navigation"); }
                var input = Find<TextBox>(view, "DevToolsModalInput");
                Click(window, input, scroll: false);
                window.KeyTextInput("测试"); Pump();
                Check(input.Text!.Contains("测试") && ReferenceEquals(window.FocusManager!.GetFocusedElement(), input), "modal preserves typing focus");
                using (var bitmap = window.CaptureRenderedFrame())
                    bitmap!.Save(Path.Combine(output, $"devtools-modal-{width}.png"), PngBitmapEncoderOptions.Default);
                Click(window, Find<Button>(view, "DevToolsModalCancel"), scroll: false);
                Check(!overlay.IsVisible && ReferenceEquals(window.FocusManager!.GetFocusedElement(), opener), "cancel reachable immediately and restores focus");
                Click(window, opener, scroll: false);
                view.Model.SelectNavCommand.Execute("home");
                Pump();
                Check(!overlay.IsVisible && view.Model.IsHomeActive, "programmatic route change closes external modal");
            }
            finally { window.Close(); Pump(); }
        }
        Console.WriteLine("PASS: developer page route, themes, whole-shell modal, narrow input and navigation cleanup");
    }

    private static T Find<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("DevTools shell: " + message); }
    private static void Pump()
    { for (int i = 0; i < 4; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } }
    private static void Click(Window window, Control control, bool scroll)
    {
        if (scroll) { control.BringIntoView(); Pump(); }
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        Check(new Rect(window.ClientSize).Contains(center), control.Name + " must be in viewport");
        window.MouseMove(center); window.MouseDown(center, MouseButton.Left); window.MouseUp(center, MouseButton.Left); Pump();
    }
}
