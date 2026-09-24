using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Alas.UI.DeploySettings;
using Alas.UI.Settings;

namespace Alas.UI.Headless;

/// <summary>
/// 系统设置页（上游 /settings）的离屏检查，按上游 useDeploySettings 的状态对齐：
/// ① 未接 Core → 停在加载态 + 显示真实原因，不渲染任何设置项；
/// ② 读取错误 → 错误框显示真实消息（上游 error）；
/// ③ 本地暂存错误 → 第二条错误框（上游 edits.storageError），与读取错误互不覆盖；
/// ④ 有数据 → 渲染「除远程访问/WebUI 之外的全部」分组，按**分组键**归属；
/// ⑤ 字段类型、数值校验、默认值回落与上游 FieldInput / prepareValue 一致。
/// </summary>
internal static class SettingsChecks
{
    internal static void Run()
    {
        Disconnected();
        ErrorState();
        StorageErrorState();
        DataState();
        GroupSplit();
        NumericValidation();
        QueueBehaviour();
        AsyncQueueAndFocus();
        BackendAndReadIsolation();
        MultilinePersistenceAndCancellation();
    }

    private static void AsyncQueueAndFocus()
    {
        var transport = new FakeTransport();
        var hold = transport.Hold();
        var backend = new FakeBackend(Snapshot(("host", "TEST")));
        var session = new DeploySettingsSession(transport, backend.ReadSchemaAsync, new FakeStore(), new FakeClock());
        var view = new SettingsView(backend, session);
        var window = Show(view);
        var notifications = 0;
        session.Edits.Changed += () =>
        {
            Check(Dispatcher.UIThread.CheckAccess(), "asynchronous queue notifications run on the UI thread");
            notifications++;
        };
        try
        {
            var input = Find<Control>(view, "DeployFieldRowhost").GetVisualDescendants().OfType<TextBox>().First();
            Check(input.Text == "TEST", "ordinary text keeps capital T characters");
            window.Width = 390;
            Pump();
            var origin = input.TranslatePoint(default, window) ?? throw new Exception("input is detached");
            Check(origin.X >= 0 && origin.X + input.Bounds.Width <= window.ClientSize.Width,
                "the whole settings input fits the narrow viewport");
            var point = origin + new Vector(input.Bounds.Width / 2, input.Bounds.Height / 2);
            window.MouseMove(point);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Check(input.IsFocused, "a real pointer click reaches the narrow settings input");
            input.CaretIndex = input.Text!.Length;
            window.KeyTextInput("a");
            Pump();
            Check(input.IsFocused && ReferenceEquals(input,
                Find<Control>(view, "DeployFieldRowhost").GetVisualDescendants().OfType<TextBox>().First()),
                "typing keeps the same focused input control while saving");
            Task.Run(() => hold.SetResult()).GetAwaiter().GetResult();
            WaitFor(() => session.Edits.Edit("host")?.Status == DeployEditStatus.Saved);
            Pump();
            Check(session.Edits.Edit("host")?.Status == DeployEditStatus.Saved,
                "a real background completion saves without cross-thread UI exceptions");
            window.KeyTextInput("b");
            Pump();
            Check(input.IsFocused && input.Text == "TESTab" && session.Edits.Edit("host")?.Value == "TESTab",
                "continuous keyboard input survives a background save response");
            Check(notifications >= 3, "queued, saving and asynchronous saved notifications were delivered");
        }
        finally { window.Close(); }
    }

