using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Alas.UI.ViewModels;
using Alas.UI.Views;

namespace Alas.UI.Headless;

/// <summary>
/// 只用 Avalonia 原生 Headless 窗口后端与 Skia 离屏渲染；不连接系统桌面、服务或设备。
/// 截图写入 .runtime/ui-headless，供与上游基准（.runtime/ui-reference）逐尺寸对照。
/// </summary>
internal static class Program
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    public static async Task<int> Main(string[] args)
    {
        string output = Path.GetFullPath(args.Length > 0 ? args[0] : ".runtime/ui-headless");
        Directory.CreateDirectory(output);
        try
        {
            // Dispatch may complete inline on its own worker. Async disposal lets that worker
            // unwind instead of synchronously waiting for itself in IDisposable.Dispose().
            await using (var session = HeadlessUnitTestSession.StartNew(typeof(Program)))
                await session.Dispatch(() => Verify(output), CancellationToken.None);
            Console.WriteLine("PASS: Avalonia Headless shell and overview layout, navigation, theme, drawer and log rendering; no device or visible window.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void Verify(string output)
    {
        // 1) 对照帧：每种尺寸/主题用全新的视图与窗口，避免交互状态（筛选行、日志条数、指针悬停）进入对照图。
        CaptureClean(output, 1280, 820, dark: false, "overview-light-1280x820.png");
        CaptureClean(output, 1280, 820, dark: true, "overview-dark-1280x820.png");
        CaptureClean(output, 1920, 1080, dark: false, "overview-light-1920x1080.png");
        CaptureClean(output, 1920, 1080, dark: true, "overview-dark-1920x1080.png");
        CaptureClean(output, 390, 844, dark: false, "overview-light-390x844.png");
        CaptureClean(output, 390, 844, dark: true, "overview-dark-390x844.png");
        // 抽屉与右栏浮层：同样是干净窗口，只把抽屉状态打开，不带任何点击残留。
        CaptureCleanState(output, 390, 844, dark: false, "overview-light-390x844-drawer.png",
            view => view.Model.IsDrawerOpen = true);
        CaptureCleanState(output, 390, 844, dark: true, "overview-dark-390x844-drawer.png",
            view => view.Model.IsDrawerOpen = true);
        CaptureCleanState(output, 390, 844, dark: false, "overview-light-390x844-rail.png",
            view => view.Model.IsRailOpen = true);

        // 2) 几何与交互验证：同一个窗口连续操作。
        var view = new MainView();
        var window = new Window { Width = 1280, Height = 820, Content = view };
        // Show attaches the visual tree to the in-memory HeadlessWindowImpl, not a native window.
        window.Show();
        try
        {
            var model = view.Model;
            model.IsDark = false;
            Pump();

            // 字体：与上游视觉对照需要稳定的中文字形（上游用系统中文字体栈）。
            Check(FontManager.Current.TryGetGlyphTypeface(new Typeface(view.FontFamily), out var font), "embedded font loads");
            Check(font!.FamilyName == "Noto Sans CJK SC", $"embedded font family (got {font.FamilyName})");
            Check("中文工作区 Alpha 123".All(c => font.CharacterToGlyphMap.TryGetGlyph(c, out var glyph) && glyph != 0),
                "embedded Chinese and Latin glyphs");

            // 外壳几何：侧栏 232、列间距 14、顶栏行高 41.5、右栏 292（上游 layout.css/apple.css）。
            var sidebar = Find<Border>(view, "SidebarPanel");
            var topbar = Find<Border>(view, "TopbarPanel");
            var rail = Find<Border>(view, "RailPanel");
            var main = Find<ScrollViewer>(view, "MainScroll");
            Check(!model.IsNarrow, "wide viewport is not narrow");
            Check(Near(sidebar.Bounds.Width, 232), $"sidebar width 232 (got {sidebar.Bounds.Width})");
            Check(Near(sidebar.Bounds.X, 0), $"sidebar at left edge (got {sidebar.Bounds.X})");
            Check(Near(topbar.Bounds.Height, 41.5), $"topbar height 41.5 (got {topbar.Bounds.Height})");
            Check(Near(main.Bounds.X, 246), $"content starts after sidebar + gap (got {main.Bounds.X})");
            Check(Near(main.Bounds.Y, 41.5), $"content starts below topbar (got {main.Bounds.Y})");
            Check(Near(rail.Bounds.Width, 292), $"rail width 292 at 1280px (got {rail.Bounds.Width})");
            Check(Near(rail.Bounds.X + rail.Bounds.Width, 1280), $"rail flush right (got {rail.Bounds.X})");

            // 侧栏内容：两个一级入口与十个任务分组（上游 TaskNavTree groupIcons 的十组）。
            var primaryNav = Find<ItemsControl>(view, "PrimaryNav");
            var taskNav = Find<ItemsControl>(view, "TaskNav");
            Check(primaryNav.ItemCount == 2, $"two primary nav entries (got {primaryNav.ItemCount})");
            Check(taskNav.ItemCount == 10, $"ten task groups (got {taskNav.ItemCount})");
            Check(model.PrimaryNav[0].Label == "运行总览" && model.PrimaryNav[1].Label == "资源统计", "primary nav labels");
            Check(model.TaskGroups[0].Title == "系统" && model.TaskGroups[^1].Title == "工具Plus", "task group order");

            // 资源卡：弹性换行、等宽拉伸；1280 内容宽 664 时 4 张各 160（上游实测 160）。
            var resourceCards = Find<ItemsControl>(view, "ResourceCards");
            Check(resourceCards.ItemCount == 4, $"four resource cards (got {resourceCards.ItemCount})");
            var cards = resourceCards.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("resource-card")).ToList();
            Check(cards.Count == 4, $"four rendered resource cards (got {cards.Count})");
            Check(cards.All(c => Near(c.Bounds.Width, 160, 2)), $"resource cards stretch equally (got {string.Join('/', cards.Select(c => Math.Round(c.Bounds.Width, 1)))})");
            Check(cards.Select(c => Math.Round(c.Bounds.Y, 1)).Distinct().Count() == 1, "resource cards share one row");
            Check(Near(cards[0].TranslatePoint(new Point(0, 0), window)!.Value.X, 278),
                $"first resource card aligns with content padding (got {cards[0].TranslatePoint(new Point(0, 0), window)!.Value.X})");
            Check(model.Overview.Resources[0].Value == "14,200" && model.Overview.Resources[1].Value == "186,420", "resource values mirror the upstream mock");
            Check(model.Overview.Resources.Select(r => r.IsTint0 || r.IsTint1 || r.IsTint2 || r.IsTint3).All(v => v), "resource tint cycles by display index");
            // 文本行盒必须容得下内置中文字体的字形高度，否则会出现上下裁切（照抄 CSS 紧凑行高的回归点）。
            Check(Find<TextBlock>(view, "TitleText").Bounds.Height >= 32 * 1.35, "title line box fits the embedded font");
            Check(Find<TextBlock>(view, "ValueText").Bounds.Height >= 23 * 1.35, "value line box fits the embedded font");
            Check(Find<TextBlock>(view, "NameText").Bounds.Height >= 10 * 1.35, "resource name line box fits the embedded font");
            Check(Find<TextBlock>(view, "FootText").Bounds.Height >= 8 * 1.35, "resource foot line box fits the embedded font");
            // 宽屏内容区按视口算好高度，不应出现滚动条。
            Check(main.Extent.Height <= main.Viewport.Height + 0.5,
                $"wide content area does not overflow (extent {main.Extent.Height}, viewport {main.Viewport.Height})");
            // 纵向位置对齐上游实测：页标题 89.5、资源卡 153.5、监控面板 258.5、调度器卡 127.5。
            var overviewWindow = window;
            Check(Near(Find<Grid>(view, "PageTitle").TranslatePoint(new Point(0, 0), overviewWindow)!.Value.Y, 89.5, 2),
                "page title top matches upstream 89.5");
            Check(Near(cards[0].TranslatePoint(new Point(0, 0), overviewWindow)!.Value.Y, 153.5, 3),
                $"resource row top matches upstream 153.5 (got {cards[0].TranslatePoint(new Point(0, 0), overviewWindow)!.Value.Y})");
            Check(Near(Find<Border>(view, "MonitorPanel").TranslatePoint(new Point(0, 0), overviewWindow)!.Value.Y, 258.5, 4),
                "monitor panel top matches upstream 258.5");
            Check(Near(cards[0].Bounds.Height, 95, 4), $"resource card height 95 (got {cards[0].Bounds.Height})");
            // 上游 .monitor-panel{flex:1}：宽屏撑满内容区剩余高度（视口 - 顶栏 - 内容下内边距）。
            var panel = Find<Border>(view, "MonitorPanel");
            var panelBottom = panel.TranslatePoint(new Point(0, panel.Bounds.Height), overviewWindow)!.Value.Y;
            Check(Near(panelBottom, 820 - 32, 2), $"monitor panel fills the content area (bottom {panelBottom})");
            Check(Near(Find<Border>(view, "SchedulerWidget").TranslatePoint(new Point(0, 0), overviewWindow)!.Value.Y, 127.5, 2),
                "scheduler widget top matches upstream 127.5");

            // 右栏：调度器三格与任务计划三组（待运行 4 / 等待中 2）。
            Check(model.Rail.Groups.Count == 3, $"three rail groups (got {model.Rail.Groups.Count})");
            Check(model.Rail.Groups[0].IsEmpty && model.Rail.Groups[1].Tasks.Count == 4 && model.Rail.Groups[2].Tasks.Count == 2, "rail task counts");
            Check(model.Rail.Groups[1].Tasks[0].Name == "重启设置" && model.Rail.Groups[2].Tasks[0].Name == "主线图-1Plus", "rail task names");
            Check(Find<TextBlock>(view, "BreadcrumbTail").IsVisible == false, "overview hides the breadcrumb tail");

            // 主题：顶栏主题入口是真实入口（不是只改 VM 属性），暗色令牌与主题变体一起切换。
            Pump();
            var themeToggle = Find<Button>(view, "ThemeToggle");
            Check(themeToggle.IsEnabled, "topbar theme entry is enabled");
            Check(Find<Border>(view, "SidebarPanel").Background is SolidColorBrush lightSidebar
                && lightSidebar.Color == Color.Parse("#FFFFFF"), "light sidebar token before the theme entry is used");
            Click(window, themeToggle);
            Pump();
            Check(model.IsDark && Application.Current!.RequestedThemeVariant == ThemeVariant.Dark,
                "topbar theme entry switches to dark");
            Check(Find<Border>(view, "SidebarPanel").Background is SolidColorBrush darkSidebar
                && darkSidebar.Color == Color.Parse("#242426"), "dark sidebar token applied by the theme entry");
            Click(window, themeToggle);
            Pump();
            Check(!model.IsDark && Application.Current!.RequestedThemeVariant == ThemeVariant.Light,
                "topbar theme entry switches back to light");

            // 本阶段没有后端的入口一律禁用并写明原因，不留点了没反应的死绑定。
            foreach (var disabled in new[] { "ExportButton", "InstanceSettingsButton", "InstanceCaption", "InstanceSwitch" })
            {
                var control = Find<Button>(view, disabled);
                Check(!control.IsEnabled, $"{disabled} is disabled while its backend is not implemented");
                Check(control.GetValue(ToolTip.TipProperty) is string tip && tip.Contains("后续切片"),
                    $"{disabled} explains why it is unavailable");
            }

            // 监控页签：日志 ↔ 截图（上游 SegmentedControl 两页）。
            var logsView = Find<Grid>(view, "LogsView");
            var previewView = Find<StackPanel>(view, "PreviewView");
            Check(logsView.IsVisible && !previewView.IsVisible, "logs tab is the default view");
            Click(window, Find<Button>(view, "PreviewTab"));
            Check(!logsView.IsVisible && previewView.IsVisible, "preview tab switches the monitor view");
            Click(window, Find<Button>(view, "LogsTab"));
            Check(logsView.IsVisible && !previewView.IsVisible, "logs tab switches back");

            // 日志工具：筛选行展开、清理、排序（都为真实本地行为，不是只翻标志位）。
            Click(window, Find<Button>(view, "FilterButton"));
            Check(Find<Border>(view, "LogFilters").IsVisible, "filter row expands");

            // 搜索：输入后可见行真的按内容过滤，并且无命中时显示空状态。
            var searchBox = Find<TextBox>(view, "LogSearchBox");
            searchBox.Focus();
            searchBox.SelectAll();
            window.KeyTextInput("不存在的日志内容");
            Pump();
            Check(model.Overview.VisibleLogs.Count == 0, $"search filters every visible row (got {model.Overview.VisibleLogs.Count})");
            Check(Find<StackPanel>(view, "LogEmpty").IsVisible, "empty state shows when nothing matches");
            Check(Find<TextBlock>(view, "LogEmptyTitle").Text == "没有匹配的日志", "empty state matches the upstream copy");
            searchBox.Focus();
            searchBox.SelectAll();
            window.KeyTextInput("模拟");
            Pump();
            Check(model.Overview.VisibleLogs.Count > 0
                && model.Overview.VisibleLogs.All(line => line.Message.Contains("模拟")),
                "search keeps only matching rows");
            model.Overview.SearchText = string.Empty;
            Pump();

            // 级别筛选：只保留所选级别。
            model.Overview.AppendLog("警告样例", "WARNING");
            model.Overview.AppendLog("错误样例", "ERROR");
            Pump();
            Check(model.Overview.VisibleLogs.Count == 3, $"three rows before level filter (got {model.Overview.VisibleLogs.Count})");
            model.Overview.LogLevel = "WARNING";
            Pump();
            Check(model.Overview.VisibleLogs.Count == 1 && model.Overview.VisibleLogs[0].Level == "WARNING",
                "level filter keeps only the selected level");
            model.Overview.LogLevel = "ALL";
            Pump();

            // 排序：真正反转可见行顺序。
            var ascendingFirst = model.Overview.VisibleLogs[0];
            var ascendingLast = model.Overview.VisibleLogs[^1];
            Click(window, Find<Button>(view, "OrderButton"));
            Check(model.Overview.IsDescending, "order toggle flips the local sort flag");
            Check(ReferenceEquals(model.Overview.VisibleLogs[0], ascendingLast)
                && ReferenceEquals(model.Overview.VisibleLogs[^1], ascendingFirst),
                "descending order really reverses the rendered rows");
            Click(window, Find<Button>(view, "OrderButton"));
            Check(!model.Overview.IsDescending, "order toggle returns to ascending");

            // 跟随：开启时自动滚到最新一条，暂停后保持当前位置。
            var logScroll = Find<ScrollViewer>(view, "LogScroll");
            for (var index = 0; index < 40; index++) model.Overview.AppendLog($"跟随样例 {index:D2}");
            Pump();
            Check(logScroll.Offset.Y > 0, $"follow keeps the view at the newest row (offset {logScroll.Offset.Y})");
            model.Overview.IsFollowing = false;
            Pump();
            logScroll.Offset = new Vector(0, 40);
            Pump();
            var pausedOffset = logScroll.Offset.Y;
            model.Overview.AppendLog("暂停后追加");
            Pump();
            Check(Near(logScroll.Offset.Y, pausedOffset, 0.5), "paused follow does not move the view");
            model.Overview.IsFollowing = true;
            model.Overview.AppendLog("恢复跟随");
            Pump();
            Check(logScroll.Offset.Y >= pausedOffset, "resumed follow scrolls towards the newest row");

            // 缓存上限：上游 LogPanel 只保留最近 400 条。
            for (var index = 0; index < 420; index++) model.Overview.AppendLog($"容量样例 {index:D3}");
            Pump();
            Check(model.Overview.CachedLogCount == 400, $"log cache is capped at 400 (got {model.Overview.CachedLogCount})");
            Check(model.Overview.VisibleLogs.Count == 400, "visible rows follow the cache cap");
            // 淘汰必须在两种排序下都按真实缓存：被淘汰的是最旧一条，倒序时它在可见列表尾部。
            model.Overview.ClearCommand.Execute(null);
            model.Overview.SearchText = string.Empty;
            model.Overview.LogLevel = "ALL";
            model.Overview.IsDescending = false;
            Pump();
            for (var index = 0; index < 400; index++) model.Overview.AppendLog($"倒序样例 {index:D3}");
            Pump();
            Check(model.Overview.CachedLogCount == 400 && model.Overview.VisibleLogs.Count == 400,
                "cache and visible list sit at the cap before the descending eviction");
            model.Overview.IsDescending = true;
            Pump();
            var oldestVisible = model.Overview.VisibleLogs[^1];
            model.Overview.AppendLog("倒序追加第 401 条");
            Pump();
            Check(model.Overview.CachedLogCount == 400, "cache stays at 400 after an append in descending order");
            Check(model.Overview.VisibleLogs.Count == 400,
                $"descending visible list stays at the cap (got {model.Overview.VisibleLogs.Count})");
            Check(!model.Overview.VisibleLogs.Contains(oldestVisible), "descending eviction removes the oldest line from the tail");
            Check(model.Overview.VisibleLogs[0].Message == "倒序追加第 401 条", "descending append puts the newest line first");

            // 过滤态：被淘汰的行可能不在可见列表里，淘汰仍要按缓存进行。
            model.Overview.ClearCommand.Execute(null);
            model.Overview.IsDescending = false;
            Pump();
            for (var index = 0; index < 400; index++) model.Overview.AppendLog($"甲组样例 {index:D3}");
            Pump();
            model.Overview.SearchText = "甲组样例";
            Pump();
            Check(model.Overview.VisibleLogs.Count == 400, "filtered visible list starts at the cap");
            var firstFiltered = model.Overview.VisibleLogs[0];
            model.Overview.AppendLog("乙组样例（不匹配筛选）");
            Pump();
            Check(model.Overview.CachedLogCount == 400, "cache stays at 400 while a filter is active");
            Check(model.Overview.VisibleLogs.Count == 399,
                $"filtered visible list drops the evicted line (got {model.Overview.VisibleLogs.Count})");
            Check(!model.Overview.VisibleLogs.Contains(firstFiltered), "evicted line leaves the filtered visible list");
            Check(model.Overview.VisibleLogs.All(line => line.Message.Contains("甲组样例")), "filter still applies after eviction");
            model.Overview.SearchText = string.Empty;
            model.Overview.LogLevel = "ALL";
            Pump();

            // 等宽字体：系统等宽缺失时必须回退到随包字体，并且这个字体真的能渲染中文/数字/拉丁。
            var logMessage = Find<TextBlock>(view, "LogMessage");
            Check(logMessage.FontFamily.ToString().Contains("Noto Sans CJK SC"),
                $"log font falls back to the packaged font (got {logMessage.FontFamily})");
            Check(FontManager.Current.TryGetGlyphTypeface(new Typeface(logMessage.FontFamily), out var monoFace),
                "log font resolves to a typeface");
            Check("2026-09-24 abc123".All(c => monoFace!.CharacterToGlyphMap.TryGetGlyph(c, out var latinGlyph) && latinGlyph != 0),
                "primary log face renders digits and Latin");
            // 随包回退字体本身必须能渲染中文（不依赖任何系统字体）。
            Check(FontManager.Current.TryGetGlyphTypeface(
                    new Typeface(new FontFamily("avares://Alas.UI/Assets/Fonts#Noto Sans CJK SC")), out var packagedFace),
                "packaged fallback face resolves");
            Check("中文日志测试".All(c => packagedFace!.CharacterToGlyphMap.TryGetGlyph(c, out var cjkGlyph) && cjkGlyph != 0),
                "packaged fallback face renders CJK");

            Click(window, Find<Button>(view, "ClearButton"));
            Check(model.Overview.CachedLogCount == 0 && model.Overview.VisibleLogs.Count == 0, "clear empties the log view");
            model.Overview.AppendLog("测试实例已就绪，所有操作均为模拟。");
            Pump();

            // 调度器按钮：只改本地模拟状态，并追加一条如实说明是模拟的日志。
            Click(window, Find<Button>(view, "SchedulerToggle"));
            Check(model.Overview.IsSchedulerRunning, "scheduler toggle flips preview state");
            Check(model.Overview.SchedulerButtonText == "停止运行", "scheduler button label follows state");
            Check(model.Rail.RunningCount == "1", "rail running count follows preview state");
            Check(model.Overview.VisibleLogs.Any(line => line.Message.Contains("模拟调度器已启动")), "scheduler log says it is simulated");
            Click(window, Find<Button>(view, "SchedulerToggle"));
            Check(!model.Overview.IsSchedulerRunning && model.Rail.RunningCount == "0", "scheduler toggle returns to stopped");

            // 侧栏任务分组：点击展开/收起子项，并选择到明确的「未实现」页（本切片不连服务、不跑游戏逻辑）。
            var firstGroup = model.TaskGroups[0];
            Check(!firstGroup.IsExpanded && firstGroup.Tasks.Count == 3, "task groups start collapsed with the upstream catalog");
            Check(firstGroup.Tasks[0].Label == "系统设置" && model.TaskGroups[^1].Tasks[^1].Label == "指挥喵评分",
                "task labels come from the upstream catalog");
            var groupButton = taskNav.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(button => button.Classes.Contains("task-group"));
            Check(groupButton is not null, "task group button is rendered");
            Click(window, groupButton!);
            Check(firstGroup.IsExpanded, "clicking a task group expands its submenu");
            var submenu = taskNav.GetVisualDescendants().OfType<ItemsControl>()
                .FirstOrDefault(control => control.Name == "TaskSubmenu");
            Check(submenu is { IsVisible: true } && submenu.ItemCount == 3, "expanded submenu lists the group's tasks");
            var subItem = submenu!.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(button => button.Classes.Contains("task-submenu-item"));
            Check(subItem is not null, "task submenu item is rendered");
            Click(window, subItem!);
            Pump();
            Check(!model.IsOverviewActive, "selecting a task leaves the overview");
            Check(model.Placeholder.Title == "系统设置", $"placeholder shows the selected task (got {model.Placeholder.Title})");
            Check(model.Placeholder.Hint.Contains("任务配置页"), "placeholder states the task page is not implemented yet");
            Capture(window, output, "task-placeholder-light-1280x820.png");
            Click(window, groupButton!);
            Check(!firstGroup.IsExpanded, "clicking the group again collapses the submenu");

            // 一级导航：未实现的页面如实显示占位，而不是伪造内容。
            var overviewHost = Find<Panel>(view, "OverviewHost");
            var placeholderHost = Find<Panel>(view, "PlaceholderHost");
            Click(window, Find<Button>(view, "HomeLink"));
            Pump();
            Check(!model.IsOverviewActive, "home entry leaves the overview page");
            Check(model.Placeholder.Title == "主页", $"placeholder title follows the entry (got {model.Placeholder.Title})");
            Check(!overviewHost.IsVisible && placeholderHost.IsVisible, "overview page hidden while placeholder shows");
            Check(Find<TextBlock>(view, "BreadcrumbTail").IsVisible, "breadcrumb tail appears off the overview page");
            Capture(window, output, "placeholder-light-1280x820.png");
            var overviewNav = primaryNav.GetVisualDescendants().OfType<Button>().First();
            Click(window, overviewNav);
            Check(model.IsOverviewActive && model.ActiveNavKey == "overview", "overview entry restores the page");
            Check(overviewHost.IsVisible && !placeholderHost.IsVisible, "overview page visible again");

            // 宽屏：断点 >1562.5px 时右栏加宽到 320（上游 layout.css:154 / apple.css:9）。
            window.Width = 1920;
            window.Height = 1080;
            Pump();
            Check(!model.IsNarrow, "1920 is wide");
            Check(Near(rail.Bounds.Width, 320), $"rail widens to 320 above 1562.5px (got {rail.Bounds.Width})");

            // 窄屏：950px 断点把侧栏与右栏换成浮层抽屉（上游 layout.css:156 / apple.css:305）。
            window.Width = 390;
            window.Height = 844;
            Pump();
            Check(model.IsNarrow, "390px is narrow");
            Check(!sidebar.IsVisible || sidebar.Bounds.Width == 232, "narrow sidebar keeps its 232px width");
            var drawerTransform = (TranslateTransform)sidebar.RenderTransform!;
            Check(drawerTransform.X <= -249, $"closed drawer is off canvas (got {drawerTransform.X})");
            Check(Near(topbar.Bounds.Height, 56), $"narrow topbar height 56 (got {topbar.Bounds.Height})");
            Check(Find<Button>(view, "MobileToggle").IsVisible, "narrow shows the drawer toggle");
            Check(Find<Button>(view, "RailToggle").IsVisible, "narrow shows the rail toggle");
            // 上游 apple.css:331 的 ≤950px 规则：监控面板固定 520px，不再撑满。
            Check(Near(panel.Bounds.Height, 520, 2), $"narrow monitor panel is 520 tall (got {panel.Bounds.Height})");
            // 窄屏长行：横向 StackPanel 会按无限宽测量，TextWrapping 失效导致长中文日志被裁。
            model.Overview.ClearCommand.Execute(null);
            model.Overview.SearchText = string.Empty;
            model.Overview.LogLevel = "ALL";
            model.Overview.IsDescending = false;
            model.Overview.AppendLog("这是一条很长的中文日志，用来验证窄屏下正文会按可用宽度换行，而不是被面板右边界裁掉。");
            Pump();
            if (!Find<Border>(view, "LogFilters").IsVisible) Click(window, Find<Button>(view, "FilterButton"));
            Pump();
            var wrapped = Find<TextBlock>(view, "LogMessage");
            Check(wrapped.Bounds.Height > 26, $"long log line wraps on a narrow screen (height {wrapped.Bounds.Height})");
            var wrappedRight = wrapped.TranslatePoint(new Point(wrapped.Bounds.Width, 0), window)!.Value.X;
            Check(wrappedRight <= window.ClientSize.Width + 1,
                $"wrapped log line stays inside the window (right edge {wrappedRight})");
            // 筛选行在 390px 下必须换行且三个控件都可达（搜索/级别/条数）。
            foreach (var controlName in new[] { "LogSearchBox", "LogLevelBox", "LogCountText" })
            {
                var control = Find<Control>(view, controlName);
                var origin = control.TranslatePoint(new Point(0, 0), window)!.Value;
                Check(control.IsVisible && origin.X >= 0 && origin.X + control.Bounds.Width <= window.ClientSize.Width + 1,
                    $"{controlName} is reachable on a narrow screen (x {origin.X}, width {control.Bounds.Width})");
            }

            var scrim = Find<Border>(view, "Scrim");
            Check(!scrim.IsVisible, "no click catcher while the overlays are closed");
            Click(window, Find<Button>(view, "MobileToggle"));
            Check(model.IsDrawerOpen, "drawer opens");
            Check(Near(((TranslateTransform)sidebar.RenderTransform!).X, 0), "open drawer slides into view");
            Check(scrim.IsVisible && scrim.Bounds.Width >= 389 && scrim.Bounds.Height >= 843,
                "drawer shows a full-window click catcher");
            Check(Find<Button>(view, "DrawerCloseButton").IsVisible, "drawer shows its close button");
            // 点抽屉外的背景应关闭抽屉；抽屉自身 ZIndex 更高，仍然可交互。
            var sidebarOrigin = sidebar.TranslatePoint(new Point(0, 0), window)!.Value;
            var outsidePoint = new Point(sidebarOrigin.X + 300, 500);
            window.MouseMove(outsidePoint);
            window.MouseDown(outsidePoint, MouseButton.Left);
            window.MouseUp(outsidePoint, MouseButton.Left);
            Pump();
            Check(!model.IsDrawerOpen, "clicking the background closes the drawer through the click catcher");
            Check(drawerTransform.X <= -249, "closed drawer returns off canvas");
            Check(!scrim.IsVisible, "click catcher hides again with the overlay");

            Click(window, Find<Button>(view, "MobileToggle"));
            Check(model.IsDrawerOpen, "drawer opens again");
            Click(window, Find<Button>(view, "DrawerCloseButton"));
            Check(!model.IsDrawerOpen, "close button collapses the drawer");

            Click(window, Find<Button>(view, "RailToggle"));
            Check(model.IsRailOpen && rail.IsVisible, "rail opens as an overlay");
            Check(rail.Bounds.X > 0, $"narrow rail is not attached to the grid column (got {rail.Bounds.X})");
            Check(scrim.IsVisible, "rail overlay also installs the click catcher");
            Check(Find<Button>(view, "RailCloseButton").IsVisible, "rail overlay shows its close button");
            Click(window, Find<Button>(view, "RailCloseButton"));
            Check(!model.IsRailOpen, "rail close button collapses the overlay");

            // 点背景同样收起右栏浮层（侧栏与右栏共用同一个收起命令）。
            Click(window, Find<Button>(view, "RailToggle"));
            Check(model.IsRailOpen, "rail reopens for the background-click check");
            var backgroundPoint = new Point(20, 700);
            window.MouseMove(backgroundPoint);
            window.MouseDown(backgroundPoint, MouseButton.Left);
            window.MouseUp(backgroundPoint, MouseButton.Left);
            Pump();
            Check(!model.IsRailOpen && !scrim.IsVisible, "clicking the background closes the rail overlay");
            // 遮罩必须与浮层同层且层级更低：打开抽屉后点里面的任务分组要真的生效，且抽屉不会被误关。
            Click(window, Find<Button>(view, "MobileToggle"));
            Check(model.IsDrawerOpen, "drawer opens for the z-order check");
            var narrowGroupButton = taskNav.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(button => button.Classes.Contains("task-group"));
            var expandedBefore = model.TaskGroups[0].IsExpanded;
            Click(window, narrowGroupButton!);
            Check(model.TaskGroups[0].IsExpanded != expandedBefore,
                "clicking a task group inside the open drawer really toggles it (scrim must not cover the sidebar)");
            Check(model.IsDrawerOpen, "drawer stays open while being used");
            // 子项点击也必须穿过遮罩生效：展开后的任务子项点进去要真的换页。
            var narrowSubmenu = taskNav.GetVisualDescendants().OfType<ItemsControl>()
                .FirstOrDefault(control => control.Name == "TaskSubmenu" && control.IsVisible);
            Check(narrowSubmenu is not null, "expanded submenu is reachable inside the open drawer");
            var narrowSubItem = narrowSubmenu!.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(button => button.Classes.Contains("task-submenu-item"));
            Click(window, narrowSubItem!);
            Pump();
            Check(!model.IsOverviewActive && model.Placeholder.Hint.Contains("任务配置页"),
                "clicking a task submenu item inside the open drawer really navigates");
            Check(model.IsDrawerOpen == false, "selecting a task closes the drawer as before");
            Click(window, Find<Button>(view, "HomeLink"));
            Pump();
            Check(model.IsOverviewActive == false, "home link still works after the drawer interaction");
            Click(window, Find<Button>(view, "DrawerCloseButton"));
            Check(!model.IsDrawerOpen, "drawer close button still works after the z-order change");

            // 右栏浮层同理：打开右栏后点调度器必须真的执行。
            Click(window, Find<Button>(view, "RailToggle"));
            Check(model.IsRailOpen, "rail opens for the z-order check");
            var schedulerBefore = model.Overview.IsSchedulerRunning;
            Click(window, Find<Button>(view, "SchedulerToggle"));
            Check(model.Overview.IsSchedulerRunning != schedulerBefore,
                "clicking the scheduler inside the open rail really runs (scrim must not cover the rail)");
            Check(model.IsRailOpen, "rail stays open while being used");
            Click(window, Find<Button>(view, "RailCloseButton"));
            Check(!model.IsRailOpen, "rail close button still works after the z-order change");

            // 宽窄往返后回到基线的常驻布局。
            window.Width = 1280;
            window.Height = 820;
            model.IsDark = false;
            Pump();
            Check(!model.IsNarrow && !model.IsRailOpen && !model.IsDrawerOpen, "resize back to wide resets the overlays");
            Check(rail.IsVisible && Near(rail.Bounds.Width, 292), "wide rail restores 292px");
            Check(Application.Current!.RequestedThemeVariant == ThemeVariant.Light, "light variant restored");
            Console.WriteLine($"PASS: shell geometry and interactions verified; frames written to {output}.");
        }
        finally { window.Close(); }

        // 3) 旧原型离线能力回归：外壳复刻不再展示这一页，但能力与其回归必须保留
        //    （中文输入双向绑定、任务队列 JSON 校验、2,000 行日志上限与虚拟化、主题往返）。
        VerifyOfflinePreview(output);
    }

    private static void VerifyOfflinePreview(string output)
    {
        var view = new OfflinePreviewView();
        var model = view.Model;
        var window = new Window { Width = 1100, Height = 800, Content = view };
        window.Show();
        try
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
            model.IsDark = false;
            Pump();

            Check(model.IsOverview, "offline preview starts on its overview page");
            Capture(window, output, "offline-preview-light-1100x800.png");

            Click(window, Find<Button>(view, "PreviewSettingsButton"));
            Check(model.IsSettings, "offline preview navigation works");
            var name = Find<TextBox>(view, "WorkspaceNameInput");
            name.Focus();
            name.SelectAll();
            window.KeyTextInput("中文工作区 Alpha 123");
            Pump();
            Check(model.WorkspaceName == "中文工作区 Alpha 123", "Chinese text input updates the shared model");

            var editor = Find<TextBox>(view, "QueueEditor");
            editor.Focus();
            editor.SelectAll();
            window.KeyTextInput("{invalid");
            Click(window, Find<Button>(view, "ValidateButton"));
            Check(Find<TextBlock>(view, "ValidationText").Text!.Contains("JSON 语法错误"), "invalid JSON feedback is bound");
            editor.Focus();
            editor.SelectAll();
            window.KeyTextInput("{\"tasks\":[]}");
            Click(window, Find<Button>(view, "ValidateButton"));
            Check(model.ValidationMessage.Contains("不代表任务已运行"), "valid JSON does not claim execution");

            Click(window, Find<Button>(view, "PreviewLogsButton"));
            var logs = Find<ListBox>(view, "LogList");
            Check(logs.ItemCount == 200, $"initial log binding (got {logs.ItemCount})");
            for (var index = 0; index < 21; index++) Click(window, Find<Button>(view, "AddLogsButton"));
            Check(logs.ItemCount == 2000, $"log retention bound at 2,000 (got {logs.ItemCount})");
            logs.ScrollIntoView(model.Logs[^1]);
            Pump();
            var realized = logs.GetVisualDescendants().OfType<ListBoxItem>().Count();
            Check(realized > 0 && realized < 100, $"long logs are virtualized (realized {realized})");
            Check(logs.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == model.Logs[^1].Message),
                "last log can be reached");
            Capture(window, output, "offline-preview-logs-1100x800.png");

            Click(window, Find<Button>(view, "ThemeButton"));
            Check(model.IsDark && Application.Current!.RequestedThemeVariant == ThemeVariant.Dark,
                "offline preview theme command and resources");
            Click(window, Find<Button>(view, "ThemeButton"));
            Check(!model.IsDark && Application.Current!.RequestedThemeVariant == ThemeVariant.Light,
                "offline preview theme switches back");
            Console.WriteLine($"PASS: offline preview regressions kept: Chinese input binding, JSON validation, " +
                $"2,000-row log list with {realized} realized controls.");
        }
        finally { window.Close(); }
    }

    /// <summary>用全新视图与窗口渲染一帧对照图：不含任何交互状态与指针悬停。</summary>
    private static void CaptureClean(string output, double width, double height, bool dark, string filename)
    {
        var view = new MainView();
        // 新视图的 IsDark 初值相同，赋值不会触发变更；这里直接同步应用主题，避免沿用上一帧的主题。
        Application.Current!.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        view.Model.IsDark = dark;
        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        try { Capture(window, output, filename); }
        finally { window.Close(); }
    }

    /// <summary>在干净窗口上先摆好一个可复现的状态再截图（抽屉、右栏浮层），不含点击残留。</summary>
    private static void CaptureCleanState(string output, double width, double height, bool dark, string filename, Action<MainView> arrange)
    {
        var view = new MainView();
        Application.Current!.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        view.Model.IsDark = dark;
        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        try
        {
            Pump();
            arrange(view);
            Capture(window, output, filename);
        }
        finally { window.Close(); }
    }

    private static T Find<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
        ?? throw new Exception($"Missing control: {name}");

    private static void Click(Window window, Control control)
    {
        control.BringIntoView();
        Pump();
        // 先真实悬停再取点：上游顶栏的快捷开关在悬停时会展开并把后面的元素右移，
        // 按悬停前坐标按下会在布局变化后落空。
        var probe = control.TranslatePoint(new Point(1, control.Bounds.Height / 2), window)
            ?? throw new Exception($"Control is detached: {control.Name}");
        window.MouseMove(probe);
        Pump();
        // 悬停引起的布局变化可能延后一轮才生效：等到控件位置连续两次一致再按，
        // 否则按下点会落在移动前的位置上（顶栏快捷开关展开时就出现过这种竞态）。
        Point? previous = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var current = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
                ?? throw new Exception($"Control is detached: {control.Name}");
            if (previous is { } stable && Math.Abs(stable.X - current.X) < 0.5 && Math.Abs(stable.Y - current.Y) < 0.5)
                break;
            previous = current;
            Pump();
        }
        var point = previous ?? throw new Exception($"Control is detached: {control.Name}");
        window.MouseMove(point);
        Pump();
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Pump();
    }

    private static void Capture(Window window, string output, string filename)
    {
        // 截图前把指针移到角落：上游基准是无指针状态，悬停样式不应进入截图。
        window.MouseMove(new Point(2, 2));
        Pump();
        using var bitmap = window.CaptureRenderedFrame() ?? throw new Exception("No headless rendered frame");
        Check(bitmap.PixelSize.Width > 300 && bitmap.PixelSize.Height > 400, "frame dimensions");
        bitmap.Save(Path.Combine(output, filename), PngBitmapEncoderOptions.Default);
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>布局取整后允许 1px 误差（CSS 的 41.5px 在 Avalonia 会取整为 42）。</summary>
    private static bool Near(double actual, double expected, double tolerance = 1) => Math.Abs(actual - expected) <= tolerance;

    private static void Check(bool value, string label)
    {
        if (!value) throw new Exception("FAIL: " + label);
    }
}
