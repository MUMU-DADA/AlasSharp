using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Alas.UI.Overview;

/// <summary>
/// 概览页「资源卡片设置」需要的能力（上游 <c>resource.settings</c>：选择显示哪些卡片、以及它们的顺序）。
/// 与其他页面一致：只依赖页面局部接口，不触碰共享外壳接线与后端实现。
/// </summary>
public interface IResourceCardSettings
{
    /// <summary>读取可选卡片与当前启用顺序；失败时返回带 Error 的结果而不抛异常。</summary>
    Task<ResourceCardSettingsSnapshot> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>保存启用清单（顺序即显示顺序）。未接能力时必须返回带 Error 的结果。</summary>
    Task<ResourceCardSettingsSnapshot> SaveAsync(IReadOnlyList<string> enabledKeys, CancellationToken cancellationToken = default);
}

/// <summary>一张可选资源卡：Key 为持久化标识，Label 为展示名。</summary>
public sealed record ResourceCardOption(string Key, string Label);

/// <summary>卡片设置快照：全部可选项 + 当前启用顺序 + 错误。</summary>
public sealed record ResourceCardSettingsSnapshot(
    IReadOnlyList<ResourceCardOption> Options,
    IReadOnlyList<string> EnabledKeys,
    string? Error = null)
{
    public bool IsAvailable => string.IsNullOrEmpty(Error);
}

/// <summary>未接能力时的默认实现：如实报告不可用，不伪造卡片清单。</summary>
public sealed class DisconnectedResourceCardSettings : IResourceCardSettings
{
    public static DisconnectedResourceCardSettings Instance { get; } = new();

    public const string Notice = "未连接 Alas.Core：资源卡片清单由 Core 提供，当前不可用。";

    private static ResourceCardSettingsSnapshot Off =>
        new(Array.Empty<ResourceCardOption>(), Array.Empty<string>(), Notice);

    public Task<ResourceCardSettingsSnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Off);

    public Task<ResourceCardSettingsSnapshot> SaveAsync(IReadOnlyList<string> enabledKeys, CancellationToken cancellationToken = default) =>
        Task.FromResult(Off);
}

/// <summary>
/// 「资源卡片设置」面板（上游 resource.settings + resource.cardsHint）：
/// 列出可选卡片并允许启用/停用与排序；未接能力时显示真实空/错误态，**不显示任何假卡片**。
/// 离线遗留面板，仅用于旧接口回归和性能参照。生产 Flyout 使用同一份 ResourceSelection 的即时偏好路径。
/// </summary>
public sealed class ResourceCardSettingsPanel : UserControl
{
    private readonly ResourceCardSettingsViewModel _model;

    public ResourceCardSettingsPanel()
        : this(DisconnectedResourceCardSettings.Instance)
    {
    }

    public ResourceCardSettingsPanel(IResourceCardSettings backend)
    {
        _model = new ResourceCardSettingsViewModel(backend);
        DataContext = _model;
        Content = Build();
        _ = _model.RefreshAsync();
    }

    public ResourceCardSettingsViewModel Model => _model;

    /// <summary>
    /// 上游 ResourceCards.tsx 的常量：`defaultResourceKeys = ['Oil','Coin','Gem','Cube']`
    /// （石油 / 物资 / 钻石 / 心智魔方）——「恢复默认」就是把这四张重新选上。
    /// 这里逐字对应上游常量，不自行发明默认集合。
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultKeys = new[] { "Oil", "Coin", "Gem", "Cube" };

