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
using Alas.UI.DeploySettings;
using Alas.UI.RemoteAccess;
using Alas.UI.Settings;

namespace Alas.UI.Headless;

/// <summary>
/// 远程访问页（上游 /remote）的离屏检查：
/// ① 未接 Core → 「未启用」+ 不可用原因，且**不显示地址、不显示可点的复制**（不假装已连接）；
/// ② 四种状态（未启用/启动中/已连接/连接失败）文案与上游 i18n 一致，未知取值按失败处理；
/// ③ 只有 ready 才显示地址与复制；失败态显示真实错误文案；
/// ④ 本页渲染远程访问与 WebUI **两组**设置（不是只交地址卡），按分组键归属、与系统设置页互补；
/// ⑤ 复制走注入的异步剪贴板：成功才显示「已复制」，失败保留可选择地址并给出真实原因；
/// ⑥ 两页注入同一个会话：来回导航不丢未保存的输入。
/// </summary>
internal static class RemoteAccessChecks
{
    internal static void Run()
    {
        Disconnected();
        States();
        GroupOwnership();
        ClipboardDelegation();
        SharedDraftAcrossPages();
        ToggleFailuresAndSharedUnavailable();
    }

    private static void Disconnected()
    {
        var view = new RemoteAccessView();
        var window = Show(view);
        try
        {
            WaitIdle(view);
            var model = view.Model;
            Check(model.StateLabel == "未启用", $"disconnected remote access reports 未启用 (got {model.StateLabel})");
            Check(!model.HasAddress && !Find<TextBlock>(view, "RemoteAddressText").IsVisible,
                "disconnected remote access shows no address");
            Check(!Find<Button>(view, "RemoteCopyButton").IsEnabled, "copy stays disabled without an address");
            Check(Find<TextBlock>(view, "RemoteNotice").IsVisible && model.Notice.Contains("不可用"),
                "disconnected remote access explains why it is unavailable (got '" + model.Notice + "')");
            // 上游是 section.panel.config-group：卡片必须套外壳的 panel 样式（外观取自皮肤）。
            Check(view.GetVisualDescendants().OfType<Border>().Any(border => border.Classes.Contains("panel")),
                "the remote access card uses the shell panel style");
        }
        finally { window.Close(); }
    }

    private static void States()
    {
        CheckState("disabled", "未启用", address: string.Empty, expectAddress: false);
        CheckState("starting", "启动中", address: string.Empty, expectAddress: false);
        CheckState("direct_p2p", "已连接", address: "https://remote.example/abc", expectAddress: true);
        CheckState("waiting_peer", "已连接", address: "https://remote.example/abc", expectAddress: true);
        CheckState("turn_relay", "已连接", address: "https://remote.example/abc", expectAddress: true);
        CheckState("ssh_forward", "已连接", address: "https://remote.example/abc", expectAddress: true);
        CheckState("signaling", "启动中", address: string.Empty, expectAddress: false);
        CheckState("reconnecting", "启动中", address: string.Empty, expectAddress: false);
        CheckState("failed", "连接失败", address: string.Empty, expectAddress: false, error: "隧道启动失败");
        // 上游 remoteStatus()：未知取值一律按失败处理，避免界面显示成正常。
        CheckState("weird-unknown-state", "连接失败", address: string.Empty, expectAddress: false);
    }

