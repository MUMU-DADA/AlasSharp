using System;
using Avalonia.Controls;
using Avalonia.Threading;
using Alas.UI.ViewModels;

namespace Alas.UI.Views;

/// <summary>
/// 运行总览页：资源卡与运行监控（上游 .overview-page / .resource-grid / .monitor-panel）。
/// 视图侧只负责上游的呈现行为：跟随开关决定日志是否自动滚到最新一条。
/// </summary>
public partial class OverviewView : UserControl
{
    private OverviewViewModel? _model;

    public OverviewView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as OverviewViewModel);
    }

    private void Attach(OverviewViewModel? model)
    {
        if (ReferenceEquals(_model, model)) return;
        if (_model is not null) _model.VisibleLogsChanged -= OnVisibleLogsChanged;
        _model = model;
        if (_model is not null) _model.VisibleLogsChanged += OnVisibleLogsChanged;
    }

    /// <summary>跟随开启时滚到底部；暂停后不再自动滚动，保持用户当前查看位置。</summary>
    private void OnVisibleLogsChanged(object? sender, EventArgs args)
    {
        if (_model is null || !_model.IsFollowing) return;
        LogScroll.ScrollToEnd();
        // 新行可能还没参与测量，下一轮再滚一次；回调里重新确认跟随状态，
        // 否则用户在两次派发之间点「暂停」仍会被拉到底部。
        Dispatcher.UIThread.Post(
            () =>
            {
                if (_model is { IsFollowing: true }) LogScroll.ScrollToEnd();
            },
            DispatcherPriority.Loaded);
    }
}
