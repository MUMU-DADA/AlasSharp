using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Alas.UI.Updater;

namespace Alas.UI.Headless;

/// <summary>
/// 更新器页（上游 /updater）的离屏检查：
/// ① 未接 Core → 显示「更新失败」+ 不可用原因，**不显示「已是最新」、不显示 HEAD、动作全部禁用**
///   （未取得版本信息时任何版本结论都是伪造的）；
/// ② 各状态文案与上游 i18n 一致；③ 只有 available 能「更新」、忙态能「取消」、HEAD/提交按需显示。
/// </summary>
internal static class UpdaterChecks
{
    internal static void Run()
    {
        Disconnected();
        States();
        Paging();
        DelayedRequestsAndBackendSwitch();
        FailedRequests();
    }

    /// <summary>
    /// 分页（上游 Updater.tsx:54，每页 50 条）：首页无上一页、末页无下一页、指示文案与行数正确，
    /// 翻页命令把新的 offset 交给后端；未取得版本信息时不显示分页行。
    /// </summary>
    private static void Paging()
    {
        // 120 条提交、每页 50：首页 1–50、第二页 51–100、末页 101–120。
        var backend = new PagedBackend(total: 120);
        var view = new UpdaterView(backend);
        var window = Show(view);
        try
        {
            var model = view.Model;
            var pager = Find<StackPanel>(view, "UpdaterPager");
            Check(pager.IsVisible, "pager shows once version information is available");
            Check(!Find<Button>(view, "UpdaterPreviousButton").IsEnabled, "first page has no previous page");
            Check(Find<Button>(view, "UpdaterNextButton").IsEnabled, "first page has a next page");
            Check(model.PageLabel == "1–50 / 120", $"first page label follows upstream (got {model.PageLabel})");
            Check(Find<ItemsControl>(view, "UpdaterCommitList").ItemCount == 50, "first page renders 50 rows");

            Find<Button>(view, "UpdaterNextButton").Command!.Execute(null);
            Pump();
            Check(backend.LastOffset == 50, $"next page asks the backend for offset 50 (got {backend.LastOffset})");
            Check(model.PageLabel == "51–100 / 120", $"second page label (got {model.PageLabel})");
            Check(Find<Button>(view, "UpdaterPreviousButton").IsEnabled, "second page has a previous page");

            model.LoadPageAsync(100).GetAwaiter().GetResult();
            Pump();
            Check(model.PageLabel == "101–120 / 120", $"last page label (got {model.PageLabel})");
            Check(!Find<Button>(view, "UpdaterNextButton").IsEnabled, "last page has no next page");
            Check(Find<ItemsControl>(view, "UpdaterCommitList").ItemCount == 20, "last page renders the remainder");
        }
        finally { window.Close(); }

        var disconnected = new UpdaterView();
        var disconnectedWindow = Show(disconnected);
        try
        {
            Check(!Find<StackPanel>(disconnected, "UpdaterPager").IsVisible, "no pager without version information");
        }
        finally { disconnectedWindow.Close(); }
    }

    /// <summary>按 offset 分页返回提交记录（只用于离屏检查）。</summary>
    private sealed class PagedBackend(int total) : IUpdaterBackend
    {
        public int LastOffset { get; private set; }

        private UpdaterStatus Page(int offset)
        {
            LastOffset = offset;
            var size = Math.Min(UpdaterStatus.PageSize, Math.Max(0, total - offset));
            var commits = Enumerable.Range(offset, size)
                .Select(index => new UpdaterCommit($"c{index}", $"提交 {index}"))
                .ToList();
            return new UpdaterStatus("idle", "abc1234", "def5678", 0, commits, null, total, offset);
        }

        public Task<UpdaterStatus> ReadStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(Page(0));

        public Task<UpdaterStatus> FetchAsync(CancellationToken cancellationToken = default) => Task.FromResult(Page(0));

        public Task<UpdaterStatus> ApplyAsync(CancellationToken cancellationToken = default) => Task.FromResult(Page(0));

