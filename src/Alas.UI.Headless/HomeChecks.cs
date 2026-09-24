using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Alas.UI.ViewModels;
using Alas.UI.Views;

namespace Alas.UI.Headless;

/// <summary>
/// 主页（Home）的无头验收：只断言真实渲染出来的几何与状态，不连接服务、设备或可见窗口。
///
/// 口径与上游基准 <c>.runtime/ui-reference/shots/&lt;主题&gt;/&lt;尺寸&gt;/home.json</c> 一致：
/// <list type="bullet">
///   <item>文案与字号取 home.css 的规则值（clamp 后的实测值）。</item>
///   <item>几何取 light/desktop 与 light/mobile 的实测值，容差见每一处的 Near 参数。</item>
///   <item>「没有服务」必须是真实断线态：不渲染任何实例卡，操作禁用并给出原因。</item>
/// </list>
/// </summary>
internal static class HomeChecks
{
    /// <summary>上游 home.css 的 ≤950px 断点：两列变单列。</summary>
    private const double NarrowBreakpoint = 950;

    /// <summary>上游 home.css 的 ≤620px 断点：紧凑档。</summary>
    private const double CompactBreakpoint = 620;

    /// <summary>1280 视口下上游实测的外壳内容宽（1280 − 232 侧栏 − 14 间距），用于复算卡片尺寸。</summary>
    private const double ContentWidth1280 = 1034;

    /// <summary>1280 视口下上游实测的内容区高（820 − 41.5 顶栏）。</summary>
    private const double ContentHeight1280 = 778.5;

    /// <summary>
    /// 在已 Show() 的窗口上验收主页。
    /// 调用前提：MainView 已经把 HomeView 挂进可视树，且主页是当前可见页（默认路由）。
    /// </summary>
    internal static void Run(Window window, MainView view)
    {
        var home = view.GetVisualDescendants().OfType<HomeView>().FirstOrDefault()
            ?? throw new Exception(
                "FAIL: MainView 的可视树里没有 HomeView：外壳尚未把主页接进内容区（HomeChecks 只验收真实挂载的页面）。");
        RunOn(window, home);
    }

    /// <summary>
    /// 在已 Show() 的窗口上验收一个主页实例（<see cref="Run"/> 找到外壳里的那个之后走这里）。
    /// 主页必须已经是该窗口的可见内容；独立离屏检查也用它。
    /// </summary>
    internal static void RunOn(Window window, HomeView home)
    {
        if (home.DataContext is not HomeViewModel model)
            throw new Exception(
                $"FAIL: HomeView 的 DataContext 不是 HomeViewModel（实际 {home.DataContext?.GetType().Name ?? "null"}）。");
        Check(home.IsVisible && home.Bounds.Width > 0 && home.Bounds.Height > 0,
            $"调用 HomeChecks 时主页必须是当前可见页（可见 {home.IsVisible}，尺寸 {home.Bounds.Width}x{home.Bounds.Height}）");

        Pump();
        CheckDisconnectedDefault(window, home, model);
        CheckDeck(home);
        CheckHeading(home, model);
        CheckNarrow(window, home);
        CheckCardGridFixture();
        CheckNarrowGridFixture();

        // 还原成调用前的宽屏基线，避免影响后续检查。
        window.Width = 1280;
        window.Height = 820;
        Pump();
        // 外壳里主页外面还有 ScrollViewer 与栅格，恢复尺寸后需要第二次布局才量得到新宽度。
        Pump();
        Check(Editorial(home).Bounds.Width > 900, "主页在两列布局下恢复了宽屏基线");
    }

    // ---------------------------------------------------------------- 断线默认态

