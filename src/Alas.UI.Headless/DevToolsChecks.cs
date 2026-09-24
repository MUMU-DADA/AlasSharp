using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Alas.UI.DevTools;

namespace Alas.UI.Headless;

/// <summary>
/// 开发者工具页（上游 /dev）的离屏检查：
/// ① 未接 Core → 显示不可用原因，全部工具按钮禁用（不伪造模拟/自检成功）；
/// ② 未开启开发者模式 → 按钮同样禁用，且不显示「开发者模式已开启」；
/// ③ 开启后按钮可用、模拟文案与上游 i18n 一致、「清除模拟」只在有模拟时可用；
/// ④ 故意抛错时页面显示**真实异常信息**而不是"自检通过"。
/// </summary>
internal static class DevToolsChecks
{
    internal static void Run()
    {
        Disconnected();
        Disabled();
        Enabled();
        AutoRestore();
        ThrowTest();
        PanelsAndControls();
        RepairAcceptance();
    }

    /// <summary>
    /// 035/037 的 B 补修验收。根因是"控件存在、状态正确，但用户够不着"：
    /// ① 页面自己没有滚动容器，超长展示页依赖外层给一个，没有就整块够不着；
    /// ② Modal 覆盖层曾被放进滚动内容里，打开后弹窗落在滚动区中部（视口外），还要再滚一次才点得到取消；
    /// ③ 此前的 Click 辅助调用 RaiseEvent(ClickEvent)，等于绕过命中测试，
    ///    所以"打开按钮在视口外"和"弹窗不在视口内"两件事都测不出来。
    ///
    /// 因此这里全部改用**视口内的真实指针与键盘输入**：点开、取消、确认、Tab 循环、输入、Esc 关闭
    /// 都必须由窗口输入管线命中真实控件；断言覆盖"打开按钮可达"和"打开后无需再滚动即可取消/确认"。
    /// </summary>
    private static void RepairAcceptance()
    {
        var view = new DevToolsView(new FakeBackend(new DevToolsStatus(true, null, false)));
        var window = new Window { Width = 1280, Height = 900, Content = view };
        window.Show();
        Pump();
        try
        {
            var lab = Find<Border>(view, "DevToolsVisualLab");
            view.UiTheme = Alas.UI.Theming.UiTheme.Light;
            Pump();
            Check(lab.IsVisible, "the visual lab is visible on the material (light) theme");
            view.UiTheme = Alas.UI.Theming.UiTheme.Extreme;
            Pump();
            Check(!lab.IsVisible, "the visual lab hides on a non-material theme");
            view.UiTheme = Alas.UI.Theming.UiTheme.Dark;
            Pump();
            Check(lab.IsVisible, "the visual lab reappears on the material (dark) theme");

            var glass = Find<Border>(view, "DevToolsGlassStage");
            var before = (glass.Background as Avalonia.Media.SolidColorBrush)?.Color.A;
            Find<Slider>(view, "DevToolsSurface").Value = 10;
            Pump();
            var after = (glass.Background as Avalonia.Media.SolidColorBrush)?.Color.A;
            Check(before is not null && after is not null && before != after,
                $"the surface slider changes the surface brush alpha ({before} -> {after})");
            Check(Math.Abs(glass.Opacity - 1) < 0.001,
                "changing the surface does not fade the whole layer (text stays readable)");

            // 页面自持滚动容器：这块超长展示页在外层不给 ScrollViewer 的宿主里也必须滚得动。
            var scroller = Find<ScrollViewer>(view, "DevToolsScroll");
            Pump();
            Check(scroller.Extent.Height > scroller.Viewport.Height + 100,
                $"the page owns a scroll container for its long content " +
                $"(extent {scroller.Extent.Height}, viewport {scroller.Viewport.Height})");

            var overlay = Find<Border>(view, "DevToolsModalOverlay");
            var card = Find<Border>(view, "DevToolsModalCard");
            var openButton = Find<Button>(view, "DevToolsOpenModalButton");
            var modalInput = Find<TextBox>(view, "DevToolsModalInput");
            var modalCancel = Find<Button>(view, "DevToolsModalCancel");
            var modalConfirm = Find<Button>(view, "DevToolsModalConfirm");
            var probe = Find<Button>(view, "DevToolsButtonDefault");
            var first = Find<Button>(view, "DevToolsSegmentControlItem0");
            var second = Find<Button>(view, "DevToolsSegmentControlItem1");
            // 被遮罩挡住的那块内容：与打开按钮同处“按钮与操作”面板，滚到打开按钮时它也在
            // 视口里——这样才能用同一批视口坐标证明"打开前后同一位置点得到 / 点不到"。
            var shielded = Find<Button>(view, "DevToolsIconButtonClose");
            var vpWidth = TopLevel.GetTopLevel(view)?.ClientSize.Width ?? window.Width;
            var vpHeight = TopLevel.GetTopLevel(view)?.ClientSize.Height ?? window.Height;
            var probeBefore = probe.Bounds;
            var extentBefore = scroller.Extent.Height;
            var scrolledBefore = scroller.Offset.Y;

            // 先滚动到「打开 Modal」所在的“按钮与操作”面板，再取被遮罩挡住那块的坐标：
            // 打开按钮本身在这一页也曾在视口外（035 就是这个缺陷），所以"打开前先滚"是用户真实路径。
            ScrollIntoViewport(openButton, window);

            // ⓪ 先证明这个位置**真的能点到内容**（否则"遮罩拦住了"可能只是因为本来就点不到）。
            var shieldedCentre = ViewportPoint(shielded, window,
                new Point(shielded.Bounds.Width / 2, shielded.Bounds.Height / 2));
            Check(shieldedCentre.X > 0 && shieldedCentre.Y > 0
                    && shieldedCentre.X < vpWidth && shieldedCentre.Y < vpHeight,
                $"the shielding probe control is inside the viewport (at {shieldedCentre})");
            var hitBefore = window.InputHitTest(shieldedCentre) as Visual;
            Check(hitBefore is not null && (hitBefore == shielded || hitBefore.GetVisualAncestors().Contains(shielded)),
                $"the shielding probe position hits the content while the modal is closed " +
                $"(hit {hitBefore?.GetType().Name ?? "(null)"})");
            // 从这里开始的坐标都以"打开前"的滚动位置为基准，下面用它证明打开动作没有把页面滚走。
            var extentBeforeOpen = scroller.Extent.Height;
            var scrolledBeforeOpen = scroller.Offset.Y;
            var probeBeforeOpen = probe.Bounds;

            // ① 打开按钮真的点得到：滚到它 → 真实指针按下抬起（不再 RaiseEvent 代劳）。
            PointerClick(window, openButton, "打开 Modal");
            Check(overlay.IsVisible, "the open button is reachable in the viewport and opens the modal");
            Check(overlay.Bounds.Width > 0 && overlay.Bounds.Height > 0,
                $"the overlay is laid out (bounds {overlay.Bounds})");
            Check(ReferenceEquals(overlay.Parent, Find<Panel>(view, "DevToolsRoot")),
                "the overlay is mounted in the page's layering container");
            Check(!scroller.GetVisualDescendants().Contains(overlay),
                "the overlay is not inside the page scroll container");

            // ② 打开后立即可见/居中：视口矩形 + 卡片居中 + 取消/确认都在视口内（不用再滚一次）。
            var overlayOrigin = ViewportPoint(overlay, window, new Point(0, 0));
            var cardOrigin = ViewportPoint(card, window, new Point(0, 0));
            var cardCenter = new Point(cardOrigin.X + card.Bounds.Width / 2, cardOrigin.Y + card.Bounds.Height / 2);
            Check(Math.Abs(overlayOrigin.X) < 1 && Math.Abs(overlayOrigin.Y) < 1
                    && Math.Abs(overlay.Bounds.Width - vpWidth) < 1 && Math.Abs(overlay.Bounds.Height - vpHeight) < 1,
                $"the overlay covers the visible viewport, not the long content " +
                $"(at {overlayOrigin}, size {overlay.Bounds.Size}, viewport {vpWidth}x{vpHeight})");
            Check(Math.Abs(cardCenter.X - vpWidth / 2) < 2 && Math.Abs(cardCenter.Y - vpHeight / 2) < 2,
                $"the modal card is centered in the viewport (center {cardCenter}, viewport {vpWidth}x{vpHeight})");
            foreach (var (name, control) in new (string, Control)[]
                     {
                         ("modal input", modalInput), ("cancel", modalCancel), ("confirm", modalConfirm),
                     })
            {
                var centre = ViewportPoint(control, window, new Point(control.Bounds.Width / 2, control.Bounds.Height / 2));
                Check(centre.X > 0 && centre.Y > 0 && centre.X < vpWidth && centre.Y < vpHeight,
                    $"the {name} is inside the viewport as soon as the modal opens (at {centre}, no extra scroll)");
            }
            var layoutWhenOpen = OverlayRect(overlay, window);
            Check(!OverlaySticksOut(overlay, window, vpWidth, vpHeight),
                $"no part of the overlay sticks out of the viewport (rect {layoutWhenOpen})");

            // ③ 遮罩拦截底层点击：用 ⓪ 里确认过"点得到内容"的**同一视口坐标**再点一次，
            //    命中的必须是覆盖层；分段控件的选中态不能变。
            window.MouseMove(shieldedCentre);
            window.MouseDown(shieldedCentre, Avalonia.Input.MouseButton.Left);
            window.MouseUp(shieldedCentre, Avalonia.Input.MouseButton.Left);
            Pump();
            Check(first.Classes.Contains("active") && !second.Classes.Contains("active"),
                "the overlay shields the content underneath from pointer input");
            Check(ReferenceEquals(window.InputHitTest(shieldedCentre), overlay),
                "the pointer at the shielded position hits the overlay, not the content behind it");

            // ④ 打开前后滚动范围不变（覆盖层不参与文档流、没把内容顶长），页面内容也没重排，
            //    被遮罩挡住的那段内容也没有被顺带滚走（弹窗不是靠"再滚一次"才进来的）。
            Check(scroller.Extent.Height <= extentBeforeOpen + 0.5,
                $"opening the modal does not lengthen the page (extent {extentBeforeOpen} -> {scroller.Extent.Height})");
            Check(Math.Abs(scroller.Offset.Y - scrolledBeforeOpen) < 0.5,
                $"opening the modal does not scroll the page (offset {scrolledBeforeOpen} -> {scroller.Offset.Y})");
            Check(probe.Bounds == probeBeforeOpen,
                $"opening the modal does not reflow the page content (was {probeBeforeOpen}, now {probe.Bounds})");

            // ⑤ Tab / Shift+Tab 只在覆盖层内的控件之间循环，不跑到页面其它部分；也不需要再滚一次。
            modalInput.Focus();
            Pump();
            foreach (var (expected, label) in new (Control, string)[]
                     {
                         (modalCancel, "Tab 1 reaches cancel"), (modalConfirm, "Tab 2 reaches confirm"),
                         (modalInput, "Tab 3 wraps back to the input"),
                     })
            {
                Key(window, Avalonia.Input.Key.Tab, Avalonia.Input.RawInputModifiers.None);
                Check(ReferenceEquals(FocusManager(window), expected), label);
                Check(!probe.IsFocused, $"{label}: focus never escapes to the page behind the modal");
            }
            foreach (var (expected, label) in new (Control, string)[]
                     {
                         (modalConfirm, "Shift+Tab goes back to confirm"),
                         (modalCancel, "Shift+Tab continues to cancel"),
                         (modalInput, "Shift+Tab wraps back to the input"),
                     })
            {
                Key(window, Avalonia.Input.Key.Tab, Avalonia.Input.RawInputModifiers.Shift);
                Check(ReferenceEquals(FocusManager(window), expected), label);
            }

            // ⑥ 编辑时不丢焦点：在覆盖层的输入框里键入，焦点与文本都留在输入框。
            modalInput.Focus();
            Pump();
            window.KeyTextInput("X");
            Pump();
            Check(modalInput.IsFocused, "typing in the modal input keeps focus there");
            Check(modalInput.Text is not null && modalInput.Text.Contains("X", StringComparison.Ordinal),
                $"typing in the modal input reaches its text (got {modalInput.Text})");

            // ⑦ 取消 / 确认都是视口内的真实指针点击，关闭后焦点回到打开它的按钮。
            PointerClick(window, modalCancel, "取消");
            Check(!overlay.IsVisible, "cancel closes the modal overlay");
            Check(openButton.IsFocused, "cancel returns focus to the opener");
            PointerClick(window, openButton, "打开 Modal");
            PointerClick(window, modalConfirm, "确认");
            Check(!overlay.IsVisible, "confirm closes the modal overlay");
            Check(openButton.IsFocused, "confirm returns focus to the opener");

            // ⑧ Esc 用原生键盘输入关闭（焦点在覆盖层上）。
            PointerClick(window, openButton, "打开 Modal");
            window.KeyPress(Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None,
                Avalonia.Input.PhysicalKey.Escape, null);
            window.KeyRelease(Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None,
                Avalonia.Input.PhysicalKey.Escape, null);
            Pump();
            Check(!overlay.IsVisible, "Escape closes the modal overlay");
            Check(openButton.IsFocused, "focus returns to the button that opened the modal");

            // ⑨ 宿主给的挂载点：ModalHost 指向整壳容器时，覆盖层跟着走、仍按视口对齐，
            //    并且遮罩与键盘约束在"覆盖层不在页面子树里"的情况下同样成立
            //    （路由事件只走可视祖先，覆盖层此时不是内容的祖先，拦截不能再依赖路由）。
            var host = new Panel { Name = "ShellModalHost" };
            var hosted = new DevToolsView(new FakeBackend(new DevToolsStatus(true, null, false)));
            var hostedContent = new Panel { Children = { hosted, host } };
            var hostedWindow = new Window { Width = 1280, Height = 900, Content = hostedContent };
            hostedWindow.Show();
            Pump();
            try
            {
                hosted.ModalHost = host;
                Pump();
                var hostedOverlay = Find<Border>(hostedContent, "DevToolsModalOverlay");
                var hostedOpen = Find<Button>(hosted, "DevToolsOpenModalButton");
                var hostedShielded = Find<Button>(hosted, "DevToolsIconButtonClose");
                Check(ReferenceEquals(hostedOverlay.Parent, host),
                    "injecting ModalHost moves the overlay to the shell container");
                Check(!hosted.GetVisualDescendants().Contains(hostedOverlay),
                    "the overlay leaves the page once the shell owns it");

                var hostedShieldedCentre = ViewportPoint(hostedShielded, hostedWindow,
                    new Point(hostedShielded.Bounds.Width / 2, hostedShielded.Bounds.Height / 2));
                ScrollIntoViewport(hostedOpen, hostedWindow);
                hostedShieldedCentre = ViewportPoint(hostedShielded, hostedWindow,
                    new Point(hostedShielded.Bounds.Width / 2, hostedShielded.Bounds.Height / 2));
                var hostedHitBefore = hostedWindow.InputHitTest(hostedShieldedCentre) as Visual;
                Check(hostedHitBefore is not null
                        && (hostedHitBefore == hostedShielded
                            || hostedHitBefore.GetVisualAncestors().Contains(hostedShielded)),
                    "the hosted shielding probe position hits the content while the modal is closed");

                PointerClick(hostedWindow, hostedOpen, "打开 Modal");
                Check(hostedOverlay.IsVisible, "the hosted overlay opens through real pointer input");
                var hostedOrigin = ViewportPoint(hostedOverlay, hostedWindow, new Point(0, 0));
                var hostedViewport = TopLevel.GetTopLevel(hosted)?.ClientSize ?? new Size(1280, 900);
                Check(Math.Abs(hostedOrigin.X) < 1 && Math.Abs(hostedOrigin.Y) < 1
                        && Math.Abs(hostedOverlay.Bounds.Width - hostedViewport.Width) < 1
                        && Math.Abs(hostedOverlay.Bounds.Height - hostedViewport.Height) < 1,
                    $"the hosted overlay still covers the viewport (at {hostedOrigin}, " +
                    $"size {hostedOverlay.Bounds.Size}, viewport {hostedViewport})");
                var hostedCard = Find<Border>(hostedContent, "DevToolsModalCard");
                var hostedCardCentre = ViewportPoint(hostedCard, hostedWindow,
                    new Point(hostedCard.Bounds.Width / 2, hostedCard.Bounds.Height / 2));
                Check(Math.Abs(hostedCardCentre.Y - hostedViewport.Height / 2) < 2,
                    $"the hosted modal stays vertically centred (centre {hostedCardCentre})");
                hostedWindow.MouseMove(hostedShieldedCentre);
                hostedWindow.MouseDown(hostedShieldedCentre, Avalonia.Input.MouseButton.Left);
                hostedWindow.MouseUp(hostedShieldedCentre, Avalonia.Input.MouseButton.Left);
                Pump();
                Check(ReferenceEquals(hostedWindow.InputHitTest(hostedShieldedCentre), hostedOverlay),
                    "the hosted overlay shields the content underneath from pointer input");

                var hostedInput = Find<TextBox>(hostedContent, "DevToolsModalInput");
                var hostedCancel = Find<Button>(hostedContent, "DevToolsModalCancel");
                Check(hostedInput.Focus(), "the hosted modal input can take focus");
                Pump();
                Key(hostedWindow, Avalonia.Input.Key.Tab, Avalonia.Input.RawInputModifiers.None);
                Check(ReferenceEquals(FocusManager(hostedWindow), hostedCancel),
                    "Tab reaches the hosted modal's controls");
                Key(hostedWindow, Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None);
                Check(!hostedOverlay.IsVisible, "Escape closes the hosted modal");
                Check(hostedOpen.IsFocused, "the hosted modal returns focus to its opener");
                PointerClick(hostedWindow, hostedOpen, "打开 Modal");
                PointerClick(hostedWindow, Find<Button>(hostedContent, "DevToolsModalCancel"), "取消");
                Check(!hostedOverlay.IsVisible, "the hosted modal cancels through real pointer input");
            }
            finally { hostedWindow.Close(); }
        }
        finally { window.Close(); }
    }

