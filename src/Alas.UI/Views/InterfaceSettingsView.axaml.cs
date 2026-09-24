using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Alas.UI.ViewModels;

namespace Alas.UI.Views;

/// <summary>
/// 界面设置页视图；数据上下文是 <see cref="InterfaceSettingsViewModel"/>。
///
/// 这里承担上游 <c>components/ui.tsx</c> 的 <c>Modal</c> 行为（原生 <c>&lt;dialog&gt;</c> + <c>showModal()</c> 的等价物）：
/// ① 覆盖层在滚动容器**之外**，按视口覆盖，弹窗居中且**不越视口**（窄屏由外层 ScrollViewer 提供滚动）；
/// ② Escape 关闭；
/// ③ 打开时焦点移入弹窗，Tab / Shift+Tab 只在弹窗内的可聚焦控件之间循环，不跑到页面其它控件；
/// ④ 关闭后焦点回到打开它的控件（上游 dialog 的焦点归还）；
/// ⑤ 打开/关闭只切换覆盖层可见性与内容命中测试，**不重建**弹窗内的控件——
///    否则每次属性通知都会重建输入框，用户正在输入的内容与焦点都会丢。
/// </summary>
public partial class InterfaceSettingsView : UserControl
{
    private Control? _focusReturn;
    private bool _hooked;

    public InterfaceSettingsView()
    {
        InitializeComponent();
        HookDialog();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>最近一次打开弹窗的控件：关闭后焦点回到它。</summary>
    public Control? PaletteDialogOpener => _focusReturn;

    private void HookDialog()
    {
        if (_hooked) return;
        _hooked = true;
        var overlay = this.FindControl<Border>("PaletteDialogOverlay");
        if (overlay is null) return;

        // 视口变化后重新对齐：弹窗要保持"当前可见区域居中"，且不能越出视口。
        overlay.LayoutUpdated += (_, _) => AlignDialog();
        // Escape 与 Tab 约束挂在覆盖层上：它是滚动容器的兄弟节点，键盘事件到达它就说明焦点在弹窗内。
        overlay.KeyDown += OnDialogKeyDown;

        // 取色控件报告十六进制框非法时，也要让「保存配色」跟着变灰（上游 required/pattern 的等效行为）。
        foreach (var name in new[] { "PalettePrimaryField", "PaletteSecondaryField" })
        {
            if (this.FindControl<ColorField>(name) is not { } field) continue;
            field.InvalidChanged += () =>
            {
                var primary = this.FindControl<ColorField>("PalettePrimaryField");
                var secondary = this.FindControl<ColorField>("PaletteSecondaryField");
                if (DataContext is InterfaceSettingsViewModel bound)
                {
                    bound.HasInvalidColorText = (primary?.IsInvalid ?? false) || (secondary?.IsInvalid ?? false);
                }
            };
        }

        // 取色控件报告十六进制框非法时，也要让「保存配色」跟着变灰（上游 required/pattern 的等效行为）。
        foreach (var name in new[] { "PalettePrimaryField", "PaletteSecondaryField" })
        {
            if (this.FindControl<ColorField>(name) is not { } field) continue;
            field.InvalidChanged += () =>
            {
                var primary = this.FindControl<ColorField>("PalettePrimaryField");
                var secondary = this.FindControl<ColorField>("PaletteSecondaryField");
                if (DataContext is InterfaceSettingsViewModel bound)
                {
                    bound.HasInvalidColorText = (primary?.IsInvalid ?? false) || (secondary?.IsInvalid ?? false);
                }
            };
        }

        if (DataContext is InterfaceSettingsViewModel model) model.PropertyChanged += OnModelChanged;
        // XAML 里声明的视图在**构造时还没有 DataContext**（外壳随后赋值），
        // 因此这里必须同时挂 DataContextChanged：只在构造函数里取一次会让订阅静默失效，
        // 弹窗状态永远推不到覆盖层上。
        DataContextChanged += (_, _) =>
        {
            if (DataContext is InterfaceSettingsViewModel bound)
            {
                bound.PropertyChanged += OnModelChanged;
                // 订阅后立刻同步一次当前状态（此时可能已经是打开态）。
                SyncDialog(bound);
            }
        };
    }

    /// <summary>把模型的弹窗状态同步到覆盖层（含初始同步）。</summary>
    private void SyncDialog(InterfaceSettingsViewModel model)
    {
        var overlay = this.FindControl<Border>("PaletteDialogOverlay");
        if (overlay is null) return;
        overlay.IsVisible = model.IsPaletteDialogOpen;
        HidePaletteFields(model.IsPaletteDialogOpen);
        if (model.IsPaletteDialogOpen) AlignDialog();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(InterfaceSettingsViewModel.IsPaletteDialogOpen)) return;
        var model = DataContext as InterfaceSettingsViewModel;
        var overlay = this.FindControl<Border>("PaletteDialogOverlay");
        if (model is null || overlay is null) return;
        overlay.IsVisible = model.IsPaletteDialogOpen;
        HidePaletteFields(model.IsPaletteDialogOpen);
        if (model.IsPaletteDialogOpen)
        {
            // 记住打开它的控件（指针/键盘触发时焦点在它上面）。
            _focusReturn = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
            // 焦点必须等这一轮**布局之后**再设：属性通知到达时覆盖层可能还不可见，
            // 此时 Focus() 不会生效（实测焦点留在页面控件上，Tab 于是跑到弹窗外）。
            Dispatcher.UIThread.Post(() =>
            {
                AlignDialog();
                var first = this.FindControl<TextBox>("PaletteNameInput");
                if (first is { IsEffectivelyVisible: true }) first.Focus();
                else overlay.Focus();
            }, DispatcherPriority.Loaded);
        }
        else
        {
            _focusReturn?.Focus();
            _focusReturn = null;
        }
    }

