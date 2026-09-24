using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Alas.UI.Controls;

/// <summary>
/// 仅日志列表使用的**回收式虚拟化面板**（日志页专用，不用于其它页面）。
///
/// 目标：日志缓存上限 400 行（上游语义）时，**实现出来的控件只随视口变化**，并且逐行追加的成本
/// 只等于"一行进入 + 一行离开"。框架的 <c>ItemsControl + VirtualizingStackPanel</c> 在"上限淘汰
/// 发生在头部"时会把整页可见控件重建（下标整体位移导致容器失效），因此这里自己做虚拟化。
///
/// 与框架的滚动模型保持一致（这一点很关键）：**滚动归外层 ScrollViewer**。Avalonia 12 的
/// <c>ScrollContentPresenter</c> 才是滚动的主人，它按内容尺寸平移内容；框架自带的虚拟化面板也
/// 不实现 <c>ILogicalScrollable</c>，只负责"把与视口相交的行实现出来、按内容坐标排布"。
/// 因此本面板：
/// <list type="bullet">
///   <item><b>不接管滚动</b>：测量时返回完整内容高度（滚动宿主据此得到正确的 Extent），
///     排布时按**内容坐标**放行；滚轮/键盘/滚动条/跳转由框架处理，阅读锚点在布局后请求宿主补偿；</item>
///   <item><b>只随视口实现</b>：实现区间由外层当前偏移与视口高度决定，并在
///     <c>ScrollViewer.ScrollChanged</c> 时重算（挂接时订阅、离树时退订）；</item>
///   <item><b>行按数据项身份复用</b>：集合头部淘汰时视口内的行只是位置与索引平移，控件与已排版
///     文本原样保留；只有真正进入/离开区间的行才从池里取用或归还，且只改
///     <see cref="StyledElement.DataContext"/>，不重建模板；</item>
///   <item><b>一套高度模型</b>：按数据项身份缓存实测行高，未测过的用均值估算；区间、行位置、
///     内容高度三处都走同一套 <see cref="HeightAt"/> / <see cref="HeightBefore"/>，
///     变高行（自动换行）、窗口缩放、首行淘汰时不会出现"估算与实测各说各话"的漂移；</item>
///   <item><b>缓存淘汰按离可见区间的距离</b>，而不是索引最大者；</item>
///   <item><b>生命周期</b>：只有挂在可视树上才订阅数据源与滚动事件；离树退订并释放已实现的行
///     （控件留在池里），模板变化时整批重建，不复用旧模板树。</item>
/// </list>
///
/// 使用前提（由调用方保证）：
/// <list type="bullet">
///   <item><see cref="ItemsSource"/> 需实现 <see cref="IList"/>（按索引取项）；产品绑定的是
///     <c>ObservableCollection&lt;LogLineViewModel&gt;</c>；</item>
///   <item>集合里每个数据项是**不同实例**：行控件按身份复用，同一实例出现多次时只会实现一行。</item>
/// </list>
/// </summary>
public sealed class LogViewport : Panel
{
    public static readonly StyledProperty<IList?> ItemsSourceProperty =
        AvaloniaProperty.Register<LogViewport, IList?>(nameof(ItemsSource));

    public static readonly StyledProperty<IDataTemplate?> ItemTemplateProperty =
        AvaloniaProperty.Register<LogViewport, IDataTemplate?>(nameof(ItemTemplate));

    /// <summary>
    /// 视口两端之外各多保留的行数：小幅滚动与增删时不至于每动一点就换行。
    /// 取 1 是因为"上一行"已足够覆盖大步跳转的顶部边界（再多保留只会增加实现成本，实测会抬高稳态每行分配）。
    /// </summary>
    public static readonly StyledProperty<int> OverscanProperty =
        AvaloniaProperty.Register<LogViewport, int>(nameof(Overscan), 1);

    /// <summary>已实现的行（按索引升序）。</summary>
    private readonly List<Row> _visible = new();
    private readonly Dictionary<object, Row> _byItem = new(ReferenceEqualityComparer.Instance);
    private readonly Stack<Control> _pool = new();

    /// <summary>按数据项身份缓存的实测行高（上限就是日志容量，远小于"为每行构造控件"）。</summary>
    private readonly Dictionary<object, double> _heights = new(ReferenceEqualityComparer.Instance);

    /// <summary>尚未测量到任何行时使用的中性估计（一旦测到真实行高就被实测均值取代）。</summary>
    private const double DefaultRowHeight = 18;