    /// <summary>覆盖层在视口里的矩形（左上角视口坐标 + 尺寸）。</summary>
    private static Rect OverlayRect(Visual overlay, Window window)
    {
        var origin = ViewportPoint(overlay, window, new Point(0, 0));
        return new Rect(origin, overlay.Bounds.Size);
    }

    /// <summary>覆盖层是否越出视口（多出或少于视口都算没对齐）。</summary>
    private static bool OverlaySticksOut(Visual overlay, Window window, double width, double height)
    {
        var rect = OverlayRect(overlay, window);
        const double tolerance = 1;
        return rect.X < -tolerance || rect.Y < -tolerance
            || rect.Right > width + tolerance || rect.Bottom > height + tolerance;
    }

    /// <summary>真实指针点击：先滚到控件、确认中心点落在视口内，再按真实输入管线按下抬起。</summary>
    private static void PointerClick(Window window, Control control, string label)
    {
        ScrollIntoViewport(control, window);
        var viewport = TopLevel.GetTopLevel(control)?.ClientSize ?? window.ClientSize;
        var centre = ViewportPoint(control, window, new Point(control.Bounds.Width / 2, control.Bounds.Height / 2));
        var scroller = control.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        Check(centre.X > 0 && centre.Y > 0 && centre.X < viewport.Width && centre.Y < viewport.Height,
            $"the {label} control must be inside the viewport before a real click (at {centre}, viewport {viewport}, " +
            $"scroller {(scroller is null ? "(none)" : $"{scroller.Name} offset={scroller.Offset} " +
                $"viewport={scroller.Viewport} extent={scroller.Extent}")})");
        window.MouseMove(centre);
        Pump();
        window.MouseDown(centre, Avalonia.Input.MouseButton.Left);
        window.MouseUp(centre, Avalonia.Input.MouseButton.Left);
        Pump();
    }

    /// <summary>
    /// 把控件滚进最近的外层滚动容器。
    ///
    /// 不能用 `BringIntoView()`：它只沿**可视祖先**上浮，而 Modal 覆盖层是滚动容器的兄弟节点
    /// （这正是"弹窗不能被滚走"的原因），请求因此到不了滚动容器，弹窗里的按钮永远停在视口外。
    /// 这里显式按几何关系移动外层滚动容器的偏移。
    /// </summary>
    private static void ScrollIntoViewport(Control control, Window window)
    {
        var scroller = control.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        if (scroller is null) return;
        var viewport = scroller.Viewport.Height;
        if (viewport <= 0) return;
        // 控件在滚动内容坐标系里的位置：相对滚动容器视口原点，再加上当前偏移。
        var inViewport = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), scroller);
        if (inViewport is not { } point) return;
        var contentY = point.Y + scroller.Offset.Y;
        var target = Math.Clamp(contentY - viewport / 2, 0, Math.Max(0, scroller.Extent.Height - viewport));
        scroller.Offset = new Vector(scroller.Offset.X, target);
        Pump();
    }

    /// <summary>控件上某点在顶层窗口里的坐标（`window` 与 `ClientSize` 同坐标系：都是客户区）。</summary>
    private static Point ViewportPoint(Visual control, Window window, Point point) =>
        control.TranslatePoint(point, window) ?? throw new Exception("Control is detached: " + (control as Control)?.Name);

    /// <summary>真实键盘输入：按下 + 抬起（键盘处理在 KeyDown 上，抬起保证没有残留状态）。</summary>
    private static void Key(Window window, Avalonia.Input.Key key, Avalonia.Input.RawInputModifiers modifiers)
    {
        window.KeyPress(key, modifiers, Avalonia.Input.PhysicalKey.None, null);
        window.KeyRelease(key, modifiers, Avalonia.Input.PhysicalKey.None, null);
        Pump();
    }

    /// <summary>当前焦点所在的可视控件。</summary>
    private static Control? FocusManager(Visual root) =>
        root.GetVisualDescendants().OfType<Control>().FirstOrDefault(control => control.IsFocused);

    private static void Check(bool value, string label)
    {
        if (!value) throw new Exception("FAIL: " + label);
    }

    /// <summary>
    /// 上游 DevControls 的区块存在性 + 主要控件的输入/状态（028 的 B 验收）：
    /// 12 个展示面板都要在；Modal 能开能关、分段与 Tabs 能切换选中态、
    /// 视觉实验室的圆角滑杆真的作用到玻璃块、表单控件有值/禁用态、表格有表头与三行。
    /// </summary>
    private static void PanelsAndControls()
    {
        var view = new DevToolsView(new FakeBackend(new DevToolsStatus(true, null, false)));
        // 页面自己持有滚动容器（035 的 B 补修），这里不再包一层外层 ScrollViewer：
        // 外层滚动只会在无头里给出"内层视口等于整段内容"的假尺寸，反而让指针坐标失去意义。
        var window = new Window { Width = 1280, Height = 1200, Content = view };
        window.Show();
        Pump();
        try
        {
            var names = view.GetVisualDescendants().OfType<Control>()
                .Select(control => control.Name ?? string.Empty).ToHashSet();
            foreach (var panel in new[]
                     {
                         "DevToolsIntroPanel", "DevToolsTypographyPanel", "DevToolsButtonsPanel",
                         "DevToolsFeedbackPanel", "DevToolsFormControlsPanel", "DevToolsSelectorsStatusPanel",
                         "DevToolsLayoutPanel", "DevToolsLayersPanel", "DevToolsDataTablesPanel",
                         "DevToolsModalPanel", "DevToolsQuickToolsPanel", "DevToolsColorsTokensPanel",
                     })
            {
                Check(names.Contains(panel), $"the {panel} block exists");
            }
            // 视觉实验室只在材质主题显示：元素存在，可见性由画面主题控制（见 ThemeSwitching）。
            Check(names.Contains("DevToolsVisualLab"), "the visual lab block exists");
            Check(names.Contains("DevToolsGlassStage"), "the visual lab has its glass stage");

            // Modal：点开 → 弹层出现；点确认 → 消失。上游只有一个 Modal 状态，按钮与弹层是同一实例
            // （页面里同一动作只应有一个按钮）。
            var overlay = Find<Border>(view, "DevToolsModalOverlay");
            Check(view.GetVisualDescendants().OfType<Button>()
                    .Count(candidate => candidate.Name == "DevToolsOpenModalButton") == 1,
                "exactly one open-modal button exists (no duplicate, unwired copy)");
            Check(!overlay.IsVisible, "the modal preview starts closed");
            PointerClick(window, Find<Button>(view, "DevToolsOpenModalButton"), "打开 Modal");
            Check(overlay.IsVisible, "opening the modal preview shows the overlay");
            Check(Find<TextBox>(view, "DevToolsModalInput").Text == "AzurPilot Dev Mode",
                "the modal preview carries the sample input");
            PointerClick(window, Find<Button>(view, "DevToolsModalConfirm"), "确认");
            Check(!overlay.IsVisible, "confirming closes the modal preview");

            // 分段控件：点第二项 → 它 active、第一项失去 active。
            var first = Find<Button>(view, "DevToolsSegmentControlItem0");
            var second = Find<Button>(view, "DevToolsSegmentControlItem1");
            Check(first.Classes.Contains("active") && !second.Classes.Contains("active"),
                "the segmented control starts on the first option");
            PointerClick(window, second, "分段控件第二项");
            Check(second.Classes.Contains("active") && !first.Classes.Contains("active"),
                "clicking a segment moves the active state");

            // 视觉实验室：圆角滑杆作用到玻璃块。
            var glass = Find<Border>(view, "DevToolsGlassStage");
            var radius = Find<Slider>(view, "DevToolsRadius");
            var before = glass.CornerRadius.TopLeft;
            radius.Value = before > 20 ? 8 : 32;
            Pump();
            Check(Math.Abs(glass.CornerRadius.TopLeft - radius.Value) < 0.01,
                $"the radius slider drives the glass block (radius {radius.Value}, glass {glass.CornerRadius.TopLeft})");

            // 表单控件：值在、禁用态在。
            Check(Find<TextBox>(view, "DevToolsTextInput").Text == "AzurPilot", "the text sample keeps its value");
            Check(!Find<TextBox>(view, "DevToolsDisabledInput").IsEnabled, "the disabled input stays disabled");
            Check(Find<CheckBox>(view, "DevToolsToggleOn").IsChecked == true &&
                Find<CheckBox>(view, "DevToolsToggleOff").IsChecked == false &&
                !Find<CheckBox>(view, "DevToolsToggleDisabled").IsEnabled,
                "the toggle samples cover on / off / disabled");

            // 表格：表头 + 三行数据。
            var cells = view.GetVisualDescendants().OfType<TextBlock>()
                .Where(block => block.IsEffectivelyVisible).Select(block => block.Text).ToList();
            Check(cells.Contains("主线出击") && cells.Contains("科研项目") && cells.Contains("委托检查"),
                "the table lists the three demo rows");
            Check(cells.Contains("时间") && cells.Contains("耗时"), "the table has its header cells");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// 模拟的 10 秒自动恢复（上游 developer.simulateIconsHint）：到点前保持、到点后清掉。
    /// 时钟由调用方传入，不依赖真实计时器，避免离屏检查出现时序抖动。
    /// </summary>
    private static void AutoRestore()
    {
        var view = new DevToolsView(new FakeBackend(new DevToolsStatus(true, null, false)));
        var window = Show(view);
        try
        {
            var model = view.Model;
            var start = DateTimeOffset.UnixEpoch;
            model.StartSimulation("running", start);
            Check(model.Simulated == "running", "simulation starts");
            Check(model.SimulatedUntil == start + TimeSpan.FromSeconds(10),
                $"simulation window is 10 seconds (got {model.SimulatedUntil})");
            Check(!model.RestoreIfExpired(start + TimeSpan.FromSeconds(9)),
                "simulation survives until the window elapses");
            Check(model.Simulated == "running", "simulation is still active before the deadline");
            Check(model.RestoreIfExpired(start + TimeSpan.FromSeconds(10)),
                "simulation restores exactly at the deadline");
            Check(model.Simulated is null && model.SimulatedUntil is null, "auto restore clears the simulation");
            Check(model.SimulationLabel == "当前没有模拟状态。", "label returns to the idle text after auto restore");
        }
        finally { window.Close(); }
    }

    private static void Disconnected()
    {
        var view = new DevToolsView();
        var window = Show(view);
        try
        {
            var model = view.Model;
            Check(!model.Enabled, "disconnected dev tools are not enabled");
            Check(!Find<TextBlock>(view, "DevToolsEnabledLabel").IsVisible, "no enabled banner while unconnected");
            Check(Find<TextBlock>(view, "DevToolsNotice").IsVisible && model.Notice.Contains("尚未接线"),
                "disconnected dev tools explain why they are unavailable");
            Check(!model.CanUseTools && !Find<Button>(view, "DevToolsSimulateRunningButton").IsEnabled
                && !Find<Button>(view, "DevToolsThrowButton").IsEnabled, "every dev tool button is disabled while unconnected");
            Check(model.SimulationLabel == "当前没有模拟状态。",
                $"disconnected dev tools show no simulation (got {model.SimulationLabel})");
        }
        finally { window.Close(); }
    }

    private static void Disabled()
    {
        var view = new DevToolsView(new FakeBackend(new DevToolsStatus(false, null, false)));
        var window = Show(view);
        try
        {
            Check(!view.Model.Enabled, "developer mode off stays off");
            Check(!Find<Button>(view, "DevToolsSimulateErrorButton").IsEnabled, "tools stay disabled when developer mode is off");
            Check(!Find<TextBlock>(view, "DevToolsEnabledLabel").IsVisible, "no enabled banner when developer mode is off");
        }
        finally { window.Close(); }
    }

    private static void Enabled()
    {
        var backend = new FakeBackend(new DevToolsStatus(true, null, false));
        var view = new DevToolsView(backend);
        var window = Show(view);
        try
        {
            var model = view.Model;
            Check(model.Enabled, "developer mode is reported as enabled");
            // 上游把快捷工具与（本轮补的）颜色令牌都渲染为 section.panel：两块的根节点必须带 panel 类。
            var panels = view.GetVisualDescendants().OfType<Border>()
                .Where(border => border.Classes.Contains("panel"))
                .Select(border => border.Name)
                .ToList();
            Check(panels.Contains("DevToolsQuickToolsPanel") && panels.Contains("DevToolsColorsTokensPanel"),
                $"quick tools and colors/tokens are panelized (got {string.Join(",", panels)})");
            Check(Find<TextBlock>(view, "DevToolsEnabledLabel").IsVisible, "enabled banner follows the backend");
            Check(Find<Button>(view, "DevToolsSimulateRunningButton").IsEnabled
                && Find<Button>(view, "DevToolsUpdateNoticeButton").IsEnabled
                && Find<Button>(view, "DevToolsThrowButton").IsEnabled, "tools become available");
            Check(!Find<Button>(view, "DevToolsSimulateClearButton").IsEnabled, "clear stays disabled without a simulation");

            // 模拟运行中 → 文案与上游一致，清除按钮可用。
            backend.Status = new DevToolsStatus(true, "running", false);
            model.RefreshAsync().GetAwaiter().GetResult();
            Pump();
            Check(model.SimulationLabel == "正在模拟：运行中", $"simulation label follows upstream i18n (got {model.SimulationLabel})");
            Check(Find<Button>(view, "DevToolsSimulateClearButton").IsEnabled, "clear becomes available while simulating");

            // 模拟异常 / 更新中 的文案。
            backend.Status = new DevToolsStatus(true, "error", false);
            model.RefreshAsync().GetAwaiter().GetResult();
            Check(model.SimulationLabel == "正在模拟：异常", $"error simulation label (got {model.SimulationLabel})");
            backend.Status = new DevToolsStatus(true, "updating", true);
            model.RefreshAsync().GetAwaiter().GetResult();
            Check(model.SimulationLabel == "正在模拟：更新中", $"updating simulation label (got {model.SimulationLabel})");
            Check(model.UpdateNoticePreview, "update-notice preview state follows the backend");
        }
        finally { window.Close(); }
    }

    private static void ThrowTest()
    {
        var backend = new FakeBackend(new DevToolsStatus(true, null, false));
        var view = new DevToolsView(backend);
        var window = Show(view);
        try
        {
            view.Model.ThrowCommand.Execute(null);
            Pump();
            Check(view.Model.Notice.Contains("错误页测试异常"),
                $"self-check surfaces the real exception instead of claiming success (got {view.Model.Notice})");
            Check(!view.Model.CanUseTools, "tools are disabled after the self-check error");
        }
        finally { window.Close(); }
    }

    private static Window Show(DevToolsView view)
    {
        var window = new Window { Width = 1280, Height = 820, Content = view };
        window.Show();
        Pump();
        return window;
    }

    private static T Find<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
        ?? throw new Exception("Missing control: " + name);

    private static void Pump()
    {
        for (var i = 0; i < 8; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>页面局部接口的假实现：只用于离屏检查，不访问服务、设备或真实诊断能力。</summary>
    private sealed class FakeBackend(DevToolsStatus status) : IDevToolsBackend
    {
        public DevToolsStatus Status { get; set; } = status;

        public Task<DevToolsStatus> ReadStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(Status);

        public Task<DevToolsStatus> SimulateAsync(string status, CancellationToken cancellationToken = default)
        {
            Status = Status with { Simulated = status };
            return Task.FromResult(Status);
        }

        public Task<DevToolsStatus> ClearSimulationAsync(CancellationToken cancellationToken = default)
        {
            Status = Status with { Simulated = null };
            return Task.FromResult(Status);
        }

        public Task<DevToolsStatus> SetUpdateNoticeAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            Status = Status with { UpdateNoticePreview = enabled };
            return Task.FromResult(Status);
        }

        public Task ThrowTestAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