    /// <summary>没有服务时必须显示断线态：不渲染实例卡、统计值未知、重试按钮可用。</summary>
    private static void CheckDisconnectedDefault(Window window, HomeView home, HomeViewModel model)
    {
        Check(!model.IsConnected, "默认数据源必须是未连接（不得把演示实例当成已连接）");
        Check(model.Instances.Count == 0, $"未连接时不渲染任何实例（实际 {model.Instances.Count} 个）");
        Check(Find<ItemsControl>(home, "InstanceGrid").ItemCount == 0, "实例网格里没有任何卡片");

        var panel = Find<Panel>(home, "DisconnectedPanel");
        Check(panel.IsVisible, "未接服务时断线面板可见");
        Check(!Find<Button>(home, "EmptyStatePanel").IsVisible, "未接服务时不显示「创建第一个实例」空态");
        Check(Find<TextBlock>(home, "DisconnectedTitle").Text == "未连接 Alas 服务",
            $"断线标题为「未连接 Alas 服务」（实际 {Find<TextBlock>(home, "DisconnectedTitle").Text}）");
        Check(Find<TextBlock>(home, "DisconnectedText").Text is { Length: > 10 } text && text.Contains("不显示任何实例"),
            "断线说明写清不会显示任何实例");

        var retry = Find<Button>(home, "RetryButton");
        Check(retry.IsEnabled, "断线态的重试按钮可用（不是死按钮）");
        Check(Find<TextBlock>(home, "RetryText").Text == "重试连接", "重试按钮文案与上游断线口径一致");

        // 卡片上的运行状态徽标一个都不应出现。
        Check(!home.GetVisualDescendants().OfType<Border>().Any(border => border.Classes.Contains("status")),
            "断线态下没有渲染任何实例状态徽标");

        // 重试：数据源不支持刷新时如实说明，不假装已经连上。
        Click(window, retry);
        Check(!model.IsConnected, "重试不会把未连接状态伪装成已连接");
        Check(model.HasStatusMessage && model.StatusMessage.Contains("未连接"), "重试后如实播报仍未连接");
    }

    // ---------------------------------------------------------------- 左列指挥台