    private double _rowHeight = DefaultRowHeight;
    private Size _viewport;
    private double _scrollOffset;
    private bool _attached;
    private bool _itemsSubscribed;
    private bool _resyncPending;
    private ScrollViewer? _scrollOwner;
    private INotifyCollectionChanged? _observed;
    private bool? _preserveReadingPosition;
    private object? _anchorItem;
    private double _anchorViewportY;
    private int _anchorGeneration;
    private int _queuedAnchorGeneration = -1;
    private Size _ownerExtent;
    private Size _ownerViewport;
    private bool _userScrollPending;

    /// <summary>同步补偿期间不应被页面误认为用户主动离开最新端。</summary>
    public bool IsRestoringReadingAnchor { get; private set; }

    static LogViewport()
    {
        AffectsMeasure<LogViewport>(OverscanProperty);
        ItemsSourceProperty.Changed.AddClassHandler<LogViewport>((viewport, _) => viewport.ObserveItems());
        ItemTemplateProperty.Changed.AddClassHandler<LogViewport>((viewport, _) => viewport.RebuildForTemplateChange());
    }

    public LogViewport()
    {
        AttachedToVisualTree += (_, _) =>
        {
            _attached = true;
            SubscribeItems();
            HookScrollOwner();
            // 离树期间集合内容可能被整体替换（那时收不到通知）：先按身份裁掉旧的高度缓存，
            // 再按现存缓存重算估计账本（否则旧长行的均值会继续污染新内容的估算），最后重同步实现集合。
            PruneHeightCache();
            RecalculateHeightStats();
            _resyncPending = true;
            InvalidateMeasure();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            CaptureReadingAnchor();
            _anchorGeneration++;
            _queuedAnchorGeneration = -1;
            _attached = false;
            UnsubscribeItems();
            UnhookScrollOwner();
            ReleaseAll();
        };
        // 主题/字体尺寸变化：屏外旧行高不再有效。
        ActualThemeVariantChanged += (_, _) => ResetHeightModel();
    }

    public IList? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public IDataTemplate? ItemTemplate
    {
        get => GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }

    public int Overscan
    {
        get => GetValue(OverscanProperty);
        set => SetValue(OverscanProperty, value);
    }

    /// <summary>
    /// true 保留所阅行及其视口位置，false 由调用方跟随最新端；null 为独立控件默认，
    /// 只在离开两端时锚定。页面显式传入暂停/跟随意图，因此暂停在顶部也能保留旧日志。
    /// </summary>
    public bool? PreserveReadingPosition
    {
        get => _preserveReadingPosition;
        set
        {
            if (_preserveReadingPosition == value) return;
            _preserveReadingPosition = value;
            if (value == false) CancelReadingAnchor();
        }
    }

    // ── 诊断（回归用；不影响行为） ───────────────────────────────────────────────────────

    /// <summary>当前已实现的行数。</summary>
    public int RealizedRowCount => _visible.Count;

    /// <summary>布局测量次数。</summary>
    public int MeasurePasses { get; private set; }

    /// <summary>补齐区间的次数。</summary>
    public int RealizePasses { get; private set; }

    /// <summary>已实现区间的最小/最大数据索引（-1 表示没有）。</summary>
    public int FirstRealizedIndex => _visible.Count > 0 ? _visible[0].Index : -1;

    public int LastRealizedIndex => _visible.Count > 0 ? _visible[^1].Index : -1;

    /// <summary>模板建树次数（应当只在池冷或模板更换时增长）。</summary>
    public int RowsBuilt { get; private set; }

    /// <summary>从池里取行的次数。</summary>
    public int RowsRented { get; private set; }

    /// <summary>回收行的次数。</summary>
    public int RowsReleased { get; private set; }

    /// <summary>真正调用 Measure 的行次数。</summary>
    public int RowsMeasured { get; private set; }

    /// <summary>诊断：当前高度缓存条目数（不应超过集合容量）。</summary>
    public int CachedHeightCount => _heights.Count;

    /// <summary>诊断：当前用于未测量行的估计行高（完全失效后应回到默认值，而不是沿用旧内容）。</summary>
    public double EstimatedRowHeight => _rowHeight;

    /// <summary>诊断：完全失效（清缓存 + 重置账本）被触发的次数。</summary>
    public int ResetHeightModelCalls { get; private set; }