        public Task<UpdaterStatus> CancelAsync(CancellationToken cancellationToken = default) => Task.FromResult(Page(0));

        public Task<UpdaterStatus> ReadCommitsAsync(int offset, CancellationToken cancellationToken = default) =>
            Task.FromResult(Page(offset));
    }

    private static void Disconnected()
    {
        var view = new UpdaterView();
        var window = Show(view);
        try
        {
            var model = view.Model;
            Check(model.StateLabel == "更新失败", $"disconnected updater does not claim a version (got {model.StateLabel})");
            Check(!model.HasVersion, "disconnected updater shows no version information");
            Check(!Find<StackPanel>(view, "UpdaterHeads").IsVisible, "no HEAD rows without version information");
            Check(!Find<Button>(view, "UpdaterFetchButton").IsEnabled
                && !Find<Button>(view, "UpdaterApplyButton").IsEnabled
                && !Find<Button>(view, "UpdaterCancelButton").IsEnabled, "all updater actions are disabled while unconnected");
            Check(Find<TextBlock>(view, "UpdaterNotice").Text?.Contains("不可用") == true, "disconnected updater explains why it is unavailable");
        }
        finally { window.Close(); }
    }

    private static void States()
    {
        // 已是最新：没有待更新提交，只有「获取更新」可用（上游同此）。
        CheckState("idle", "已是最新", ahead: 0, commits: 0, canFetch: true, canApply: false, canCancel: false);
        // 新版本可用：可更新，显示提交记录。
        // 上游 Updater.tsx：获取更新按钮 disabled={disabled}（仅未连接/忙时禁用）；更新按钮 disabled={!canApply}。
        CheckState("available", "新版本可用", ahead: 2, commits: 2, canFetch: true, canApply: true, canCancel: false);
        // 忙态：三个动作里只有「取消更新」可用。
        CheckState("wait", "等待任务结束", ahead: 2, commits: 2, canFetch: false, canApply: false, canCancel: true);
        // 完成后：回到可获取状态。
        CheckState("finish", "更新完成", ahead: 0, commits: 0, canFetch: true, canApply: false, canCancel: false);
        // 已有版本信息的后端失败：显示真实错误，允许重新获取，不能直接更新。
        CheckState("failed", "更新失败", ahead: 0, commits: 0, canFetch: true, canApply: false, canCancel: false, error: "拉取上游失败");
    }

    private static void CheckState(string state, string label, int ahead, int commits, bool canFetch, bool canApply, bool canCancel, string? error = null)
    {
        var payload = new UpdaterStatus(state, "abc1234", "def5678", ahead,
            Enumerable.Range(1, commits).Select(i => new UpdaterCommit($"c{i}", $"提交 {i}")).ToList(), error);
        var view = new UpdaterView(new FakeBackend(payload));
        var window = Show(view);
        try
        {
            var model = view.Model;
            Check(model.StateLabel == label, $"{state} shows {label} (got {model.StateLabel})");
            Check(model.HasVersion, $"{state} has version information");
            // 上游把 HEAD 与提交记录都渲染为面板（div.panel.head-card ×2 + section.panel.commit-panel）：
            // 三张卡片都必须套外壳的 panel 样式，外观取自皮肤而非写死。
            var panels = view.GetVisualDescendants().OfType<Border>()
                .Count(border => border.Classes.Contains("panel"));
            Check(panels == 3, $"{state} renders the three upstream panels (got {panels})");
            Check(model.Relation == (ahead > 0 ? $"本地领先 {ahead} 个提交" : "本地领先"),
                $"{state} relation text (got {model.Relation})");
            Check(Find<ItemsControl>(view, "UpdaterCommitList").ItemCount == commits,
                $"{state} renders {commits} commit rows");
            Check(Find<TextBlock>(view, "UpdaterNoCommits").IsVisible == (commits == 0), $"{state} empty-commit hint visibility");
            Check(Find<Button>(view, "UpdaterFetchButton").IsEnabled == canFetch, $"{state} fetch enabled == {canFetch}");
            Check(Find<Button>(view, "UpdaterApplyButton").IsEnabled == canApply, $"{state} apply enabled == {canApply}");
            Check(Find<Button>(view, "UpdaterCancelButton").IsEnabled == canCancel, $"{state} cancel enabled == {canCancel}");
            if (error is not null)
            {
                Check(model.Notice == error, $"{state} surfaces the real error (got {model.Notice})");
                Check(canFetch && !canApply && !canCancel, $"{state} allows a fetch retry but not applying failed data");
            }
        }
        finally { window.Close(); }
    }

