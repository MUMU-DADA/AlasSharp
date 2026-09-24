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
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Alas.UI.ConfigManager;

/// <summary>实例列表的一行（上游 `instances` 的 name/server/serial/status）。</summary>
public sealed record ConfigInstanceInfo(string Name, string Server, string Serial, string Status);

/// <summary>读取到的配置本体：实例名 + revision（磁盘 JSON 的 SHA-256）+ values 原文。</summary>
public sealed record ConfigContent(string Instance, string Revision, string ValuesJson);

/// <summary>可导入的配置来源（上游 `instances.importable` 的 name/modified）。</summary>
public sealed record ConfigImportSource(string Name, DateTimeOffset ModifiedAt);

/// <summary>
/// 配置管理页需要的能力（页面局部合同）。
/// 对齐上游 `ConfigManager.tsx` 与 `App.tsx` 的 CreateInstance：只有实例列表/读取/创建/导入/可导入源/删除，
/// **没有**任意服务器文件读写、目录分组、大小或时间、也没有原文编辑器。
/// </summary>
public interface IConfigInstancesBackend
{
    Task<IReadOnlyList<ConfigInstanceInfo>> ListInstancesAsync(CancellationToken cancellationToken = default);

    Task<ConfigContent> ReadConfigAsync(string instance, CancellationToken cancellationToken = default);

    /// <summary>创建实例；source 与 importFile 互斥（上游 `{name, source, import_file}`），返回后端归一化后的实例名。</summary>
    Task<string> CreateInstanceAsync(string name, string? source, string? importFile,
        CancellationToken cancellationToken = default);

    /// <summary>把一份配置文本导入为可导入源（上游 `instances.importConfig` 的 name/content）。</summary>
    Task ImportConfigAsync(string name, string content, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConfigImportSource>> ListImportsAsync(CancellationToken cancellationToken = default);

    /// <summary>删除实例；必须带 revision（上游先 `config.get` 再 `instances.delete`）。</summary>
    Task DeleteInstanceAsync(string instance, string revision, CancellationToken cancellationToken = default);
}

/// <summary>未接入 Core 时的实现：**如实抛错**，由页面显示真实错误，不伪造实例列表。</summary>
public sealed class DisconnectedConfigInstancesBackend : IConfigInstancesBackend
{
    public static DisconnectedConfigInstancesBackend Instance { get; } = new();

    public const string Notice = "尚未连接，无法读取配置实例。";

    public Task<IReadOnlyList<ConfigInstanceInfo>> ListInstancesAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<IReadOnlyList<ConfigInstanceInfo>>(new InvalidOperationException(Notice));

    public Task<ConfigContent> ReadConfigAsync(string instance, CancellationToken cancellationToken = default) =>
        Task.FromException<ConfigContent>(new InvalidOperationException(Notice));

    public Task<string> CreateInstanceAsync(string name, string? source, string? importFile,
        CancellationToken cancellationToken = default) =>
        Task.FromException<string>(new InvalidOperationException(Notice));

    public Task ImportConfigAsync(string name, string content, CancellationToken cancellationToken = default) =>
        Task.FromException(new InvalidOperationException(Notice));

    public Task<IReadOnlyList<ConfigImportSource>> ListImportsAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<IReadOnlyList<ConfigImportSource>>(new InvalidOperationException(Notice));

    public Task DeleteInstanceAsync(string instance, string revision, CancellationToken cancellationToken = default) =>
        Task.FromException(new InvalidOperationException(Notice));
}

/// <summary>
/// 配置管理页（上游 `pages/ConfigManager.tsx`）：实例列表 + 概览/导出/删除（二次确认）+ 新建/导入表单。
/// 页面消费能力合同；导出与概览通过注入的平台委托完成。
/// </summary>
public sealed class ConfigManagerPage : UserControl
{
    private readonly ConfigManagerPageViewModel _model;
    private Border _overlay = null!;
    private Grid _layout = null!;
    private bool _narrow;
    private Action<bool>? _arrangeTitle;

    /// <summary>Shell-wide modal layer; standalone pages use their own viewport.</summary>
    public Panel? ModalHost
    {
        set
        {
            if (_overlay.Parent is Panel previous) previous.Children.Remove(_overlay);
            (value ?? _layout).Children.Add(_overlay);
        }
    }

    public ConfigManagerPage()
        : this(DisconnectedConfigInstancesBackend.Instance)
    {
    }

    public ConfigManagerPage(IConfigInstancesBackend backend)
    {
        _model = new ConfigManagerPageViewModel(backend);
        DataContext = _model;
        Content = Build();
        SizeChanged += (_, _) => ArrangeViewport();
        _ = _model.RefreshAsync();
    }

    public ConfigManagerPageViewModel Model => _model;

    /// <summary>更换数据来源时重读实例列表并隔离旧请求。</summary>
    public IConfigInstancesBackend Backend
    {
        get => _model.Backend;
        set => _model.Backend = value;
    }

    /// <summary>状态的中文标签（与外壳状态徽标一致）；未知值原文显示。</summary>
    private static string LocalizedStatus(string status) => status switch
    {
        "running" => "运行中",
        "waiting" => "待命",
        "stopped" => "已停止",
        "error" => "错误",
        "updating" => "更新中",
        _ => status,
    };