    /// <summary>deck 三段文案与字号、内外边距、统计与链接行。</summary>
    private static void CheckDeck(HomeView home)
    {
        var editorial = Editorial(home);
        var deck = Find<Border>(home, "Deck");
        var main = Find<Border>(home, "MainPanel");

        // .home-editorial {grid-template-columns: minmax(300px, 34%) minmax(0, 1fr)}：1280 实测 351.55 / 682.45。
        Check(editorial.ColumnDefinitions.Count == 2, "宽屏下指挥台与实例区是两列");
        Check(Near(deck.Bounds.Width / editorial.Bounds.Width, 0.34, 0.005),
            $"指挥台占 34%（实测 {deck.Bounds.Width:0.##}/{editorial.Bounds.Width:0.##}）");
        Check(Near(Left(deck, home), Left(editorial, home)), "指挥台在左列");
        Check(Near(Left(main, home), Left(deck, home) + deck.Bounds.Width, 1.5), "实例区紧接在指挥台右侧");

        // .home-deck {padding: clamp(28px, 3.4vw, 54px)}：1280 → 3.4vw = 43.52。
        Check(Near(deck.Padding.Left, 43.52, 0.2), $"指挥台内边距 43.52（实测 {deck.Padding.Left}）");
        Check(Near(deck.Padding.Top, 43.52, 0.2), $"指挥台纵向内边距 43.52（实测 {deck.Padding.Top}）");

        var eyebrow = Find<TextBlock>(home, "EyebrowText");
        Check(eyebrow.Text == "你的指挥中心", $"eyebrow 文案（实际 {eyebrow.Text}）");
        Check(Near(eyebrow.FontSize, 12, 0.01), $"eyebrow 字号 12（实际 {eyebrow.FontSize}）");
        Check(Near(eyebrow.LetterSpacing, 1.68, 0.01), $"eyebrow 字距 1.68（实际 {eyebrow.LetterSpacing}）");
        Check(Near(eyebrow.Opacity, 0.74, 0.01), $"eyebrow 不透明度 .74（实际 {eyebrow.Opacity}）");
        Check(eyebrow.IsVisible, "宽屏下 eyebrow 可见");

        var greeting = Find<TextBlock>(home, "GreetingText");
        Check(greeting.Text is "上午好，指挥官！" or "下午好，指挥官！" or "晚上好，指挥官！",
            $"问候语按本地时间取上游三段之一（实际 {greeting.Text}）");
        Check(HomeViewModel.GreetingFor(new DateTime(2026, 1, 1, 9, 0, 0)) == "上午好，指挥官！"
            && HomeViewModel.GreetingFor(new DateTime(2026, 1, 1, 14, 0, 0)) == "下午好，指挥官！"
            && HomeViewModel.GreetingFor(new DateTime(2026, 1, 1, 22, 0, 0)) == "晚上好，指挥官！",
            "getGreeting 的三段边界（5-12 / 12-18 / 其余）与上游一致");
        // .home-deck-greeting {font-size: clamp(30px, 3vw, 42px); line-height: 1.24}：1280 → 38.4 / 47.616。
        Check(Near(greeting.FontSize, 38.4, 0.05), $"问候语字号 38.4（实际 {greeting.FontSize}）");
        Check(Near(greeting.LineHeight, 47.616, 0.01), $"问候语行高 47.616（实际 {greeting.LineHeight}）");
        Check(Near(greeting.LetterSpacing, -0.8, 0.01), $"问候语字距 -0.8（实际 {greeting.LetterSpacing}）");

        var subtitle = Find<TextBlock>(home, "SubtitleText");
        Check(subtitle.Text == "每一次出航，都井然有序。所有实例与任务，尽在掌握。",
            $"副标题文案（实际 {subtitle.Text}）");
        Check(Near(subtitle.FontSize, 13.5, 0.01), $"副标题字号 13.5（实际 {subtitle.FontSize}）");
        Check(Near(subtitle.LineHeight, 24.3, 0.01), $"副标题行高 24.3（实际 {subtitle.LineHeight}）");
        Check(Near(subtitle.Opacity, 0.82, 0.01), $"副标题不透明度 .82（实际 {subtitle.Opacity}）");
        Check(subtitle.IsVisible, "宽屏下副标题可见");

        // .home-stats：全部实例 / 运行中 / 需要处理，dt 12px、dd 22px。
        var stats = Find<WrapPanel>(home, "Stats");
        var labels = stats.GetVisualDescendants().OfType<TextBlock>().Where(block => block.Classes.Contains("home-stat-label")).ToList();
        Check(labels.Select(block => block.Text).SequenceEqual(new[] { "全部实例", "运行中", "需要处理" }),
            $"统计三项文案与顺序（实际 {string.Join('/', labels.Select(block => block.Text))}）");
        Check(labels.All(block => Near(block.FontSize, 12, 0.01)), "统计标签 12px");
        var values = stats.GetVisualDescendants().OfType<TextBlock>().Where(block => block.Classes.Contains("home-stat-value")).ToList();
        Check(values.Count == 3 && values.All(block => Near(block.FontSize, 22, 0.01)), "统计数字 22px");
        Check(values.All(block => block.Text == HomeViewModel.UnknownValue),
            $"未连接时统计值是未知占位而不是 0（实际 {string.Join('/', values.Select(block => block.Text))}）");

        // .home-deck-links：开源项目（外链）+ 旧版界面切换。
        var links = Find<WrapPanel>(home, "DeckLinks");
        Check(links.Children.Count == 2, "指挥台底部有两个药丸链接");
        var openSource = Find<Button>(home, "OpenSourceLink");
        Check(openSource.IsEnabled, "开源项目链接可用");
        Check(openSource.Bounds.Width > 90 && Near(openSource.Bounds.Height, 34, 1),
            $"药丸链接高 34（实测 {openSource.Bounds.Height}）");
        Check(home.DataContext is HomeViewModel deckModel
              && deckModel.OpenSourceUrl.StartsWith("https://github.com/", StringComparison.Ordinal),
            "开源项目地址是上游仓库");

        var legacy = Find<Button>(home, "LegacyUiToggle");
        Check(!legacy.IsEnabled, "旧版界面快捷开关在未被外壳接管时禁用");
        Check(Tip(legacy) is { Length: > 8 }, "旧版界面开关给出了禁用原因");
    }

    // ---------------------------------------------------------------- 右列实例区

