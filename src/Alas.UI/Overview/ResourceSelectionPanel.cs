using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Alas.UI.Overview;

/// <summary>Edits the overview's per-instance resource selection directly.</summary>
public sealed class ResourceSelectionPanel : UserControl
{
    private readonly StackPanel _selected = new() { Name = "CardSettingsSelected", Spacing = 4 };
    private readonly StackPanel _available = new() { Name = "CardSettingsAvailable", Spacing = 4 };
    private readonly TextBlock _empty = new() { Name = "CardSettingsEmpty", Text = "尚未选择资源卡片。" };
    private readonly TextBlock _allAdded = new() { Name = "CardSettingsAllAdded", Text = "所有可用资源都已经添加了" };
    private readonly TextBlock _error = new() { Name = "CardSettingsError", TextWrapping = TextWrapping.Wrap };
    private readonly Button _restore = new() { Name = "CardSettingsRestoreButton", Content = "恢复默认", Padding = new Thickness(8, 3) };
    private ResourceSelection? _selection;
    private bool _attached;
    private bool _refreshQueued;

    public ResourceSelectionPanel()
    {
        _restore.Click += (_, _) => _selection?.RestoreDefault();
        Content = new StackPanel
        {
            Name = "CardSettingsPanel", Spacing = 8, MinWidth = 270, MaxWidth = 360,
            Children =
            {
                new TextBlock { Name = "CardSettingsTitle", Text = "资源卡片设置", FontSize = 14,
                    FontWeight = FontWeight.SemiBold },
                _restore, _error, _empty, _selected,
                new TextBlock { Text = "可添加" }, _allAdded, _available,
            },
        };
        AttachedToVisualTree += (_, _) =>
        {
            _attached = true;
            Subscribe();
            Refresh();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            Unsubscribe();
            _attached = false;
            _refreshQueued = false;
        };
        Refresh();
    }

    public ResourceSelection? Selection
    {
        get => _selection;
        set
        {
            if (ReferenceEquals(_selection, value)) return;
            Unsubscribe();
            _selection = value;
            Subscribe();
            Refresh();
        }
    }

    private void Subscribe()
    {
        if (_attached && _selection is not null)
            _selection.PropertyChanged += OnSelectionChanged;
    }

    private void Unsubscribe()
    {
        if (_attached && _selection is not null)
            _selection.PropertyChanged -= OnSelectionChanged;
    }

    private void OnSelectionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is null or nameof(ResourceSelection.Selected) or nameof(ResourceSelection.Available))
        {
            if (_refreshQueued) return;
            _refreshQueued = true;
            Dispatcher.UIThread.Post(() =>
            {
                if (!_refreshQueued) return;
                _refreshQueued = false;
                if (_attached) Refresh();
            }, DispatcherPriority.Loaded);
        }
        else if (args.PropertyName == nameof(ResourceSelection.StorageError))
        {
            _error.Text = _selection?.StorageError ?? string.Empty;
            _error.IsVisible = !string.IsNullOrEmpty(_error.Text);
        }
    }

    private void Refresh()
    {
        var selection = _selection;
        _selected.Children.Clear();
        _available.Children.Clear();
        if (selection is not null)
        {
            foreach (var choice in selection.Selected)
                _selected.Children.Add(SelectedRow(selection, choice));
            foreach (var choice in selection.Available)
                _available.Children.Add(AvailableRow(selection, choice));
        }
        _restore.IsEnabled = selection is not null;
        _empty.IsVisible = selection is null || selection.Selected.Count == 0;
        _allAdded.IsVisible = selection is not null && selection.Available.Count == 0;
        _error.Text = selection?.StorageError ?? string.Empty;
        _error.IsVisible = !string.IsNullOrEmpty(_error.Text);
    }

    private Control SelectedRow(ResourceSelection selection, ResourceChoice choice)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), Margin = new Thickness(0, 2) };
        row.Children.Add(new TextBlock { Text = choice.Label, VerticalAlignment = VerticalAlignment.Center });
        var index = selection.Keys.ToList().IndexOf(choice.Key);
        var up = ActionButton("CardSettingsMoveUp", "↑", "上移" + choice.Label,
            () => Move(selection, choice.Key, -1));
        up.IsEnabled = index > 0;
        Grid.SetColumn(up, 1);
        row.Children.Add(up);
        var down = ActionButton("CardSettingsMoveDown", "↓", "下移" + choice.Label,
            () => Move(selection, choice.Key, 1));
        down.IsEnabled = index < selection.Keys.Count - 1;
        Grid.SetColumn(down, 2);
        row.Children.Add(down);
        var remove = ActionButton("CardSettingsRemove", "×", "移除" + choice.Label,
            () => { if (ReferenceEquals(_selection, selection)) selection.Remove(choice.Key); });
        Grid.SetColumn(remove, 3);
        row.Children.Add(remove);
        return row;
    }

    private Control AvailableRow(ResourceSelection selection, ResourceChoice choice)
    {
        var button = ActionButton("CardSettingsAdd", "+ " + choice.Label, "添加" + choice.Label,
            () => { if (ReferenceEquals(_selection, selection)) selection.Add(choice.Key); });
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        return button;
    }

    private void Move(ResourceSelection selection, string key, int delta)
    {
        if (!ReferenceEquals(_selection, selection)) return;
        var keys = selection.Keys;
        var index = keys.ToList().IndexOf(key);
        var target = index + delta;
        if (index >= 0 && target >= 0 && target < keys.Count)
            selection.Move(key, keys[target]);
    }

    private static Button ActionButton(string name, string label, string accessibleName, Action action)
    {
        var button = new Button { Name = name, Content = label, Padding = new Thickness(8, 3), Margin = new Thickness(2, 0) };
        Avalonia.Automation.AutomationProperties.SetName(button, accessibleName);
        ToolTip.SetTip(button, accessibleName);
        button.Click += (_, _) => action();
        return button;
    }
}