    /// <summary>状态对应的样式类（外壳的 task-state.running / .waiting 等）。</summary>
    private static string LocalizedState(string status) => status switch
    {
        "running" => "running",
        "waiting" => "waiting",
        _ => string.Empty,
    };

    /// <summary>按皮肤主题键取 ControlTheme（代码构造的按钮需要显式套用主题）。</summary>
    private static Avalonia.Styling.ControlTheme? ThemeOrDefault(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true
            ? value as Avalonia.Styling.ControlTheme
            : null;

    private Control Build()
    {
        var title = new TextBlock { Name = "ConfigTitle", Classes = { "page-title" }, Text = "配置管理" };
        var import = new Button
        {
            Name = "ConfigImportButton", Content = "导入配置", Padding = new Thickness(14, 9),
            IsEnabled = _model.CanCreate, Command = _model.OpenImportCommand,
            Theme = ThemeOrDefault("GhostButtonTheme"),
        };
        var create = new Button
        {
            Name = "ConfigCreateButton", Content = "新建配置", Padding = new Thickness(14, 9),
            IsEnabled = _model.CanCreate, Command = _model.OpenCreateCommand,
            Theme = ThemeOrDefault("PrimaryButtonTheme"),
        };
        var titleActions = new StackPanel
        {
            Name = "ConfigTitleActions", Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { import, create },
        };
        var titleRow = new Grid
        {
            Name = "ConfigTitleRow", ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 32, 0, 0),
        };
        titleRow.Children.Add(title);
        Grid.SetColumn(titleActions, 1);
        titleRow.Children.Add(titleActions);
        _arrangeTitle = narrow =>
        {
            titleRow.ColumnDefinitions = new(narrow ? "*" : "*,Auto");
            titleRow.RowDefinitions = new(narrow ? "Auto,Auto" : "Auto");
            titleRow.RowSpacing = narrow ? 17 : 0;
            Grid.SetColumn(titleActions, narrow ? 0 : 1);
            Grid.SetRow(titleActions, narrow ? 1 : 0);
        };

        var error = new TextBlock
        {
            Name = "ConfigError", FontSize = 12, TextWrapping = TextWrapping.Wrap,
            IsVisible = _model.HasError, Text = _model.Error,
        };
        error[!TextBlock.ForegroundProperty] =
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AlasDangerBrush");

        var list = new ItemsControl
        {
            Name = "ConfigList", ItemsSource = _model.Instances,
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<ConfigInstanceInfo>((item, _) => Row(item)),
        };
        var empty = new StackPanel
        {
            Name = "ConfigEmpty", Spacing = 6, IsVisible = _model.IsEmpty,
            Children =
            {
                new TextBlock { Text = "还没有配置", FontSize = 15, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "点右上角新建一个配置实例。", FontSize = 12, Opacity = 0.7 },
            },
        };
        var notice = new TextBlock
        {
            Name = "ConfigExported", FontSize = 12, IsVisible = _model.HasNotice, Text = _model.Notice,
        };
        var loading = new TextBlock
        {
            Name = "ConfigLoading", Text = "正在读取…", FontSize = 12, IsVisible = _model.Loading,
        };

        _model.PropertyChanged += (_, args) =>
        {
            switch (args.PropertyName)
            {
                case nameof(ConfigManagerPageViewModel.Error):
                    error.Text = _model.Error;
                    error.IsVisible = _model.HasError;
                    break;
                case nameof(ConfigManagerPageViewModel.IsEmpty):
                    empty.IsVisible = _model.IsEmpty;
                    break;
                case nameof(ConfigManagerPageViewModel.Notice):
                case nameof(ConfigManagerPageViewModel.HasNotice):
                    notice.Text = _model.Notice;
                    notice.IsVisible = _model.HasNotice;
                    break;
                case nameof(ConfigManagerPageViewModel.Loading):
                    loading.IsVisible = _model.Loading;
                    break;
                case nameof(ConfigManagerPageViewModel.CanCreate):
                    create.IsEnabled = _model.CanCreate;
                    import.IsEnabled = _model.CanCreate;
                    break;
            }
        };

        // 创建/导入表单（上游 CreateInstance 的 Modal）：Form 非空时显示，控件一次建好、按状态刷新。
        var formTitle = new TextBlock { FontSize = 15, FontWeight = FontWeight.SemiBold };
        var formHint = new TextBlock { FontSize = 12, Opacity = 0.7 };
        var pickFile = new Button { Name = "ConfigFormPickFile", Padding = new Thickness(10, 6), FontSize = 12, Theme = ThemeOrDefault("GhostButtonTheme") };
        var pickImport = new Button { Name = "ConfigFormPickImport", Padding = new Thickness(10, 6), FontSize = 12, Theme = ThemeOrDefault("GhostButtonTheme") };
        var importLabel = new TextBlock { Name = "ConfigFormImportLabel", FontSize = 12 };
        var importSelect = new ComboBox { Name = "ConfigFormImportSelect", MinWidth = 220 };
        var importHint = new TextBlock { Name = "ConfigFormImportHint", FontSize = 11, Opacity = 0.7 };
        var importBlock = new StackPanel { Name = "ConfigFormImportBlock", Spacing = 4 };
        importBlock.Children.Add(importLabel);
        importBlock.Children.Add(importSelect);
        importBlock.Children.Add(importHint);
        var nameLabel = new TextBlock { Name = "ConfigFormNameLabel", FontSize = 12 };
        var nameBox = new TextBox { Name = "ConfigFormName", MaxLength = 64, MinWidth = 220 };
        var sourceLabel = new TextBlock { Name = "ConfigFormSourceLabel", FontSize = 12 };
        var sourceSelect = new ComboBox { Name = "ConfigFormSource", MinWidth = 220 };
        var formError = new TextBlock { Name = "ConfigFormError", FontSize = 11, TextWrapping = TextWrapping.Wrap };
        formError[!TextBlock.ForegroundProperty] =
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AlasDangerBrush");
        var submit = new Button
        {
            Name = "ConfigFormSubmit", Padding = new Thickness(14, 9), FontSize = 12,
            Theme = ThemeOrDefault("PrimaryButtonTheme"),
        };
        var cancel = new Button { Name = "ConfigFormCancel", Content = "取消", Padding = new Thickness(12, 8), FontSize = 12, Theme = ThemeOrDefault("GhostButtonTheme") };
        cancel.Click += (_, _) => _model.CloseForm();
        var formHost = new Border
        {
            Name = "ConfigForm", Classes = { "panel" }, Padding = new Thickness(16, 14), IsVisible = false,
            MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new ScrollViewer { Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    formTitle, formHint, pickFile, pickImport, importBlock,
                    nameLabel, nameBox, sourceLabel, sourceSelect, formError,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { submit, cancel } },
                },
            } },
        };

        // 上游 CreateInstance 是**模态**：这里用页面内的遮罩层包住表单（离屏可验，不显示系统窗口）。
        // 遮罩可聚焦并处理 Esc 关闭；文件选择被取消不会走到这里，因此不会连带关闭表单。
        var formOverlay = new Border
        {
            Name = "ConfigFormOverlay", IsVisible = false, Focusable = true,
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(0x66, 0, 0, 0)),
            Padding = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = formHost,
            ZIndex = 30,
        };
        _overlay = formOverlay;
        KeyboardNavigation.SetTabNavigation(formOverlay, KeyboardNavigationMode.Cycle);
        IInputElement? returnFocus = null;
        formOverlay.KeyDown += (_, args) =>
        {
            if (args.Key == Avalonia.Input.Key.Escape)
            {
                _model.CloseForm();
                args.Handled = true;
            }
        };

        // 表单绑定与刷新：控件一次建好，每次打开按 Form 的状态重设（含事件重挂，避免重复触发）。
        // 同时订阅表单自身的 PropertyChanged——否则字段变化（如输入名称）不会更新按钮状态，
        ConfigCreateFormViewModel? rememberedForm = null;
        bool wasPicking = false;
        // 刷新期间禁止回写：给下拉框赋 SelectedItem 会触发 SelectionChanged，
        // 若那时把值写回模型，就会在刷新过程中打断用户/调用方刚设好的状态。
        var refreshing = false;
        void OnFormChanged(object? sender, PropertyChangedEventArgs args) => RefreshForm();
        void RefreshForm()
        {
            var form = _model.Form;
            bool changedForm = !ReferenceEquals(rememberedForm, form);
            if (changedForm)
            {
                wasPicking = false;
                if (rememberedForm is not null) rememberedForm.PropertyChanged -= OnFormChanged;
                rememberedForm = form;
                if (rememberedForm is not null) rememberedForm.PropertyChanged += OnFormChanged;
            }
            formOverlay.IsVisible = form is not null;
            if (changedForm)
            {
                if (form is not null)
                {
                    returnFocus = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
                    Dispatcher.UIThread.Post(() => { if (ReferenceEquals(_model.Form, form)) nameBox.Focus(); });
                }
                else { returnFocus?.Focus(); returnFocus = null; }
            }
            formHost.IsVisible = form is not null;
            if (form is null) return;
            bool restoreAfterPicker = wasPicking && !form.Picking;
            wasPicking = form.Picking;
            refreshing = true;
            try
            {
                formTitle.Text = form.Title;
                formHint.Text = form.Hint;
                pickFile.Content = form.ImportPickLabel;
                pickImport.Content = form.ImportButtonLabel;
                pickImport.IsEnabled = !form.LoadingImports && !form.Picking && !form.Busy;
                pickFile.IsEnabled = !form.Picking && !form.Busy && !form.LoadingImports;
                importLabel.Text = form.ImportSelectLabel;
                importHint.Text = form.ImportHint;
                importBlock.IsVisible = form.HasImports;
                importSelect.ItemsSource = form.ImportChoices;
                importSelect.SelectedItem = form.ImportChoices
                    .FirstOrDefault(choice => choice.Value == form.ImportFile) ?? form.ImportChoices[0];
                nameLabel.Text = form.NameLabel;
                nameBox.Text = form.Name;
                sourceLabel.Text = form.InitialConfigLabel;
                var sources = new List<ConfigCreateFormViewModel.ImportChoice> { new("", form.DefaultConfigLabel) };
                sources.AddRange(_model.Instances.Select(item => new ConfigCreateFormViewModel.ImportChoice(item.Name, item.Name)));
                sourceSelect.ItemsSource = sources;
                sourceSelect.SelectedItem = sources.FirstOrDefault(item => item.Value == form.Source);
                formError.Text = form.Error;
                formError.IsVisible = form.HasError;
                submit.Content = form.SubmitLabel;
                submit.IsEnabled = form.CanSubmit;
            }
            finally { refreshing = false; }
            if (restoreAfterPicker)
                Dispatcher.UIThread.Post(() => { if (ReferenceEquals(_model.Form, form)) pickFile.Focus(); });
            submit.Command = form.SubmitCommand;
            pickFile.Command = form.PickFileCommand;
            pickImport.Command = form.PickImportCommand;
            // 按钮只通过命令执行一次动作。
            nameBox.PropertyChanged -= OnNameChanged;
            nameBox.PropertyChanged += OnNameChanged;
            importSelect.SelectionChanged -= OnImportSelected;
            importSelect.SelectionChanged += OnImportSelected;
            sourceSelect.SelectionChanged -= OnSourceSelected;
            sourceSelect.SelectionChanged += OnSourceSelected;
        }

        void OnNameChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (refreshing) return;
            if (args.Property == TextBox.TextProperty) _model.Form?.Name = nameBox.Text ?? string.Empty;
        }
        void OnImportSelected(object? sender, SelectionChangedEventArgs args)
        {
            if (refreshing) return;
            // 值才是来源标识：空值 =「使用默认配置」，此时清掉导入选择（chooseImport('') 不改名与初始配置）。
            if (importSelect.SelectedItem is ConfigCreateFormViewModel.ImportChoice choice)
            {
                _model.Form?.ChooseImport(choice.Value);
            }
        }
        void OnSourceSelected(object? sender, SelectionChangedEventArgs args)
        {
            if (refreshing) return;
            if (sourceSelect.SelectedItem is not ConfigCreateFormViewModel.ImportChoice chosen) return;
            // 「使用默认配置」是**显示标签**、不是实例名：它对应"不指定初始配置"，
            // 不能把标签当值写回模型（否则会在每次刷新时清掉用户刚选中的导入源）。
            _model.Form?.SelectSource(chosen.Value);
        }

        var body = new StackPanel
        {
            Name = "ConfigPage", Spacing = 12, Margin = new Thickness(24, 0, 24, 24),
            Children = { titleRow, error, notice, loading, list, empty },
        };
        _model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ConfigManagerPageViewModel.Form)) RefreshForm();
            if (args.PropertyName is nameof(ConfigManagerPageViewModel.Confirming)
                or nameof(ConfigManagerPageViewModel.Busy)
                or nameof(ConfigManagerPageViewModel.Connected)
                or nameof(ConfigManagerPageViewModel.CanCreate)
                or nameof(ConfigManagerPageViewModel.Loading))
            {
                // 行的删除标签取决于"是否二次确认"、导出/删除按钮的可用性取决于"是否在忙/已连接"，
                // 而行控件只在创建时确定一次，所以这些状态一变就重建行——否则按钮会停在旧状态上
                list.ItemsSource = null;
                list.ItemsSource = _model.Instances;
            }
        };
        RefreshForm();
        _layout = new Grid { Children = { new ScrollViewer { Content = body }, formOverlay } };
        return _layout;
    }

    /// <summary>一行实例：名称 + 服务器/序列号 + 状态徽标 + 概览/导出/删除（删除需二次确认）。</summary>
    private Control Row(ConfigInstanceInfo item)
    {
        var name = new TextBlock { Text = item.Name, FontSize = 16, FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap };
        var meta = new TextBlock
        {
            FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap,
            Text = item.Server is "" or "disabled" ? item.Serial : $"{item.Server} · {item.Serial}",
        };
        var main = new StackPanel { Spacing = 6, Children = { name, meta } };

        // 状态徽标：复用外壳的状态样式与中文标签（上游用 StatusBadge + status.* 文案）；
        // 未知状态**原文显示**，不吞掉后端给的值。
        var badge = new Border
        {
            Classes = { "task-state", LocalizedState(item.Status) },
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = LocalizedStatus(item.Status), FontSize = 10 },
        };
        var overview = new Button
        {
            Name = "ConfigOverviewButton", Content = "概览", FontSize = 12, Padding = new Thickness(10, 6),
            Theme = ThemeOrDefault("GhostButtonTheme"),
            Command = _model.OpenOverviewCommand, CommandParameter = item.Name,
        };
        var export = new Button
        {
            Name = "ConfigExportButton", Content = "导出", FontSize = 12, Padding = new Thickness(10, 6),
            Theme = ThemeOrDefault("GhostButtonTheme"),
            IsEnabled = _model.CanCreate && !_model.IsBusy(item.Name),
            Command = _model.ExportCommand, CommandParameter = item.Name,
        };
        // 二次确认：未确认时是「删除实例」，确认后变成「确认删除」（上游 confirming === name 的分支）。
        var remove = new Button
        {
            Name = "ConfigDeleteButton", FontSize = 12, Padding = new Thickness(10, 6),
            Theme = ThemeOrDefault(_model.IsConfirming(item.Name) ? "DangerButtonTheme" : "DangerSubtleButtonTheme"),
            Content = _model.IsConfirming(item.Name) ? "确认删除" : "删除实例",
            IsEnabled = _model.CanCreate && !_model.IsBusy(item.Name),
            Command = _model.DeleteCommand, CommandParameter = item.Name,
        };
        var actions = new Grid
        {
            ColumnDefinitions = new("Auto,Auto,Auto"), ColumnSpacing = 8, VerticalAlignment = VerticalAlignment.Center,
            Children = { overview, export, remove },
        };
        Grid.SetColumn(export, 1); Grid.SetColumn(remove, 2);
        var row = new Grid { Name = "ConfigInstanceRow", ColumnDefinitions = new("*,Auto,Auto") };
        row.Children.Add(main);
        Grid.SetColumn(badge, 1);
        row.Children.Add(badge);
        Grid.SetColumn(actions, 2);
        row.Children.Add(actions);
        ArrangeRow(row, _narrow);
        var panel = new Border { Classes = { "panel" }, Padding = new Thickness(22, 18), Margin = new Thickness(0, 0, 0, 12), Child = row };
        return panel;
    }

    // Matches the upstream components.css/layout.css viewport breakpoint.
    private void ArrangeViewport()
    {
        double width = TopLevel.GetTopLevel(this)?.ClientSize.Width ?? Bounds.Width;
        bool narrow = width is > 0 and <= 950;
        if (_narrow == narrow) return;
        _narrow = narrow;
        _arrangeTitle?.Invoke(narrow);
        foreach (var row in this.GetVisualDescendants().OfType<Grid>().Where(row => row.Name == "ConfigInstanceRow"))
            ArrangeRow(row, narrow);
    }

    private static void ArrangeRow(Grid row, bool narrow)
    {
        row.ColumnDefinitions = new(narrow ? "*" : "*,Auto,Auto");
        row.RowDefinitions = new(narrow ? "Auto,Auto,Auto" : "Auto");
        row.RowSpacing = narrow ? 12 : 0;
        row.ColumnSpacing = narrow ? 0 : 16;
        for (int i = 0; i < row.Children.Count; i++)
        {
            Grid.SetColumn(row.Children[i], narrow ? 0 : i);
            Grid.SetRow(row.Children[i], narrow ? i : 0);
        }
        ((Grid)row.Children[2]).ColumnDefinitions = new(narrow ? "*,*,*" : "Auto,Auto,Auto");
    }
}

