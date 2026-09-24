using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Alas.UI.ViewModels;

namespace Alas.UI.Views;

/// <summary>
/// 运行总览页：资源卡与运行监控（上游 <c>.overview-page</c> / <c>.resource-grid</c> / <c>.monitor-panel</c>）。
///
/// 视图侧负责上游的呈现行为：
/// <list type="bullet">
///   <item>视口本来就贴在新日志那一端时，把它继续贴在那一端；用户翻走看历史后，新行不会把他拉回底部；</item>
///   <item>同一帧里到达多行只排一次滚动，而不是每行排队一次滚动；</item>
///   <item>用户显式重新打开跟随时回到最新一行。</item>
/// </list>
///
/// 异步回调的失效规则（避免旧回调拉动新视口）：
/// 换 DataContext、离树都递增<b>代次</b>；排队的回调携带发起时的代次与模型引用，派发时不匹配就丢弃；
/// 回调里重新确认"此刻仍在跟随"——用户"恢复跟随 → 派发前又暂停/翻走"时必须放弃，
/// 恢复令牌只豁免"视口必须贴边"这一条，不豁免暂停。离树只失效回调并退订，不销毁任何 VM，
/// 重新挂接后恢复订阅。
/// </summary>
public partial class OverviewView : UserControl
{
    /// <summary>贴边容差（像素）：距最新端这么近就算"用户在跟随"。</summary>
    private const double PinSlack = 2;

    private OverviewViewModel? _model;
    private bool _subscribed;
    private bool _attached;
    private int _generation;
    private int _queuedGeneration = -1;
    private bool _queuedResume;
    private bool _pinnedToNewest = true;
    private bool _scrollingProgrammatically;
    private Size _lastScrollExtent;
    private Size _lastScrollViewport;

    public OverviewView()
    {
        InitializeComponent();
        _lastScrollExtent = LogScroll.Extent;
        _lastScrollViewport = LogScroll.Viewport;
        LogScroll.PropertyChanged += OnScrollPropertyChanged;
        LogScroll.ScrollChanged += OnScrollChanged;
        DataContextChanged += (_, _) => Bind(DataContext as OverviewViewModel);
        AttachedToVisualTree += (_, _) =>
        {
            _generation++;
            _attached = true;
            Subscribe();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            // 离树：失效在途回调 + 退订，但不销毁 VM；重新挂接会重新订阅。
            _attached = false;
            _generation++;
            _queuedGeneration = -1;
            _queuedResume = false;
            Unsubscribe();
        };
    }

    /// <summary>换数据上下文：旧回调一律失效，且只有真的挂在可视树上才订阅。</summary>
    private void Bind(OverviewViewModel? model)
    {
        if (ReferenceEquals(_model, model)) return;
        _generation++;
        _queuedGeneration = -1;
        _queuedResume = false;
        Unsubscribe();
        _model = model;
        if (_attached) Subscribe();
        _pinnedToNewest = IsPinnedToNewest();
    }

    private void Subscribe()
    {
        if (!_attached || _subscribed || _model is null) return;
        _subscribed = true;
        _model.VisibleLogsChanged += OnVisibleLogsChanged;
        _model.PropertyChanged += OnModelPropertyChanged;
    }

    private void Unsubscribe()
    {
        if (!_subscribed || _model is null) return;
        _subscribed = false;
        _model.VisibleLogsChanged -= OnVisibleLogsChanged;
        _model.PropertyChanged -= OnModelPropertyChanged;
    }

