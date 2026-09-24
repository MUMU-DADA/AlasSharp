using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Alas.UI.DevTools;

/// <summary>
/// 开发者工具页（上游 <c>/dev</c>）需要的**页面局部能力**：模拟实例状态徽章、更新角标预览、故意抛错自检。
/// 与其他页面一样只依赖局部接口，不触碰共享外壳接线与后端实现。
/// </summary>
public interface IDevToolsBackend
{
    /// <summary>读取当前开发者状态（是否开启、正在模拟的状态、更新角标是否预览中）。</summary>
    Task<DevToolsStatus> ReadStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>模拟一个实例状态（running/error/updating），上游会 10 秒后自动恢复。</summary>
    Task<DevToolsStatus> SimulateAsync(string status, CancellationToken cancellationToken = default);

    /// <summary>清除模拟（上游 developer.simulateClear）。</summary>
    Task<DevToolsStatus> ClearSimulationAsync(CancellationToken cancellationToken = default);

    /// <summary>切换侧栏「有新版本」角标预览（上游 developer.updateNoticeToggle）。</summary>
    Task<DevToolsStatus> SetUpdateNoticeAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>故意抛一个异常用于检查错误页（上游 developer.throwTest）；未接能力时如实报告不可用。</summary>
    Task ThrowTestAsync(CancellationToken cancellationToken = default);
}

/// <summary>开发者工具状态。</summary>
public sealed record DevToolsStatus(bool Enabled, string? Simulated, bool UpdateNoticePreview, string? Error = null)
{
    public bool IsSimulating => !string.IsNullOrEmpty(Simulated);

    public bool CanUseTools => Enabled && string.IsNullOrEmpty(Error);

    /// <summary>模拟文案与上游 i18n 一致（developer.simulating / developer.simulateIdleHint）。</summary>
    public string SimulationLabel => Simulated switch
    {
        "running" => "正在模拟：运行中",
        "error" => "正在模拟：异常",
        "updating" => "正在模拟：更新中",
        _ => "当前没有模拟状态。",
    };
}

/// <summary>未接能力时的默认实现：一切操作如实报告不可用，不伪造模拟成功。</summary>
public sealed class DisconnectedDevToolsBackend : IDevToolsBackend
{
    public static DisconnectedDevToolsBackend Instance { get; } = new();

    public const string Notice = "开发者工具尚未接线：需要外壳注入开发者工具能力后可用。";

    private static DevToolsStatus Off => new(false, null, false, Notice);

    public Task<DevToolsStatus> ReadStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(Off);

    public Task<DevToolsStatus> SimulateAsync(string status, CancellationToken cancellationToken = default) => Task.FromResult(Off);

    public Task<DevToolsStatus> ClearSimulationAsync(CancellationToken cancellationToken = default) => Task.FromResult(Off);

    public Task<DevToolsStatus> SetUpdateNoticeAsync(bool enabled, CancellationToken cancellationToken = default) => Task.FromResult(Off);

    public Task ThrowTestAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
/// 开发者工具页：标题「开发者工具」+ 快捷工具（模拟状态图标 / 清除模拟 / 更新角标 / 抛出异常）。
/// 未连接或未开启开发者模式时，按钮禁用并显示真实原因，**不伪造模拟或自检成功**。
/// </summary>
public sealed class DevToolsView : UserControl
{
    private readonly DevToolsViewModel _model;
    private Panel? _root;
    private Panel? _modalHost;
    private ScrollViewer? _scroller;
    private Border? _overlay;

    public DevToolsView()
        : this(DisconnectedDevToolsBackend.Instance)
    {
    }

    public DevToolsView(IDevToolsBackend backend)
    {
        _model = new DevToolsViewModel(backend);
        DataContext = _model;
        Content = Build();
        _ = _model.RefreshAsync();
    }

    public DevToolsViewModel Model => _model;

    /// <summary>
    /// 外壳注入点（与其它页面的 Backend 属性同一模式）：写入当前主题后，页面会重算
    /// "是否走玻璃材质"（视觉实验室据此显示/隐藏）。主题变化时请再次赋值。
    /// 属性名不叫 Theme：`StyledElement.Theme` 已占用该名字，隐藏它会拿到 CS0108 警告，
    /// 也让"这是我们的皮肤枚举还是控件的 ControlTheme"变得含糊。
    /// </summary>
    public Alas.UI.Theming.UiTheme UiTheme
    {
        get => _model.Theme;
        set => _model.Theme = value;
    }

    /// <summary>
    /// 外壳注入点：Modal 覆盖层挂载到哪个分层容器。
    ///
    /// 覆盖层必须是**滚动容器之外的兄弟节点**，否则它会成为滚动内容的一部分——页面很长时
    /// "打开后弹窗在视口外、还要再滚一次才点得到取消"，那就不是模态。外壳接入时把这个属性
    /// 指向整壳根部的覆盖层容器（不滚动、不裁剪），Modal 就会跟着整壳视口居中；不注入时
    /// 覆盖层由页面根部分层容器承载，页面自身仍然是正确的模态。被指向的容器必须不裁剪
    /// （不要放进 ScrollViewer）：裁剪祖先会切掉覆盖层，那种情况下如实反映为"覆盖层不完整"，
    /// 页面不另做逐容器兜底。
    /// </summary>
    public Panel? ModalHost
    {
        get => _modalHost;
        set
        {
            if (ReferenceEquals(_modalHost, value)) return;
            if (_overlay is not null)
            {
                // 先摘下原来的父节点：控件一次只能有一个可视父节点，直接挂到新宿主上会被拒绝。
                if (_overlay.GetVisualParent() is Panel previous) previous.Children.Remove(_overlay);
                else if (_root is not null) _root.Children.Remove(_overlay);
            }
            _modalHost = value;
            if (_overlay is null) return;
            var host = value ?? _root;
            if (host is not null)
            {
                host.Children.Add(_overlay);
                AlignOverlayToViewport();
            }
        }
    }

    /// <summary>
    /// 外壳注入点（按 master 的承载约定）：设置后由 VM 清掉旧后端的模拟窗口并按新后端刷新。
    /// 未注入时保持"未连接"的真实状态，不伪造开发者工具状态。
    /// </summary>
    public IDevToolsBackend Backend
    {
        get => _model.Backend;
        set => _model.Backend = value;
    }