/// <summary>配置管理页状态：实例列表、忙碌、二次确认、提示与错误。</summary>
public sealed class ConfigManagerPageViewModel : INotifyPropertyChanged
{
    private IConfigInstancesBackend _backend;

    /// <summary>后端代次：每次换后端自增。在途操作据此判断"我的结果是否还属于当前后端"。</summary>
    private int _generation;
    private long _refreshSequence;

    private readonly HashSet<string> _busyRows = new();
    private string _confirming = string.Empty;
    private string _error = string.Empty;
    private string _notice = string.Empty;
    private bool _loading;
    private bool _connected;

    public ConfigManagerPageViewModel(IConfigInstancesBackend backend)
    {
        _backend = backend;
        OpenCreateCommand = new ConfigPageCommand(_ => OpenCreateForm(importFirst: false));
        OpenImportCommand = new ConfigPageCommand(_ => OpenCreateForm(importFirst: true));
        OpenOverviewCommand = new ConfigPageCommand(parameter =>
        {
            if (parameter is string instance && !string.IsNullOrEmpty(instance)) OpenOverview?.Invoke(instance);
        });
        ExportCommand = new ConfigPageCommand(parameter => _ = ExportAsync(parameter as string));
        DeleteCommand = new ConfigPageCommand(parameter => _ = DeleteAsync(parameter as string));
    }

