using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;

namespace Alas.UI.TaskEditor;

/// <summary>
/// Shared native/WASM task form. Every control is generated from upstream metadata, with no runtime
/// reflection bindings. The host supplies a model/backend and may place report/log panels below it.
/// </summary>
public sealed class TaskEditorView : UserControl, IDisposable
{
    private TaskEditorViewModel _model = new();
    private readonly StackPanel _cards = new() { Spacing = 16 };
    private readonly StackPanel _navigation = new() { Spacing = 5 };
    private readonly Grid _layout = new();
    private readonly List<Action> _detach = [];
    private readonly List<Action<bool>> _reflow = [];
    private readonly List<Action> _refreshFields = [];
    private readonly List<(TaskFieldGroup Group, Border Card, Button Link, List<(TaskFieldViewModel Field, Control Row)> Rows)> _groups = [];
    private readonly TextBlock _title = Text("任务设置", 26, FontWeight.Bold);
    private readonly TextBlock _status = Text("", 12);
    private readonly TextBlock _error = Text("", 13);
    private readonly TextBlock _message = Text("", 12);
    private readonly TextBlock _empty = Text("此任务暂无可显示的配置", 14);
    private readonly TextBox _search = new() { Name = "TaskConfigSearch", PlaceholderText = "搜索配置名称、帮助或参数", MinWidth = 120 };
    private readonly Button _save = Button("保存修改", "TaskConfigSave", primary: true);
    private readonly Button _discard = Button("放弃修改", "TaskConfigDiscard");
    private readonly Button _run = Button("运行任务", "TaskConfigRun");
    private readonly Button _confirm = Button("确认运行", "TaskConfigConfirmRun", primary: true);
    private readonly Button _cancel = Button("取消", "TaskConfigCancelRun");
    private readonly Border _overlay;
    private readonly StackPanel _main;
    private bool _legacyLayout;
    private bool _compactLayout;
    private bool? _appliedNarrow;
    private bool? _appliedLegacy;