    /// <summary>
    /// 换行重测会在贴边之后继续修正内容高度，缩放还会收缩可滚范围并钳制 Offset。
    /// 这些布局变化不是用户翻走：保持原来的贴边意图，并合并排队到新的最新端。
    /// Offset 的意图在同步属性通知里处理，避免异步 ScrollChanged 把用户上滚与布局修正合并。
    /// </summary>
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs args)
    {
        if (_scrollingProgrammatically) return;
        if (args.ExtentDelta != default || args.ViewportDelta != default)
        {
            // 清空/筛选到不足一屏时已无历史位置可离开；下一次填充仍从最新端跟随。
            if (LogScroll.Extent.Height <= LogScroll.Viewport.Height) _pinnedToNewest = true;
            if (_pinnedToNewest && _model is { IsFollowing: true } model) QueueFollow(model);
        }
    }

    private void OnScrollPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property == ScrollViewer.OffsetProperty && !_scrollingProgrammatically
            && LogScroll.Extent == _lastScrollExtent && LogScroll.Viewport == _lastScrollViewport)
        {
            // 范围收缩时 ScrollViewer 会先钳制 Offset，再发 Extent/Viewport 属性通知；
            // 此时实际尺寸与上一帧不同，不能把这种钳制误认为用户滚动。
            _pinnedToNewest = IsPinnedToNewest();
            if (!_pinnedToNewest) _queuedResume = false;
        }
        if (args.Property == ScrollViewer.ExtentProperty) _lastScrollExtent = LogScroll.Extent;
        if (args.Property == ScrollViewer.ViewportProperty) _lastScrollViewport = LogScroll.Viewport;
    }

    /// <summary>
    /// 新日志到达：变更前那一帧视口贴在新日志端**且跟随开着**时，把它继续贴在那一端；
    /// 用户翻走看历史、或暂停跟随时，都不动视口（上滚不跳底、暂停时看的是所阅日志）。
    /// </summary>
    private void OnVisibleLogsChanged(object? sender, EventArgs args)
    {
        // 日志视口是自绘的回收式面板：集合变化后要让滚动宿主重新测量内容，否则"已贴底、Offset 不变"时
        // 不会发生布局，新行不会进入已实现集合。只标脏宿主（Presenter），不要把整页标脏——
        // 整页重排会把资源卡等无关部分一起算进去。
        if (LogScroll.Presenter is { } presenter) presenter.InvalidateMeasure();
        else LogScroll.InvalidateMeasure();
        if (_model is null || !_model.IsFollowing || !_pinnedToNewest) return;
        QueueFollow(_model);
    }

    /// <summary>重新打开跟随是用户的显式动作：此时不要求视口已经贴边。</summary>
    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(OverviewViewModel.IsFollowing)) return;
        if (_model is not { IsFollowing: true }) return;
        QueueFollow(_model, resume: true);
    }

    /// <summary>
    /// 排一次"布局之后"的贴边滚动。排队状态**按代次归属**：
    /// 同一代次内只排一次，恢复请求在本次排队里合并；旧代次的回调既不能清掉新代次的排队标记，
    /// 也不能吞掉新的恢复请求。回调执行时再复核：代次/模型/是否仍在跟随/是否仍贴边。
    /// </summary>
    private void QueueFollow(OverviewViewModel model, bool resume = false)
    {
        var generation = _generation;
        if (_queuedGeneration == generation)
        {
            // 同一代次已有排队：把恢复请求并进去，不重复排。
            _queuedResume |= resume;
            return;
        }
        _queuedGeneration = generation;
        _queuedResume = resume;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (_queuedGeneration != generation) return;   // 旧代次（或已被新代次接管）：不碰新队列
                var isResume = _queuedResume;
                _queuedGeneration = -1;
                _queuedResume = false;
                if (generation != _generation || !ReferenceEquals(model, _model)) return;
                if (!model.IsFollowing) return;              // 派发前又暂停 → 放弃（恢复令牌也不例外）
                if (!isResume && !_pinnedToNewest) return;   // 期间用户翻走
                ScrollToNewest();
            },
            DispatcherPriority.Loaded);
    }

    /// <summary>视口是否贴在新日志那一端：正序看底部，倒序（最新在顶部）看顶部。</summary>
    private bool IsPinnedToNewest()
    {
        var extent = LogScroll.Extent.Height;
        var viewport = LogScroll.Viewport.Height;
        if (viewport <= 0 || extent <= viewport) return true;   // 还没测量或内容不足一屏：怎么都在最新端
        return _model?.IsDescending == true
            ? LogScroll.Offset.Y <= PinSlack
            : LogScroll.Offset.Y >= extent - viewport - PinSlack;
    }

    private void ScrollToNewest()
    {
        var extent = LogScroll.Extent.Height;
        var viewport = LogScroll.Viewport.Height;
        if (viewport <= 0 || extent <= viewport) return;    // 内容不足一屏：没有可滚的
        _scrollingProgrammatically = true;
        try
        {
            // 滚动归外层 ScrollViewer：日志视口只负责"把与视口相交的行实现出来"。
            if (_model?.IsDescending == true) LogScroll.Offset = new Vector(LogScroll.Offset.X, 0);
            else LogScroll.ScrollToEnd();
        }
        finally
        {
            _scrollingProgrammatically = false;
        }
        _pinnedToNewest = true;
    }
}