    public ObservableCollection<ConfigInstanceInfo> Instances { get; } = new();

    /// <summary>创建/导入表单（打开时非空）：页面内的模态区。</summary>
    public ConfigCreateFormViewModel? Form { get; private set; }

    public ICommand OpenCreateCommand { get; }
    public ICommand OpenImportCommand { get; }
    public ICommand OpenOverviewCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand DeleteCommand { get; }

    /// <summary>连接就绪才允许新建/导入/导出/删除（上游 `connection !== 'ready'` 时禁用工具栏）。</summary>
    public bool Connected
    {
        get => _connected;
        set
        {
            if (_connected == value) return;
            _connected = value;
            if (!value)
            {
                _generation++;
                _busyRows.Clear();
                _confirming = string.Empty;
                CloseForm();
                Loading = false;
                Notify(nameof(Busy));
                Notify(nameof(Confirming));
            }
            Notify(nameof(Connected));
            Notify(nameof(CanCreate));
        }
    }

    public bool CanCreate => Connected && !Loading;

    public bool HasError => !string.IsNullOrEmpty(Error);

    public string Error
    {
        get => _error;
        private set { if (SetField(ref _error, value)) Notify(nameof(HasError)); }
    }

    public bool HasNotice => !string.IsNullOrEmpty(Notice);

