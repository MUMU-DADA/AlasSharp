using Avalonia;
using Alas.UI.EngineSettings;
using Alas.UI.Simulation;

namespace Alas.UI.Desktop;

internal static class Program
{
    private static readonly MemorySettingsDraftStore SessionDrafts = new();

    [STAThread]
    public static void Main(string[] args)
    {
        // 桌面首选项落在 LocalApplicationData/AlasSharp（路径由系统目录推导，不硬编码用户目录）。
        var options = UiLaunchOptions.Parse(args);
        App.ThemeStoreFactory = () => options.CreateThemeStore(static () => new DesktopThemeStore());
        App.ResourceStoreFactory = () => options.CreateResourceStore(static () => new DesktopResourceSelectionStore());
        App.SettingsDraftStoreFactory = () => options.CreateSettingsDraftStore(static () => SessionDrafts);
        App.BackendFactory = () => options.CreateBackend(static () => new DirectEngineBackend());
        AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace()
            .StartWithClassicDesktopLifetime(args);
    }
}