    private static void BackendAndReadIsolation()
    {
        var first = new FakeTransport { Generation = 1 };
        var hold = first.Hold();
        var queue = new DeployEditQueue("actual-swap", first, new FakeStore());
        queue.Change("port", "8080", "8080");
        var second = new FakeTransport { Identity = "other", Generation = 2 };
        queue.UseTransport(second);
        queue.Change("port", "9090", "9090");
        Task.Run(() => hold.SetResult()).GetAwaiter().GetResult();
        queue.FlushAsync().GetAwaiter().GetResult();
        Check(first.Sent.Select(item => item.Payload).SequenceEqual(new[] { "8080" })
            && second.Sent.Select(item => item.Payload).SequenceEqual(new[] { "9090" }),
            "the same queue sends each input through its actual backend generation");
        Check(queue.Edit("port") is { Status: DeployEditStatus.Saved, BackendGeneration: 2, Value: "9090" },
            "an old backend response cannot overwrite the newer confirmed input");

        var slowRead = new TaskCompletionSource<DeploySchema?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new DeploySettingsSession(first, _ => slowRead.Task, new FakeStore());
        var oldRead = session.AttachAndReadAsync();
        var current = DeploySchemaAdapter.ToSchema(Snapshot(("host", "current")));
        session.Reader = _ => Task.FromResult(current);
        session.AttachAndReadAsync(second).GetAwaiter().GetResult();
        Task.Run(() => slowRead.SetResult(DeploySchemaAdapter.ToSchema(Snapshot(("host", "obsolete")))))
            .GetAwaiter().GetResult();
        WaitFor(() => oldRead.IsCompleted);
        oldRead.GetAwaiter().GetResult();
        Check(session.CurrentValue("host") == "current", "a slow previous backend read cannot replace current data");
        session.Edits.Change("host", "new backend value", "new backend value");
        session.FlushAsync().GetAwaiter().GetResult();
        Check(second.Sent.Any(item => item.Payload == "new backend value"),
            "session backend replacement also changes its shared edit queue transport");
    }

    private static void MultilinePersistenceAndCancellation()
    {
        var store = new FakeStore();
        var offline = new FakeTransport { Ready = false };
        var draft = new DeployEditQueue("multiline", offline, store);
        const string value = "Title: TEST\n  list:\tvalue\n  path: \\tmp\\notes";
        draft.Change("yaml", value, value);
        var restored = new DeployEditQueue("multiline", offline, store);
        Check(restored.Edit("yaml")?.Payload == value && restored.Edit("yaml")?.Value == value,
            "multiline, tabs and backslashes survive draft persistence exactly");
        offline.Ready = true;
        restored.FlushAsync().GetAwaiter().GetResult();
        Check(store.Read("deploy-edits.multiline") is null,
            "confirming a restored draft removes its persisted copy");

        var canceled = new FakeTransport { FailWith = new OperationCanceledException() };
        var retryClock = new FakeClock();
        var queue = new DeployEditQueue("canceled", canceled, new FakeStore(), retryClock);
        queue.Change("port", "8080", "8080");
        queue.FlushAsync().GetAwaiter().GetResult();
        Check(queue.Edit("port") is { Status: DeployEditStatus.Error, Retryable: true, Value: "8080" },
            "a canceled transport keeps a retryable draft instead of leaving Saving forever");
        canceled.FailWith = null;
        retryClock.Advance();
        queue.FlushAsync().GetAwaiter().GetResult();
        Check(queue.Edit("port")?.Status == DeployEditStatus.Saved, "a canceled save can recover on retry");
    }