    private static void CheckState(string state, string label, string address, bool expectAddress, string? error = null)
    {
        var backend = new FakeBackend(new RemoteAccessSchema(state, address, Error: error));
        var view = new RemoteAccessView(backend);
        var window = Show(view);
        try
        {
            WaitIdle(view);
            var model = view.Model;
            Check(model.IsEnabled == (state != "disabled"), "enabled state does not require a connected address");
            Check(model.StateLabel == label, $"{state} shows {label} (got {model.StateLabel})");
            Check(model.HasAddress == expectAddress,
                $"{state} address visibility {expectAddress} (got {model.HasAddress})");
            Check(Find<Button>(view, "RemoteCopyButton").IsEnabled == expectAddress,
                $"{state} copy enabled == {expectAddress}");
            if (error is not null)
            {
                Check(model.Error == error, $"{state} surfaces the real error (got {model.Error})");
            }
            if (expectAddress)
            {
                Check(Find<TextBlock>(view, "RemoteAddressText").Text == address, "ready state shows the real address");
            }
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// 分组归属：本页只渲染 RemoteAccess / Webui 两组（按 **key**，标题是中文也算），
    /// 系统分组不在这里出现——两页是互补关系，「只交地址卡」不合格。
    /// </summary>
    private static void GroupOwnership()
    {
        var backend = new FakeBackend(new RemoteAccessSchema("direct_p2p", "https://remote.example/abc", new[]
        {
            Group("RemoteAccess", "分囊", ("Tunnel", "隧道", "on")),
            Group("Webui", "网页界面", ("Port", "网页端口", "8080")),
            Group("Git", "Git", ("Repository", "仓库", "origin")),
        }));
        var view = new RemoteAccessView(backend);
        var window = Show(view);
        try
        {
            WaitIdle(view);
            var names = view.GetVisualDescendants().OfType<Control>()
                .Select(control => control.Name ?? string.Empty).ToHashSet();
            Check(names.Contains("DeployGroupRemoteAccess"), "the remote-access group renders on this page");
            Check(names.Contains("DeployGroupWebui"), "the WebUI group renders on this page too");
            Check(!names.Contains("DeployGroupGit"),
                "a system group does not render on the remote page (it belongs to system settings)");
            Check(view.Session.RemoteGroups.Count == 2 && view.Session.SystemGroups.Count == 1,
                $"the two pages split the groups by key (remote {view.Session.RemoteGroups.Count}, " +
                $"system {view.Session.SystemGroups.Count})");
            var portBox = Find<Control>(view, "DeployFieldRowPort")
                .GetVisualDescendants().OfType<TextBox>().First();
            Check(portBox.Text == "8080", $"the WebUI group shows its value (got {portBox.Text})");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// 复制必须调用注入的异步剪贴板：成功才显示「已复制」；失败或没注入能力时
    /// 保留地址并给出真实原因，**不声称已复制**。
    /// </summary>
    private static void ClipboardDelegation()
    {
        var calls = new List<string>();
        var backend = new FakeBackend(new RemoteAccessSchema("direct_p2p", "https://remote.example/abc"));
        var view = new RemoteAccessView(backend, null, text =>
        {
            calls.Add(text);
            return Task.FromResult(true);
        });
        var window = Show(view);
        try
        {
            WaitIdle(view);
            Check(view.Model.CopyLabel == "复制", "the copy button starts as 复制");
            // 用**视口内真实指针**点「复制」，而不是直接调方法：地址存在时这个按钮必须真的点得到。
            var copy = Find<Button>(view, "RemoteCopyButton");
            var origin = copy.TranslatePoint(new Point(0, 0), window)
                ?? throw new Exception("the copy button is detached");
            var centre = new Point(origin.X + copy.Bounds.Width / 2, origin.Y + copy.Bounds.Height / 2);
            Check(centre.X > 0 && centre.Y > 0 && centre.X < window.ClientSize.Width
                    && centre.Y < window.ClientSize.Height,
                $"the copy button is inside the viewport (at {centre}, viewport {window.ClientSize})");
            window.MouseMove(centre);
            window.MouseDown(centre, Avalonia.Input.MouseButton.Left);
            window.MouseUp(centre, Avalonia.Input.MouseButton.Left);
            WaitFor(() => calls.Count > 0);
            Pump();
            Check(calls.Count == 1 && calls[0] == "https://remote.example/abc",
                $"clicking copy calls the clipboard with the address (got {string.Join(",", calls)})");
            Check(view.Model.CopyLabel == "已复制" && Find<Button>(view, "RemoteCopyButton").Content as string == "已复制",
                "a successful copy shows 已复制");
            Check(!view.Model.HasCopyError, "a successful copy reports no error");
        }
        finally { window.Close(); }

        // 剪贴板失败：不显示已复制，地址保留可选，原因如实给出。
        var failing = new RemoteAccessView(
            new FakeBackend(new RemoteAccessSchema("direct_p2p", "https://remote.example/abc")), null,
            _ => Task.FromResult(false));
        var failingWindow = Show(failing);
        try
        {
            WaitIdle(failing);
            failing.Model.CopyAsync().GetAwaiter().GetResult();
            Pump();
            Check(failing.Model.CopyLabel == "复制", "a failed copy does not claim 已复制");
            Check(failing.Model.HasCopyError, "a failed copy explains why");
            Check(Find<TextBlock>(failing, "RemoteCopyError").IsVisible
                && Find<TextBlock>(failing, "RemoteCopyError").Text?.Contains("剪贴板") == true,
                "the clipboard failure is rendered in the page, not only stored in the model");
            Check(Find<TextBlock>(failing, "RemoteAddressText").IsVisible && failing.Model.HasAddress,
                "the address stays selectable after a failed copy");
        }
        finally { failingWindow.Close(); }

        // 没注入剪贴板：如实报告不可用，同样不声称已复制。
        var missing = new RemoteAccessView(new FakeBackend(
            new RemoteAccessSchema("direct_p2p", "https://remote.example/abc")));
        var missingWindow = Show(missing);
        try
        {
            WaitIdle(missing);
            missing.Model.CopyAsync().GetAwaiter().GetResult();
            Pump();
            Check(missing.Model.CopyLabel == "复制" && missing.Model.CopyError?.Contains("剪贴板") == true,
                $"a missing clipboard capability is reported honestly (got {missing.Model.CopyError})");
        }
        finally { missingWindow.Close(); }
    }

    /// <summary>
    /// 两页共享同一份草稿会话：在远程页输入、切到系统设置页再切回来，
    /// 输入仍在（同一草稿实例），且不会各自建一条保存队列。
    /// </summary>
    private static void SharedDraftAcrossPages()
    {
        var backend = new FakeBackend(new RemoteAccessSchema("direct_p2p", "https://remote.example/abc", new[]
        {
            Group("RemoteAccess", "远程访问", ("Tunnel", "隧道", "on")),
            Group("Webui", "网页界面", ("Port", "网页端口", "8080")),
        }));
        var session = new DeploySettingsSession(new NoopTransport(), backend.ReadSchemaAsync);
        var remote = new RemoteAccessView(backend, session, _ => Task.FromResult(true));
        var remoteWindow = Show(remote);
        try
        {
            WaitIdle(remote);
            Check(ReferenceEquals(remote.Session, session), "the remote page uses the injected session");
            var port = Find<Control>(remote, "DeployFieldRowPort").GetVisualDescendants().OfType<TextBox>().First();
            port.Text = "9090";
            // 输入**立即**进草稿（上游输入即提交）：这一步不等异步，草稿里就该有新值。
            Check(session.Edits.Edit("Port")?.Value == "9090",
                $"the remote page records its draft in the shared session (got {session.Edits.Edit("Port")?.Value})");
            WaitIdle(remote);
            // 提交成功后草稿被确认并清掉（上游 reconcile 语义）：字段回到服务端值，不再有本地覆盖。
            Check(session.Edits.Edit("Port") is null,
                $"a confirmed draft is reconciled away after saving (got {session.Edits.Edit("Port")?.Status})");
        }
        finally { remoteWindow.Close(); }

        // 切到系统设置页（同一个会话实例）：未确认的草稿必须还在，且由同一条队列提交。
        var settingsBackend = new SettingsFakeBackend(new SettingsSnapshot(new[]
        {
            Group("RemoteAccess", "远程访问", ("Tunnel", "隧道", "on")),
            Group("Webui", "网页界面", ("Port", "网页端口", "8080")),
            Group("Git", "Git", ("Repository", "仓库", "origin")),
        }));
        // 提交通道故意停在"未连接"：输入的草稿因此留在队列里不被确认，
        // 这正是"导航不丢输入"要覆盖的状态（已保存的草稿按上游 reconcile 语义本就会被清掉）。
        var settingsSession = new DeploySettingsSession(
            new OfflineTransport(), settingsBackend.ReadSchemaAsync);
        settingsSession.Reader = settingsBackend.ReadSchemaAsync;
        var remoteAgain = new RemoteAccessView(backend, settingsSession, _ => Task.FromResult(true));
        var againWindow = Show(remoteAgain);
        try
        {
            WaitIdle(remoteAgain);
            var port = Find<Control>(remoteAgain, "DeployFieldRowPort").GetVisualDescendants().OfType<TextBox>().First();
            port.Text = "9090";
            Check(settingsSession.Edits.Edit("Port")?.Value == "9090", "the draft is kept in the session");
            Check(settingsSession.Edits.Edit("Port")?.Status == DeployEditStatus.Queued,
                $"an unsubmitted draft stays queued (got {settingsSession.Edits.Edit("Port")?.Status})");

            // 系统设置页拿着**同一个会话实例**：它不渲染远程分组（分流互补），但会话里的草稿必须还是同一条。
            var settings = new SettingsView(settingsBackend, settingsSession);
            var settingsWindow = Show(settings);
            try
            {
                WaitIdle(settings);
                Check(ReferenceEquals(settings.Session, settingsSession),
                    "both pages share one session instance");
                // 系统设置页不渲染远程分组：分流是互补的。
                var names = settings.GetVisualDescendants().OfType<Control>()
                    .Select(control => control.Name ?? string.Empty).ToHashSet();
                Check(!names.Contains("DeployGroupWebui") && names.Contains("DeployGroupGit"),
                    "the system settings page renders the complement of the remote groups");
                // 在系统设置页输入：走的是同一条队列。
                var repository = Find<Control>(settings, "DeployFieldRowRepository")
                    .GetVisualDescendants().OfType<TextBox>().First();
                repository.Text = "https://example.invalid/repo.git";
                WaitIdle(settings);
                Check(settingsSession.Edits.Edit("Repository")?.Value == "https://example.invalid/repo.git",
                    "the system settings page records its draft in the same session");
                // 切回远程页：两页的未确认输入都在（不是各建一份队列）。
                Check(settingsSession.Edits.Edit("Port")?.Value == "9090",
                    "the remote page's draft survives navigation to the system settings page");
            }
            finally { settingsWindow.Close(); }

            var remoteBack = new RemoteAccessView(backend, settingsSession, _ => Task.FromResult(true));
            var backWindow = Show(remoteBack);
            try
            {
                WaitIdle(remoteBack);
                var portBox = Find<Control>(remoteBack, "DeployFieldRowPort")
                    .GetVisualDescendants().OfType<TextBox>().First();
                Check(portBox.Text == "9090",
                    $"navigating back to the remote page restores the draft (got {portBox.Text})");
            }
            finally { backWindow.Close(); }
        }
        finally { againWindow.Close(); }
    }

    private static void ToggleFailuresAndSharedUnavailable()
    {
        var backend = new ToggleBackend();
        var view = new RemoteAccessView(backend);
        var window = Show(view);
        try
        {
            WaitIdle(view);
            Check(view.Model.IsEnabled && view.Model.CanToggle,
                "an enabled provider that is still starting can be stopped");
            view.Model.ToggleAsync().GetAwaiter().GetResult();
            Pump();
            Check(backend.Requested == false, "starting provider toggle requests disable, not another enable");
            Check(view.Model.Error == "停用失败" && Find<TextBlock>(view, "RemoteError").Text == "停用失败",
                "a failed operation result is displayed even when subsequent reading succeeds");
        }
        finally { window.Close(); }

        var settings = new SettingsFakeBackend(new SettingsSnapshot(new[]
        {
            Group("Webui", "网页界面", ("Port", "网页端口", "8080")),
        }));
        var session = new DeploySettingsSession(new NoopTransport(), settings.ReadSchemaAsync);
        var shared = new RemoteAccessView(DisconnectedRemoteAccessBackend.Instance, session, null);
        var sharedWindow = Show(shared);
        try
        {
            WaitIdle(shared);
            Check(shared.Model.Notice.Contains("不可用") && !shared.Model.CanToggle && !shared.Model.HasAddress,
                "shared deployment data does not pretend that a remote service is available");
            Check(Find<Control>(shared, "DeployFieldRowPort") is not null,
                "shared deployment WebUI fields remain visible while remote status is unavailable");
        }
        finally { sharedWindow.Close(); }
    }

    private sealed class ToggleBackend : IRemoteAccessBackend
    {
        public bool? Requested { get; private set; }
        public Task<RemoteAccessSchema> ReadStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RemoteAccessSchema("starting", string.Empty));
        public Task<RemoteAccessStatus> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            Requested = enabled;
            return Task.FromResult(new RemoteAccessStatus("starting", string.Empty, "停用失败"));
        }
    }

    private static SettingsGroup Group(string key, string title, params (string Key, string Label, string Value)[] fields) =>
        new(title, fields.Select(field => new SettingsField(field.Key, field.Label, field.Value, true)).ToList(), key);

    private static Window Show(RemoteAccessView view)
    {
        var window = new Window { Width = 1280, Height = 900, Content = view };
        window.Show();
        Pump();
        return window;
    }

    private static Window Show(SettingsView view)
    {
        var window = new Window { Width = 1280, Height = 900, Content = view };
        window.Show();
        Pump();
        return window;
    }

    /// <summary>泵到条件成立（提交/复制的异步续体可能落在后台线程）。</summary>
    private static void WaitFor(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (condition()) return;
            Thread.Sleep(2);
            Pump();
        }
    }

    /// <summary>等异步读取与草稿提交落定。</summary>
    private static void WaitIdle(RemoteAccessView view)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            Pump();
            var flushing = view.Session.FlushAsync();
            if (!flushing.IsCompleted) flushing.GetAwaiter().GetResult();
            var refreshing = view.Session.RefreshAsync();
            if (!refreshing.IsCompleted) refreshing.GetAwaiter().GetResult();
            Pump();
            if (!view.Session.IsLoading) return;
        }
    }

    private static void WaitIdle(SettingsView view)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            Pump();
            var flushing = view.Session.FlushAsync();
            if (!flushing.IsCompleted) flushing.GetAwaiter().GetResult();
            var refreshing = view.Session.RefreshAsync();
            if (!refreshing.IsCompleted) refreshing.GetAwaiter().GetResult();
            Pump();
            if (!view.Session.IsLoading) return;
        }
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

    /// <summary>系统设置页用的假后端（只提供读取，提交由会话的通道承担）。</summary>
    private sealed class SettingsFakeBackend(SettingsSnapshot snapshot) : ISettingsBackend
    {
        public Task<SettingsSnapshot?> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SettingsSnapshot?>(snapshot);

        public Task SaveAsync(SettingsChange change, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<DeploySchema?> ReadSchemaAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(DeploySchemaAdapter.ToSchema(snapshot));

        /// <summary>系统设置页要看到的分组（与远程页共用时用同一份后端数据）。</summary>
        public SettingsSnapshot Shown => snapshot;
    }

    /// <summary>总是成功的提交通道：只记录本次会话里提交了什么，不访问任何真实后端。</summary>
    private sealed class RecordingTransport(long generation = 1) : IDeployTransport
    {
        public List<(string Key, string Payload)> Sent { get; } = new();

        public string Identity => "recording";

        public long Generation => generation;

        public bool Ready => true;

        public Task SendAsync(string key, string payload, CancellationToken cancellationToken = default)
        {
            Sent.Add((key, payload));
            return Task.CompletedTask;
        }
    }

    /// <summary>不落到任何后端的通道：共享会话的检查只关心草稿与页面接线。</summary>
    private sealed class NoopTransport : IDeployTransport
    {
        public string Identity => "noop";

        public long Generation => 1;

        public bool Ready => true;

        public Task SendAsync(string key, string payload, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>未连接的通道：草稿排队等待，不被提交也不被确认（检查"导航后草稿仍在"用）。</summary>
    private sealed class OfflineTransport : IDeployTransport
    {
        public string Identity => "offline";

        public long Generation => 3;

        public bool Ready => false;

        public Task SendAsync(string key, string payload, CancellationToken cancellationToken = default) =>
            throw new DeployTransportException("未连接", retryable: true);
    }

    /// <summary>
    /// 页面局部接口的假实现：只用于离屏检查，不访问服务、设备或真实隧道。
    /// 同时实现 <see cref="ISettingsBackend"/> 的读取，便于把同一个后端挂到两个页面上。
    /// </summary>
    private sealed class FakeBackend(RemoteAccessSchema status) : IRemoteAccessBackend, ISettingsBackend
    {
        public List<SettingsChange> Submitted { get; } = new();

        public Task<RemoteAccessSchema> ReadStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(status);

        public Task<RemoteAccessStatus> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RemoteAccessStatus(enabled ? "starting" : "disabled", status.Address));

        public Task<SettingsSnapshot?> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SettingsSnapshot?>(new SettingsSnapshot(
                (status.Groups ?? Array.Empty<SettingsGroup>()).ToList(), status.Error));

        public Task SaveAsync(SettingsChange change, CancellationToken cancellationToken = default)
        {
            Submitted.Add(change);
            return Task.CompletedTask;
        }

        /// <summary>读取并翻译成共享会话模型（与远程页走的是同一条读取路径）。</summary>
        public Task<DeploySchema?> ReadSchemaAsync(CancellationToken cancellationToken = default) =>
            RemoteSchema.ToSchema(status);

        /// <summary>被两个页面共用时的读取：远程状态 + 分组。</summary>
        internal static class RemoteSchema
        {
            public static Task<DeploySchema?> ToSchema(RemoteAccessSchema schema) =>
                RemoteAccessSchemaReader.Read(new Constant(schema), CancellationToken.None);
        }

        private sealed class Constant(RemoteAccessSchema schema) : IRemoteAccessBackend
        {
            public Task<RemoteAccessSchema> ReadStatusAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(schema);

            public Task<RemoteAccessStatus> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
                Task.FromResult(new RemoteAccessStatus(schema.State, schema.Address));
        }
    }
}