    /// <summary>诊断：布局过程中观察到的最小/最大可用宽度（用于判断宽度是否在抖动）。</summary>
    public double MinLayoutWidth { get; private set; } = double.NaN;

    public double MaxLayoutWidth { get; private set; } = double.NaN;

    /// <summary>诊断：池里空闲的行控件数（长期运行不应无限增长）。</summary>
    public int PooledControlCount => _pool.Count;

    /// <summary>回收原因计数：区间外 / 缓存上限 / 集合淘汰 / 整体重建。</summary>
    public int ReleasedOutOfRange { get; private set; }

    public int ReleasedByCacheLimit { get; private set; }

    public int ReleasedByCollectionChange { get; private set; }

    public int ReleasedByReset { get; private set; }

    // ── 数据源与滚动宿主 ────────────────────────────────────────────────────────────────

    private void ObserveItems()
    {
        CancelReadingAnchor();
        UnsubscribeItems();
        _observed = ItemsSource as INotifyCollectionChanged;
        SubscribeItems();
        ReleaseAll();
        ResetHeightModel();
        _scrollOffset = 0;
        InvalidateMeasure();
    }

    /// <summary>只在挂在可视树上时订阅集合变更：离树期间不持有数据源通知。</summary>
    private void SubscribeItems()
    {
        if (!_attached || _itemsSubscribed || _observed is null) return;
        _itemsSubscribed = true;
        _observed.CollectionChanged += OnItemsChanged;
    }

    private void UnsubscribeItems()
    {
        if (_observed is null || !_itemsSubscribed) return;
        _itemsSubscribed = false;
        _observed.CollectionChanged -= OnItemsChanged;
    }

    /// <summary>订阅外层滚动：偏移变化时重算实现区间（滚动本身仍由框架完成）。</summary>
    private void HookScrollOwner()
    {
        var owner = this.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        if (ReferenceEquals(owner, _scrollOwner)) return;
        UnhookScrollOwner();
        _scrollOwner = owner;
        if (_scrollOwner is not null)
        {
            _ownerExtent = _scrollOwner.Extent;
            _ownerViewport = _scrollOwner.Viewport;
            _scrollOwner.ScrollChanged += OnScrollChanged;
            _scrollOwner.PropertyChanged += OnScrollOwnerPropertyChanged;
        }
    }

    private void UnhookScrollOwner()
    {
        if (_scrollOwner is null) return;
        _scrollOwner.ScrollChanged -= OnScrollChanged;
        _scrollOwner.PropertyChanged -= OnScrollOwnerPropertyChanged;
        _scrollOwner = null;
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs args)
    {
        if (args.OffsetDelta == default && args.ExtentDelta == default && args.ViewportDelta == default) return;
        InvalidateMeasure();
    }

