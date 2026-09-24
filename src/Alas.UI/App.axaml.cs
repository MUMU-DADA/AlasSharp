using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Alas.UI.Theming;
using Alas.UI.ViewModels;
using Alas.UI.Views;

namespace Alas.UI;

public partial class App : Application
{
    /// <summary>
    /// 平台主题偏好存储。桌面与浏览器各自提供落盘实现；未提供时用内存实现，
    /// 保证 Headless 与离线预览不会因为缺少平台接口而失败。
    /// </summary>
    public static Func<IThemeStore>? ThemeStoreFactory { get; set; }

    /// <summary>
    /// 平台后端组合点。桌面传入直接调用 Alas.Core 的实现；浏览器传入 HTTP
    /// 传输适配器。共享视图只依赖能力接口，不把传输方式写进页面。
    /// </summary>
    public static Func<IAlasUiBackend>? BackendFactory { get; set; }
    public static Func<Overview.IResourceSelectionStore>? ResourceStoreFactory { get; set; }

    private IAlasUiBackend? _backend;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var store = ThemeStoreFactory?.Invoke() ?? new MemoryThemeStore();
        _backend = BackendFactory?.Invoke();
        var view = new MainView(store, _backend, resourceStore: ResourceStoreFactory?.Invoke());
        // 启动即恢复持久化偏好（上游在首次渲染前读 localStorage 并写 <html> 属性）。
        view.Model.Theme.ApplyStored();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new Window
            {
                Title = _backend?.IsSimulation == true ? "AlasSharp · UI 隔离模式" : "AlasSharp",
                Width = 1280, Height = 820,
                MinWidth = 380, MinHeight = 520, Content = view,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
            };
            desktop.Exit += (_, _) => _backend?.Dispose();
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime browser)
            browser.MainView = view;
        base.OnFrameworkInitializationCompleted();
    }
}