    public string Notice
    {
        get => _notice;
        private set { if (SetField(ref _notice, value)) Notify(nameof(HasNotice)); }
    }

    public bool Loading
    {
        get => _loading;
        private set { if (SetField(ref _loading, value)) { Notify(nameof(CanCreate)); Notify(nameof(IsEmpty)); } }
    }

    /// <summary>空态：没有实例、且不在读取中、也没有错误（上游 `!instances.length` + Empty 组件）。</summary>
    public bool IsEmpty => !Loading && !HasError && Instances.Count == 0;

    /// <summary>导出落盘委托（平台能力，由外壳注入）：只有它完成才算导出成功。</summary>
    public Func<string, Func<Task<string>>, Task>? ExportFileAsync { get; set; }

    /// <summary>概览跳转委托（外壳路由，由外壳注入）。</summary>
    public Action<string>? OpenOverview { get; set; }

    /// <summary>选择本机配置文件（平台能力，由外壳注入）：浏览器/桌面只给内容，给不了服务器路径。</summary>
    public Func<Task<(string Name, string Content)?>>? PickImportFileAsync { get; set; }

    public IConfigInstancesBackend Backend
    {
        get => _backend;
        set
        {
            _backend = value;
            // 代次自增：切换后旧后端的在途返回一律作废，且不得再对旧对象发起下一次写操作。
            _generation++;
            _busyRows.Clear();
            _confirming = string.Empty;
            Form = null;
            // 三处都要通知：行按钮的可用性、确认态、以及"旧表单必须立刻消失"。
            Notify(nameof(Busy));
            Notify(nameof(Confirming));
            Notify(nameof(Form));
            _ = RefreshAsync();
        }
    }