    /// <summary>标题行（h2 + 新建实例）与五个实例动作的禁用与说明。</summary>
    private static void CheckHeading(HomeView home, HomeViewModel model)
    {
        var main = Find<Border>(home, "MainPanel");
        // .home-main {padding: clamp(26px,3.2vw,52px) clamp(26px,3.6vw,56px)}：1280 → 40.96 / 46.08。
        Check(Near(main.Padding.Top, 40.96, 0.2), $"实例区上内边距 40.96（实际 {main.Padding.Top}）");
        Check(Near(main.Padding.Left, 46.08, 0.2), $"实例区左内边距 46.08（实际 {main.Padding.Left}）");

        var title = Find<TextBlock>(home, "InstancesTitle");
        Check(title.Text == "实例", $"标题为「实例」（实际 {title.Text}）");
        Check(Near(title.FontSize, 28.16, 0.05), $"标题字号 28.16（实际 {title.FontSize}）");
        Check(Near(title.LetterSpacing, -0.6, 0.01), $"标题字距 -0.6（实际 {title.LetterSpacing}）");

        // 上游 .button.primary「新建实例」实测 112x40、圆角 20（classic 主题的 --theme-radius-button）。
        var create = Find<Button>(home, "NewInstanceButton");
        Check(Near(create.Bounds.Width, 112, 1), $"新建实例按钮宽 112（实际 {create.Bounds.Width}）");
        Check(Near(create.Bounds.Height, 40, 1), $"新建实例按钮高 40（实际 {create.Bounds.Height}）");
        Check(Near(Right(create, home), Right(main, home) - 46.08, 1.5),
            $"新建实例按钮贴住实例区右内边距（按钮右 {Right(create, home):0.##}，实例区右 {Right(main, home):0.##}）");
        Check(!create.IsEnabled, "未接服务时「新建实例」禁用");

        // 五个实例动作一律禁用并写明原因，不留点了没反应的死按钮。
        foreach (var (name, action) in new[]
                 {
                     ("NewInstanceButton", "新建实例"),
                     ("ImportInstanceButton", "导入实例"),
                     ("DeleteInstanceButton", "删除实例"),
                     ("SwitchInstanceButton", "切换实例"),
                     ("InstanceSettingsButton", "实例设置"),
                 })
        {
            var button = Find<Button>(home, name);
            Check(!button.IsEnabled, $"{action}在未接服务时禁用");
            var tip = Tip(button);
            Check(tip is { Length: > 8 }, $"{action}给出了禁用原因（实际 {tip ?? "无 ToolTip"}）");
        }

        Check(Find<Button>(home, "ImportInstanceButton").IsEnabled == model.CanImportInstance
            && Find<Button>(home, "InstanceSettingsButton").IsEnabled == model.CanOpenInstanceSettings,
            "按钮可用性与视图模型一致（不是写死的 IsEnabled）");
    }

    // ---------------------------------------------------------------- 窄屏

