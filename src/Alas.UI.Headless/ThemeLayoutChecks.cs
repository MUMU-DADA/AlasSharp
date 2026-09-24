using Alas.UI.Controls;
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

internal static class ThemeLayoutChecks
{
    public static void Run(string output)
    {
        var view = new MainView();
        var window = new Window { Width = 1280, Height = 820, Content = view };
        window.Show();
        try
        {
            view.Model.SelectInstance("demo-main");
            Pump();
            var group = view.Model.TaskGroups[0];
            group.IsExpanded = true;
            var sidebar = view.FindControl<SidebarView>("Sidebar")!;
            var nav = sidebar.FindControl<ItemsControl>("PrimaryNav")!;
            var page = view.FindControl<OverviewView>("OverviewPage")!;
            var resources = page.FindControl<ItemsControl>("ResourceCards")!;
            foreach (var theme in UiThemes.All)
            {
                window.Width = 1280;
                view.Model.ApplyTheme(ThemePreference.Default with { Theme = theme });
                Pump();
                bool legacy = theme is UiTheme.LegacyLight or UiTheme.LegacyDark;
                bool extreme = theme == UiTheme.Extreme;
                bool minimal = theme == UiTheme.Minimal;
                double sidebarWidth = legacy ? 192 : extreme ? 178 : minimal ? 240 : 232;
                double topbarHeight = legacy ? 56 : extreme ? 44 : minimal ? 69 : 41.5;
                var sidebarPanel = view.FindControl<Border>("SidebarPanel")!;
                var topbar = view.FindControl<Border>("TopbarPanel")!;
                var rail = view.FindControl<Border>("RailPanel")!;
                Check(Near(sidebarPanel.Bounds.Width, sidebarWidth), theme + " upstream sidebar width");
                Check(Near(topbar.Bounds.Height, topbarHeight), theme + " upstream topbar height");
                Check(ReferenceEquals(view.Model.TaskGroups[0], group) && group.IsExpanded,
                    theme + " theme change preserves the expanded task group");
                var navButton = nav.GetVisualDescendants().OfType<Button>().First();
                Check(Near(navButton.Bounds.Height, extreme ? 30 : minimal ? 42 : 44),
                    theme + " upstream navigation density: " + navButton.Bounds.Height);
                var cards = resources.GetVisualDescendants().OfType<CardGrid>().Single();
                Check(cards.Children.Count == 4 && Near(cards.Children[0].Bounds.Width, (cards.Bounds.Width - 24) / 4),
                    theme + " four resource columns follow the available content width");
                if (legacy)
                {
                    Check(Near(topbar.Bounds.Width, 1280) && Near(sidebarPanel.Bounds.Top, 56), "legacy full-width topbar");
                    Check(ReferenceEquals(view.FindControl<TopBarView>("Topbar")!.FindControl<StackPanel>("Breadcrumb")!.Parent,
                        view.FindControl<Panel>("LegacyNavigationHost")), "legacy navigation is in its own content row");
                    double railRight = rail.TranslatePoint(new Point(rail.Bounds.Width, 0), window)!.Value.X;
                    double cardsLeft = resources.TranslatePoint(default, window)!.Value.X;
                    Check(cardsLeft >= railRight + 13, "legacy scheduler column does not overlap resource cards");
                }
                using (var frame = window.CaptureRenderedFrame())
                    frame!.Save(Path.Combine(output, $"integrated-{UiThemes.ToId(theme)}-1280.png"), PngBitmapEncoderOptions.Default);

                window.Width = 390;
                view.Model.IsDrawerOpen = true;
                Pump();
                Check(Near(sidebarPanel.Bounds.Width, 232), theme + " narrow drawer stays usable");
                Check(view.Model.MainPadding.Left < 30, theme + " narrow content has no desktop rail offset");
                Click(window, sidebar.FindControl<Button>("DrawerCloseButton")!);
                Check(!view.Model.IsDrawerOpen, theme + " real pointer can close narrow drawer");
                Check(Near(cards.Children[0].Bounds.Width, (cards.Bounds.Width - 8) / 2), theme + " two narrow resource columns");
            }

            window.Width = 1280;
            view.Model.ApplyTheme(ThemePreference.Default);
            Pump();
            Click(window, sidebar.FindControl<Button>("TaskSearchButton")!);
            var search = sidebar.FindControl<TextBox>("TaskSearchBox")!;
            Click(window, search);
            window.KeyTextInput("指挥喵"); Pump();
            Check(view.Model.TaskGroups.SelectMany(item => item.Tasks).Any()
                && view.Model.TaskGroups.SelectMany(item => item.Tasks).All(item => item.Matches("指挥喵")), "real Chinese input filters task navigation");
            Click(window, sidebar.FindControl<Button>("TaskSearchButton")!);
            Check(view.Model.TaskSearchText.Length == 0 && view.Model.TaskGroups.Count > 1, "closing search restores task navigation");
            view.Model.SelectNavCommand.Execute("home"); Pump();
            foreach (var theme in new[] { UiTheme.LegacyLight, UiTheme.Extreme, UiTheme.Light })
            {
                view.Model.ApplyTheme(ThemePreference.Default with { Theme = theme }); Pump();
                Check(view.Model.IsHomeActive && !view.Model.IsRailVisible, "global routes do not acquire an instance rail");
            }
        }
        finally { window.Close(); Pump(); }

        var sparse = new CardGrid { Columns = 4, ItemGap = 8, Children = { new Border { Height = 40 } } };
        sparse.Measure(new Size(424, 100)); sparse.Arrange(new Rect(0, 0, 424, 100));
        Check(Near(sparse.Children[0].Bounds.Width, 100), "one selected resource retains the four-column track width");
        Console.WriteLine("PASS: six theme layouts, legacy content rail, narrow input, task search and fixed resource columns");
    }

    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) <= 1;
    private static void Check(bool ok, string message)
    { if (!ok) throw new InvalidOperationException("Theme layout: " + message); }
    private static void Pump()
    { for (int i = 0; i < 4; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } }
    private static void Click(Window window, Control control)
    {
        control.BringIntoView(); Pump();
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        Check(new Rect(window.ClientSize).Contains(center), control.Name + " is in the viewport");
        window.MouseMove(center); window.MouseDown(center, MouseButton.Left); window.MouseUp(center, MouseButton.Left); Pump();
    }
}