    private Control Build()
    {
        var title = new TextBlock
        {
            Name = "CardSettingsTitle", Text = "资源卡片设置", FontSize = 14, FontWeight = FontWeight.SemiBold,
        };
        var hint = new TextBlock
        {
            Name = "CardSettingsHint",
            Text = "勾选显示的卡片，使用上移或下移调整顺序后保存。",
            FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap,
        };
        var error = new TextBlock
        {
            Name = "CardSettingsError", Text = _model.Error, FontSize = 11,
            TextWrapping = TextWrapping.Wrap, IsVisible = _model.HasError,
        };
        var list = new ItemsControl
        {
            Name = "CardSettingsList",
            ItemsSource = _model.Options,
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<ResourceCardChoice>((choice, _) =>
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var toggle = new CheckBox
                {
                    Name = "CardSettingsToggle", Content = choice.Label, FontSize = 12,
                    IsEnabled = _model.CanEdit,
                };
                // 单向绑定负责"模型 → 界面"：否则模型侧改动（「恢复默认」、保存后重建选择）
                // 不会反映到勾选框——这正是新增断言当场查出的缺陷。回写在下面的 PropertyChanged 里。
                toggle.Bind(CheckBox.IsCheckedProperty, new Avalonia.Data.Binding(nameof(ResourceCardChoice.IsEnabled))
                {
                    Mode = Avalonia.Data.BindingMode.OneWay,
                });
                // 上游 resource.remove =「移除{label}」：卡片的启停开关对读屏用户要说明"移除哪一张"，
                // 只读标签名不足以表达这个动作。
                Avalonia.Automation.AutomationProperties.SetName(toggle, "移除" + choice.Label);
                // 与设置页同一策略：走属性变更管线（离屏下程序化改动也能回写）。
                toggle.PropertyChanged += (_, args) =>
                {
                    if (args.Property != CheckBox.IsCheckedProperty) return;
                    // 容器回收保护：列表回收行时框架会先改 IsChecked、此时 DataContext 已是另一张卡，
                    // 那一笔回写会把启用集合改错（排序断言实测到 `got Coin`）。只认 DataContext
                    // 仍是本行的改动——那才是用户操作或测试里的程序化改动。
                    if (!ReferenceEquals(toggle.DataContext, choice)) return;
                    _model.SetEnabled(choice.Key, toggle.IsChecked == true);
                };
                row.Children.Add(toggle);
                // 排序：上游是拖拽；这里提供等价的上移/下移按钮（可键盘操作、可离屏断言）。
                row.Children.Add(new Button
                {
                    Name = "CardSettingsMoveUp", Content = "上移", Padding = new Thickness(8, 3), FontSize = 11,
                    IsEnabled = _model.CanEdit, Command = new CardCommand(_ => _model.MoveUp(choice.Key)),
                });
                row.Children.Add(new Button
                {
                    Name = "CardSettingsMoveDown", Content = "下移", Padding = new Thickness(8, 3), FontSize = 11,
                    IsEnabled = _model.CanEdit, Command = new CardCommand(_ => _model.MoveDown(choice.Key)),
                });
                return row;
            }, supportsRecycling: true),
        };
        var empty = new TextBlock
        {
            Name = "CardSettingsEmpty", Text = "没有可配置的资源卡片。", FontSize = 11,
            IsVisible = _model.IsEmpty,
        };
        // 上游 resource.restoreDefault：设置区标题右侧的「恢复默认」，把选择重置为上游常量那四张。
        var restore = new Button
        {
            Name = "CardSettingsRestoreButton", Content = "恢复默认", FontSize = 11,
            Padding = new Thickness(8, 3), HorizontalAlignment = HorizontalAlignment.Left,
            IsEnabled = _model.CanEdit, Command = _model.RestoreDefaultCommand,
        };
        var allAdded = new TextBlock
        {
            Name = "CardSettingsAllAdded", Text = "所有可用资源都已经添加了", FontSize = 11,
            IsVisible = _model.AllAdded,
        };
        var save = new Button
        {
            Name = "CardSettingsSaveButton", Content = "保存", Padding = new Thickness(12, 7),
            HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = _model.CanSave,
            Command = _model.SaveCommand,
        };
        var saved = new TextBlock
        {
            Name = "CardSettingsSaved", Text = "已保存卡片设置", FontSize = 11,
            IsVisible = _model.Saved,
        };

        _model.PropertyChanged += (_, args) =>
        {
            switch (args.PropertyName)
            {
                case nameof(ResourceCardSettingsViewModel.Error):
                    error.Text = _model.Error;
                    error.IsVisible = _model.HasError;
                    break;
                case nameof(ResourceCardSettingsViewModel.IsEmpty):
                    empty.IsVisible = _model.IsEmpty;
                    break;
                // 「所有可用资源都已经添加了」也要跟着选择变化——这条提示是本轮新加的，
                // 起初只在创建时赋了一次值，被恢复默认的断言当场查出（同一个"只赋值一次"的老毛病）。
                case nameof(ResourceCardSettingsViewModel.AllAdded):
                    allAdded.IsVisible = _model.AllAdded;
                    break;
                case nameof(ResourceCardSettingsViewModel.CanSave):
                    save.IsEnabled = _model.CanSave;
                    break;
                case nameof(ResourceCardSettingsViewModel.Saved):
                    saved.IsVisible = _model.Saved;
                    break;
            }
        };

        return new StackPanel
        {
            Name = "CardSettingsPanel", Spacing = 8,
            Children = { title, hint, restore, error, empty, allAdded, list, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { save, saved } } },
        };
    }
}

/// <summary>面板状态：可选项、启用集合、错误与保存结果。</summary>
public sealed class ResourceCardSettingsViewModel : INotifyPropertyChanged
{
    private readonly IResourceCardSettings _backend;
    private readonly HashSet<string> _enabled = new();
    private string _error = string.Empty;
    private bool _saved;

    public ResourceCardSettingsViewModel(IResourceCardSettings backend)
    {
        _backend = backend;
        SaveCommand = new CardCommand(_ => _ = SaveAsync());
        RestoreDefaultCommand = new CardCommand(_ => RestoreDefault());
    }

    public ObservableCollection<ResourceCardChoice> Options { get; } = new();

    public ICommand SaveCommand { get; }

