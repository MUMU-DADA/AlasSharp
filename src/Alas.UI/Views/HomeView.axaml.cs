using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Alas.UI.Theming;
using Alas.UI.ViewModels;

namespace Alas.UI.Views;

/// <summary>
/// 主页视图（上游 <c>src/pages/Home.tsx</c> + <c>src/styles/home.css</c>）。
///
/// 上游用 CSS 媒体查询（≤950px 单列、≤620px 紧凑）与 <c>vw</c> 单位表达的所有尺寸，
/// Avalonia 都没有对应语法，因此集中在 <see cref="ApplyResponsive"/> 里按同一公式换算：
/// <list type="bullet">
///   <item><c>clamp(a, Xvw, b)</c> → <see cref="Clamp"/>(a, X/100 * 视口宽, b)</item>
///   <item><c>@media (max-width: 950px)</c> → <see cref="NarrowBreakpoint"/></item>
///   <item><c>@media (max-width: 620px)</c> → <see cref="CompactBreakpoint"/></item>
/// </list>
/// 视口宽取 <see cref="TopLevel"/> 的 ClientSize（上游的 <c>vw</c> 就是窗口宽，不是内容区宽）。
/// </summary>
public partial class HomeView : UserControl
{
    /// <summary>上游 home.css 的 <c>@media (max-width: 950px)</c>：两列变单列、deck 在上。</summary>
    public const double NarrowBreakpoint = 950;

    /// <summary>上游 home.css 的 <c>@media (max-width: 620px)</c>：deck 更矮、隐藏 eyebrow/subtitle、标题竖排。</summary>
    public const double CompactBreakpoint = 620;

    /// <summary>上游 <c>min-height: calc(var(--viewport-height) - 56px)</c> 里的常量。</summary>
    private const double ViewportHeightAllowance = 56;

    /// <summary>上游 <c>.home-editorial</c> 的左列占比：<c>minmax(300px, 34%)</c>。</summary>
    private const double DeckSharePercent = 34;

    private TopLevel? _topLevel;
    private INotifyCollectionChanged? _mergedDictionaries;
    private double _cardGap = 20;
    private UiSkin? _appliedSkin;

    public HomeView()
        : this(new HomeViewModel())
    {
    }

    public HomeView(HomeViewModel model)
    {
        InitializeComponent();
        Model = model ?? throw new ArgumentNullException(nameof(model));
        DataContext = model;

        AttachedToVisualTree += (_, _) =>
        {
            HookTopLevel();
            HookApplication();
            ApplySkin();
            ApplyResponsive();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            UnhookTopLevel();
            UnhookApplication();
        };
        SizeChanged += (_, _) => ApplyResponsive();
    }

    /// <summary>视图默认的视图模型；外壳若用绑定替换 DataContext，以 DataContext 为准。</summary>
    public HomeViewModel Model { get; }

    private HomeViewModel? ViewModel => DataContext as HomeViewModel ?? Model;

    /// <summary>
    /// 主题切换由 ThemeService 换掉 Application 资源里的皮肤字典，这里跟着重算指挥台配色。
    /// 资源集合在通知时点上的可见状态并不稳定（合并/移除各触发一次），
    /// 因此事件里立刻算一次，再排一次到消息队列校正；布局时也会兜底比对一次。
    /// </summary>
    private void HookApplication()
    {
        UnhookApplication();
        if (Application.Current?.Resources is not { } resources) return;
        if (resources.MergedDictionaries is not INotifyCollectionChanged merged) return;
        _mergedDictionaries = merged;
        merged.CollectionChanged += OnThemeResourcesChanged;
    }

    private void UnhookApplication()
    {
        if (_mergedDictionaries is null) return;
        _mergedDictionaries.CollectionChanged -= OnThemeResourcesChanged;
        _mergedDictionaries = null;
    }

    private void OnThemeResourcesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 立刻算一次；再排一次到消息队列，等整轮皮肤替换结束后按最终资源重算一遍。
        // 两次都是幂等的赋值，成本只有几个画笔，不做「皮肤没变就跳过」的判断：
        // 同一皮肤换明暗（classic light ↔ dark）时皮肤名不变，但取到的画笔对象必须更新。
        ApplySkin();
        Avalonia.Threading.Dispatcher.UIThread.Post(ApplySkin, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>皮肤没变就不重复上色；布局与主题通知都会走到这里。</summary>
    private void EnsureSkin()
    {
        if (_appliedSkin != CurrentSkin()) ApplySkin();
    }

    private void HookTopLevel()
    {
        UnhookTopLevel();
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null) _topLevel.SizeChanged += OnTopLevelSizeChanged;
    }