    public bool IsConfirming(string name) => _confirming == name;

    /// <summary>当前后端代次（供表单判断"我的结果是否还属于当前后端"）。</summary>
    internal int Generation => _generation;

    public bool IsBusy(string name) => _busyRows.Contains(name);

    /// <summary>
    /// 是否有任意实例操作在途。它不是给界面直接读的值，而是**变更信号**：
    /// 行的导出/删除按钮可用性依赖它，所以忙碌集合每次变化都必须通知
    /// </summary>
    public bool Busy => _busyRows.Count > 0;

    public async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
    {
        // 捕获代次与后端：慢的旧请求晚到时不得覆盖新后端的列表。
        var generation = _generation;
        long request = ++_refreshSequence;
        var backend = _backend;
        Loading = true;
        try
        {
            var items = await backend.ListInstancesAsync(cancellationToken);
            if (generation != _generation || request != _refreshSequence) return false;
            Instances.Clear();
            foreach (var item in items) Instances.Add(item);
            Error = string.Empty;
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            if (generation != _generation || request != _refreshSequence) return false;
            Instances.Clear();
            Error = failure.Message;
            return false;
        }
        finally
        {
            if (generation == _generation && request == _refreshSequence)
            {
                Loading = false;
                Notify(nameof(IsEmpty));
            }
        }
    }

    internal bool IsCurrent(ConfigCreateFormViewModel form, int generation)
        => Connected && generation == _generation && ReferenceEquals(Form, form);

    /// <summary>打开创建/导入表单；`importFirst` 对应从「导入配置」入口进入时直接列出可导入源。</summary>
    public void OpenCreateForm(bool importFirst)
    {
        if (!CanCreate) return;
        Error = string.Empty;
        Notice = string.Empty;
        Form = new ConfigCreateFormViewModel(this, _backend, importFirst);
        Notify(nameof(Form));
        _ = Form.LoadImportsIfNeededAsync();
    }

    public void CloseForm()
    {
        Form = null;
        Notify(nameof(Form));
    }

    /// <summary>导出：读配置 → 交给平台委托落盘 → **完成后**才提示已导出（上游 exportConfig）。</summary>
    public async Task ExportAsync(string? name)
    {
        if (!CanCreate || string.IsNullOrEmpty(name)) return;
        // 同一实例已在忙时直接忽略：重复点击不得再发一次读取。
        if (IsBusy(name)) return;
        Error = string.Empty;
        Notice = string.Empty;
        var generation = _generation;
        var backend = _backend;          // 捕获后端：await 之后不得再读可变字段
        MarkBusy(name, true);
        try
        {
            if (ExportFileAsync is null)
            {
                Error = "当前环境无法保存文件。";
                return;
            }
            await ExportFileAsync($"{name}.json", async () =>
            {
                if (generation != _generation || !Connected) throw new OperationCanceledException();
                var config = await backend.ReadConfigAsync(name);
                if (generation != _generation || !Connected) throw new OperationCanceledException();
                return config.ValuesJson;
            });
            if (generation != _generation) return;
            Notice = $"已导出 {name}";
        }
        catch (OperationCanceledException) { }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            if (generation != _generation) return;
            Error = failure.Message;   // 失败**不**提示已导出
        }
        finally
        {
            if (generation == _generation) MarkBusy(name, false);
        }
    }

    /// <summary>删除：第一次点击只进入二次确认；确认后先读 revision 再删（上游 remove）。</summary>
    public async Task DeleteAsync(string? name)
    {
        if (!CanCreate || string.IsNullOrEmpty(name)) return;
        if (!IsConfirming(name))
        {
            _confirming = name;
            Notify(nameof(Confirming));
            return;   // 未确认：不发任何调用
        }
        if (IsBusy(name)) return;   // 已在删同一个实例：忽略重复确认

        Error = string.Empty;
        Notice = string.Empty;
        var generation = _generation;
        var backend = _backend;          // 捕获后端：revision 必须发给**同一个**后端
        MarkBusy(name, true);
        try
        {
            var config = await backend.ReadConfigAsync(name);
            if (generation != _generation) return;   // 切换过后端：不再向旧后端发写操作
            await backend.DeleteInstanceAsync(name, config.Revision);
            if (generation != _generation) return;
            _confirming = string.Empty;
            Notify(nameof(Confirming));
            if (await RefreshAsync() && generation == _generation) Notice = "实例已移入备份";
        }
        catch (OperationCanceledException) { }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            if (generation != _generation) return;
            Error = failure.Message;
        }
        finally
        {
            if (generation == _generation) MarkBusy(name, false);
        }
    }

    /// <summary>忙碌集合的唯一写入口：每次都通知 `Busy`，让行的导出/删除按钮随之禁用与恢复。</summary>
    private void MarkBusy(string name, bool busy)
    {
        var changed = busy ? _busyRows.Add(name) : _busyRows.Remove(name);
        if (changed) Notify(nameof(Busy));
    }

    /// <summary>当前处于二次确认的实例名（无则空串）。</summary>
    public string Confirming => _confirming;

    /// <summary>创建表单提交成功后：刷新列表 → 按**后端归一化后的名字**跳转到该实例概览（上游 submit）。</summary>
    internal async Task OnCreatedAsync(ConfigCreateFormViewModel form, int generation, string createdName)
    {
        if (!IsCurrent(form, generation) || !await RefreshAsync() || !IsCurrent(form, generation)) return;
        CloseForm();
        Notice = "实例已创建，请设置模拟器连接";
        if (!string.IsNullOrEmpty(createdName)) OpenOverview?.Invoke(createdName);
    }

    internal void SetError(string message) => Error = message;

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

