using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Alas.UI.ViewModels;

namespace Alas.UI.Views;

public partial class MainView : UserControl
{
    public WorkspaceViewModel Model { get; } = new();

    public MainView()
    {
        InitializeComponent();
        DataContext = Model;
        Model.PropertyChanged += UpdateTheme;
        SizeChanged += (_, _) =>
        {
            bool compact = Bounds.Width < 720;
            Navigation.IsVisible = !compact;
            Shell.ColumnDefinitions[0].Width = new GridLength(compact ? 0 : 220);
            CompactNavigation.IsVisible = compact;
        };
    }

    private void UpdateTheme(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(WorkspaceViewModel.IsDark) && Application.Current is { } app)
            app.RequestedThemeVariant = Model.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
    }
}