    /// <summary>
    /// 草稿队列的行为（上游 config/EditQueue.ts）：
    /// 输入立即进草稿并按序号串行提交；旧响应只确认自己那一次输入；
    /// 永久错误保留原文且不自动重试；可重试失败保留输入并延迟重试；持久化失败单独报告。
    /// </summary>
    private static void QueueBehaviour()
    {
        // ① 串行提交：多次输入按序号依次发出，不会并发乱序。
        var transport = new FakeTransport();
        var queue = new DeployEditQueue("test-serial", transport, new FakeStore());
        queue.Change("a", "1");
        queue.Change("b", "2");
        queue.FlushAsync().GetAwaiter().GetResult();
        Check(string.Join(",", transport.Sent.Select(entry => entry.Key)) == "a,b",
            $"inputs are sent in sequence order (got {string.Join(",", transport.Sent.Select(e => e.Key))})");
        Check(transport.MaxInFlight == 1, $"submissions stay serial (max in flight {transport.MaxInFlight})");

        // ② 旧响应只确认自己那一次输入：提交在途时用户又改了，回执不能把新输入标成已保存。
        var slow = new FakeTransport();
        var backlog = new DeployEditQueue("test-stale", slow, new FakeStore());
        var pending = slow.Hold();
        backlog.Change("port", "8080");
        Check(backlog.Edit("port")?.Status == DeployEditStatus.Saving,
            $"the first input goes in flight (got {backlog.Edit("port")?.Status})");
        Check(slow.Sent.Count == 0, "the held submission has not been recorded yet");
        backlog.Change("port", "9090");
        // 在途回执**只能确认它自己那一次输入**：此刻队列里的输入已经是新的序号，
        // 所以 8080 的回执不得把 9090 标成已保存。
        Check(backlog.Edit("port")?.Payload == "9090" && backlog.HasPending("port"),
            $"the newer input supersedes the in-flight one (payload {backlog.Edit("port")?.Payload}, " +
            $"status {backlog.Edit("port")?.Status})");
        pending.SetResult();
        backlog.FlushAsync().GetAwaiter().GetResult();
        Check(backlog.Edit("port")?.Payload == "9090" && backlog.Edit("port")?.Status == DeployEditStatus.Saved,
            $"the newer input is what ends up confirmed (got {backlog.Edit("port")?.Status})");
        // 在途的那一笔不会因为被超越就取消（它已经在网络上），但**只有新输入会成为最终草稿**：
        // 旧回执只确认它自己那一次输入，因此不会把新输入标成已保存。
        Check(slow.Sent.Select(e => e.Payload).SequenceEqual(new[] { "8080", "9090" }),
            $"both submissions happen in order without cancelling the in-flight one " +
            $"(got {string.Join(",", slow.Sent.Select(e => e.Payload))})");

        // ③ 永久校验错误：保留原文，且不自动重试（重试也一定失败）。
        var permanent = new FakeTransport { FailWith = new DeployTransportException("该值不被接受", retryable: false) };
        var permanentQueue = new DeployEditQueue("test-permanent", permanent, new FakeStore());
        permanentQueue.Change("mode", "bogus", "bogus");
        permanentQueue.FlushAsync().GetAwaiter().GetResult();
        var failed = permanentQueue.Edit("mode");
        Check(failed?.Status == DeployEditStatus.Error && failed.Value == "bogus" && failed.Error == "该值不被接受",
            $"a permanent error keeps the original text (got {failed?.Status}/{failed?.Value}/{failed?.Error})");
        Check(!failed!.Retryable, "a permanent error is not marked retryable");
        permanent.FailWith = null;
        permanentQueue.Retry();
        permanentQueue.FlushAsync().GetAwaiter().GetResult();
        Check(permanentQueue.Edit("mode")?.Status == DeployEditStatus.Error,
            "the permanent error is not retried (it would fail again)");

        // ④ 可重试失败：保留输入，重连后重试并确认。
        var flaky = new FakeTransport { FailWith = new DeployTransportException("连接中断", retryable: true) };
        var flakyQueue = new DeployEditQueue("test-retry", flaky, new FakeStore(), new FakeClock());
        flakyQueue.Change("host", "10.0.0.9", "10.0.0.9");
        flakyQueue.FlushAsync().GetAwaiter().GetResult();
        var retryable = flakyQueue.Edit("host");
        Check(retryable?.Status == DeployEditStatus.Error && retryable.Retryable && retryable.Value == "10.0.0.9",
            $"a retryable failure keeps the input (got {retryable?.Status}/{retryable?.Retryable})");
        flaky.FailWith = null;
        Check(flakyQueue.ShouldRetryOnReconnect(), "a retryable failure is expected to retry after reconnect");
        flakyQueue.ResumeAfterReconnect(flaky.Generation);
        flakyQueue.FlushAsync().GetAwaiter().GetResult();
        Check(flakyQueue.Edit("host")?.Status == DeployEditStatus.Saved,
            $"a reconnect retries the pending input (got {flakyQueue.Edit("host")?.Status})");

        // ⑤ 另一后端的草稿不被当前后端提交，也不被它的回执清掉。
        var first = new FakeTransport { Generation = 1 };
        var shared = new DeployEditQueue("test-backends", first, new FakeStore());
        shared.Change("port", "8080");
        shared.FlushAsync().GetAwaiter().GetResult();
        Check(first.Sent.Count == 1, "the first backend submits its own draft");
        var second = new RecordingTransport(first) { Identity = "second", Generation = 2 };
        var swapped = new DeployEditQueue("test-backends-2", second, new FakeStore());
        swapped.Change("port", "9090");
        swapped.FlushAsync().GetAwaiter().GetResult();
        Check(second.Sent.Count == 1 && second.Sent[0].Payload == "9090",
            $"the second backend submits its own draft (got {string.Join(",", second.Sent.Select(e => e.Payload))})");
        Check(swapped.Edit("port")?.Status == DeployEditStatus.Saved,
            "the second backend's response confirms its own draft");

        // ⑥ 持久化失败：单独报告，内存草稿照旧可用、照旧提交。
        var noStore = new DeployEditQueue("test-nostore", new FakeTransport(), new ThrowingStore());
        noStore.Change("port", "8080");
        noStore.FlushAsync().GetAwaiter().GetResult();
        Check(noStore.StorageError?.Contains("草稿无法写入本地存储") == true,
            $"a persistence failure is reported separately (got {noStore.StorageError})");
        Check(noStore.Edit("port")?.Status == DeployEditStatus.Saved,
            "the draft is still submitted when persistence fails");

        // ⑦ 会话把队列的持久化错误单独暴露出来（上游 edits.storageError 那条通道），与读取错误互不覆盖。
        var sessionStore = new ThrowingStore();
        var sessionQueue = new DeployEditQueue("test-session-storage", new FakeTransport(), sessionStore);
        sessionQueue.Change("port", "8080");
        sessionQueue.FlushAsync().GetAwaiter().GetResult();
        var session = new DeploySettingsSession(new FakeTransport(), _ => Task.FromResult<DeploySchema?>(null), sessionStore);
        session.Edits.Change("port", "8080");
        Check(session.StorageError?.Contains("草稿无法写入本地存储") == true,
            $"the session surfaces the persistence failure on its own channel (got `{session.StorageError}`)");
        Check(!session.HasError, "the persistence failure does not touch the read-error channel");

        // ⑧ 恢复：未确认的输入被读回并重新排队；空的永久错误草稿被丢弃（否则字段会被永久钉死）。
        var store = new FakeStore();
        store.Write("deploy-edits.test-restore", "port\t8080\t8080\t3\tQueued\t0\t1\t");
        var restored = new DeployEditQueue("test-restore", new FakeTransport(), store);
        Check(restored.Edit("port")?.Value == "8080", "unconfirmed drafts are restored and requeued");
        store.Write("deploy-edits.test-empty-error", "mode\t\t\t4\tError\t0\t1\tbad");
        var restoredEmpty = new DeployEditQueue("test-empty-error", new FakeTransport(), store);
        Check(restoredEmpty.Edit("mode") is null,
            "an empty permanent-error draft is dropped so the field is not pinned");
    }