    /// <summary>
    /// 设计系统展示里的一个颜色令牌：色块 + 名称。颜色用皮肤里的键（DynamicResource），
    /// 因此在六套皮肤下显示的都是当前皮肤的真实取值，而不是写死的示例色。
    /// </summary>
    private static Control Token(string resourceKey, string label)
    {
        var swatch = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
        };
        swatch[!Border.BackgroundProperty] =
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(resourceKey);
        swatch[!Border.BorderBrushProperty] =
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AlasBorderBrush");
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Width = 150 };
        stack.Children.Add(swatch);
        stack.Children.Add(new TextBlock { Text = label, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        return stack;
    }

    private Control Build()
    {
        var title = new TextBlock
        {
            Name = "DevToolsTitle", Text = "开发者工具",
            // 外壳的页面标题样式（32/窄屏 40），不写死字号。
            Classes = { "page-title" },
            Margin = new Thickness(0, 28, 0, 14),
        };
        var enabled = new TextBlock { Name = "DevToolsEnabledLabel", Text = "开发者模式已开启", FontSize = 12, IsVisible = _model.Enabled };
        var notice = new TextBlock
        {
            Name = "DevToolsNotice", Text = _model.Notice, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            IsVisible = _model.HasNotice,
        };
        var quickTitle = new TextBlock { Name = "DevToolsQuickToolsTitle", Text = "快捷工具", FontSize = 14, FontWeight = FontWeight.SemiBold };
        var simulateTitle = new TextBlock { Name = "DevToolsSimulateTitle", Text = "模拟状态图标", FontSize = 13 };
        var simulateHint = new TextBlock
        {
            Name = "DevToolsSimulateHint",
            Text = "临时把实例列表上的状态徽章改成指定值，10 秒后自动恢复。",
            FontSize = 12, TextWrapping = TextWrapping.Wrap,
        };
        var running = new Button { Name = "DevToolsSimulateRunningButton", Content = "模拟运行中", Padding = new Thickness(12, 7), IsEnabled = _model.CanUseTools, Command = _model.SimulateRunningCommand };
        var error = new Button { Name = "DevToolsSimulateErrorButton", Content = "模拟异常", Padding = new Thickness(12, 7), IsEnabled = _model.CanUseTools, Command = _model.SimulateErrorCommand };
        var updating = new Button { Name = "DevToolsSimulateUpdatingButton", Content = "模拟更新中", Padding = new Thickness(12, 7), IsEnabled = _model.CanUseTools, Command = _model.SimulateUpdatingCommand };
        var clear = new Button { Name = "DevToolsSimulateClearButton", Content = "清除模拟", Padding = new Thickness(12, 7), IsEnabled = _model.CanClear, Command = _model.ClearCommand };
        var simulation = new TextBlock { Name = "DevToolsSimulationLabel", Text = _model.SimulationLabel, FontSize = 12 };
        var noticeTitle = new TextBlock { Name = "DevToolsUpdateNoticeTitle", Text = "更新提示", FontSize = 13, Margin = new Thickness(0, 10, 0, 0) };
        var noticeHint = new TextBlock { Name = "DevToolsUpdateNoticeHint", Text = "预览「有新版本」角标在侧栏的样子。", FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var toggle = new Button { Name = "DevToolsUpdateNoticeButton", Content = "切换更新角标", Padding = new Thickness(12, 7), IsEnabled = _model.CanUseTools, Command = _model.ToggleUpdateNoticeCommand };
        var throwTitle = new TextBlock { Name = "DevToolsThrowTitle", Text = "抛出异常", FontSize = 13, Margin = new Thickness(0, 10, 0, 0) };
        var throwHint = new TextBlock
        {
            Name = "DevToolsThrowHint",
            Text = "故意抛一个前端异常，用来检查错误页；测试后需要刷新页面。",
            FontSize = 12, TextWrapping = TextWrapping.Wrap,
        };
        var throwButton = new Button { Name = "DevToolsThrowButton", Content = "抛出异常", Padding = new Thickness(12, 7), IsEnabled = _model.CanUseTools, Command = _model.ThrowCommand };

        _model.PropertyChanged += (_, args) =>
        {
            switch (args.PropertyName)
            {
                case nameof(DevToolsViewModel.Enabled):
                    enabled.IsVisible = _model.Enabled;
                    SetEnabled(_model.CanUseTools, running, error, updating, toggle, throwButton);
                    break;
                case nameof(DevToolsViewModel.Notice):
                    notice.Text = _model.Notice;
                    notice.IsVisible = _model.HasNotice;
                    break;
                case nameof(DevToolsViewModel.SimulationLabel):
                    simulation.Text = _model.SimulationLabel;
                    break;
                case nameof(DevToolsViewModel.CanClear):
                    clear.IsEnabled = _model.CanClear;
                    break;
                case nameof(DevToolsViewModel.CanUseTools):
                    SetEnabled(_model.CanUseTools, running, error, updating, toggle, throwButton);
                    break;
            }
        };

        // 上游只有一个 Modal 状态（modalOpen）：按钮在“按钮与操作”里，弹层是**页面级覆盖层**——
        // 撑满视口、带遮罩（拦截底层输入）、内容居中、可聚焦以接收 Esc；关闭后焦点回到打开它的按钮。
        var modalCancel = Themed("DevToolsModalCancel", "GhostButtonTheme", "取消");
        var modalConfirm = Themed("DevToolsModalConfirm", "PrimaryButtonTheme", "确认");
        var modalInput = new TextBox { Name = "DevToolsModalInput", Text = "AzurPilot Dev Mode", FontSize = 12 };
        var modalCard = new Border
        {
            Name = "DevToolsModalCard", Classes = { "panel" }, Padding = new Thickness(16, 14), Width = 360,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Name = "DevToolsModalTitle", Text = "Modal 样式预览", FontSize = 14, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = "这里使用项目真实 Modal、表单与按钮样式。", FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = "示例输入", FontSize = 12 },
                    modalInput,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { modalCancel, modalConfirm } },
                },
            },
        };
        var modalOverlay = new Border
        {
            Name = "DevToolsModalOverlay", IsVisible = false, Focusable = true,
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(0x66, 0, 0, 0)),
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            Child = modalCard,
        };
        _overlay = modalOverlay;
        var openModalButton = Themed("DevToolsOpenModalButton", "GhostButtonTheme", "打开 Modal");
        void CloseModal()
        {
            modalOverlay.IsVisible = false;
            SetContentHitTestable(true);
            openModalButton.Focus();   // 关闭后焦点回到打开它的控件
        }
        openModalButton.Click += (_, _) =>
        {
            modalOverlay.IsVisible = true;
            SetContentHitTestable(false);
            AlignOverlayToViewport();
            modalOverlay.Focus();      // 焦点移入覆盖层，Esc 才能被它收到
        };
        modalCancel.Click += (_, _) => CloseModal();
        modalConfirm.Click += (_, _) => CloseModal();
        // 焦点与键盘：路由事件只沿可视祖先上浮/下沉，而覆盖层是**兄弟节点**（内容之外），
        // 所以它收不到内容树里的事件。Esc 与 Tab 约束因此挂在具体被聚焦的控件上，
        // 覆盖层自己的处理器保留给"焦点在遮罩本体上"的情况（两层都设 Handled，不会重复处理）。
        void TrapKey(object? _, Avalonia.Input.KeyEventArgs args)
        {
            if (args.Key == Avalonia.Input.Key.Escape)
            {
                CloseModal();
                args.Handled = true;
                return;
            }
            if (args.Key != Avalonia.Input.Key.Tab) return;
            // 焦点约束：Tab / Shift+Tab 只在覆盖层内的可聚焦控件之间循环，不跑到页面其他部分。
            var ring = new Control[] { modalInput, modalCancel, modalConfirm };
            var backwards = args.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift);
            var current = Array.FindIndex(ring, control => control.IsFocused);
            var next = current < 0
                ? (backwards ? ring.Length - 1 : 0)
                : (current + (backwards ? ring.Length - 1 : 1)) % ring.Length;
            ring[next].Focus();
            args.Handled = true;
        }
        modalOverlay.KeyDown += TrapKey;
        modalInput.KeyDown += TrapKey;
        modalCancel.KeyDown += TrapKey;
        modalConfirm.KeyDown += TrapKey;

        // 输入拦截：遮罩挡住的不只是**指针命中**，还有已经落在内容上的键盘与手势事件。
        // 覆盖层不是内容的祖先（它在分层容器里与滚动内容并列），无法靠路由拦截，
        // 因此打开时关掉内容树的命中测试、关闭时恢复。
        void SetContentHitTestable(bool value)
        {
            if (_scroller is not null) _scroller.IsHitTestVisible = value;
        }

        // 视觉实验室：只在材质主题显示，并**跟随主题变化**（外壳注入 Theme 后这里重算）。
        var visualLab = VisualLab();
        visualLab.IsVisible = _model.UsesMaterial;
        _model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(DevToolsViewModel.UsesMaterial))
            {
                visualLab.IsVisible = _model.UsesMaterial;
            }
        };

        var rows = new StackPanel
        {
            Name = "DevToolsPage", Spacing = 8,
            Children =
            {
                title, enabled, notice,
                // 上游 DevControls 其实是一整页"设计系统展示"（dev-intro + quickTools + visualLab + layers +
                // colorsTokens + typography + buttons + formControls + selectorsStatus + layout + dataTables + feedback）。
                // 本步先补已核对的 colorsTokens 一块：用皮肤键渲染色块，六套皮肤下显示各自真实取值。
                new Border
                {
                    Name = "DevToolsColorsTokensPanel",
                    Classes = { "panel" },
                    Padding = new Thickness(16, 12),
                    Margin = new Thickness(0, 0, 0, 12),
                    Child = new StackPanel
                    {
                        Spacing = 8,
                        Children =
                        {
                            new TextBlock { Name = "DevToolsColorsTokensTitle", Text = "颜色与令牌", FontSize = 14, FontWeight = FontWeight.SemiBold },
                            new WrapPanel
                            {
                                ItemSpacing = 10, LineSpacing = 8,
                                Children =
                                {
                                    Token("AlasAccentBrush", "强调色"),
                                    Token("AlasAccentHoverBrush", "强调色（悬停）"),
                                    Token("AlasAccentSoftBrush", "强调色（浅）"),
                                    Token("AlasSurfaceBrush", "表面"),
                                    Token("AlasSurfaceMutedBrush", "表面（弱）"),
                                    Token("AlasBorderBrush", "边框"),
                                    Token("AlasDangerBrush", "危险"),
                                    Token("AlasTextBrush", "正文"),
                                    Token("AlasWarningBrush", "警告"),
                                },
                            },
                        },
                    },
                },
                // 上游是 section.panel.config-group.dev-quick-tools：快捷工具整块套面板（外观取自皮肤）。
                new Border
                {
                    Name = "DevToolsQuickToolsPanel",
                    Classes = { "panel" },
                    Padding = new Thickness(16, 12),
                    Margin = new Thickness(0, 0, 0, 12),
                    Child = new StackPanel
                    {
                        Spacing = 8,
                        Children =
                        {
                            quickTitle, simulateTitle, simulateHint,
                            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { running, error, updating, clear } },
                            simulation,
                        },
                    },
                },
                noticeTitle, noticeHint, toggle,
                throwTitle, throwHint, throwButton,
                // 以下按上游 DevControls.tsx 的区块顺序补展示面板，文案逐条取自 i18n.dev.ts。
                Panel("DevToolsIntroPanel", "UI 试验场",
                    "集中展示项目真实控件与视觉系统。调整 tokens.css、components.css、apple.css 后可在这里一次检查各种状态。",
                    "仅 Dev 模式可见"),
                Panel("DevToolsTypographyPanel", "文字层级与内容样式",
                    TypeSample("页面标题", "AzurPilot 开发者", 22, FontWeight.Bold, false),
                    TypeSample("章节标题", "界面样式检查", 16, FontWeight.SemiBold, false),
                    TypeSample("卡片标题", "实例运行状态", 14, FontWeight.SemiBold, false),
                    TypeSample("正文", "用于检查正文文字的颜色、字重、行高和中英文混排效果。The quick brown fox jumps over the lazy dog.", 13, FontWeight.Normal, false),
                    TypeSample("弱化 / 次要文字", "这是弱化说明文本，用来观察背景变化时的可读性。", 12, FontWeight.Normal, true),
                    TypeSample("代码", "Scheduler.Enable = true", 12, FontWeight.Normal, false, monospace: true),
                    TypeSample("数字", "12,345.67 / 99.8%", 14, FontWeight.SemiBold, false),
                    TypeSample("省略", "这是一段故意非常非常非常非常非常长的文本，用于观察单行溢出、省略号和窄容器表现", 12, FontWeight.Normal, true, ellipsis: true)),
                Panel("DevToolsButtonsPanel", "按钮与操作",
                    new TextBlock { Name = "DevToolsButtonStylesHint", Text = "按钮样式：默认、主按钮、次按钮、危险、禁用", FontSize = 11, Opacity = 0.7 },
                    new WrapPanel
                    {
                        ItemSpacing = 8, LineSpacing = 8,
                        Children =
                        {
                            new Button { Name = "DevToolsButtonDefault", Content = "默认按钮", Padding = new Thickness(12, 7) },
                            Themed("DevToolsButtonPrimary", "PrimaryButtonTheme", "主按钮"),
                            Themed("DevToolsButtonSecondary", "GhostButtonTheme", "次按钮"),
                            Themed("DevToolsButtonDanger", "DangerButtonTheme", "危险操作"),
                            Themed("DevToolsButtonDangerSubtle", "DangerSubtleButtonTheme", "危险 · Subtle"),
                            new Button { Name = "DevToolsButtonDisabled", Content = "禁用按钮", Padding = new Thickness(12, 7), IsEnabled = false },
                        },
                    },
                    new TextBlock { Name = "DevToolsLightActionsHint", Text = "轻量操作：图标按钮、文字按钮、弹窗", FontSize = 11, Opacity = 0.7 },
                    new WrapPanel
                    {
                        ItemSpacing = 8, LineSpacing = 8,
                        Children =
                        {
                            Themed("DevToolsIconButtonSettings", "IconButtonTheme", "⚙"),
                            Themed("DevToolsIconButtonClose", "IconButtonTheme", "✕"),
                            Themed("DevToolsTextButton", "TextButtonTheme", "文字按钮"),
                            openModalButton,
                        },
                    }),
                Panel("DevToolsFeedbackPanel", "反馈状态",
                    FeedbackSample("错误提示", ErrorSample("这是用于检查错误提示布局的示例消息。")),
                    FeedbackSample("加载状态", new TextBlock { Name = "DevToolsLoadingSample", Text = "正在加载…", FontSize = 12, Opacity = 0.7 }),
                    FeedbackSample("空状态", new StackPanel
                    {
                        Spacing = 2,
                        Children =
                        {
                            new TextBlock { Name = "DevToolsEmptySample", Text = "暂无内容", FontSize = 12 },
                            new TextBlock { Text = "用于观察空状态的字号、间距和图标。", FontSize = 11, Opacity = 0.65 },
                        },
                    })),
                // 上游的 Modal 预览：本页内的本地弹层（不调后端、不影响外壳）。
                // 上游的「打开 Modal」按钮在“按钮与操作”里（那里已放），Modal 覆盖层在页面根部；
                // 这一块只做说明，避免同一控件被挂到两个父节点上。
                Panel("DevToolsModalPanel", "Modal 预览",
                    SectionLabel("打开 Modal", "这里使用项目真实 Modal、表单与按钮样式。")),
                // 上游 visualLab 只在材质皮肤（classic 的浅/深色）显示：可见性由 _model.UsesMaterial
                // 在构建时设置、并在主题变化时更新（见上面的订阅）。
                visualLab,
                // 上游 formControls：14 个字段，标签/说明逐条取自 i18n.dev.ts；控件复用设置页的 field-row 外观。
                Panel("DevToolsFormControlsPanel", "表单控件",
                    FieldRow("文本输入框", "普通 text input，包括 placeholder、focus 与输入文字。",
                        Input("DevToolsTextInput", "dev-text", "AzurPilot")),
                    FieldRow("数字输入框", "项目 FieldInput 的 number/int 样式。",
                        Input("DevToolsNumberInput", "dev-number", "25548")),
                    FieldRow("密码输入框", string.Empty,
                        Input("DevToolsPasswordInput", "dev-password", "developer", password: true)),
                    FieldRow("下拉框", "使用项目真实 select 渲染路径。",
                        Combo("DevToolsSelectInput", new[] { "Auto", "ADB", "Nemulator" })),
                    FieldRow("日期时间", string.Empty, Input("DevToolsDateTimeInput", "dev-datetime", "2026-09-15 09:00:00")),
                    FieldRow("月份选择", string.Empty, Input("DevToolsMonthInput", "dev-month", "2026-09")),
                    FieldRow("开关", "开启、关闭与禁用状态。",
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal, Spacing = 12,
                            Children =
                            {
                                Toggle("DevToolsToggleOn", "开启状态", true),
                                Toggle("DevToolsToggleOff", "关闭状态", false),
                                Toggle("DevToolsToggleDisabled", "禁用开关", true, disabled: true),
                            },
                        }),
                    FieldRow("多选框", "项目 multiselect / checkbox 组合。",
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal, Spacing = 12,
                            Children =
                            {
                                Toggle("DevToolsMultiAlas", "Alas", true),
                                Toggle("DevToolsMultiOpsi", "Opsi", true),
                                Toggle("DevToolsMultiCommission", "Commission", false),
                                Toggle("DevToolsMultiEvent", "Event", false),
                            },
                        }),
                    FieldRow("禁用输入框", string.Empty, Input("DevToolsDisabledInput", "dev-disabled", "不可编辑", disabled: true)),
                    FieldRow("错误输入框", "用于调整 aria-invalid 对应的错误状态。",
                        new StackPanel
                        {
                            Spacing = 4,
                            Children =
                            {
                                Input("DevToolsInvalidInput", "dev-invalid", "invalid-value"),
                                ErrorSample(),
                            },
                        }),
                    FieldRow("带图标输入框", string.Empty,
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal, Spacing = 6,
                            Children =
                            {
                                new TextBlock { Text = "🔍", FontSize = 13, VerticalAlignment = VerticalAlignment.Center },
                                Input("DevToolsIconInput", "dev-search", "搜索任务、配置或实例…"),
                            },
                        }),
                    FieldRow("多行输入框", string.Empty,
                        Input("DevToolsTextarea", "dev-textarea", "这里用于观察多行输入框的字号、行高、圆角与聚焦状态。", multiline: true)),
                    FieldRow("YAML 编辑器", "CodeMirror 实际编辑器，用于检查代码字体、边框、选中与暗色主题。",
                        Input("DevToolsYamlEditor", "dev-yaml", "Scheduler:\n  Enable: true\n  SuccessInterval: 30", multiline: true, monospace: true))),
                // 上游 selectorsStatus：分段控制器 + 状态徽标 + 统计页 Tabs（都有本地选中态）。
                Panel("DevToolsSelectorsStatusPanel", "选择器与状态",
                    SectionLabel("分段控制器", "复用运行监控页的 segmented control。"),
                    Segment("DevToolsSegmentControl", new[] { "日志", "截图" }, 0),
                    SectionLabel("状态徽标", "运行、待命、错误、更新。"),
                    new WrapPanel
                    {
                        ItemSpacing = 8, LineSpacing = 8,
                        Children =
                        {
                            Badge("DevToolsBadgeRunning", "运行中", "running"),
                            Badge("DevToolsBadgeStopped", "待命", "waiting"),
                            Badge("DevToolsBadgeError", "错误", "error"),
                            Badge("DevToolsBadgeUpdating", "更新中", "updating"),
                            new Border
                            {
                                Classes = { "count-badge" }, Child = new TextBlock { Text = "12", FontSize = 11 },
                            },
                            new TextBlock { Text = "● 实时", FontSize = 11, VerticalAlignment = VerticalAlignment.Center },
                            new Border
                            {
                                Classes = { "update-notice" },
                                Child = new TextBlock { Text = "新着", FontSize = 11 },
                            },
                        },
                    },
                    SectionLabel("统计页 Tabs", "检查胶囊滑块、长文本和选中态。"),
                    Segment("DevToolsStatsTabs", new[] { "资源", "掉落", "行动", "委托" }, 0)),
                // 上游 layout：导航、卡片与层级关系（静态预览，仅本页展示）。
                Panel("DevToolsLayoutPanel", "导航、卡片与层级关系",
                    SectionLabel("主导航", "普通、当前、悬停三种状态。"),
                    new StackPanel
                    {
                        Name = "DevToolsNavPreview", Spacing = 2, Width = 220,
                        Children =
                        {
                            NavItem("DevToolsNavNormal", "普通导航", false),
                            NavItem("DevToolsNavCurrent", "当前页面", true),
                            NavItem("DevToolsNavHover", "悬停查看", false),
                        },
                    },
                    SectionLabel("一级任务菜单", "展开态与子菜单项。"),
                    new StackPanel
                    {
                        Name = "DevToolsTaskGroupPreview", Spacing = 2, Width = 220,
                        Children =
                        {
                            new Button
                            {
                                Name = "DevToolsTaskGroupButton", Content = "一级任务菜单", FontSize = 13,
                                Padding = new Thickness(10, 8), HorizontalAlignment = HorizontalAlignment.Stretch,
                                Classes = { "task-group" },
                            },
                            new Button
                            {
                                Name = "DevToolsSubmenuCurrent", Content = "当前子菜单", FontSize = 11,
                                Padding = new Thickness(8, 6), Classes = { "task-submenu-item", "active" },
                            },
                            new Button
                            {
                                Name = "DevToolsSubmenuNormal", Content = "普通子菜单", FontSize = 11,
                                Padding = new Thickness(8, 6), Classes = { "task-submenu-item" },
                            },
                        },
                    },
                    SectionLabel("实例卡片", "名称、设备与状态。"),
                    new Border
                    {
                        Name = "DevToolsInstanceCard", Classes = { "panel" }, Padding = new Thickness(14, 12), Width = 260,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Child = new StackPanel
                        {
                            Spacing = 6,
                            Children =
                            {
                                Badge("DevToolsInstanceCardBadge", "运行中", "running"),
                                new TextBlock { Text = "dev-instance", FontSize = 14, FontWeight = FontWeight.SemiBold },
                                new TextBlock { Text = "ADB · 127.0.0.1:5555", FontSize = 12, Opacity = 0.7 },
                                new TextBlock { Text = "正在运行主线任务 →", FontSize = 12 },
                            },
                        },
                    },
                    SectionLabel("指标卡片", "数值与上限。"),
                    new WrapPanel
                    {
                        Name = "DevToolsMetricCards", ItemSpacing = 8, LineSpacing = 8,
                        Children =
                        {
                            MetricCard("DevToolsMetricCoin", "物资", "52,840", "/ 600,000"),
                            MetricCard("DevToolsMetricOil", "石油", "14,320", "/ 25,000"),
                            MetricCard("DevToolsMetricRuns", "运行次数", "128", "次"),
                        },
                    }),
                // 上游 layers：玻璃、表面与层级（表面/阴影/圆角三组样例）。
                Panel("DevToolsLayersPanel", "玻璃、表面与层级",
                    SectionLabel("表面", "var(--surface) / --surface-muted / --accent-soft / 玻璃材质。"),
                    new WrapPanel
                    {
                        Name = "DevToolsSurfaceGrid", ItemSpacing = 10, LineSpacing = 10,
                        Children =
                        {
                            SurfaceSample("DevToolsSurfacePlain", "普通表面", "AlasSurfaceBrush"),
                            SurfaceSample("DevToolsSurfaceMuted", "弱化表面", "AlasSurfaceMutedBrush"),
                            SurfaceSample("DevToolsSurfaceAccent", "强调表面", "AlasAccentSoftBrush"),
                            SurfaceSample("DevToolsSurfaceGlass", "项目真实玻璃材质", "AlasGlassTintBrush"),
                        },
                    },
                    SectionLabel("阴影", "无 / 轻 / 面板 / 浮层 / Modal。"),
                    new WrapPanel
                    {
                        Name = "DevToolsShadowGrid", ItemSpacing = 10, LineSpacing = 10,
                        Children =
                        {
                            ShadowSample("DevToolsShadowNone", "无阴影", "none"),
                            ShadowSample("DevToolsShadowLight", "轻阴影", "0 4px 14px #00000010"),
                            ShadowSample("DevToolsShadowPanel", "面板阴影", "AlasPanelShadow"),
                            ShadowSample("DevToolsShadowFloating", "浮层阴影", "0 18px 56px #0003"),
                            ShadowSample("DevToolsShadowModal", "Modal 阴影", "0 24px 100px #0003"),
                        },
                    },
                    SectionLabel("圆角", "0 / 6 / 10 / 14 / 18 / 22 / 26 / 32 / 胶囊。"),
                    new WrapPanel
                    {
                        Name = "DevToolsRadiusGrid", ItemSpacing = 10, LineSpacing = 10,
                        Children =
                        {
                            RadiusSample("DevToolsRadius0", 0), RadiusSample("DevToolsRadius6", 6),
                            RadiusSample("DevToolsRadius10", 10), RadiusSample("DevToolsRadius14", 14),
                            RadiusSample("DevToolsRadius18", 18), RadiusSample("DevToolsRadius22", 22),
                            RadiusSample("DevToolsRadius26", 26), RadiusSample("DevToolsRadius32", 32),
                            RadiusSample("DevToolsRadiusPill", 999),
                        },
                    }),
                // 上游 dataTables：数据、表格与滚动区域（工具栏 + 3 行表格 + 12 行滚动样例）。
                Panel("DevToolsDataTablesPanel", "数据、表格与滚动区域",
                    new StackPanel
                    {
                        Name = "DevToolsTableToolbar", Orientation = Orientation.Horizontal, Spacing = 10,
                        Children =
                        {
                            new TextBox
                            {
                                Name = "DevToolsTableSearch", Width = 220, FontSize = 12,
                                Watermark = "搜索任务、配置或实例…",
                            },
                            new TextBlock { Text = "3 条记录", FontSize = 12, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center },
                        },
                    },
                    new StackPanel
                    {
                        Name = "DevToolsTable", Spacing = 0,
                        Children =
                        {
                            TableRow(true, "时间", "任务", "状态", "耗时"),
                            TableRow(false, "09:18:02", "主线出击", "完成", "01:42"),
                            TableRow(false, "09:15:43", "科研项目", "同步", "00:18"),
                            TableRow(false, "09:12:10", "委托检查", "待命", "00:06"),
                        },
                    },
                    SectionLabel("滚动区域", "检查滚动条、行高与边缘裁切。"),
                    new ScrollViewer
                    {
                        Name = "DevToolsScrollSample", Height = 160, Width = 320,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                        Content = ScrollSample(),
                    }),
            },
        };
        // 页面根部是**分层容器**：滚动内容在下、Modal 覆盖层在上。覆盖层与滚动内容是兄弟节点，
        // 不是滚动内容的一部分——放进滚动内容里，页面一长"打开后弹窗就在视口外"，
        // 用户还得再滚一次才点得到取消，那就不算模态。页面自己持有滚动容器，
        // 因此这块超长展示页在任何宿主里都滚得动（此前依赖外层给 ScrollViewer，没有就够不着）。
        var scroller = new ScrollViewer
        {
            Name = "DevToolsScroll",
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = rows,
        };
        var root = new Panel { Name = "DevToolsRoot", Children = { scroller } };
        _scroller = scroller;
        var host = _modalHost ?? root;
        host.Children.Add(modalOverlay);
        // 视口尺寸变化后重算覆盖层矩形：弹窗要保持"当前可见区域居中"，不能停在旧尺寸上。
        LayoutUpdated += (_, _) =>
        {
            if (modalOverlay.IsVisible) AlignOverlayToViewport();
        };
        // 换宿主会把覆盖层重新挂一次：挂上之后布局才有真实坐标，这里再对齐一次。
        modalOverlay.AttachedToVisualTree += (_, _) =>
        {
            if (modalOverlay.IsVisible) AlignOverlayToViewport();
        };
        _root = root;
        return root;
    }

    /// <summary>
    /// 把覆盖层对齐到**当前视口**矩形。
    ///
    /// 覆盖层的父节点是分层容器：它本身撑满宿主时，覆盖层跟着它走就是对的（外壳注入整壳根部时
    /// 属于这种情况）；但父节点若处在可滚动/带偏移的容器里，直接撑满父节点会把弹窗摆到视口之外。
    /// 这里按父节点相对顶层视口的位置补偿偏移，统一得到"覆盖整个可见区域、弹窗居中"的结果，
    /// 不需要外壳把注入点放在哪个特定层级上。父节点被裁剪（例如被放进 ScrollViewer）时，
    /// 裁剪来自外壳的挂载选择，页面不做逐容器兜底。
    /// </summary>
    private void AlignOverlayToViewport()
    {
        if (_overlay is null) return;
        var host = _overlay.Parent as Visual;
        var viewport = TopLevel.GetTopLevel(this);
        if (host is null || viewport is null || ReferenceEquals(viewport, host)) { ResetOverlayRect(); return; }
        var origin = host.TranslatePoint(new Point(0, 0), viewport);
        if (origin is not { } topLeft) { ResetOverlayRect(); return; }
        var size = viewport.Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) { ResetOverlayRect(); return; }
        _overlay.Width = size.Width;
        _overlay.Height = size.Height;
        _overlay.Margin = new Thickness(-topLeft.X, -topLeft.Y, 0, 0);
    }

    /// <summary>回到"撑满父容器"的默认形态（父节点就是视口根时不需要补偿）。</summary>
    private void ResetOverlayRect()
    {
        if (_overlay is null) return;
        _overlay.Width = double.NaN;
        _overlay.Height = double.NaN;
        _overlay.Margin = default;
    }

    /// <summary>上游 section.panel.config-group：一块展示面板（标题 + 内容）。</summary>
    private static Border Panel(string name, string title, params Control[] children)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock
        {
            Name = name + "Title", Text = title, FontSize = 14, FontWeight = FontWeight.SemiBold,
        });
        foreach (var child in children) stack.Children.Add(child);
        return new Border
        {
            Name = name, Classes = { "panel" }, Padding = new Thickness(16, 12),
            Margin = new Thickness(0, 0, 0, 12), Child = stack,
        };
    }

    /// <summary>上游 dev-intro：标题 + 说明 + 右侧「仅 Dev 模式可见」小标签。</summary>
    private static Border Panel(string name, string title, string hint, string badge)
    {
        var text = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Name = name + "Title", Text = title, FontSize = 14, FontWeight = FontWeight.SemiBold },
                new TextBlock { Name = name + "Hint", Text = hint, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap },
            },
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(text);
        var label = new TextBlock { Name = name + "Badge", Text = badge, FontSize = 11, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Top };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        return new Border
        {
            Name = name, Classes = { "panel" }, Padding = new Thickness(16, 12),
            Margin = new Thickness(0, 0, 0, 12), Child = grid,
        };
    }

    /// <summary>文字层级样例：小标签 + 一段按字号/字重渲染的文本。</summary>
    private static Control TypeSample(string label, string text, double size, FontWeight weight, bool muted,
        bool monospace = false, bool ellipsis = false)
    {
        var sample = new TextBlock
        {
            Text = text, FontSize = size, FontWeight = weight, Opacity = muted ? 0.7 : 1,
            TextWrapping = ellipsis ? TextWrapping.NoWrap : TextWrapping.Wrap,
            TextTrimming = ellipsis ? TextTrimming.CharacterEllipsis : TextTrimming.None,
        };
        if (monospace) sample.FontFamily = new FontFamily("Consolas, Menlo, monospace");
        return new StackPanel
        {
            Spacing = 2,
            Children = { new TextBlock { Text = label, FontSize = 11, Opacity = 0.6 }, sample },
        };
    }

    /// <summary>带皮肤主题的按钮（主题缺失时退回默认外观，不写死颜色）。</summary>
    private static Button Themed(string name, string themeKey, string content) => new()
    {
        Name = name, Content = content, Padding = new Thickness(12, 7), FontSize = 12,
        Theme = Application.Current?.TryFindResource(themeKey, out var value) == true
            ? value as Avalonia.Styling.ControlTheme
            : null,
    };

    /// <summary>反馈样例：小标签 + 控件。</summary>
    private static Control FeedbackSample(string label, Control control) => new StackPanel
    {
        Spacing = 4,
        Children = { new TextBlock { Text = label, FontSize = 11, Opacity = 0.6 }, control },
    };

    /// <summary>错误框样例（上游 ErrorBox）：用皮肤的危险色，不写死。</summary>
    private static Control ErrorSample(string message)
    {
        var text = new TextBlock
        {
            Name = "DevToolsErrorSample", Text = message, FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        text[!TextBlock.ForegroundProperty] =
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AlasDangerBrush");
        return text;
    }

    /// <summary>默认错误样例（错误输入框那一处用的就是上游 invalidInputMessage）。</summary>
    private static Control ErrorSample() => ErrorSample("请输入有效值，当前输入已保留。");

    /// <summary>导航项样例（用与侧栏一致的 nav-item 类）。</summary>
    private static Control NavItem(string name, string text, bool active)
    {
        var button = new Button
        {
            Name = name, Content = text, FontSize = 13, Padding = new Thickness(10, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch, Classes = { "nav-item" },
        };
        if (active) button.Classes.Add("active");
        return button;
    }

    /// <summary>指标卡片样例：标题 + 主数值 + 上限。</summary>
    private static Control MetricCard(string name, string label, string value, string limit) => new Border
    {
        Name = name, Classes = { "panel" }, Padding = new Thickness(12, 10), Width = 160,
        Child = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = label, FontSize = 11, Opacity = 0.7 },
                new TextBlock { Text = value, FontSize = 16, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = limit, FontSize = 11, Opacity = 0.6 },
            },
        },
    };

    /// <summary>
    /// 视觉实验室（上游 visualLab）：5 个调参滑杆 + 6 个模糊预设，只在材质皮肤显示。
    /// 说明：网页的 `backdrop-filter: blur()` 在 Avalonia 里没有等价属性，因此这里把滑杆作用到
    /// **能表达的部分**（圆角、阴影、表面透明度、玻璃染色的可见度），并在界面上如实标注这一限制。
    /// </summary>
    private static Control VisualLab()
    {
        var stack = new StackPanel { Name = "DevToolsVisualLabPanel", Spacing = 10 };
        var panel = new Border
        {
            Name = "DevToolsVisualLab", Classes = { "panel" }, Padding = new Thickness(16, 12),
            Margin = new Thickness(0, 0, 0, 12), Child = stack,
            // 可见性由调用方按**真实主题**（`UsesMaterial`）控制，并在主题变化时更新——
            // 不能在这里比对皮肤名字符串（实际值是 Classic.Light / Classic.Dark 这类带明暗的名字），
            // 也不能只在创建时判一次。
        };

        var title = new TextBlock { Text = "视觉效果实验室", FontSize = 14, FontWeight = FontWeight.SemiBold };
        var badge = new TextBlock { Text = "实时调参", FontSize = 11, Opacity = 0.7 };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Children.Add(title);
        Grid.SetColumn(badge, 1);
        head.Children.Add(badge);
        stack.Children.Add(head);

        var glass = new Border
        {
            Name = "DevToolsGlassStage", Padding = new Thickness(14, 12), Width = 360,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = "背景玻璃", FontSize = 11, Opacity = 0.6 },
                    new TextBlock { Text = "模糊 / 饱和 / 透明 / 圆角 / 阴影", FontSize = 14, FontWeight = FontWeight.SemiBold },
                    new TextBlock
                    {
                        Text = "后面的色块和文字用于判断玻璃层对背景细节、亮度和色彩的处理。",
                        FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap,
                    },
                },
            },
        };
        glass[!Border.BackgroundProperty] =
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AlasGlassTintBrush");
        glass[!Border.BorderBrushProperty] =
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AlasGlassEdgeBrush");
        glass.BorderThickness = new Thickness(1);
        stack.Children.Add(glass);

        var controls = new StackPanel { Name = "DevToolsEffectControls", Spacing = 6 };
        Slider(controls, "DevToolsBlur", "模糊", 24, 0, 48, "px");
        Slider(controls, "DevToolsSaturate", "饱和度", 130, 70, 180, "%");
        var surface = Slider(controls, "DevToolsSurface", "表面透明度", 72, 0, 100, "%");
        // 表面透明度**真的改表面画刷的 alpha**：只换 Background 的透明度，
        // 不动 Opacity（那会把文字与子控件一起变淡，等于把整块内容灰掉）。
        surface.ValueChanged += (_, args) =>
        {
            glass.Background = WithAlpha(glass.Background, args.NewValue / 100.0);
        };
        var radius = Slider(controls, "DevToolsRadius", "圆角", 26, 0, 48, "px");
        var shadow = Slider(controls, "DevToolsShadow", "阴影", 24, 0, 48, "px");
        // 圆角与阴影滑杆直接作用到玻璃块上（这两项在 Avalonia 里有等价表达）。
        radius.ValueChanged += (_, args) => glass.CornerRadius = new CornerRadius(args.NewValue);
        var shadowValue = shadow.Value;
        shadow.ValueChanged += (_, args) =>
        {
            var spread = (int)args.NewValue;
            glass.BoxShadow = spread == 0
                ? default
                : Avalonia.Media.BoxShadows.Parse($"0 {spread / 3} {spread * 2} 0 #00000020");
        };
        glass.CornerRadius = new CornerRadius(radius.Value);
        if (shadowValue > 0)
        {
            glass.BoxShadow = Avalonia.Media.BoxShadows.Parse(
                $"0 {(int)shadowValue / 3} {(int)shadowValue * 2} 0 #00000020");
        }
        stack.Children.Add(controls);

        var presets = new WrapPanel { Name = "DevToolsBlurPresets", ItemSpacing = 10, LineSpacing = 8 };
        foreach (var value in new[] { 0, 6, 12, 18, 24, 32 })
        {
            var label = value == 0 ? "无模糊" : value <= 12 ? "轻度" : value <= 24 ? "中度" : "重度";
            presets.Children.Add(new StackPanel
            {
                Spacing = 2, Width = 90,
                Children =
                {
                    new Border
                    {
                        Name = $"DevToolsBlurPreset{value}", Height = 36, CornerRadius = new CornerRadius(8),
                        Background = Avalonia.Application.Current?
                            .TryFindResource("AlasSurfaceBrush", out var brush) == true
                            ? brush as Avalonia.Media.IBrush
                            : null,
                    },
                    new TextBlock { Text = $"模糊 {value}px", FontSize = 11, Opacity = 0.7 },
                    new TextBlock { Text = label, FontSize = 10, Opacity = 0.6 },
                },
            });
        }
        stack.Children.Add(new TextBlock { Text = "模糊预设", FontSize = 13, FontWeight = FontWeight.SemiBold });
        stack.Children.Add(presets);
        return panel;
    }

    /// <summary>把画刷换成同色但指定 alpha 的实心画刷（只影响该层背景）。</summary>
    private static Avalonia.Media.IBrush? WithAlpha(Avalonia.Media.IBrush? brush, double alpha)
    {
        if (brush is Avalonia.Media.SolidColorBrush solid)
        {
            var color = solid.Color;
            return new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.FromArgb((byte)Math.Clamp(alpha * 255, 0, 255), color.R, color.G, color.B));
        }
        // 非实心画刷（渐变等）取不到单色：退回皮肤里的玻璃染色再套 alpha。
        if (Avalonia.Application.Current?.TryFindResource("AlasGlassTintBrush", out var tint) == true &&
            tint is Avalonia.Media.SolidColorBrush tintSolid)
        {
            var color = tintSolid.Color;
            return new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.FromArgb((byte)Math.Clamp(alpha * 255, 0, 255), color.R, color.G, color.B));
        }
        return brush;
    }

    /// <summary>带实时数值标签的滑杆（上游 dev-effect-controls 的一行）。</summary>
    private static Slider Slider(Panel host, string name, string label, double value, double min, double max, string unit)
    {
        var readout = new TextBlock { Text = $"{value:0}{unit}", FontSize = 12, FontWeight = FontWeight.SemiBold };
        var slider = new Slider
        {
            Name = name, Minimum = min, Maximum = max, Value = value, Width = 240,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        slider.PropertyChanged += (_, args) =>
        {
            if (args.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty)
            {
                readout.Text = $"{slider.Value:0}{unit}";
            }
        };
        host.Children.Add(new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 6,
                    Children = { new TextBlock { Text = label, FontSize = 12 }, readout },
                },
                slider,
            },
        });
        return slider;
    }

    /// <summary>表格行：表头用较粗字重与下边框分隔（上游 dev-table-preview 的观感）。</summary>
    private static Control TableRow(bool header, params string[] cells)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*,90,70") };
        for (var index = 0; index < cells.Length; index++)
        {
            var cell = new TextBlock
            {
                Text = cells[index], FontSize = 12,
                FontWeight = header ? FontWeight.SemiBold : FontWeight.Normal,
                Opacity = header ? 0.75 : 1,
            };
            Grid.SetColumn(cell, index);
            grid.Children.Add(cell);
        }
        return new Border
        {
            Padding = new Thickness(8, 6), BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = header
                ? Avalonia.Application.Current?.TryFindResource("AlasBorderBrush", out var brush) == true
                    ? brush as Avalonia.Media.IBrush
                    : null
                : null,
            Child = grid,
        };
    }

    /// <summary>滚动区域样例：12 行（上游 Array.from({length: 12})）。</summary>
    private static Control ScrollSample()
    {
        var stack = new StackPanel { Name = "DevToolsScrollSampleContent", Spacing = 4 };
        for (var index = 1; index <= 12; index++)
        {
            stack.Children.Add(new TextBlock
            {
                Text = $"{index:00} 这是用于检查滚动条、行高和长容器边缘裁切的内容。",
                FontSize = 12, Opacity = 0.8,
            });
        }
        return stack;
    }

    /// <summary>表面样例：色块 + 名称 + 皮肤键名。</summary>
    private static Control SurfaceSample(string name, string label, string brushKey)
    {
        var swatch = new Border { Height = 40, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        swatch[!Border.BackgroundProperty] =
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(brushKey);
        swatch[!Border.BorderBrushProperty] =
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AlasBorderBrush");
        return new StackPanel
        {
            Name = name, Spacing = 4, Width = 150,
            Children =
            {
                swatch,
                new TextBlock { Text = label, FontSize = 12 },
                new TextBlock { Text = brushKey, FontSize = 10, Opacity = 0.6 },
            },
        };
    }

    /// <summary>
    /// 阴影样例：`var(--glass-shadow)` 这类以皮肤键表达（Alas*），其余按上游的**字面量**显示
    /// —— 本页是视觉实验室，就是要展示那几个确切值；不能凭空发明皮肤键（键缺失会静默取空）。
    /// </summary>
    private static Control ShadowSample(string name, string label, string shadow)
    {
        var box = new Border { Height = 36, CornerRadius = new CornerRadius(8), Margin = new Thickness(6) };
        box[!Border.BackgroundProperty] =
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AlasSurfaceBrush");
        if (shadow.StartsWith("Alas", StringComparison.Ordinal))
        {
            box[!Border.BoxShadowProperty] =
                new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(shadow);
        }
        else if (!string.Equals(shadow, "none", StringComparison.Ordinal))
        {
            // 显示保留上游的 CSS 原文（含 px），解析时去掉单位：Avalonia 的 BoxShadows.Parse 只接受
            // 不带单位的数值写法。
            box.BoxShadow = Avalonia.Media.BoxShadows.Parse(
                System.Text.RegularExpressions.Regex.Replace(shadow, @"(\d)px", "$1"));
        }
        return new StackPanel
        {
            Name = name, Spacing = 4, Width = 130,
            Children = { box, new TextBlock { Text = label, FontSize = 12 }, new TextBlock { Text = shadow, FontSize = 10, Opacity = 0.6 } },
        };
    }

    /// <summary>圆角样例：按给定圆角渲染的方块 + 数值（999 显示为「胶囊」）。</summary>
    private static Control RadiusSample(string name, double radius) => new StackPanel
    {
        Name = name, Spacing = 4, Width = 64,
        Children =
        {
            new Border
            {
                Height = 36, CornerRadius = new CornerRadius(radius),
                Background = Avalonia.Media.Brushes.Transparent, BorderThickness = new Thickness(1),
                BorderBrush = Avalonia.Application.Current?.TryFindResource("AlasBorderBrush", out var brush) == true
                    ? brush as Avalonia.Media.IBrush
                    : null,
            },
            new TextBlock { Text = radius >= 999 ? "胶囊" : $"{radius}px", FontSize = 10, Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center },
        },
    };

    /// <summary>区块内的小标题 + 说明（上游 dev-control-label）。</summary>
    private static Control SectionLabel(string title, string hint) => new StackPanel
    {
        Spacing = 2,
        Children =
        {
            new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeight.SemiBold },
            new TextBlock { Text = hint, FontSize = 11, Opacity = 0.65, TextWrapping = TextWrapping.Wrap },
        },
    };

    /// <summary>本地的分段选择器：点击切换选中项并更新 active 类（只影响本页展示，不接后端）。</summary>
    private static Control Segment(string name, IReadOnlyList<string> options, int initial)
    {
        var buttons = new List<Button>();
        var row = new StackPanel
        {
            Name = name, Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 2),
        };
        for (var index = 0; index < options.Count; index++)
        {
            var position = index;
            var button = new Button
            {
                Name = $"{name}Item{index}", Content = options[index], FontSize = 12,
                Padding = new Thickness(12, 6), Classes = { "segment-tab" },
            };
            if (position == initial) button.Classes.Add("active");
            button.Click += (_, _) =>
            {
                foreach (var other in buttons) other.Classes.Remove("active");
                button.Classes.Add("active");
            };
            buttons.Add(button);
            row.Children.Add(button);
        }
        return row;
    }

    /// <summary>状态徽标样例：用与右栏一致的 task-state 外观与皮肤键配色。</summary>
    private static Control Badge(string name, string text, string state) => new Border
    {
        Name = name, Classes = { "task-state", state }, VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 10 },
    };

    /// <summary>上游 DevField：字段行（标签 + 可选说明 + 控件），外观与设置页一致。</summary>
    private static Control FieldRow(string label, string help, Control control)
    {
        var texts = new StackPanel
        {
            Spacing = 2,
            Children = { new TextBlock { Text = label, FontSize = 12 } },
        };
        if (!string.IsNullOrEmpty(help))
        {
            texts.Children.Add(new TextBlock { Text = help, FontSize = 11, Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
        }
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("240,*"), Margin = new Thickness(0, 2) };
        row.Children.Add(texts);
        Grid.SetColumn(control, 1);
        row.Children.Add(control);
        return row;
    }

    private static Control Input(string name, string automationId, string text, bool password = false,
        bool multiline = false, bool monospace = false, bool disabled = false)
    {
        var box = new TextBox
        {
            Name = name, Text = text, FontSize = 12, MinWidth = 220, IsEnabled = !disabled,
            AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            PasswordChar = password ? '•' : '\0',
            Height = multiline ? 72 : double.NaN,
        };
        if (monospace) box.FontFamily = new FontFamily("Consolas, Menlo, monospace");
        Avalonia.Automation.AutomationProperties.SetAutomationId(box, automationId);
        return box;
    }

    private static Control Combo(string name, IReadOnlyList<string> options) => new ComboBox
    {
        Name = name, ItemsSource = options, SelectedIndex = 0, FontSize = 12, MinWidth = 160,
    };

    private static Control Toggle(string name, string label, bool isOn, bool disabled = false) => new CheckBox
    {
        Name = name, Content = label, IsChecked = isOn, FontSize = 12, IsEnabled = !disabled,
    };

    private static void SetEnabled(bool value, params Button[] buttons)
    {
        foreach (var button in buttons) button.IsEnabled = value;
    }
}

