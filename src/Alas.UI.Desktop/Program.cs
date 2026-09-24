using Avalonia;

namespace Alas.UI.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 桌面首选项落在 LocalApplicationData/AlasSharp（路径由系统目录推导，不硬编码用户目录）。
        App.ThemeStoreFactory = static () => new DesktopThemeStore();
        App.ResourceStoreFactory = static () => new DesktopResourceSelectionStore();
        App.BackendFactory = static () => new DirectCoreBackend();
        AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace()
            .StartWithClassicDesktopLifetime(args);
    }
}