    public TaskEditorView()
    {
        Resource(this, ForegroundProperty, "AlasTextBrush");
        Resource(_error, TextBlock.ForegroundProperty, "AlasDangerBrush");
        Resource(_status, TextBlock.ForegroundProperty, "AlasMutedBrush");
        Resource(_message, TextBlock.ForegroundProperty, "AlasSuccessBrush");
        _search.TextChanged += (_, _) => { if (_model.Search != _search.Text) _model.Search = _search.Text ?? ""; };
        AutomationProperties.SetName(_search, "搜索配置");
        _save.Click += async (_, _) => await _model.SaveAsync();
        _discard.Click += (_, _) => _model.Discard();
        _run.Click += (_, _) => _model.RequestRun();
        _confirm.Click += async (_, _) => await _model.ConfirmRunAsync();
        _cancel.Click += (_, _) => _model.CancelRun();
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var control in new Control[] { _save, _discard, _run }) { control.Margin = new Thickness(0, 0, 8, 8); actions.Children.Add(control); }
        _layout.Children.Add(_navigation); _layout.Children.Add(_cards);
        _main = new StackPanel { Spacing = 14, Children = { _title, _search, actions, _status, _error, _message, _layout, _empty } };
        var warning = Text("运行任务会操作已连接的游戏设备。未保存的修改会先提交；保存失败时不会启动任务。", 14);
        var confirmActions = new WrapPanel { Orientation = Orientation.Horizontal, Children = { _confirm, _cancel } };
        _confirm.Margin = new Thickness(0, 0, 8, 8);
        var dialog = Card(new StackPanel { Spacing = 16, Children = { Text("确认运行任务", 20, FontWeight.Bold), warning, confirmActions } });
        dialog.MaxWidth = 460; dialog.Margin = new Thickness(16); dialog.HorizontalAlignment = HorizontalAlignment.Center;
        dialog.VerticalAlignment = VerticalAlignment.Center;
        _overlay = new Border { Name = "TaskConfigConfirmation", Child = dialog, IsVisible = false, Focusable = true };
        Resource(_overlay, Border.BackgroundProperty, "AlasScrimBrush");
        KeyboardNavigation.SetTabNavigation(_overlay, KeyboardNavigationMode.Cycle);
        Content = new Grid { Children = { _main, _overlay } };
        _model.PropertyChanged += OnModelChanged;
        SizeChanged += (_, _) => Reflow();
        DataContextChanged += (_, _) => { if (DataContext is TaskEditorViewModel value && value != _model) Model = value; };
        BuildGroups();
    }

    public TaskEditorViewModel Model
    {
        get => _model;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (_model == value) return;
            _model.PropertyChanged -= OnModelChanged;
            _model = value;
            _model.PropertyChanged += OnModelChanged;
            BuildGroups();
        }
    }
    // Theme layout is selected by the host; brushes and radii remain dynamic resources.
    public bool LegacyLayout { get => _legacyLayout; set { _legacyLayout = value; Reflow(); RefreshState(); } }
    public bool CompactLayout { get => _compactLayout; set { _compactLayout = value; RefreshState(); } }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(TaskEditorViewModel.Groups))
        {
            BuildGroups();
            return;
        }

        if (args.PropertyName == nameof(TaskEditorViewModel.Search))
        {
            RefreshSearch();
            return;
        }

        if (args.PropertyName == nameof(TaskEditorViewModel.HasChanges))
        {
            // A field already refreshed its own editor and row state. Only the action/status
            // chrome depends on the aggregate dirty flag; avoid walking every field delegate.
            RefreshChrome();
            return;
        }

        // Field controls already subscribe to their own model. Aggregate changes only affect
        // action/status chrome. StateVersion marks operations that also require global field
        // refreshes (for example IsBusy changes that gate script actions).
        RefreshState();
    }
    private void BuildGroups()
    {
        foreach (var detach in _detach) detach();
        _detach.Clear(); _refreshFields.Clear(); _reflow.Clear(); _groups.Clear(); _cards.Children.Clear(); _navigation.Children.Clear();
        foreach (var group in _model.Groups)
        {
            var content = new StackPanel { Spacing = 0 };
            var heading = Text(group.Label, 16, FontWeight.SemiBold);
            heading.Margin = new Thickness(0, 0, 0, 14);
            content.Children.Add(heading);
            if (group.Help.Length > 0) { var help = Text(group.Help, 12); help.Margin = new Thickness(0, 0, 0, 14); content.Children.Add(help); }
            var rows = new List<(TaskFieldViewModel, Control)>();
            foreach (var field in group.Fields)
            {
                var row = CreateField(field); content.Children.Add(row); rows.Add((field, row));
            }
            var card = Card(content);
            card.Name = "ConfigGroup_" + group.Key;
            _cards.Children.Add(card);
            var link = Button(group.Label, "ConfigJump_" + group.Key);
            link.HorizontalAlignment = HorizontalAlignment.Stretch;
            link.HorizontalContentAlignment = HorizontalAlignment.Left;
            link.Click += (_, _) => card.BringIntoView();
            _navigation.Children.Add(link);
            _groups.Add((group, card, link, rows));
        }
        _appliedNarrow = null;
        Reflow();
        RefreshSearch();
        RefreshState();
    }

    public void Dispose()
    {
        _model.PropertyChanged -= OnModelChanged;
        foreach (var detach in _detach) detach();
        _detach.Clear();
    }

    private Control CreateField(TaskFieldViewModel field)
    {
        var label = Text(field.Label + (field.ReadOnly ? " · 只读" : ""), 13, FontWeight.Medium);
        var help = Text(field.Help, 12);
        Resource(help, TextBlock.ForegroundProperty, "AlasMutedBrush");
        var labels = new StackPanel { Spacing = 6, Children = { label, help } };
        var controls = new StackPanel { Spacing = 6 };
        var editor = CreateInput(field);
        controls.Children.Add(editor);
        Action? refreshFieldActions = null;
        if (field.Kind == TaskFieldKind.Lua)
        {
            var check = Button("检查脚本", "Check_" + field.Path);
            var apply = Button("应用脚本", "Apply_" + field.Path, primary: true);
            check.Click += async (_, _) => await _model.CheckScriptAsync(field);
            apply.Click += async (_, _) => await _model.ApplyScriptAsync(field);
            var scriptStatus = Text("", 12);
            var diagnostics = Text("", 12);
            Resource(diagnostics, TextBlock.ForegroundProperty, "AlasDangerBrush");
            controls.Children.Add(new WrapPanel { Children = { check, apply } }); controls.Children.Add(scriptStatus); controls.Children.Add(diagnostics);
            refreshFieldActions = () =>
            {
                check.IsEnabled = !field.ReadOnly && !field.IsChecking && !_model.IsBusy && _model.Backend is not null;
                apply.IsEnabled = !field.ReadOnly && !field.IsChecking && !_model.IsBusy && field.ScriptValidated && _model.Backend is not null;
                scriptStatus.Text = field.ScriptStatus;
                diagnostics.Text = string.Join(Environment.NewLine, field.Diagnostics.Select(d =>
                    (d.Line is { } line ? $"{line}:{d.Column ?? 1} " : "") + $"{d.Severity}: {d.Message}"));
                diagnostics.IsVisible = field.Diagnostics.Count > 0;
            };
            _refreshFields.Add(refreshFieldActions);
        }
        if (field.CanResetSchedule)
        {
            var reset = Button("立即调度", "Reset_" + field.Path);
            ToolTip.SetTip(reset, "恢复上游默认调度时间；保存后由调度器决定何时执行。");
            reset.Click += (_, _) => field.ResetSchedule();
            controls.Children.Add(reset);
        }
        if (field.CanClearStorage)
        {
            var clear = Button("清除记录", "Clear_" + field.Path);
            var approve = Button("确认清除", "ClearConfirm_" + field.Path);
            var cancel = Button("取消", "ClearCancel_" + field.Path);
            var confirmation = new WrapPanel { IsVisible = false, Children = { approve, cancel } };
            clear.Click += (_, _) => confirmation.IsVisible = true;
            cancel.Click += (_, _) => confirmation.IsVisible = false;
            approve.Click += (_, _) => { field.ClearStorage(); confirmation.IsVisible = false; };
            controls.Children.Add(clear); controls.Children.Add(confirmation);
        }
        var error = Text("", 12);
        Resource(error, TextBlock.ForegroundProperty, "AlasDangerBrush");
        controls.Children.Add(error);
        var state = Text("", 11);
        Resource(state, TextBlock.ForegroundProperty, "AlasMutedBrush");
        controls.Children.Add(state);
        var remote = Text("", 12);
        var mine = Button("保留我的修改", "KeepMine_" + field.Path);
        var theirs = Button("采用远端值", "KeepRemote_" + field.Path);
        var resolution = new StackPanel { Spacing = 6, Children = { remote, new WrapPanel { Children = { mine, theirs } } } };
        mine.Click += (_, _) => field.ResolveConflict(true);
        theirs.Click += (_, _) => field.ResolveConflict(false);
        controls.Children.Add(resolution);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Children = { labels, controls } };
        var row = new Border { Padding = new Thickness(0, 14), BorderThickness = new Thickness(0, 1, 0, 0), Child = grid };
        row.Name = "ConfigField_" + field.Path;
        Resource(row, Border.BorderBrushProperty, "AlasBorderBrush");
        _reflow.Add(narrow =>
        {
            if (narrow || field.IsMultiline)
            {
                grid.ColumnDefinitions = new ColumnDefinitions("*"); grid.RowDefinitions = new RowDefinitions("Auto,Auto");
                Grid.SetColumn(controls, 0); Grid.SetRow(controls, 1); labels.Margin = new Thickness(0, 0, 0, 10);
            }
            else
            {
                grid.ColumnDefinitions = new ColumnDefinitions("*,*"); grid.RowDefinitions = new RowDefinitions("Auto");
                Grid.SetColumn(controls, 1); Grid.SetRow(controls, 0); labels.Margin = new Thickness(0, 0, 24, 0);
            }
        });
        void Update()
        {
            error.Text = field.Error; error.IsVisible = field.Error.Length > 0;
            state.Text = field.IsDirty ? "尚未保存" : ""; state.IsVisible = field.IsDirty;
            resolution.IsVisible = field.HasConflict;
            remote.Text = "远端值：" + (field.Kind == TaskFieldKind.Password ? "••••••" : field.RemoteText);
        }
        PropertyChangedEventHandler handler = (_, _) =>
        {
            Update();
            refreshFieldActions?.Invoke();
            if (row.IsVisible != _model.Matches(field)) RefreshSearch();
        };
        field.PropertyChanged += handler; _detach.Add(() => field.PropertyChanged -= handler);
        _refreshFields.Add(Update);
        return row;
    }

    private Control CreateInput(TaskFieldViewModel field)
    {
        Control input;
        Action refresh;
        var changing = false;
        if (field.Kind == TaskFieldKind.Boolean)
        {
            var toggle = new ToggleSwitch { IsEnabled = !field.ReadOnly, OnContent = "开", OffContent = "关" };
            toggle.IsCheckedChanged += (_, _) => { if (!changing) field.SetBoolean(toggle.IsChecked == true); };
            refresh = () => { changing = true; toggle.IsChecked = field.BoolValue; changing = false; };
            input = toggle;
        }
        else if (field.Kind == TaskFieldKind.Select)
        {
            var options = new List<TaskFieldOption>(field.Options);
            var select = new ComboBox { ItemsSource = options, HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = !field.ReadOnly };
            select.SelectionChanged += (_, _) => { if (!changing && select.SelectedItem is TaskFieldOption option) field.SelectOption(option); };
            refresh = () =>
            {
                changing = true;
                var selected = options.FirstOrDefault(option => System.Text.Json.Nodes.JsonNode.DeepEquals(option.Value, field.Value));
                if (selected is null)
                {
                    selected = new TaskFieldOption(field.Value, field.Text.Length == 0 ? "未设置" : field.Text);
                    options = [selected, .. field.Options]; select.ItemsSource = options;
                }
                select.SelectedItem = selected; changing = false;
            };
            input = select;
        }
        else if (field.Kind == TaskFieldKind.MultiSelect)
        {
            var panel = new WrapPanel();
            var boxes = field.Options.Select(option =>
            {
                var box = new CheckBox { Content = option.Label, IsEnabled = !field.ReadOnly, Margin = new Thickness(0, 0, 12, 4) };
                box.IsCheckedChanged += (_, _) => { if (!changing) field.ToggleOption(option, box.IsChecked == true); };
                panel.Children.Add(box); return (option, box);
            }).ToArray();
            refresh = () => { changing = true; foreach (var (option, box) in boxes) box.IsChecked = field.IsSelected(option); changing = false; };
            input = panel;
        }
        else
        {
            var text = new TextBox
            {
                IsReadOnly = field.ReadOnly, AcceptsReturn = field.IsMultiline, TextWrapping = TextWrapping.Wrap,
                MinHeight = field.IsMultiline ? 130 : 36, MaxHeight = field.IsMultiline ? 520 : double.PositiveInfinity,
                PasswordChar = field.Kind == TaskFieldKind.Password ? '●' : '\0', HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            text.TextChanged += (_, _) => { if (!changing && text.Text != field.Text) field.SetText(text.Text); };
            refresh = () => { changing = true; if (text.Text != field.Text) text.Text = field.Text; changing = false; };
            if (field.Kind is TaskFieldKind.Yaml or TaskFieldKind.Lua)
                ToolTip.SetTip(text, field.Kind == TaskFieldKind.Yaml ? "YAML 文本；保留原始缩进，保存时由服务端校验" : "受限 Lua；服务端检查通过后才能保存");
            input = text;
        }
        input.Name = "Field_" + field.Path;
        AutomationProperties.SetName(input, field.Label);
        AutomationProperties.SetHelpText(input, field.Help);
        PropertyChangedEventHandler handler = (_, _) => refresh();
        field.PropertyChanged += handler; _detach.Add(() => field.PropertyChanged -= handler);
        refresh();
        return input;
    }

    private void RefreshState()
    {
        RefreshChrome();
        foreach (var update in _refreshFields) update();
    }

    private void RefreshChrome()
    {
        _title.Text = _model.Title; _title.IsVisible = !LegacyLayout && !CompactLayout;
        if (_search.Text != _model.Search) _search.Text = _model.Search;
        _save.IsEnabled = _model.CanSave; _discard.IsEnabled = _model.HasChanges && !_model.IsBusy;
        _run.IsEnabled = _model.CanRun; _confirm.IsEnabled = _model.CanRun; _cancel.IsEnabled = !_model.IsBusy;
        _status.Text = _model.Backend is null ? _model.EditStatus + " · 尚未连接服务" : _model.EditStatus;
        _error.Text = _model.Error; _error.IsVisible = _model.Error.Length > 0;
        _message.Text = _model.Message; _message.IsVisible = _model.Message.Length > 0;
        var wasConfirming = _overlay.IsVisible;
        _overlay.IsVisible = _model.ConfirmRun; _main.IsEnabled = !_model.ConfirmRun;
        if (!wasConfirming && _model.ConfirmRun) _confirm.Focus();
        if (wasConfirming && !_model.ConfirmRun) _run.Focus();
    }

    private void RefreshSearch()
    {
        var visible = 0;
        foreach (var group in _groups)
        {
            var count = 0;
            foreach (var (field, row) in group.Rows)
            {
                var isVisible = _model.Matches(field);
                if (row.IsVisible != isVisible) row.IsVisible = isVisible;
                if (isVisible) count++;
            }
            var groupVisible = count > 0;
            if (group.Card.IsVisible != groupVisible) group.Card.IsVisible = groupVisible;
            if (group.Link.IsVisible != groupVisible) group.Link.IsVisible = groupVisible;
            visible += count;
        }
        var emptyVisible = visible == 0;
        if (_empty.IsVisible != emptyVisible) _empty.IsVisible = emptyVisible;
        _empty.Text = _model.Search.Length > 0 ? "未找到匹配配置，请尝试其他关键词" : "此任务暂无可显示的配置";
    }
    private void Reflow()
    {
        if (Bounds.Width <= 0) return;
        var narrow = Bounds.Width < 720;
        if (_appliedNarrow == narrow && _appliedLegacy == LegacyLayout) return;
        _appliedNarrow = narrow;
        _appliedLegacy = LegacyLayout;
        _navigation.IsVisible = !narrow;
        _layout.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : LegacyLayout ? "*,180" : "180,*");
        Grid.SetColumn(_cards, narrow || LegacyLayout ? 0 : 1);
        Grid.SetColumn(_navigation, LegacyLayout && !narrow ? 1 : 0);
        _navigation.Margin = LegacyLayout ? new Thickness(18, 0, 0, 0) : new Thickness(0, 0, 18, 0);
        foreach (var reflow in _reflow) reflow(narrow);
    }

    private static TextBlock Text(string text, double size, FontWeight? weight = null) => new()
    { Text = text, FontSize = size, FontWeight = weight ?? FontWeight.Normal, TextWrapping = TextWrapping.Wrap };
    private static Button Button(string label, string name, bool primary = false)
    {
        var button = new Button { Content = label, Name = name, MinHeight = 36, Padding = new Thickness(12, 7), HorizontalAlignment = HorizontalAlignment.Left };
        Resource(button, ThemeProperty, primary ? "PrimaryButtonTheme" : "GhostButtonTheme");
        AutomationProperties.SetName(button, label);
        return button;
    }
    private static Border Card(Control child)
    {
        var card = new Border { Child = child, Padding = new Thickness(20), BorderThickness = new Thickness(1) };
        Resource(card, Border.BackgroundProperty, "AlasSurfaceBrush");
        Resource(card, Border.BorderBrushProperty, "AlasBorderBrush");
        Resource(card, Border.CornerRadiusProperty, "AlasPanelRadius");
        return card;
    }
    private static void Resource(AvaloniaObject target, AvaloniaProperty property, string key) => target.Bind(property, new DynamicResourceExtension(key));
}