/// <summary>页面状态：开发者模式、模拟状态、更新角标预览与动作可用性，全部来自注入的局部接口。</summary>
public sealed class DevToolsViewModel : INotifyPropertyChanged
{
    private IDevToolsBackend _backend;

    /// <summary>
    /// 可替换的后端：外壳按 master 的承载约定注入（XAML 传不了构造参数，所以不能只读）。
    /// 换后端时清掉本地的模拟窗口（`SimulatedUntil`）——那是针对旧后端的模拟，跨后端沿用会让界面
    /// 显示"正在模拟：某状态"而新后端其实什么都没在跑。
    /// </summary>
    public IDevToolsBackend Backend
    {
        get => _backend;
        set
        {
            _backend = value;
            SimulatedUntil = null;
            Notify(nameof(SimulationLabel));
            Notify(nameof(CanClear));
            _ = RefreshAsync();
        }
    }
    private bool _enabled;
    private string? _simulated;
    private bool _updateNoticePreview;
    private string _notice = DisconnectedDevToolsBackend.Notice;

    public DevToolsViewModel(IDevToolsBackend backend)
    {
        _backend = backend;
        SimulateRunningCommand = new DevCommand(_ => Run(() => _backend.SimulateAsync("running")));
        SimulateErrorCommand = new DevCommand(_ => Run(() => _backend.SimulateAsync("error")));
        SimulateUpdatingCommand = new DevCommand(_ => Run(() => _backend.SimulateAsync("updating")));
        ClearCommand = new DevCommand(_ => Run(() => _backend.ClearSimulationAsync()));
        ToggleUpdateNoticeCommand = new DevCommand(_ => Run(() => _backend.SetUpdateNoticeAsync(!UpdateNoticePreview)));
        ThrowCommand = new DevCommand(_ => Run(async () =>
        {
            await _backend.ThrowTestAsync();
            // 自检抛错：把真实异常交给页面显示，不假装“自检通过”。
            throw new InvalidOperationException("开发者自检：错误页测试异常");
        }));
    }