    private void UnhookTopLevel()
    {
        if (_topLevel is not null) _topLevel.SizeChanged -= OnTopLevelSizeChanged;
        _topLevel = null;
    }

    private void OnTopLevelSizeChanged(object? sender, SizeChangedEventArgs e) => ApplyResponsive();

    /// <summary>上游的 <c>vw</c>：窗口宽。未上屏时退回自身宽度，避免用 0 算出全部最小值。</summary>
    private Size Viewport()
    {
        var size = _topLevel?.ClientSize ?? default;
        if (size.Width <= 0) size = new Size(Bounds.Width, Bounds.Height);
        return size;
    }

    /// <summary>
    /// 按当前视口套用上游的断点与 clamp 公式。所有数值都来自
    /// <c>.runtime/ui-reference/shots/&lt;主题&gt;/&lt;尺寸&gt;/home.json</c> 实测与上游 home.css 的同一份规则。
    /// </summary>
    private void ApplyResponsive()
    {
        var viewport = Viewport();
        var width = viewport.Width;
        var height = viewport.Height;
        if (width <= 0) return;

        EnsureSkin();
        var narrow = width <= NarrowBreakpoint;
        var compact = width <= CompactBreakpoint;

        // .home-editorial：grid-template-columns minmax(300px, 34%) minmax(0, 1fr)；≤950px 变 1fr（deck 在上）。
        Editorial.ColumnDefinitions = narrow
            ? new ColumnDefinitions("*")
            : new ColumnDefinitions($"{DeckSharePercent}*,{100 - DeckSharePercent}*");
        Editorial.RowDefinitions = narrow ? new RowDefinitions("Auto,*") : new RowDefinitions("*");
        Grid.SetColumn(Deck, 0);
        Grid.SetRow(Deck, 0);
        Grid.SetColumn(MainPanel, narrow ? 0 : 1);
        Grid.SetRow(MainPanel, narrow ? 1 : 0);
        // 上游 main:has(> .home-editorial) 的 min-height，保证矮窗口下指挥台仍按整屏高度居中。
        Editorial.MinHeight = Math.Max(320, height - ViewportHeightAllowance);

        // .home-deck：padding clamp(28px, 3.4vw, 54px)；≤950px 26px 30px 且 min-height 248；≤620px 22px 20px 且 min-height 210。
        Deck.Padding = narrow
            ? compact ? new Thickness(20, 22, 20, 22) : new Thickness(30, 26, 30, 26)
            : new Thickness(Clamp(28, 54, Vw(3.4, width)));
        Deck.MinHeight = compact ? 210 : narrow ? 248 : 0;
        DeckLayout.RowSpacing = compact ? 16 : 26;

        // .home-main：padding clamp(26px, 3.2vw, 52px) clamp(26px, 3.6vw, 56px)；≤950px 30px 26px；≤620px 24px 18px。
        MainPanel.Padding = narrow
            ? compact ? new Thickness(18, 24, 18, 24) : new Thickness(26, 30, 26, 30)
            : new Thickness(Clamp(26, 56, Vw(3.6, width)), Clamp(26, 52, Vw(3.2, width)));

        // .home-deck-copy 三行：eyebrow 12px；h1 clamp(30px, 3vw, 42px)，≤950px 换 clamp(28px, 7vw, 40px)，行高 1.24；subtitle 13.5px/1.8。
        var greeting = narrow ? Clamp(28, 40, Vw(7, width)) : Clamp(30, 42, Vw(3, width));
        GreetingText.FontSize = greeting;
        GreetingText.LineHeight = Math.Round(greeting * 1.24, 3);
        // ≤620px 隐藏 eyebrow 与 subtitle（上游 .home-deck-eyebrow, .home-deck-subtitle {display:none}）。
        EyebrowText.IsVisible = !compact;
        SubtitleText.IsVisible = !compact;

        // .home-main-heading h2 clamp(22px, 2.2vw, 30px)；≤620px 竖排（gap 14）。
        InstancesTitle.FontSize = Clamp(22, 30, Vw(2.2, width));
        HeadingRow.ColumnDefinitions = compact ? new ColumnDefinitions("*") : new ColumnDefinitions("*,Auto");
        HeadingRow.RowDefinitions = compact ? new RowDefinitions("Auto,Auto") : new RowDefinitions("Auto");
        HeadingRow.ColumnSpacing = compact ? 0 : 20;
        HeadingRow.RowSpacing = compact ? 14 : 0;
        Grid.SetColumn(InstancesTitle, 0);
        Grid.SetRow(InstancesTitle, 0);
        Grid.SetColumn(HeadingActions, compact ? 0 : 1);
        Grid.SetRow(HeadingActions, compact ? 1 : 0);
        HeadingActions.HorizontalAlignment = compact ? Avalonia.Layout.HorizontalAlignment.Left
            : Avalonia.Layout.HorizontalAlignment.Right;
        HeadingActions.VerticalAlignment = compact ? Avalonia.Layout.VerticalAlignment.Top
            : Avalonia.Layout.VerticalAlignment.Bottom;
        InstancesTitle.VerticalAlignment = compact ? Avalonia.Layout.VerticalAlignment.Top
            : Avalonia.Layout.VerticalAlignment.Bottom;

        // .button：宽屏 height 40；≤950px 变 min-height 44（apple.css:319）。图标按钮同步放大到 44 触摸目标。
        NewInstanceButton.Height = narrow ? 44 : 40;
        var actionSize = narrow ? 44d : 32d;
        foreach (var action in new[] { ImportInstanceButton, DeleteInstanceButton, SwitchInstanceButton, InstanceSettingsButton })
        {
            action.Width = actionSize;
            action.Height = actionSize;
            action.CornerRadius = new CornerRadius(actionSize / 2);
        }

        // .home-instance-grid：gap 20；≤620px 变 16（列数由 CardGrid 按 minmax(min(100%,250px),1fr) 算）。
        _cardGap = compact ? 16 : 20;
        ApplyCardGap();
    }