/// <summary>
/// 新建/导入表单（上游 `CreateInstance`）：名称、初始配置、可导入源（按需拉取）、
/// 本机文件上传；**导入源与初始配置互斥**，选中导入源会自动填实例名。
/// </summary>
public sealed class ConfigCreateFormViewModel : INotifyPropertyChanged
{
    private readonly ConfigManagerPageViewModel _page;
    private readonly IConfigInstancesBackend _backend;
    private readonly int _generation;
    private string _name = string.Empty;
    private string _source = string.Empty;
    private string _importFile = string.Empty;
    private string _error = string.Empty;
    private bool _loadingImports;
    private bool _busy;
    private bool _picking;
    public bool Picking { get => _picking; private set => SetField(ref _picking, value); }
    private bool IsCurrent => _page.IsCurrent(this, _generation);

    public ConfigCreateFormViewModel(ConfigManagerPageViewModel page, IConfigInstancesBackend backend, bool importFirst)
    {
        _page = page;
        _backend = backend;
        _generation = page.Generation;
        ImportFirst = importFirst;
        SubmitCommand = new ConfigPageCommand(_ => _ = SubmitAsync());
        PickImportCommand = new ConfigPageCommand(_ => _ = LoadImportsAsync());
        ChooseImportCommand = new ConfigPageCommand(parameter => ChooseImport(parameter as string ?? string.Empty));
        PickFileCommand = new ConfigPageCommand(_ => _ = PickLocalFileAsync());
    }

    public bool ImportFirst { get; }
    public ICommand SubmitCommand { get; }
    public ICommand PickImportCommand { get; }
    public ICommand ChooseImportCommand { get; }
    public ICommand PickFileCommand { get; }

    public ObservableCollection<ConfigImportSource> Imports { get; } = new();

    /// <summary>
    /// 导入源下拉的选项：**显示名与值分离**——值才是来源标识（空值即「使用默认配置」）。
    /// </summary>
    public sealed record ImportChoice(string Value, string Display)
    {
        public override string ToString() => Display;
    }

    public IReadOnlyList<ImportChoice> ImportChoices =>
        new[] { new ImportChoice(string.Empty, DefaultConfigLabel) }
            .Concat(Imports.Select(item => new ImportChoice(item.Name, item.Name)))
            .ToList();

    public string Title => "创建配置实例";
    public string Hint => "每个实例独立保存任务计划与模拟器连接。";
    public string ImportPickLabel => "选择本机配置文件…";
    public string ImportButtonLabel => LoadingImports ? "正在读取…" : "导入配置";
    public string ImportSelectLabel => "选择配置文件";
    public string ImportHint => "从已有配置文件导入，选中后自动填好实例名。";
    public string DefaultConfigLabel => "使用默认配置";
    public string NameLabel => "实例名称";
    public string InitialConfigLabel => "初始配置";
    public string SubmitLabel => Busy ? "正在创建…" : "创建实例";

    public bool HasImports => Imports.Count > 0;

    public bool LoadingImports
    {
        get => _loadingImports;
        private set
        {
            if (SetField(ref _loadingImports, value)) Notify(nameof(ImportButtonLabel));
        }
    }

    public bool Busy
    {
        get => _busy;
        private set { if (SetField(ref _busy, value)) Notify(nameof(SubmitLabel)); }
    }

    public string Name
    {
        get => _name;
        set { if (SetField(ref _name, value)) Notify(nameof(CanSubmit)); }
    }

    /// <summary>初始配置（来自现有实例）；选择它会清掉导入源（两处互斥）。</summary>
    public string Source
    {
        get => _source;
        set
        {
            if (!SetField(ref _source, value)) return;
            if (!string.IsNullOrEmpty(value)) SetImportFile(string.Empty);
        }
    }