    public ICommand SimulateRunningCommand { get; }
    public ICommand SimulateErrorCommand { get; }
    public ICommand SimulateUpdatingCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand ToggleUpdateNoticeCommand { get; }
    public ICommand ThrowCommand { get; }

    private Alas.UI.Theming.UiTheme _theme = Alas.UI.Theming.UiTheme.Light;

    /// <summary>
    /// 退出开发者模式（页面局部回调：外壳接路由/模式切换，不需要后端能力）。
    /// 上游 DevControls 的「退出 Dev 模式」只切前端模式并导航回首页。
    /// </summary>
    public Action? ExitDevMode { get; set; }

    /// <summary>
    /// 当前页面主题（由外壳在代码后置里注入，并在主题变化时再次赋值）。
    /// 材质主题判定按上游 `MATERIAL_THEMES = ['light','dark']`——即经典皮肤的浅/深色；
    /// 不能拿皮肤名字符串比对（实际值是 Classic.Light / Classic.Dark 这类带明暗的名字）。
    /// </summary>
    public Alas.UI.Theming.UiTheme Theme
    {
        get => _theme;
        set
        {
            if (!SetField(ref _theme, value)) return;
            Notify(nameof(UsesMaterial));
        }
    }

    /// <summary>是否走玻璃材质（视觉实验室只在材质主题下显示）。</summary>
    public bool UsesMaterial => Theme is Alas.UI.Theming.UiTheme.Light or Alas.UI.Theming.UiTheme.Dark;