    /// <summary>
    /// 数值校验与默认值回落（上游 FieldInput + prepareValue）：
    /// 非法整数 / 非法数字 / 超出范围各报真实原因；**清空数值字段回落到原值并改写输入框**。
    /// </summary>
    private static void NumericValidation()
    {
        var backend = new FakeBackend(new SettingsSnapshot(new[]
        {
            new SettingsGroup("数值", new[] { new SettingsField("count", "数量", "5", true, "int", IsInteger: true) },
                "Numbers"),
        }, Values: new Dictionary<string, string?> { ["count"] = "5" }));
        var numericSession = new DeploySettingsSession(
            new SettingsTransport(backend, 1), backend.ReadSchemaAsync, new FakeStore());
        var view = new SettingsView(backend, numericSession);
        var window = Show(view);
        try
        {
            WaitIdle(view);
            string? Status() => view.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(block => block.Name == "DeployFieldStatuscount" && block.IsVisible)?.Text;

            Check(Status() is null, $"a clean field shows no status (got {Status()})");

            view.Session.Edits.Change("count", "abc", "abc", "请输入有效整数；输入内容已保留。");
            Pump();
            Check(Status()?.Contains("请输入有效整数") == true,
                $"a non-numeric value reports invalidInteger (got {Status()})");

            view.Session.Edits.Change("count", "99", "99", "请输入 1 到 10 之间的数值。");
            Pump();
            Check(Status()?.Contains("1 到 10") == true,
                $"an out-of-range value reports the allowed range (got {Status()})");

            // 清空数值字段：回落到原值（上游 prepareValue），输入框同步改写为默认值。
            var prepared = DeployValuePreparer.Prepare(string.Empty,
                new DeployFieldSpec("count", "数量", "int", Integer: true), "5");
            Check(prepared.Payload == "5" && prepared.DisplayText == "5",
                $"clearing a numeric field falls back to the configured value (got {prepared.Payload})");
            // preserve_empty 的字段不做回落：空值本身有意义。
            var preserved = DeployValuePreparer.Prepare(string.Empty,
                new DeployFieldSpec("note", "备注", "string", PreserveEmpty: true), "note");
            Check(preserved.Payload == string.Empty && preserved.DisplayText is null,
                "a preserve_empty field keeps the cleared value");
        }
        finally { window.Close(); }
    }

