using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Alas.UI.Theming;
using Alas.UI.Views;

namespace Alas.UI;

public partial class App : Application
{
    /// <summary>
    /// 平台主题偏好存储。桌面与浏览器各自提供落盘实现；未提供时用内存实现，
    /// 保证 Headless 与离线预览不会因为缺少平台接口而失败。
    /// </summary>
    public static Func<IThemeStore>? ThemeStoreFactory { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var store = ThemeStoreFactory?.Invoke() ?? new MemoryThemeStore();
        var view = new MainView(store);
        // 启动即恢复持久化偏好（上游在首次渲染前读 localStorage 并写 <html> 属性）。
        view.Model.Theme.ApplyStored();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new Window
            {
                Title = "AlasSharp · 界面预览", Width = 1280, Height = 820,
                MinWidth = 380, MinHeight = 520, Content = view,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
            };
        else if (ApplicationLifetime is ISingleViewApplicationLifetime browser)
            browser.MainView = view;
        base.OnFrameworkInitializationCompleted();
    }
}