    /// <summary>≤950px 单列、≤620px 紧凑档：与 shots/light/mobile/home.json 的实测对齐。</summary>
    private static void CheckNarrow(Window window, HomeView home)
    {
        window.Width = 390;
        window.Height = 844;
        Pump();
        Pump();

        var editorial = Editorial(home);
        var deck = Find<Border>(home, "Deck");
        var main = Find<Border>(home, "MainPanel");
        var title = Find<TextBlock>(home, "InstancesTitle");
        var greeting = Find<TextBlock>(home, "GreetingText");

        Check(editorial.ColumnDefinitions.Count == 1, "≤950px 时 home-editorial 变单列");
        Check(Grid.GetRow(deck) == 0 && Grid.GetRow(main) == 1, "单列时指挥台在上、实例区在下");
        Check(Near(deck.Bounds.Width, editorial.Bounds.Width, 1),
            $"单列时指挥台占满整行（{deck.Bounds.Width:0.##}/{editorial.Bounds.Width:0.##}）");
        Check(Near(main.Bounds.Y, deck.Bounds.Y + deck.Bounds.Height, 1),
            $"实例区紧接指挥台下方（deck 底 {deck.Bounds.Y + deck.Bounds.Height:0.##}，main 顶 {main.Bounds.Y:0.##}）");

        // ≤620px：padding 22px 20px、gap 16、min-height 210，eyebrow/subtitle 隐藏（上游 home.css:218-224）。
        Check(Near(deck.Padding.Left, 20, 0.2) && Near(deck.Padding.Top, 22, 0.2),
            $"紧凑档指挥台内边距 22/20（实测 {deck.Padding.Top}/{deck.Padding.Left}）");
        Check(Near(deck.MinHeight, 210, 0.2), $"紧凑档指挥台最小高 210（实测 {deck.MinHeight}）");
        Check(Find<Grid>(home, "DeckLayout").RowSpacing == 16, "紧凑档指挥台行距 16");
        Check(!Find<TextBlock>(home, "EyebrowText").IsVisible && !Find<TextBlock>(home, "SubtitleText").IsVisible,
            "≤620px 隐藏 eyebrow 与 subtitle");
        Check(Near(main.Padding.Left, 18, 0.2) && Near(main.Padding.Top, 24, 0.2),
            $"紧凑档实例区内边距 24/18（实测 {main.Padding.Top}/{main.Padding.Left}）");

        // mobile 实测：h1 28px/34.72、h2 22px、按钮高 44、标题竖排。
        Check(Near(greeting.FontSize, 28, 0.05), $"紧凑档问候语 28px（实际 {greeting.FontSize}）");
        Check(Near(greeting.LineHeight, 34.72, 0.05), $"紧凑档问候语行高 34.72（实际 {greeting.LineHeight}）");
        Check(Near(title.FontSize, 22, 0.05), $"紧凑档标题 22px（实际 {title.FontSize}）");
        Check(Near(Find<Button>(home, "NewInstanceButton").Bounds.Height, 44, 1),
            "≤950px 时按钮高 44（apple.css:319）");
        Check(Find<Grid>(home, "HeadingRow").RowDefinitions.Count == 2,
            "≤620px 标题行改为竖排（h2 一行、按钮一行）");
    }

    // ---------------------------------------------------------------- 卡片网格（显式演示夹具）

