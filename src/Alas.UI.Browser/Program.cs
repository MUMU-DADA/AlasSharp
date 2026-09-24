using Avalonia;
using Avalonia.Browser;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("browser")]

namespace Alas.UI.Browser;

internal static class Program
{
    private static Task Main(string[] args) => AppBuilder.Configure<App>()
        .StartBrowserAppAsync("out");
}
