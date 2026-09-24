using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Alas.UI.Views;

namespace Alas.UI.Headless;

/// <summary>Only uses Avalonia's headless window backend; never connects to the OS desktop.</summary>
internal static class Program
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    public static async Task<int> Main(string[] args)
    {
        string output = Path.GetFullPath(args.Length > 0 ? args[0] : ".runtime/ui-headless");
        Directory.CreateDirectory(output);
        try
        {
            // Dispatch may complete inline on its own worker. Async disposal lets that worker
            // unwind instead of synchronously waiting for itself in IDisposable.Dispose().
            await using (var session = HeadlessUnitTestSession.StartNew(typeof(Program)))
                await session.Dispatch(() => Verify(output), CancellationToken.None);
            Console.WriteLine("PASS: Avalonia Headless layout, input, binding, theme, JSON feedback and virtualized logs; no device or visible window.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void Verify(string output)
    {
        var view = new MainView();
        var window = new Window { Width = 1280, Height = 820, Content = view };
        // Show attaches the visual tree to the in-memory HeadlessWindowImpl, not a native window.
        window.Show();
        try
        {
            Pump();
            Check(view.Model.IsOverview, "initial overview");
            Check(view.FindControl<Border>("Navigation")!.IsVisible, "wide navigation");
            Check(FontManager.Current.TryGetGlyphTypeface(new Typeface(view.FontFamily), out var font), "embedded font loads");
            Check(font!.FamilyName == "Noto Sans CJK SC", $"embedded font family (got {font.FamilyName})");
            Check("中文工作区 Alpha 123".All(c => font.CharacterToGlyphMap.TryGetGlyph(c, out var glyph) && glyph != 0),
                "embedded Chinese and Latin glyphs");
            Capture(window, output, "overview-light.png");
            Click(window, view, "ThemeButton");
            Check(view.Model.IsDark && Application.Current!.RequestedThemeVariant == ThemeVariant.Dark, "theme command and resources");
            Capture(window, output, "overview-dark.png");
            view.FindControl<PreviewChart>("TrendChart")!.BringIntoView();
            Capture(window, output, "chart-dark.png");
            Click(window, view, "SettingsButton");
            Check(view.Model.IsSettings, "navigation command");
            var name = view.FindControl<TextBox>("WorkspaceNameInput")!;
            name.Focus();
            name.SelectAll();
            window.KeyTextInput("中文工作区 Alpha 123");
            Pump();
            Check(view.Model.WorkspaceName == "中文工作区 Alpha 123", "Chinese text input updates shared model");
            var editor = view.FindControl<TextBox>("QueueEditor")!;
            editor.Focus();
            editor.SelectAll();
            window.KeyTextInput("{invalid");
            Click(window, view, "ValidateButton");
            Check(view.FindControl<TextBlock>("ValidationText")!.Text!.Contains("JSON 语法错误"), "invalid JSON feedback is bound");
            editor.Focus();
            editor.SelectAll();
            window.KeyTextInput("{\"tasks\":[]}");
            Click(window, view, "ValidateButton");
            Check(view.Model.ValidationMessage.Contains("不代表任务已运行"), "valid JSON does not claim execution");
            Capture(window, output, "settings-dark.png");
            Click(window, view, "LogsButton");
            var logs = view.FindControl<ListBox>("LogList")!;
            Check(logs.ItemCount == 200, "initial log binding");
            for (int i = 0; i < 21; i++) Click(window, view, "AddLogsButton");
            Check(logs.ItemCount == 2000, "log retention bound");
            logs.ScrollIntoView(view.Model.Logs[^1]);
            Pump();
            int realized = logs.GetVisualDescendants().OfType<ListBoxItem>().Count();
            Check(realized > 0 && realized < 100, "long logs are virtualized");
            Check(logs.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == view.Model.Logs[^1].Message), "last log can be reached");
            Capture(window, output, "logs-dark.png");
            window.Width = 390;
            window.Height = 844;
            Pump();
            Check(!view.FindControl<Border>("Navigation")!.IsVisible, "narrow sidebar hidden");
            Check(view.FindControl<StackPanel>("CompactNavigation")!.IsVisible, "narrow navigation available");
            Click(window, view, "CompactSettingsButton");
            Check(view.Model.IsSettings, "narrow settings navigation");
            Capture(window, output, "settings-narrow.png");
            Click(window, view, "CompactOverviewButton");
            Check(view.Model.IsOverview, "narrow overview navigation");
            foreach (var button in view.FindControl<StackPanel>("CompactNavigation")!.Children.OfType<Button>())
            {
                var position = button.TranslatePoint(default, window)!.Value;
                Check(position.X >= 0 && position.X + button.Bounds.Width <= window.ClientSize.Width,
                    "narrow navigation stays within viewport");
            }
            Capture(window, output, "overview-narrow.png");
            Click(window, view, "ThemeButton");
            Check(!view.Model.IsDark && Application.Current!.RequestedThemeVariant == ThemeVariant.Light, "theme switches back");
            window.Width = 1280;
            Pump();
            Check(view.FindControl<Border>("Navigation")!.IsVisible, "wide navigation restores after resize");
            Console.WriteLine($"PASS: 2,000 log rows, {realized} realized controls; wide and narrow frames rendered offscreen.");
        }
        finally { window.Close(); }
    }

    private static void Click(Window window, MainView view, string name)
    {
        var button = view.FindControl<Button>(name) ?? throw new Exception($"Missing button: {name}");
        button.BringIntoView();
        Pump();
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)
            ?? throw new Exception($"Button is detached: {name}");
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Pump();
    }

    private static void Capture(Window window, string output, string filename)
    {
        Pump();
        using var bitmap = window.CaptureRenderedFrame() ?? throw new Exception("No headless rendered frame");
        Check(bitmap.PixelSize.Width > 300 && bitmap.PixelSize.Height > 400, "frame dimensions");
        bitmap.Save(Path.Combine(output, filename), PngBitmapEncoderOptions.Default);
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Check(bool value, string label)
    {
        if (!value) throw new Exception("FAIL: " + label);
    }
}