    /// <summary>分组归属：本页渲染除远程访问/WebUI 之外的全部（含后端新增的未知分组），按 key 判断。</summary>
    private static void GroupSplit()
    {
        var backend = new FakeBackend(new SettingsSnapshot(new[]
        {
            new SettingsGroup("远程访问", new[] { Field("tunnel", "隧道", "off") }, "RemoteAccess"),
            new SettingsGroup("网页界面", new[] { Field("port", "网页端口", "8080") }, "Webui"),
            new SettingsGroup("连接", new[] { Field("host", "主机", "127.0.0.1") }, "Connection"),
            // 后端新增的系统分组：没有专门页面认领，必须留在系统设置页，不能静默消失。
            new SettingsGroup("未知新分组", new[] { Field("shiny", "新项", "on") }, "BrandNewGroup"),
        }));
        var view = new SettingsView(backend);
        var window = Show(view);
        try
        {
            WaitIdle(view);
            var rendered = view.GetVisualDescendants().OfType<Control>()
                .Select(control => control.Name ?? string.Empty).ToHashSet();
            Check(rendered.Contains("DeployGroupConnection"), "an ordinary system group renders on this page");
            Check(rendered.Contains("DeployGroupBrandNewGroup"),
                "a backend-added system group renders too (no silent disappearance)");
            Check(!rendered.Contains("DeployGroupRemoteAccess") && !rendered.Contains("DeployGroupWebui"),
                "the remote-access groups are excluded here (they belong to the remote page)");
            Check(view.Model.Groups.Count == 2,
                $"the page owns the complement of the remote groups (got {view.Model.Groups.Count})");
        }
        finally { window.Close(); }
    }

    private static void Disconnected()
    {
        var view = new SettingsView();
        var window = Show(view);
        try
        {
            WaitIdle(view);
            var model = view.Model;
            Check(!model.HasGroups, "disconnected settings render no groups");
            Check(model.HasError, "disconnected settings surface the missing capability");
            Check(Find<TextBlock>(view, "SettingsErrorBox").Text?.Contains("不可用") == true,
                "disconnected settings explain why the page is unavailable");
            Check(!Find<TextBlock>(view, "SettingsEmptyState").IsVisible,
                "disconnected settings do not claim an empty-but-connected state");
            Check(!Find<TextBlock>(view, "SettingsLoading").IsVisible,
                "disconnected settings stop loading and show the real reason");
        }
        finally { window.Close(); }
    }