    private static void DelayedRequestsAndBackendSwitch()
    {
        var firstRead = new TaskCompletionSource<UpdaterStatus>();
        var backend = new DeferredBackend(firstRead.Task);
        var view = new UpdaterView(backend);
        var window = Show(view);
        try
        {
            var active = new DeferredBackend(Task.FromResult(Available("current")));
            view.Backend = active;
            firstRead.SetResult(Available("stale-read"));
            Pump();
            Check(view.Model.LocalHead == "current", "a replaced backend's read cannot replace current version data");
            var fetch = Find<Button>(view, "UpdaterFetchButton");
            var apply = Find<Button>(view, "UpdaterApplyButton");
            var cancel = Find<Button>(view, "UpdaterCancelButton");
            Click(window, fetch);
            Check(active.FetchCalls == 1 && !fetch.IsEnabled && !apply.IsEnabled && cancel.IsEnabled,
                "a pointer fetch immediately disables repeated updates and enables cancellation");
            view.Model.FetchCommand.Execute(null);
            view.Model.ApplyCommand.Execute(null);
            Check(active.FetchCalls == 1 && active.ApplyCalls == 0,
                "command invocation cannot bypass the pending request guard");
            Click(window, cancel);
            view.Model.CancelCommand.Execute(null);
            Check(active.CancelCalls == 1 && !cancel.IsEnabled, "cancellation is sent only once");
            active.CancelResult.SetResult(Available("cancelled"));
            Pump();
            active.FetchResult.SetResult(Available("stale-fetch"));
            Pump();
            Check(view.Model.LocalHead == "cancelled" && fetch.IsEnabled,
                "a fetch completing after cancellation cannot overwrite its result");

            Click(window, apply);
            Check(active.ApplyCalls == 1, "the apply action reaches its backend once");
            var newRead = new TaskCompletionSource<UpdaterStatus>();
            view.Backend = new DeferredBackend(newRead.Task);
            Pump();
            Check(!view.Model.HasVersion && view.Model.Commits.Count == 0 && view.Model.PageLabel == "0",
                "backend replacement immediately clears previous versions, commits and cursor");
            active.ApplyResult.SetResult(Available("stale-apply"));
            Pump();
            Check(!view.Model.HasVersion && !fetch.IsEnabled,
                "an old action cannot populate or enable the new backend while it loads");
            newRead.SetResult(Available("new"));
            Pump();
            Check(view.Model.LocalHead == "new" && fetch.IsEnabled, "only the current refresh publishes its data");

            var paged = new DeferredBackend(Task.FromResult(Available("paged")));
            view.Backend = paged;
            Click(window, Find<Button>(view, "UpdaterNextButton"));
            view.Model.NextPageCommand.Execute(null);
            Check(paged.PageCalls == 1 && !fetch.IsEnabled, "only one delayed page request may run at a time");
            view.Backend = new FakeBackend(Available("after-page"));
            paged.PageResult.SetResult(Available("stale-page") with { CommitOffset = 50 });
            Pump();
            Check(view.Model.LocalHead == "after-page" && !view.Model.HasPreviousPage,
                "old page results cannot replace the new backend's content or cursor");
        }
        finally { window.Close(); }
    }