    /// <summary>
    /// 卡片网格的列数与卡片尺寸。没有实例时网格是空的，所以这里用一份**显式标注**的演示夹具
    /// （每张卡都带 IsDemo=true，页面上渲染「演示数据」徽标）在独立离屏窗口里按上游实测尺寸复算。
    /// 产品默认数据源仍然是断线空态，夹具只存在于本检查文件里。
    /// </summary>
    private static void CheckCardGridFixture()
    {
        var model = new HomeViewModel(new FixtureInstanceSource());
        var window = OpenFixture(model, ContentWidth1280, ContentHeight1280, 1280, 820);
        try
        {
            var home = window.GetVisualDescendants().OfType<HomeView>().FirstOrDefault()
                ?? throw new Exception("FAIL: 夹具窗口没有挂载 HomeView");
            var grid = Find<ItemsControl>(home, "InstanceGrid");
            Check(grid.ItemCount == 3, $"夹具渲染三张卡片（实际 {grid.ItemCount}）");
            Check(!Find<Panel>(home, "DisconnectedPanel").IsVisible, "已连接时不显示断线面板");
            Check(!Find<Button>(home, "EmptyStatePanel").IsVisible, "有实例时不显示空态");

            var cards = Cards(window);
            Check(cards.Count == 3, $"渲染出三张卡片（实际 {cards.Count}）");
            var boxes = cards.Select(card => Box(card, window)).ToList();
            // .home-instance-grid {grid-template-columns: repeat(auto-fill, minmax(min(100%,250px),1fr)); gap:20px}
            // 1280 下内容宽 590.3 → 2 列、每列 285.14（上游实测）。
            Check(boxes.Select(box => Math.Round(box.Y, 1)).Distinct().Count() == 2,
                $"590.3 宽的网格排成两行（2 列 + 1 列）（卡片 {string.Join(" / ", boxes.Select(box => $"{box.X:0.#},{box.Y:0.#} {box.Width:0.##}x{box.Height:0.##}"))}）");
            Check(Near(boxes[0].Y, boxes[1].Y, 0.5) && boxes[2].Y > boxes[0].Y + 100,
                "前两张同一行，第三张换行");
            foreach (var card in cards)
                Check(Near(card.Bounds.Width, 285.14, 1.5), $"卡片宽 285.14（实际 {card.Bounds.Width:0.##}）");
            Check(Near(boxes[1].X - (boxes[0].X + boxes[0].Width), 20, 1), "卡片列间距 20");
            Check(Near(boxes[2].Y - (boxes[0].Y + boxes[0].Height), 20, 1), "卡片行间距 20");
            // .home-instance-grid .instance-card {padding: 24px} + .panel 的 1px 边框 → 内容偏移 25。
            Check(Near(cards[0].Padding.Left, 24, 0.01), $"卡片内边距 24（实际 {cards[0].Padding.Left}）");
            var name = cards[0].GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(block => block.Classes.Contains("instance-name"))
                ?? throw new Exception("FAIL: 卡片里没有实例名文本");
            Check(Near(Left(name, cards[0]), 25, 1.5),
                $"卡片内容从 25px 开始（24 内边距 + 1 边框，实际 {Left(name, cards[0]):0.##}）");
            // 卡片高 221（上游实测：24+44+22+27+10+15+22+31+24 的内容盒）。
            Check(Near(cards[0].Bounds.Height, 221, 12), $"卡片高约 221（实际 {cards[0].Bounds.Height:0.##}）");
            Check(Near(cards[0].Bounds.Height, cards[2].Bounds.Height, 1), "同一网格里卡片等高");

            // 卡片内容：状态徽标 + 实例名 + 设备地址 + 底部状态文案。
            var badges = cards[0].GetVisualDescendants().OfType<Border>().Where(border => border.Classes.Contains("status")).ToList();
            Check(badges.Count == 1, "卡片有一个状态徽标");
            Check(cards[0].GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "待命中"),
                "stopped 徽标用上游词表「待命中」");
            Check(cards[2].GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "需要处理"),
                "error 徽标用上游词表「需要处理」");
            Check(name.Text == "fixture-main", $"实例名取自数据源（实际 {name.Text}）");
            Check(cards[0].GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "127.0.0.1:5555"),
                "卡片显示设备地址");
            Check(cards[0].GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "未运行")
                && cards[2].GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "需要处理"),
                "卡片底部状态文案随状态变化");

            // 演示数据必须被明确标注，不能冒充真实实例。
            foreach (var card in cards)
                Check(card.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "演示数据"),
                    "演示夹具卡片带「演示数据」徽标");

            // 点卡片 = 选中该实例并把实例名交给外壳（真实可用的交互，而不是死绑定）。
            string? selected = null;
            model.InstanceSelected += (_, instance) => selected = instance;
            Click(window, cards[1]);
            Check(selected == "fixture-alt", $"点击卡片把选中的实例名交给外部（实际 {selected ?? "null"}）");

            Check(!Find<Button>(home, "NewInstanceButton").IsEnabled && model.CreateInstanceNotice.Contains("当前环境无法"),
                "已连接但未提供创建能力时说明操作不可用");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>390px 下卡片网格必须变单列（mobile 实测：354 宽、gap 16）。</summary>
    private static void CheckNarrowGridFixture()
    {
        var model = new HomeViewModel(new FixtureInstanceSource());
        var window = OpenFixture(model, 390, 844, 390, 844);
        try
        {
            var cards = Cards(window);
            Check(cards.Count == 3, "紧凑夹具同样渲染三张卡片");
            var boxes = cards.Select(card => Box(card, window)).ToList();
            Check(Near(cards[0].Bounds.Width, 354, 1.5), $"390px 下卡片宽 354（实际 {cards[0].Bounds.Width:0.##}）");
            Check(boxes[0].Y < boxes[1].Y && boxes[1].Y < boxes[2].Y,
                $"390px 下卡片网格是单列（{string.Join(" / ", boxes.Select(box => $"{box.X:0.#},{box.Y:0.#}"))}）");
            Check(Near(boxes[1].Y - (boxes[0].Y + boxes[0].Height), 16, 1), "紧凑档卡片间距 16");
        }
        finally
        {
            window.Close();
        }
    }

    // ---------------------------------------------------------------- 夹具与工具

    /// <summary>
    /// 只在无头检查里使用的夹具数据源：显式声明已连接，且三条都是 <c>IsDemo=true</c> 的演示实例，
    /// 卡片上会渲染「演示数据」徽标。产品默认数据源（<see cref="DisconnectedInstanceSource"/>）不受影响。
    /// </summary>
    private sealed class FixtureInstanceSource : IInstanceSource
    {
        public bool IsConnected => true;

        public IReadOnlyList<InstanceCardViewModel> Instances { get; } = new[]
        {
            InstanceCardViewModel.Create("fixture-main", "stopped", "EN", "127.0.0.1:5555", isDemo: true),
            InstanceCardViewModel.Create("fixture-alt", "stopped", "EN", "127.0.0.1:5557", isDemo: true),
            InstanceCardViewModel.Create("fixture-error", "error", "EN", "127.0.0.1:5559", isDemo: true),
        };
    }

    /// <summary>
    /// 用固定内容尺寸的离屏窗口复算上游 1280 视口下的几何：
    /// 视口宽决定 vw 换算，宿主的宽高决定列与卡片尺寸（等价于外壳 232 + 14 之后的内容区）。
    /// </summary>
    private static Window OpenFixture(HomeViewModel model, double hostWidth, double hostHeight, double width, double height)
    {
        var host = new Border
        {
            Width = hostWidth,
            Height = hostHeight,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Child = new HomeView(model),
        };
        var window = new Window { Width = width, Height = height, Content = host };
        window.Show();
        Pump();
        return window;
    }

    private static List<Button> Cards(Window window) => window.GetVisualDescendants().OfType<Button>()
        .Where(button => button.Classes.Contains("instance-card")).ToList();

    private static Grid Editorial(HomeView home) => Find<Grid>(home, "Editorial");

    /// <summary>控件左上角相对另一个控件的横坐标（Bounds.X 只相对直接父级，跨层比较会错）。</summary>
    private static double Left(Visual visual, Visual relativeTo) =>
        visual.TranslatePoint(new Point(0, 0), relativeTo)?.X ?? double.NaN;

    /// <summary>控件在另一个控件坐标系里的矩形（ItemsControl 的项外面还套着 ContentPresenter）。</summary>
    private static Rect Box(Control control, Visual relativeTo) =>
        control.TranslatePoint(new Point(0, 0), relativeTo) is { } origin
            ? new Rect(origin, control.Bounds.Size)
            : throw new Exception($"FAIL: 控件已脱离可视树 {control.Name}");

    /// <summary>控件右上角相对另一个控件的横坐标。</summary>
    private static double Right(Control control, Visual relativeTo) =>
        control.TranslatePoint(new Point(control.Bounds.Width, 0), relativeTo)?.X ?? double.NaN;

    private static string? Tip(Control control) => control.GetValue(ToolTip.TipProperty) as string;

    private static T Find<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
        ?? throw new Exception($"FAIL: 找不到控件 {name}");

    /// <summary>真实指针点击（与 Program.cs 的做法一致：先悬停再按下，避免布局竞态）。</summary>
    private static void Click(Window window, Control control)
    {
        control.BringIntoView();
        Pump();
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
            ?? throw new Exception($"FAIL: 控件已脱离可视树 {control.Name}");
        window.MouseMove(point);
        Pump();
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Pump();
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>布局取整允许的误差；上游 CSS 的小数像素在 Avalonia 会落到 1px 内。</summary>
    private static bool Near(double actual, double expected, double tolerance = 1) =>
        Math.Abs(actual - expected) <= tolerance;

    private static void Check(bool value, string label)
    {
        if (!value) throw new Exception("FAIL: " + label);
    }
}