    private static void ErrorState()
    {
        var view = new SettingsView(new FakeBackend(new SettingsSnapshot(
            Array.Empty<SettingsGroup>(), "部署配置读取失败")));
        var window = Show(view);
        try
        {
            WaitIdle(view);
            Check(view.Model.Error == "部署配置读取失败",
                $"read error is surfaced verbatim (got {view.Model.Error})");
            Check(Find<TextBlock>(view, "SettingsErrorBox").IsVisible, "error box shows on read failure");
            Check(!Find<TextBlock>(view, "SettingsEmptyState").IsVisible, "no empty state while an error is shown");
            Check(!view.Model.IsLoading, "loading stops after a failure");
        }
        finally { window.Close(); }
    }

    private static void StorageErrorState()
    {
        // 上游把本地暂存错误放在第二条通道：它必须与读取状态同时可见、互不覆盖。
        var backend = new FakeBackend(Snapshot(("host", "127.0.0.1")));
        var session = new DeploySettingsSession(
            new SettingsTransport(backend, 1), backend.ReadSchemaAsync, new ThrowingStore());
        var view = new SettingsView(backend, session);
        var window = Show(view);
        try
        {
            WaitIdle(view);
            Check(!view.Model.HasStorageError, "no storage error before anything is typed");
            var box = Find<Control>(view, "DeployGroupConnection");
            var input = box.GetVisualDescendants().OfType<TextBox>().First();
            input.Text = "10.0.0.9";
            Check(Find<StackPanel>(view, "SettingsGroups").IsVisible,
                "groups stay visible regardless of the storage channel");
            Check(!Find<TextBlock>(view, "SettingsErrorBox").IsVisible,
                "the read-error channel is untouched by the persistence failure");
            WaitFor(() => backend.LastChanges.Any(change => change.Key == "host" && change.Value == "10.0.0.9"));
            Check(backend.LastChanges.Any(change => change.Key == "host" && change.Value == "10.0.0.9"),
                "the input is still submitted although persistence failed " +
                $"(sent {string.Join(",", backend.LastChanges.Select(change => change.Key + "=" + change.Value))})");
        }
        finally { window.Close(); }
    }