    /// <summary>「恢复默认」：把选择重置为上游常量 <see cref="ResourceCardSettingsPanel.DefaultKeys"/>。</summary>
    public ICommand RestoreDefaultCommand { get; }

    /// <summary>上游 resource.allAdded：所有可用卡片都已被选中时提示（没有"还能加什么"的余地）。</summary>
    public bool AllAdded => CanEdit && Options.Count > 0 && Options.All(choice => _enabled.Contains(choice.Key));

    public string Error { get => _error; private set => SetField(ref _error, value); }
    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool IsEmpty => !HasError && Options.Count == 0;

    /// <summary>未接能力时不可编辑（不给假的可点开关）。</summary>
    public bool CanEdit => !HasError && Options.Count > 0;

    public bool CanSave => CanEdit;

    public bool Saved { get => _saved; private set => SetField(ref _saved, value); }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Apply(await _backend.ReadAsync(cancellationToken));
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            Apply(new ResourceCardSettingsSnapshot(Array.Empty<ResourceCardOption>(), Array.Empty<string>(), failure.Message));
        }
    }

    /// <summary>启用/停用一张卡片；当前组件保存前只改本地状态，上游即时保存尚待接通。</summary>
    public void SetEnabled(string key, bool enabled)
    {
        if (!CanEdit || string.IsNullOrEmpty(key)) return;
        if (enabled) _enabled.Add(key); else _enabled.Remove(key);
        foreach (var choice in Options.Where(choice => choice.Key == key)) choice.IsEnabled = enabled;
        Saved = false;
        Notify(nameof(AllAdded));
    }

    /// <summary>恢复默认选择（上游 ResourceCards 的 defaultResourceKeys），仍由用户按保存提交。</summary>
    public void RestoreDefault()
    {
        if (!CanEdit) return;
        _enabled.Clear();
        foreach (var key in ResourceCardSettingsPanel.DefaultKeys) _enabled.Add(key);
        foreach (var choice in Options) choice.IsEnabled = _enabled.Contains(choice.Key);
        Saved = false;
        Notify(nameof(EnabledKeys));
        Notify(nameof(AllAdded));
    }

    /// <summary>当前启用顺序（按可选项顺序，即显示顺序）。</summary>
    public IReadOnlyList<string> EnabledKeys => Options.Where(choice => _enabled.Contains(choice.Key))
        .Select(choice => choice.Key).ToList();

    /// <summary>
    /// 上移一张卡片。上游用拖拽排序；离屏与键盘场景下拖拽不可达，因此提供**等价的可访问操作**：
    /// 顺序即显示顺序，移动后本地状态即为待保存的新顺序。拖拽手势本体仍未实现（已在提交说明标注）。
    /// </summary>
    public bool MoveUp(string key) => Move(key, -1);

    /// <summary>下移一张卡片（与上移同一实现，见 <see cref="MoveUp"/> 的说明）。</summary>
    public bool MoveDown(string key) => Move(key, 1);

    private bool Move(string key, int delta)
    {
        if (!CanEdit || string.IsNullOrEmpty(key)) return false;
        var index = IndexOf(key);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Options.Count) return false;
        Options.Move(index, target);
        Notify(nameof(EnabledKeys));
        Saved = false;
        return true;
    }

    private int IndexOf(string key)
    {
        for (var index = 0; index < Options.Count; index++)
        {
            if (Options[index].Key == key) return index;
        }
        return -1;
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (!CanSave) return;
        try
        {
            var snapshot = await _backend.SaveAsync(EnabledKeys, cancellationToken);
            if (!string.IsNullOrEmpty(snapshot.Error))
            {
                Error = snapshot.Error;
                Saved = false;
                return;
            }
            // 保存成功后**不重建选项顺序**：后端确认的正是本地这份清单与顺序，
            // 若用返回快照重建，会把用户刚排好的顺序按快照的原始顺序覆盖回去。
            Error = snapshot.Error ?? string.Empty;
            Saved = true;
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            Error = failure.Message;
            Saved = false;
        }
    }

    private void Apply(ResourceCardSettingsSnapshot snapshot)
    {
        Options.Clear();
        _enabled.Clear();
        foreach (var option in snapshot.Options) Options.Add(new ResourceCardChoice(option.Key, option.Label));
        foreach (var key in snapshot.EnabledKeys)
        {
            _enabled.Add(key);
            foreach (var choice in Options.Where(choice => choice.Key == key)) choice.IsEnabled = true;
        }
        Error = snapshot.Error ?? string.Empty;
        Saved = false;
        Notify(nameof(IsEmpty));
        Notify(nameof(CanEdit));
        Notify(nameof(CanSave));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(propertyName!);
        return true;
    }
}

/// <summary>面板里的一张可选卡片（可切换启用状态）。</summary>
public sealed class ResourceCardChoice(string key, string label) : INotifyPropertyChanged
{
    private bool _isEnabled;

    public string Key { get; } = key;
    public string Label { get; } = label;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed class CardCommand(Action<object?> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute(parameter);
}