    public string ImportFile
    {
        get => _importFile;
        private set => SetField(ref _importFile, value);
    }

    public void SelectSource(string source)
    {
        Source = source;
        SetImportFile(string.Empty);
    }

    public string Error
    {
        get => _error;
        private set { if (SetField(ref _error, value)) Notify(nameof(HasError)); }
    }

    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>实例名非空、长度 ≤64、且不含路径分隔符（上游 required + pattern + maxLength=64）。</summary>
    public bool CanSubmit => IsCurrent && !Busy && !Picking && !LoadingImports && IsValidName(Name);

    /// <summary>
    /// 实例名规则，**逐段移植**自上游 `app/instanceName.ts`：首字符为字母/数字/中日文，
    /// 其后 0–63 个字符可为字母、数字、下划线、点号、空格、中日文与短横线（合计 ≤64）。
    /// 该文件同时说明：保留名与首点号的拦截只在后端，表单放行的名字若不合规由后端报错。
    /// 各范围按上游写的转义序列照抄，避免兼容区字形写错码点。
    /// </summary>
    private const string NameCjk =
        "\u3041-\u3096" +                                    // 平假名
        "\u30a1-\u30fa\u30fc\u31f0-\u31ff\uff66-\uff9f" +    // 片假名（含半角）
        "\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff";           // 汉字（含扩展 A 与兼容区）

    private static readonly System.Text.RegularExpressions.Regex NamePattern =
        new($"^[A-Za-z0-9{NameCjk}][A-Za-z0-9_. {NameCjk}\\-]{{0,63}}$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    public static bool IsValidName(string name) =>
        !string.IsNullOrEmpty(name) && NamePattern.IsMatch(name);

    /// <summary>选择导入源：清掉初始配置，并在实例名为空时用它填名（上游 chooseImport）。</summary>
    public void ChooseImport(string chosen)
    {
        SetImportFile(chosen);
        if (!string.IsNullOrEmpty(chosen))
        {
            Source = string.Empty;
            if (string.IsNullOrEmpty(Name)) Name = chosen;
        }
        Notify(nameof(CanSubmit));
    }

    /// <summary>从「导入配置」入口进来时直接拉取可导入源（上游 startWithImport 时 useEffect 拉取）。</summary>
    public Task LoadImportsIfNeededAsync() => ImportFirst ? LoadImportsAsync() : Task.CompletedTask;

    /// <summary>按需拉取可导入源（上游点「导入配置」时才打接口）；失败只显示错误，不改动任何选择。</summary>
    public async Task LoadImportsAsync()
    {
        if (!IsCurrent || LoadingImports || Picking || Busy) return;
        LoadingImports = true;
        try
        {
            if (!await ReReadImportsAsync()) return;
            Error = string.Empty;
        }
        catch (OperationCanceledException) { }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            Error = failure.Message;
        }
        finally
        {
            LoadingImports = false;
        }
    }

    /// <summary>重读可导入源并把结果填进列表；**失败会抛出**（调用方自行决定是否保留原选择）。</summary>
    private async Task<bool> ReReadImportsAsync(CancellationToken cancellationToken = default)
    {
        var items = await _backend.ListImportsAsync(cancellationToken);
        if (!IsCurrent) return false;
        Imports.Clear();
        foreach (var item in items) Imports.Add(item);
        Notify(nameof(HasImports));
        return true;
    }

    /// <summary>
    /// 选择本机配置文件（上游 uploadImport 的顺序）：读文件 → **导入** → **重读可导入源**，
    /// 两步都成功后才选中并填名；任何一步失败都**保留用户原有输入**，不擅自改动选择。
    /// </summary>
    public async Task PickLocalFileAsync()
    {
        if (!IsCurrent || Picking || Busy || LoadingImports) return;
        if (_page.PickImportFileAsync is null)
        {
            Error = "当前环境无法选择文件。";
            return;
        }
        Picking = true;
        try
        {
            var picked = await _page.PickImportFileAsync();
            if (picked is not { } file || !IsCurrent) return;
            var stem = file.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? file.Name[..^5]
                : file.Name;
            await _backend.ImportConfigAsync(stem, file.Content);
            if (!IsCurrent || !await ReReadImportsAsync()) return;
            ChooseImport(stem);
            Error = string.Empty;
        }
        catch (OperationCanceledException) { }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            Error = failure.Message;   // 失败保留输入（Name/Source/ImportFile 都不动）
        }
        finally { Picking = false; }
    }

    public async Task SubmitAsync()
    {
        if (!CanSubmit) return;
        Busy = true;
        Error = string.Empty;
        try
        {
            // 后端会归一化首尾空白与尾点，用返回的规范名（上游注释）。
            var created = await _backend.CreateInstanceAsync(Name, Source is "" ? null : Source,
                ImportFile is "" ? null : ImportFile);
            await _page.OnCreatedAsync(this, _generation, created);
        }
        catch (OperationCanceledException) { }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            Error = failure.Message;   // 失败保留输入
        }
        finally
        {
            Busy = false;
        }
    }

    private void SetImportFile(string value)
    {
        if (_importFile == value) return;
        _importFile = value;
        Notify(nameof(ImportFile));
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

internal sealed class ConfigPageCommand(Action<object?> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute(parameter);
}