    private static void DataState()
    {
        var backend = new FakeBackend(Snapshot(
            ("host", "127.0.0.1"), ("port", "22267"), ("enabled", "true"), ("channels", "alpha")));
        var session = new DeploySettingsSession(
            new SettingsTransport(backend, 1), backend.ReadSchemaAsync, new FakeStore());
        var view = new SettingsView(backend, session);
        var window = Show(view);
        try
        {
            WaitIdle(view);
            var model = view.Model;
            Check(model.HasGroups, "settings render the returned groups");
            // 上游每个分组是 section.panel.config-group：分组必须套外壳的 panel 样式（外观取自皮肤）。
            Check(view.GetVisualDescendants().OfType<Border>().Any(border => border.Classes.Contains("panel")),
                "the settings group uses the shell panel style");
            var rows = view.GetVisualDescendants().OfType<Control>()
                .Where(control => control.Name?.StartsWith("DeployFieldRow") == true).ToList();
            Check(rows.Count == 6, $"every field renders a row (got {rows.Count})");
            var boxes = rows.SelectMany(row => row.GetVisualDescendants().OfType<TextBox>())
                .Where(box => box.FindAncestorOfType<ComboBox>() is null).ToList();
            // host / port / secret 三个单行输入；password 现在由 Avalonia 渲染成 PasswordBox（不是 TextBox）。
            Check(boxes.Count == 3, $"text/number/password fields render text inputs (got {boxes.Count})");
            Check(rows.SelectMany(row => row.GetVisualDescendants().OfType<CheckBox>())
                    .Count(box => box.FindAncestorOfType<ComboBox>() is null) >= 1,
                "boolean renders a switch/checkbox");
            Check(rows.SelectMany(row => row.GetVisualDescendants().OfType<ComboBox>()).Count() >= 1,
                "a field with options renders a dropdown");
            Check(rows.SelectMany(row => row.GetVisualDescendants().OfType<CheckBox>())
                    .Count(box => box.Content is "alpha" or "beta") == 2,
                "multiselect renders one box per option");
            // 标签与说明（上游 DeployGroups 在标签下渲染 field.help；没有说明的字段不占位）。
            var helps = view.GetVisualDescendants().OfType<TextBlock>()
                .Where(block => block.Name == "DeployFieldHelp").Select(block => block.Text).ToList();
            Check(helps.Count == 1 && helps[0] == "主机地址。",
                $"only fields with help text render a description (got {string.Join("|", helps)})");
            // 密码字段必须遮挡（上游 FieldInput 的 password 分支）。
            Check(view.GetVisualDescendants().OfType<TextBox>()
                    .Count(control => control.PasswordChar == '•') >= 1,
                "a password field masks its input");

            // 输入立即进草稿（上游输入即提交）：真实键盘输入到文本框，草稿与提交都要跟上。
            var hostRow = Find<Control>(view, "DeployFieldRowhost");
            var hostInput = hostRow.GetVisualDescendants().OfType<TextBox>().First();
            hostInput.Focus();
            Pump();
            hostInput.Text = "10.0.0.9";
            WaitFor(() => backend.LastChanges.Any(change => change.Key == "host" && change.Value == "10.0.0.9"));
            Check(backend.LastChanges.Any(change => change.Key == "host" && change.Value == "10.0.0.9"),
                $"typing submits immediately (got {string.Join(",", backend.LastChanges.Select(c => c.Key + "=" + c.Value))})");
            var status = Find<TextBlock>(view, "DeployFieldStatushost");
            Check(status.IsVisible && status.Text == "已保存",
                $"a confirmed input shows 已保存 (got {status.Text})");
            Check(!view.Model.HasError && !view.Model.HasStorageError, "no error boxes on a clean read");
            Check(!Find<TextBlock>(view, "SettingsLoading").IsVisible, "loading hides once data arrives");
        }
        finally { window.Close(); }

        // 已连接但没有分组、且没有错误 → 真实空态。
        var empty = new SettingsView(new FakeBackend(new SettingsSnapshot(Array.Empty<SettingsGroup>())));
        var emptyWindow = Show(empty);
        try
        {
            WaitIdle(empty);
            Check(empty.Model.IsEmpty && Find<TextBlock>(empty, "SettingsEmptyState").IsVisible,
                "connected-without-groups shows an honest empty state");
        }
        finally { emptyWindow.Close(); }
    }

    private static SettingsField Field(string key, string label, string value, string kind = "text") =>
        new(key, label, value, true, kind,
            kind == "select" ? new[] { "auto", "manual" } : kind == "multiselect" ? new[] { "alpha", "beta" } : null,
            Help: key == "host" ? "主机地址。" : string.Empty);

    /// <summary>一份测试用快照：覆盖上游 FieldInput 的主要分支。</summary>
    private static SettingsSnapshot Snapshot(params (string Key, string Value)[] values)
    {
        var map = values.ToDictionary(pair => pair.Key, pair => (string?)pair.Value);
        string Value(string key, string fallback) => map.TryGetValue(key, out var value) ? value ?? fallback : fallback;
        var fields = new List<SettingsField>
        {
            new("host", "主机", Value("host", string.Empty), true, Help: "主机地址。"),
            new("port", "端口", Value("port", "22267"), true, "int", IsInteger: true),
            new("enabled", "启用", Value("enabled", "false"), true, "bool"),
            new("mode", "模式", Value("mode", "auto"), true, "select", new[] { "auto", "manual" }),
            new("channels", "频道", Value("channels", string.Empty), true, "multiselect", new[] { "alpha", "beta" }),
            new("secret", "密码", string.Empty, true, "password"),
        };
        return new SettingsSnapshot(new[] { new SettingsGroup("连接", fields, "Connection") }, Values: map);
    }

    private static Window Show(SettingsView view)
    {
        var window = new Window { Width = 1280, Height = 900, Content = view };
        window.Show();
        Pump();
        return window;
    }

