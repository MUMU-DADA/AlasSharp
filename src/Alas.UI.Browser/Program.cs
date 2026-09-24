using Avalonia;
using Avalonia.Browser;
using Alas.UI.Simulation;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("browser")]

namespace Alas.UI.Browser;

internal static class Program
{
    private static Task Main(string[] args)
    {
        // 浏览器首选项写本源 localStorage（最小 JS 桥），与上游 azurpilot.* 同一存储域。
        var options = UiLaunchOptions.Parse(args);
        App.ThemeStoreFactory = () => options.CreateThemeStore(static () => new BrowserThemeStore());
        App.ResourceStoreFactory = () => options.CreateResourceStore(static () => new BrowserResourceSelectionStore());
        App.BackendFactory = () => options.CreateBackend(static () => new BrowserControlBackend());
        return AppBuilder.Configure<App>()
            .StartBrowserAppAsync("out");
    }
}