    private void OnScrollOwnerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (_scrollOwner is null) return;
        if (args.Property == ScrollViewer.OffsetProperty && !IsRestoringReadingAnchor
            && _scrollOwner.Extent == _ownerExtent && _scrollOwner.Viewport == _ownerViewport)
        {
            CancelReadingAnchor(); // 用户在排队补偿之前滚动：新的输入优先，旧锚点失效。
            _userScrollPending = true;
        }
        if (args.Property == ScrollViewer.ExtentProperty) _ownerExtent = _scrollOwner.Extent;
        if (args.Property == ScrollViewer.ViewportProperty) _ownerViewport = _scrollOwner.Viewport;
    }

    /// <summary>当前滚动偏移（**宿主**坐标，单位像素）。滚动由外层宿主持有，这里只读取。</summary>
    private double CurrentScrollOffset()
    {
        if (_scrollOwner is not null) return _scrollOwner.Offset.Y;
        var presenter = this.GetVisualAncestors().OfType<ScrollContentPresenter>().FirstOrDefault();
        return presenter?.Offset.Y ?? _scrollOffset;
    }

    /// <summary>
    /// 可见内容区的顶部（**本控件坐标**）。宿主的 <c>Offset</c> 是宿主坐标，而内容相对宿主顶部有内边距
    /// （生产 <c>LogScroll</c> 设了 <c>Padding="16,18"</c>）；直接把它当内容坐标会让实现区间整体偏后，
    /// 大步跳转后顶部漏掉仍然部分可见的上一行、出现空带。这里统一做一次换算，
    /// 区间计算 / 行排布 / 末行预算都用同一套坐标。
    /// </summary>
    private double VisibleContentTop() => Math.Max(0, _scrollOffset - HostPaddingTop());

    private double HostPaddingTop() => _scrollOwner?.Padding.Top ?? 0;

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        // 通知在集合改变之后到达，但行控件仍保留上一帧的排布。按身份选一个尚存的可见行，
        // 保存它相对视口的位置；同一帧的多次增删共享这个锚点，不累计估计高度误差。
        CaptureReadingAnchor();
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Add:
            {
                // 在头部/中间插入（倒序日志的新行就是这样进来的）：后面已实现行的索引要一起后移，
                // 否则"按身份复用"的行索引会整体差一位，区间与缓存淘汰都会算错。
                var inserted = args.NewItems?.Count ?? 1;
                if (args.NewStartingIndex >= 0)
                {
                    foreach (var row in _visible)
                        if (row.Index >= args.NewStartingIndex) row.Index += inserted;
                }
                break;
            }
            case NotifyCollectionChangedAction.Remove:
            {
                var removedItems = args.OldItems?.Cast<object?>().Where(item => item is not null).ToList() ?? new List<object?>();
                var removed = Math.Max(1, removedItems.Count);
                var start = args.OldStartingIndex;
                if (start >= 0)
                {
                    // 头部淘汰会让每个已实现行的索引前移：按增量平移自己的索引，
                    // 而不是把"索引处的项不是原来那个"当成不一致去回收重建。
                    for (var i = _visible.Count - 1; i >= 0; i--)
                    {
                        var row = _visible[i];
                        if (row.Index >= start && row.Index < start + removed)
                        {
                            Release(row, i, "collection");
                            continue;
                        }
                        if (row.Index > start) row.Index -= removed;
                    }
                    // 离开缓存窗口的行不会再回来，其高度缓存一并清理（估计值随之按现存缓存重算）。
                    foreach (var item in removedItems) _heights.Remove(item!);
                    UpdateRowHeightEstimate();
                }
                break;
            }
            default:
                // Replace/Move/Reset（筛选、排序、清空）：重建已实现集合。
                // 筛选/排序后剩下的行高度仍然有效 → 按现存缓存重算账本；清空则回到默认估计。
                ReleaseAll();
                PruneHeightCache();
                RecalculateHeightStats();
                break;
        }
        // 大部分稳态增删的目标仍落在现有滚动范围内；先同步补偿，省去额外一轮布局。
        // 头插超出旧 Extent 或后续换行重测的修正，仍由布局后的恢复完成。
        if (_scrollOwner is not null && ReadingAnchorOffset() is { } target
            && target <= Math.Max(0, _scrollOwner.Extent.Height - _scrollOwner.Viewport.Height))
            ApplyReadingAnchorOffset(target);
        InvalidateMeasure();
    }

    private void RebuildForTemplateChange()
    {
        CancelReadingAnchor();
        ReleaseAll();
        _pool.Clear();
        ResetHeightModel();
        InvalidateMeasure();
    }

    // ── 布局 ───────────────────────────────────────────────────────────────────────────

    protected override Size MeasureOverride(Size availableSize)
    {
        MeasurePasses++;
        CaptureReadingAnchor();
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : Math.Max(0, Bounds.Width);
        MinLayoutWidth = double.IsNaN(MinLayoutWidth) ? width : Math.Min(MinLayoutWidth, width);
        MaxLayoutWidth = double.IsNaN(MaxLayoutWidth) ? width : Math.Max(MaxLayoutWidth, width);
        // 宽度变了：屏外旧行高按旧宽度测得，不再有效（保持"一套高度模型"）。
        if (_viewport.Width > 0 && Math.Abs(_viewport.Width - width) > 0.5) ResetHeightModel();
        // 虚拟化面板被测量时高度通常是无穷（内容可以任意长），视口高度要从滚动宿主读：
        // 框架自带的虚拟化面板也是这么拿视口的。
        var height = double.IsFinite(availableSize.Height) ? availableSize.Height : ScrollHostViewportHeight();
        _viewport = new Size(width, height);
        _scrollOffset = CurrentScrollOffset();
        Realize(width);
        // 返回**完整内容高度**：滚动宿主据此得到正确的 Extent，再接收阅读锚点的补偿请求。
        return new Size(width, ContentHeight());
    }

    /// <summary>外层滚动宿主的视口高度（没有宿主时为 0，此时一屏放不下任何行）。</summary>
    private double ScrollHostViewportHeight()
    {
        if (_scrollOwner is not null) return _scrollOwner.Viewport.Height;
        var presenter = this.GetVisualAncestors().OfType<ScrollContentPresenter>().FirstOrDefault();
        return presenter?.Viewport.Height ?? 0;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var count = ItemsSource?.Count ?? 0;
        var nextIndex = _visible.Count > 0 ? _visible[0].Index : 0;
        // 行按**内容坐标**排布（不减去滚动偏移）：内容的平移由 ScrollContentPresenter 完成。
        var top = HeightBefore(nextIndex, count);
        foreach (var row in _visible)
        {
            if (row.Index > nextIndex) top += HeightRange(nextIndex, row.Index);
            row.Top = top;
            row.Control.Arrange(new Rect(0, top, finalSize.Width, row.Height));
            row.HasArranged = true;
            top += row.Height;
            nextIndex = row.Index + 1;
        }
        _userScrollPending = false;
        RestoreReadingAnchorAfterLayout();
        return finalSize;
    }

    /// <summary>单行高度：按身份取实测缓存，没有则用均值估算。</summary>
    private double HeightAt(int index)
    {
        var items = ItemsSource;
        if (items is null || index < 0 || index >= items.Count) return _rowHeight;
        var item = items[index];
        return item is not null && _heights.TryGetValue(item, out var height) ? height : _rowHeight;
    }

    /// <summary>第 <paramref name="index"/> 行之上的高度（与排布、内容高度同源）。</summary>
    private double HeightBefore(int index, int count)
    {
        var total = 0d;
        for (var i = 0; i < index && i < count; i++) total += HeightAt(i);
        return total;
    }

    private double HeightRange(int from, int to)
    {
        var total = 0d;
        var count = ItemsSource?.Count ?? 0;
        for (var i = Math.Max(0, from); i < to && i < count; i++) total += HeightAt(i);
        return total;
    }

    /// <summary>内容总高度：按同一套高度模型逐行累计（测量时返回给滚动宿主作为 Extent）。</summary>
    private double ContentHeight()
    {
        var count = ItemsSource?.Count ?? 0;
        return count == 0 ? 0 : HeightBefore(count, count);
    }

    /// <summary>与 <see cref="HeightBefore"/> 互逆：内容坐标落在哪一行。</summary>
    private int IndexAtOffset(double offset)
    {
        var count = ItemsSource?.Count ?? 0;
        if (count == 0) return 0;
        var total = 0d;
        for (var i = 0; i < count; i++)
        {
            var height = HeightAt(i);
            if (total + height > offset) return i;
            total += height;
        }
        return count - 1;
    }

    private bool ShouldPreserveReadingPosition()
    {
        if (_preserveReadingPosition is { } preserve) return preserve;
        if (_scrollOwner is null) return false;
        var offset = _scrollOwner.Offset.Y;
        var maximum = Math.Max(0, _scrollOwner.Extent.Height - _scrollOwner.Viewport.Height);
        return offset > 1 && offset < maximum - Math.Max(1, _rowHeight);
    }

    private int FindItemIndex(object item)
    {
        var items = ItemsSource;
        if (items is not null)
            for (var index = 0; index < items.Count; index++)
                if (ReferenceEquals(items[index], item)) return index;
        return -1;
    }

    private void CancelReadingAnchor()
    {
        _anchorItem = null;
        _anchorGeneration++;
        _queuedAnchorGeneration = -1;
    }

    private void CaptureReadingAnchor()
    {
        if (!_attached || _scrollOwner is null || _userScrollPending || !ShouldPreserveReadingPosition()) return;
        if (_anchorItem is not null && FindItemIndex(_anchorItem) >= 0) return;
        _anchorItem = null;
        var offset = CurrentScrollOffset();
        foreach (var row in _visible)
        {
            // 未排布的新控件不能当作旧位置；被淘汰的锚点优先交给下一条仍可见的日志。
            // 用实际取整后的矩形判断相交，不能选到数学上尚余小数像素、实际已离开视口的上一行。
            var screenY = row.Control.TranslatePoint(default, _scrollOwner)?.Y ?? double.NaN;
            if (!row.HasArranged || !(screenY + row.Control.Bounds.Height > HostPaddingTop())
                || screenY >= _scrollOwner.Bounds.Height - _scrollOwner.Padding.Bottom || FindItemIndex(row.Item) < 0) continue;
            _anchorItem = row.Item;
            _anchorViewportY = row.Top + HostPaddingTop() - offset;
            break;
        }
    }

    private double? ReadingAnchorOffset()
    {
        if (_anchorItem is null) return null;
        var index = FindItemIndex(_anchorItem);
        if (index < 0) { _anchorItem = null; return null; }
        return Math.Max(0, HeightBefore(index, ItemsSource!.Count) + HostPaddingTop() - _anchorViewportY);
    }

    private void RestoreReadingAnchorAfterLayout()
    {
        if (!_attached || _scrollOwner is null || ReadingAnchorOffset() is not { } desired) return;
        if (Math.Abs(desired - _scrollOwner.Offset.Y) <= 0.01)
        {
            _anchorItem = null;
            return;
        }
        var generation = _anchorGeneration;
        if (_queuedAnchorGeneration == generation) return;
        _queuedAnchorGeneration = generation;
        // 新 Extent 要等外层完成布局才发布。此时再请求宿主滚动，避免头插被旧上限钳制；
        // 锚点一直保留到实际偏移收敛，换行重测修正估计高度也不会移动所阅行。
        Dispatcher.UIThread.Post(() =>
        {
            if (_queuedAnchorGeneration != generation || generation != _anchorGeneration) return;
            _queuedAnchorGeneration = -1;
            if (!_attached || _scrollOwner is null || ReadingAnchorOffset() is not { } target) return;
            var maximum = Math.Max(0, _scrollOwner.Extent.Height - _scrollOwner.Viewport.Height);
            target = Math.Min(target, maximum);
            if (Math.Abs(target - _scrollOwner.Offset.Y) <= 0.01) { _anchorItem = null; return; }
            ApplyReadingAnchorOffset(target);
            InvalidateMeasure();
        }, DispatcherPriority.Loaded);
    }

    private void ApplyReadingAnchorOffset(double target)
    {
        if (_scrollOwner is null || Math.Abs(target - _scrollOwner.Offset.Y) <= 0.01) return;
        IsRestoringReadingAnchor = true;
        try { _scrollOwner.Offset = new Vector(_scrollOwner.Offset.X, target); }
        finally { IsRestoringReadingAnchor = false; }
    }

    /// <summary>从首行往后累计到覆盖"视口 + Overscan"。</summary>
    private int LastVisibleIndex(int first)
    {
        var count = ItemsSource?.Count ?? 0;
        if (count == 0) return -1;
        // 偏移可能落在首行**内部**：把这部分也算进预算，否则窄宽/混合高度下会漏掉末行、视口底部留白。
        // 注意用可见内容顶部（已换算宿主 padding），与区间计算保持同一套坐标。
        var withinFirstRow = Math.Max(0, VisibleContentTop() - HeightBefore(first, count));
        var budget = _viewport.Height + withinFirstRow + Math.Max(0, Overscan) * _rowHeight;
        var accumulated = 0d;
        var index = first;
        while (index < count - 1 && accumulated < budget)
        {
            accumulated += HeightAt(index);
            index++;
        }
        return index;
    }

    private void Realize(double width)
    {
        RealizePasses++;
        var items = ItemsSource;
        var count = items?.Count ?? 0;
        if (count == 0)
        {
            ReleaseAll();
            PruneHeightCache();
            return;
        }

        // 重新挂接后校验已实现行：离树期间集合可能被清空/替换，索引处的项已经不是原来那个了。
        if (_resyncPending)
        {
            _resyncPending = false;
            for (var i = _visible.Count - 1; i >= 0; i--)
            {
                var candidate = _visible[i];
                if (candidate.Index < 0 || candidate.Index >= count
                    || !ReferenceEquals(items![candidate.Index], candidate.Item))
                {
                    Release(candidate, i, "collection");
                }
            }
        }

        // 测量会改变行高，区间可能因此不再覆盖真实视口：最多收敛两轮。
        var first = -1;
        var last = -2;
        for (var pass = 0; pass < 2; pass++)
        {
            // 按锚点的目标视口实现，不能先按旧 Offset 回收用户正在读的控件。
            if (ReadingAnchorOffset() is { } anchoredOffset) _scrollOffset = anchoredOffset;
            var nextFirst = IndexAtOffset(VisibleContentTop());
            var nextLast = LastVisibleIndex(nextFirst);
            if (nextFirst == first && nextLast == last) break;
            first = nextFirst;
            last = nextLast;
            ApplyRange(items!, count, first, last, width);
        }
        EnforceCacheLimit(first, last);
    }

    /// <summary>
    /// 保留带比取用范围多出的行数。稳态下可见范围每行只移动一行：若保留带紧贴范围边界，
    /// **同一条边界行**会被反复"释放再取用"（实测正序跟随因此变成 2 行/行，每行分配 126KB → 187KB）。
    /// 多留一点缓冲后，这种重复回收/重入不再发生；注意这不等于"追加不需要取用"——
    /// 真正的新尾行仍然需要一次取用与一次测量（正常增量）。
    /// </summary>
    private const int ReleaseSlack = 2;

    /// <summary>把实现区间调整到 [first, last]：区间外的行回收，缺失的行取用并测量。</summary>
    private void ApplyRange(IList items, int count, int first, int last, double width)
    {
        var keepFirst = Math.Max(0, first - Overscan - ReleaseSlack);
        var keepLast = Math.Min(count - 1, last + Overscan + ReleaseSlack);

        // 释放落在"当前可见区间 + Overscan"之外的行：不可见的行留着只会白占缓存名额。
        for (var i = _visible.Count - 1; i >= 0; i--)
        {
            var row = _visible[i];
            if (row.Index < keepFirst || row.Index > keepLast) Release(row, i, "range");
        }

        // 取用范围含**上一行**（大步跳转后 first 之前的边界行必须新建，否则顶部会空带）；
        // 下方缓冲仍按 overscan 预算处理，避免稳态多取整条缓冲带带来的额外实现成本。
        for (var index = Math.Max(0, first - Overscan); index <= last && index < count; index++)
        {
            var item = items[index];
            if (item is null) continue;
            if (_byItem.TryGetValue(item, out var existing))
            {
                if (existing.Index != index) existing.Index = index;
                continue;
            }
            _visible.Add(Rent(item, index));
            _visible.Sort((a, b) => a.Index.CompareTo(b.Index));
        }

        foreach (var row in _visible)
        {
            // 只测"自己报告失效"或宽度变了的行：换行文本重新排版很贵，逐行重复测量会把复用的收益吃光。
            if (!row.NeedsMeasure && row.Control.IsMeasureValid && Math.Abs(row.MeasuredWidth - width) <= 0.01) continue;
            row.Control.Measure(new Size(width, double.PositiveInfinity));
            row.MeasuredWidth = width;
            row.Height = Math.Max(1, row.Control.DesiredSize.Height);
            row.NeedsMeasure = false;
            RowsMeasured++;
            // 高度按身份缓存；账本随缓存一起维护，并在清空/换源/换模板/换宽度时与缓存同时重置。
            _heights[row.Item] = row.Height;
            _measuredTotal += row.Height;
            _measuredRowCount++;
        }
        UpdateRowHeightEstimate();
    }

    /// <summary>
    /// 把高度缓存裁剪到仍然存在的数据项。必须**按身份逐个判断**：
    /// 缓存条目数比新集合小并不能证明现存键属于当前集合（例如缓存里是旧集合的 30 行、
    /// 新集合有 400 行，30 ≤ 400 会把旧集合的键全部留下）。
    /// </summary>
    private void PruneHeightCache()
    {
        var items = ItemsSource;
        if (items is null || items.Count == 0)
        {
            if (_heights.Count > 0) _heights.Clear();
            return;
        }
        var alive = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var item in items)
            if (item is not null) alive.Add(item);
        foreach (var key in _heights.Keys.Where(key => !alive.Contains(key)).ToList()) _heights.Remove(key);
    }

    /// <summary>
    /// 完全失效：清掉高度缓存**与累计统计**，让新内容从默认估计重新起算。
    /// 只清 <c>_heights</c> 会留下旧的长行均值，使新短行的未测项被高估——清空长日志后换短日志时，
    /// 滚动范围可以差数倍（实测 400×2000px → 400×20px 时 65,056px 对正确 8,000px）。
    /// 已实现的行同时标记重测，避免排布用旧行高、区间用估算的"两套高度"。
    /// </summary>
    private void ResetHeightModel()
    {
        ResetHeightModelCalls++;
        _heights.Clear();
        _measuredTotal = 0;
        _measuredRowCount = 0;
        _rowHeight = DefaultRowHeight;
        foreach (var row in _visible)
        {
            row.NeedsMeasure = true;
            row.MeasuredWidth = double.NaN;
        }
        InvalidateMeasure();
    }

    /// <summary>
    /// 由累计账本得到估计行高。账本与缓存必须**同时**失效：清空 / 换源 / 换模板 / 换宽度都要一起重置，
    /// 否则旧内容的高度会污染新内容的估算（实测清空长日志后换短日志会出现 65,056px 对正确 8,000px）。
    /// </summary>
    private void UpdateRowHeightEstimate()
    {
        _rowHeight = _measuredRowCount > 0
            ? Math.Clamp(_measuredTotal / _measuredRowCount, 6, 240)
            : DefaultRowHeight;
    }

    /// <summary>
    /// 按现存缓存重算账本：筛选/排序后只剩一部分行时，这些行的实测高度仍然有效，
    /// 不能整批丢弃（否则会因为一次筛选而丢失已知高度）；但账本必须与新集合一致。
    /// </summary>
    private void RecalculateHeightStats()
    {
        _measuredTotal = 0;
        foreach (var height in _heights.Values) _measuredTotal += height;
        _measuredRowCount = _heights.Count;
        UpdateRowHeightEstimate();
    }

    private double _measuredTotal;
    private int _measuredRowCount;

    /// <summary>
    /// 缓存上限只是安全网：回收**离当前可见区间最远**的行，而不是索引最大的行——
    /// 向下滚动时顶部旧行仍占名额，按索引尾部回收会把刚进入视口的行立刻移走。
    /// </summary>
    private void EnforceCacheLimit(int first, int last)
    {
        var limit = Math.Max(8, last - first + 1 + (Overscan + ReleaseSlack) * 2 + 4);
        while (_visible.Count > limit)
        {
            var victimIndex = 0;
            var worstDistance = -1;
            for (var i = 0; i < _visible.Count; i++)
            {
                var index = _visible[i].Index;
                var distance = index < first ? first - index : index > last ? index - last : 0;
                if (distance > worstDistance)
                {
                    worstDistance = distance;
                    victimIndex = i;
                }
            }
            Release(_visible[victimIndex], victimIndex, "cache");
        }
    }

    // ── 行控件池：一次建树，之后只改 DataContext ────────────────────────────────────────

    private Row Rent(object item, int index)
    {
        var control = _pool.Count > 0 ? _pool.Pop() : BuildRow(item);
        RowsRented++;
        control.DataContext = item;
        var row = new Row(item, index, control) { NeedsMeasure = true };
        _byItem[item] = row;
        Children.Add(control);
        return row;
    }

    /// <summary>用真实数据项建树（某些模板工厂会解引用数据项，传 null 会炸）；绑定仍由 DataContext 驱动。</summary>
    private Control BuildRow(object item)
    {
        RowsBuilt++;
        var control = ItemTemplate?.Build(item) ?? new ContentControl();
        control.DataContext = item;
        return control;
    }

    private void Release(Row row, int visibleIndex, string reason = "other")
    {
        RowsReleased++;
        switch (reason)
        {
            case "range": ReleasedOutOfRange++; break;
            case "cache": ReleasedByCacheLimit++; break;
            case "collection": ReleasedByCollectionChange++; break;
            case "reset": ReleasedByReset++; break;
        }
        Children.Remove(row.Control);
        _byItem.Remove(row.Item);
        row.Control.DataContext = null;
        // 实测高度按身份保留（行可能只是离开视口，滚回来时不必重排文本）。
        _pool.Push(row.Control);
        _visible.RemoveAt(visibleIndex);
    }

    private void ReleaseAll()
    {
        for (var i = _visible.Count - 1; i >= 0; i--) Release(_visible[i], i, "reset");
        _visible.Clear();
        _byItem.Clear();
    }

    private sealed class Row
    {
        public Row(object item, int index, Control control)
        {
            Item = item;
            Index = index;
            Control = control;
        }

        public object Item { get; }
        public int Index { get; set; }
        public Control Control { get; }
        public double Height { get; set; }
        public double Top { get; set; }
        public bool HasArranged { get; set; }

        /// <summary>换绑数据后需要重新测量（文本换了，换行结果可能变）。</summary>
        public bool NeedsMeasure { get; set; }

        /// <summary>上次测量时的可用宽度：宽度变了必须重测。</summary>
        public double MeasuredWidth { get; set; } = double.NaN;
    }
}
