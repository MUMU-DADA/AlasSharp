using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Alas.UI.Views;

namespace Alas.UI;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var view = new MainView();
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