    public bool Enabled { get => _enabled; private set => SetField(ref _enabled, value); }
    public string? Simulated { get => _simulated; private set => SetField(ref _simulated, value); }
    public bool UpdateNoticePreview { get => _updateNoticePreview; private set => SetField(ref _updateNoticePreview, value); }
    public string Notice { get => _notice; private set => SetField(ref _notice, value); }

    public bool HasNotice => !string.IsNullOrEmpty(Notice);
    public bool CanUseTools => Enabled && !HasNotice;
    public bool CanClear => CanUseTools && !string.IsNullOrEmpty(Simulated);

    /// <summary>模拟到期时间：上游「10 秒后自动恢复」，到期后由宿主调用 RestoreIfExpired 清掉模拟。</summary>
    public DateTimeOffset? SimulatedUntil { get; private set; }

    /// <summary>模拟恢复时限（上游 developer.simulateIconsHint 写明 10 秒）。</summary>
    public static TimeSpan SimulationWindow { get; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 到点自动恢复：now 不早于到期时间就清除模拟。时钟由调用方注入，便于离屏检查而不依赖真实计时器。
    /// 未到期返回 false，页面据此保持模拟状态。
    /// </summary>
    public bool RestoreIfExpired(DateTimeOffset now)
    {
        if (SimulatedUntil is null || now < SimulatedUntil) return false;
        SimulatedUntil = null;
        Simulated = null;
        Notify(nameof(SimulationLabel));
        Notify(nameof(CanClear));
        return true;
    }

    /// <summary>开始一次模拟：记录到期时间（上游 10 秒后自动恢复）。</summary>
    public void StartSimulation(string status, DateTimeOffset now)
    {
        Simulated = status;
        SimulatedUntil = now + SimulationWindow;
        Notify(nameof(SimulationLabel));
        Notify(nameof(CanClear));
    }

    public string SimulationLabel => new DevToolsStatus(Enabled, Simulated, UpdateNoticePreview).SimulationLabel;

    public Task RefreshAsync(CancellationToken cancellationToken = default) => Run(() => _backend.ReadStatusAsync(), cancellationToken);

    private async Task Run(Func<Task<DevToolsStatus>> operation, CancellationToken cancellationToken = default)
    {
        try
        {
            Apply(await operation());
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Notice = error.Message;
            Enabled = false;
        }
    }

    private void Apply(DevToolsStatus status)
    {
        // 未返回 Enabled 时不得让工具可用（避免在未开启开发者模式时显示可点的模拟按钮）。
        var enabled = status.Enabled && string.IsNullOrEmpty(status.Error);
        var changed = new List<string>();
        if (_enabled != enabled) { _enabled = enabled; changed.Add(nameof(Enabled)); }
        if (_simulated != status.Simulated) { _simulated = status.Simulated; changed.Add(nameof(Simulated)); }
        if (_updateNoticePreview != status.UpdateNoticePreview) { _updateNoticePreview = status.UpdateNoticePreview; changed.Add(nameof(UpdateNoticePreview)); }
        if (_notice != (status.Error ?? string.Empty)) { _notice = status.Error ?? string.Empty; changed.Add(nameof(Notice)); }

        Notify(nameof(HasNotice));
        Notify(nameof(CanUseTools));
        Notify(nameof(CanClear));
        Notify(nameof(SimulationLabel));
        foreach (var name in changed) Notify(name);
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

internal sealed class DevCommand(Action<object?> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute(parameter);
}
