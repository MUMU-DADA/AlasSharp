using Avalonia;
using Alas.UI.DeploySettings;
using Alas.UI.Simulation;

namespace Alas.UI.Desktop;

internal static class Program
{
    private static readonly MemoryDeployDraftStore SessionDrafts = new();

    [STAThread]
    public static void Main(string[] args)
    {
        // 桌面首选项落在 LocalApplicationData/AlasSharp（路径由系统目录推导，不硬编码用户目录）。
        var options = UiLaunchOptions.Parse(args);
        App.ThemeStoreFactory = () => options.CreateThemeStore(static () => new DesktopThemeStore());
        App.ResourceStoreFactory = () => options.CreateResourceStore(static () => new DesktopResourceSelectionStore());
        App.DeployDraftStoreFactory = () => options.CreateDeployDraftStore(static () => SessionDrafts);
        App.BackendFactory = () => options.CreateBackend(static () => new DirectCoreBackend());
        AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace()
            .StartWithClassicDesktopLifetime(args);
    }
}