    private void ApplyCardGap()
    {
        if (InstanceGrid.ItemsPanelRoot is Controls.CardGrid grid && Math.Abs(grid.ItemGap - _cardGap) > 0.01)
            grid.ItemGap = _cardGap;
    }

    /// <summary>
    /// 指挥台配色：上游只对 light/dark 两个玻璃主题写成白字白药丸
    /// （<c>[data-theme='light'] .home-deck, [data-theme='dark'] .home-deck {color:#fff}</c> 与
    /// <c>.home-deck-link {border-color: rgb(255 255 255 / .42); background: rgb(255 255 255 / .14)}</c>），
    /// minimal/extreme/legacy 走 <c>var(--text)</c>/<c>var(--surface-muted)</c>/<c>var(--border)</c>/<c>var(--surface)</c>。
    /// 共享令牌里没有那两档白色半透明，因此按同一公式从当前皮肤的 AlasOnAccentBrush 派生，不在 XAML 里写死颜色。
    /// </summary>
    private void ApplySkin()
    {
        var skin = CurrentSkin();
        var glass = skin == UiSkin.Classic;
        _appliedSkin = skin;
        var onAccent = ColorOf("AlasOnAccentBrush", Colors.White);
        var text = ColorOf("AlasTextBrush", Colors.Black);
        var deckForeground = new SolidColorBrush(glass ? onAccent : text);

        Deck.Background = (glass ? Brush("AlasBgBrush") : Brush("AlasSurfaceMutedBrush")) ?? Deck.Background;
        // Border 没有 Foreground，指挥台里的文字逐个上色（Avalonia 没有 CSS 那样的 color 继承链）。
        foreach (var block in DeckTexts()) block.Foreground = deckForeground;

        if (glass)
        {
            // rgb(255 255 255 / .14) 与 / .42，悬停 / .78（上游 home.css:169-170）。
            this.Resources["HomePillForegroundBrush"] = deckForeground;
            this.Resources["HomePillFillBrush"] = new SolidColorBrush(onAccent, 0.14);
            this.Resources["HomePillEdgeBrush"] = new SolidColorBrush(onAccent, 0.42);
            this.Resources["HomePillHoverEdgeBrush"] = new SolidColorBrush(onAccent, 0.78);
            this.Resources["HomePillHoverForegroundBrush"] = deckForeground;
            return;
        }

        this.Resources["HomePillForegroundBrush"] = Brush("AlasTextBrush") ?? Brushes.Black;
        this.Resources["HomePillFillBrush"] = Brush("AlasSurfaceBrush") ?? Brushes.Transparent;
        this.Resources["HomePillEdgeBrush"] = Brush("AlasBorderBrush") ?? Brushes.Transparent;
        this.Resources["HomePillHoverEdgeBrush"] = Brush("AlasAccentBrush") ?? Brushes.Transparent;
        this.Resources["HomePillHoverForegroundBrush"] = Brush("AlasAccentBrush") ?? Brushes.Transparent;
    }