    /// <summary>泵到条件成立（提交在后台线程完成，断言前必须等它落定）。</summary>
    private static void WaitFor(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (condition()) return;
            Thread.Sleep(2);
            Pump();
        }
    }

    /// <summary>等页面的异步读取与草稿提交都落定（离屏下用真实任务推进，不用真实等待）。</summary>
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
            if (!view.Session.IsLoading && !view.Model.HasQueuedChanges) return;
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

    /// <summary>页面局部接口的假实现：只用于离屏检查，不访问服务、设备或真实部署配置。</summary>
    private sealed class FakeBackend(SettingsSnapshot? snapshot) : ISettingsBackend
    {
        /// <summary>最近提交的改动（用于检查输入是否真的送达后端）。</summary>
        public List<SettingsChange> LastChanges { get; } = new();

        public Task<SettingsSnapshot?> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot);

        public Task SaveAsync(SettingsChange change, CancellationToken cancellationToken = default)
        {
            LastChanges.Add(change);
            return Task.CompletedTask;
        }

        /// <summary>把后端快照翻译成共享会话模型（与页面用的是同一条翻译，避免检查与实现漂移）。</summary>
        public Task<DeploySchema?> ReadSchemaAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(DeploySchemaAdapter.ToSchema(snapshot));
    }

    /// <summary>可控制就绪状态与失败的假通道。</summary>
    private sealed class FakeTransport : IDeployTransport
    {
        private TaskCompletionSource? _hold;

        public string Identity { get; init; } = "fake";

        public long Generation { get; set; } = 1;

        public bool Ready { get; set; } = true;

        /// <summary>非空时提交抛出它（模拟失败）。</summary>
        public Exception? FailWith { get; set; }

        public List<(string Key, string Payload)> Sent { get; } = new();

        public int InFlight { get; private set; }

        public int MaxInFlight { get; private set; }

        public TaskCompletionSource Hold()
        {
            _hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _hold;
        }

        public async Task SendAsync(string key, string payload, CancellationToken cancellationToken = default)
        {
            InFlight++;
            MaxInFlight = Math.Max(MaxInFlight, InFlight);
            try
            {
                var hold = _hold;
                _hold = null;
                if (hold is not null) await hold.Task.ConfigureAwait(false);
                if (FailWith is { } failure) throw failure;
                Sent.Add((key, payload));
            }
            finally { InFlight--; }
        }
    }

    /// <summary>记录提交但按另一后端身份工作的通道（用于"另一后端的草稿"检查）。</summary>
    private sealed class RecordingTransport(IDeployTransport inner) : IDeployTransport
    {
        public List<(string Key, string Payload)> Sent { get; } = new();

        public string Identity { get; init; } = "recording";

        public long Generation { get; init; } = 2;

        public bool Ready => true;

        public Task SendAsync(string key, string payload, CancellationToken cancellationToken = default)
        {
            Sent.Add((key, payload));
            return inner.SendAsync(key, payload, cancellationToken);
        }
    }

    /// <summary>内存草稿存储。</summary>
    private sealed class FakeStore : IDeployDraftStore
    {
        private readonly Dictionary<string, string> _values = new();

        public string? Read(string key) => _values.TryGetValue(key, out var value) ? value : null;

        public void Write(string key, string content) => _values[key] = content;

        public void Remove(string key) => _values.Remove(key);
    }

    /// <summary>
    /// 读得到、写不进去的草稿存储（模拟浏览器禁用存储的写入路径）：
    /// 读取返回空内容，写入/删除抛错，用来检查"持久化失败单独报告、草稿照旧提交"。
    /// </summary>
    private sealed class ThrowingStore : IDeployDraftStore
    {
        public string? Read(string key) => null;

        public void Write(string key, string content) => throw new InvalidOperationException("存储被禁用");

        public void Remove(string key) => throw new InvalidOperationException("存储被禁用");
    }

    /// <summary>手动推进的延迟：重试不用真实等待。</summary>
    private sealed class FakeClock : IDeployClock
    {
        private readonly List<Action> _pending = new();

        public void Delay(TimeSpan delay, Action action) => _pending.Add(action);

        public void Advance()
        {
            var actions = _pending.ToList();
            _pending.Clear();
            foreach (var action in actions) action();
        }
    }
}