    /// <summary>
    /// 弹窗打开时关掉页面内容的命中测试：覆盖层是内容的**兄弟节点**（在分层容器里并列），
    /// 无法靠路由拦截，因此显式让底层内容不参与命中——点遮罩不会误触底层控件。
    /// </summary>
    private void HidePaletteFields(bool dialogOpen)
    {
        var scroller = this.FindControl<ScrollViewer>("InterfaceSettingsContent");
        if (scroller is not null) scroller.IsHitTestVisible = !dialogOpen;
    }

    /// <summary>
    /// 让弹窗按**当前视口**排布：覆盖层撑满视口，内容在视口内居中；
    /// 视口小于弹窗时由外层 ScrollViewer 提供滚动，保证"不越视口、够得到保存"。
    /// </summary>
    private void AlignDialog()
    {
        var overlay = this.FindControl<Border>("PaletteDialogOverlay");
        if (overlay is null || !overlay.IsVisible) return;
        var viewport = TopLevel.GetTopLevel(this);
        if (viewport is null) return;
        var size = viewport.Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;
        overlay.Width = size.Width;
        overlay.Height = size.Height;
        var scroller = this.FindControl<ScrollViewer>("PaletteDialogScroller");
        if (scroller is not null) scroller.MaxHeight = Math.Max(120, size.Height - 32);
        // 窄屏：弹窗宽度跟随视口（留出边距），避免固定 420 宽越界。
        var dialog = this.FindControl<Border>("PaletteDialog");
        if (dialog is not null) dialog.MaxWidth = Math.Max(200, size.Width - 32);
    }

    /// <summary>Escape 关闭 + Tab/Shift+Tab 焦点循环（上游 dialog 的 onCancel 与原生焦点约束）。</summary>
    private void OnDialogKeyDown(object? sender, KeyEventArgs args)
    {
        var model = DataContext as InterfaceSettingsViewModel;
        if (model is null || !model.IsPaletteDialogOpen) return;
        if (args.Key == Key.Escape)
        {
            model.CancelPaletteCommand.Execute(null);
            args.Handled = true;
            return;
        }
        if (args.Key != Key.Tab) return;
        var ring = FocusRing();
        if (ring.Length == 0) return;
        var backwards = args.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var current = Array.FindIndex(ring, control => control.IsFocused);
        var next = current < 0
            ? (backwards ? ring.Length - 1 : 0)
            : (current + (backwards ? ring.Length - 1 : 1)) % ring.Length;
        ring[next].Focus();
        args.Handled = true;
    }

    /// <summary>弹窗内可聚焦控件（按可视顺序）；覆盖层在滚动容器之外，因此这圈就是完整的焦点范围。</summary>
    private Control[] FocusRing()
    {
        var overlay = this.FindControl<Border>("PaletteDialogOverlay");
        if (overlay is null) return Array.Empty<Control>();
        return overlay.GetVisualDescendants().OfType<Control>()
            .Where(control => control.Focusable && control.IsEffectivelyVisible && control.IsEffectivelyEnabled)
            .ToArray();
    }
}