    /// <summary>指挥台里需要跟随皮肤上色的文本：三行文案 + 三组统计的标签与数字。</summary>
    private IEnumerable<TextBlock> DeckTexts()
    {
        foreach (var child in DeckCopy.Children)
        {
            if (child is TextBlock block) yield return block;
        }

        foreach (var child in Stats.Children)
        {
            if (child is not Panel group) continue;
            foreach (var inner in group.Children)
            {
                if (inner is TextBlock block) yield return block;
            }
        }
    }

    /// <summary>
    /// 当前皮肤：ThemeService 把 <c>Styles/Themes/{Classic|Minimal|Legacy}.{Light|Dark}.axaml</c>
    /// 合并进 Application 资源，这里读同一份状态判定，避免主页自己再存一份主题。
    /// 资源解析是「后合并的字典优先」，所以从末尾往前找第一份皮肤字典，与真实生效的那份一致。
    /// </summary>
    private static UiSkin CurrentSkin()
    {
        if (Application.Current?.Resources is not { } resources) return UiSkin.Classic;
        var providers = resources.MergedDictionaries;
        for (var index = providers.Count - 1; index >= 0; index--)
        {
            var source = (providers[index] as ResourceInclude)?.Source?.ToString();
            if (string.IsNullOrEmpty(source)) continue;
            if (source.Contains("/Themes/Classic.", StringComparison.Ordinal)) return UiSkin.Classic;
            if (source.Contains("/Themes/Minimal.", StringComparison.Ordinal)) return UiSkin.Minimal;
            if (source.Contains("/Themes/Legacy.", StringComparison.Ordinal)) return UiSkin.Legacy;
        }
        return UiSkin.Classic;
    }

    private object? Resource(string key) => this.TryFindResource(key, out var value) ? value : null;

    private IBrush? Brush(string key) => Resource(key) as IBrush;

    private Color ColorOf(string key, Color fallback) =>
        Resource(key) is ISolidColorBrush brush ? brush.Color : fallback;

    /// <summary>CSS <c>clamp(min, Xvw, max)</c> 的 Xvw 项：<paramref name="percent"/> 是百分比数值（3.4 表示 3.4vw）。</summary>
    private static double Vw(double percent, double viewportWidth) => viewportWidth * percent / 100d;

    /// <summary>CSS <c>clamp(min, Xvw, max)</c>。</summary>
    private static double Clamp(double min, double max, double value) => Math.Min(Math.Max(min, value), max);

    private void OnInstanceCardClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: InstanceCardViewModel card }) ViewModel?.SelectInstance(card);
    }

    private async void OnOpenSourceClick(object? sender, RoutedEventArgs e)
    {
        var model = ViewModel;
        if (model is null) return;
        var launcher = TopLevel.GetTopLevel(this)?.Launcher;
        if (launcher is null)
        {
            model.Report("当前平台没有可用的外部链接打开方式。");
            return;
        }

        try
        {
            var opened = await launcher.LaunchUriAsync(new Uri(model.OpenSourceUrl));
            model.Report(opened
                ? $"已在系统浏览器打开 {model.OpenSourceUrl}"
                : $"系统未接受打开 {model.OpenSourceUrl} 的请求。");
        }
        catch (Exception error)
        {
            model.Report($"打开外部链接失败：{error.Message}");
        }
    }
}