    private static void FailedRequests()
    {
        var backend = new DeferredBackend(Task.FromResult(Available("saved-version")));
        var view = new UpdaterView(backend);
        var window = Show(view);
        try
        {
            Click(window, Find<Button>(view, "UpdaterFetchButton"));
            backend.FetchResult.SetException(new InvalidOperationException("更新服务暂时不可用"));
            Pump();
            Check(view.Model.Notice == "更新服务暂时不可用" && view.Model.StateLabel == "更新失败",
                "a delayed error displays its actual failure and leaves the busy state");
            Check(view.Model.LocalHead == "saved-version", "a failed request retains last verified version information");
            Check(Find<Button>(view, "UpdaterFetchButton").IsEnabled && !Find<Button>(view, "UpdaterApplyButton").IsEnabled,
                "a known backend can retry fetching after failure without applying failed data");
            view.Backend = new FakeBackend(new UpdaterStatus("idle", "", "", 0, Array.Empty<UpdaterCommit>()));
            Pump();
            Check(view.Model.StateLabel == "尚未获取版本信息", "no HEAD data cannot prove a latest-version conclusion");
        }
        finally { window.Close(); }
    }

    private static UpdaterStatus Available(string head) => new("available", head, "upstream", 0,
        new[] { new UpdaterCommit("entry", "测试提交") }, CommitsTotal: 120);

    private static void Click(Window window, Button button)
    {
        Pump();
        var origin = button.TranslatePoint(new Point(0, 0), window) ?? throw new Exception("Unattached button");
        var point = origin + new Vector(button.Bounds.Width / 2, button.Bounds.Height / 2);
        window.MouseMove(point);
        window.MouseDown(point, Avalonia.Input.MouseButton.Left);
        window.MouseUp(point, Avalonia.Input.MouseButton.Left);
        Pump();
    }

    private sealed class DeferredBackend(Task<UpdaterStatus> read) : IUpdaterBackend
    {
        public TaskCompletionSource<UpdaterStatus> FetchResult { get; } = new();
        public TaskCompletionSource<UpdaterStatus> ApplyResult { get; } = new();
        public TaskCompletionSource<UpdaterStatus> CancelResult { get; } = new();
        public TaskCompletionSource<UpdaterStatus> PageResult { get; } = new();
        public int FetchCalls { get; private set; }
        public int ApplyCalls { get; private set; }
        public int CancelCalls { get; private set; }
        public int PageCalls { get; private set; }
        public Task<UpdaterStatus> ReadStatusAsync(CancellationToken cancellationToken = default) => read;
        public Task<UpdaterStatus> FetchAsync(CancellationToken cancellationToken = default) { ++FetchCalls; return FetchResult.Task; }
        public Task<UpdaterStatus> ApplyAsync(CancellationToken cancellationToken = default) { ++ApplyCalls; return ApplyResult.Task; }
        public Task<UpdaterStatus> CancelAsync(CancellationToken cancellationToken = default) { ++CancelCalls; return CancelResult.Task; }
        public Task<UpdaterStatus> ReadCommitsAsync(int offset, CancellationToken cancellationToken = default) { ++PageCalls; return PageResult.Task; }
    }

    private static Window Show(UpdaterView view)
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

    private static void Check(bool value, string label)
    {
        if (!value) throw new Exception("FAIL: " + label);
    }

    /// <summary>页面局部接口的假实现：只用于离屏检查，不访问服务、设备或真实仓库。</summary>
    private sealed class FakeBackend(UpdaterStatus status) : IUpdaterBackend
    {
        public Task<UpdaterStatus> ReadStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(status);

        public Task<UpdaterStatus> FetchAsync(CancellationToken cancellationToken = default) => Task.FromResult(status);

        public Task<UpdaterStatus> ApplyAsync(CancellationToken cancellationToken = default) => Task.FromResult(status);

        public Task<UpdaterStatus> CancelAsync(CancellationToken cancellationToken = default) => Task.FromResult(status);

        public Task<UpdaterStatus> ReadCommitsAsync(int offset, CancellationToken cancellationToken = default) => Task.FromResult(status);
    }
}
