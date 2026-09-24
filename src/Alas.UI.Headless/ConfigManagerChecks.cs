using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Alas.UI.ConfigManager;

namespace Alas.UI.Headless;

/// <summary>
/// 配置管理页（上游 pages/ConfigManager.tsx + App.tsx 的 CreateInstance）的离屏检查。
/// 覆盖实际点击新建、表单校验、导入源互斥、导入失败保留输入、
/// 取消删除无调用、确认删除带 revision、导出失败不报成功、概览传正确实例；
/// 另有未连接的真实错误态与实例行渲染。
/// 交互一律走**真实控件点击**（指针按下/松开命中按钮中心），不直接调模型方法。
/// </summary>
internal static class ConfigManagerChecks
{
    internal static void Run(string output)
    {
        Disconnected();
        ListsInstances();
        CreateFormValidation();
        ImportSourceExclusive();
        ImportFailureKeepsInput();
        DeleteNeedsConfirmation();
        ExportOnlyNotifiesAfterDelegate();
        OverviewPassesInstance();
        Acceptance();
        ModalInput();
        StaleOperations();
        ShellIntegration(output);
    }

    /// <summary>
    /// 覆盖一次点击一次调用、在途去重与按钮禁用、代次保护（旧列表/旧写操作不跨端）、
    /// 旧表单隔离、归一化名导航、名称正反例、默认来源清空导入、"导入成功但重读失败"保留输入、Esc 关闭。
    /// 交互一律用真实指针点击（长页在视口外的控件由 Click 先滚入）。
    /// </summary>
    private static void Acceptance()
    {
        // ① 一次点击只触发一次文件选择
        var one = new FakeBackend();
        var picks = 0;
        var view1 = new ConfigManagerPage(one) { Model = { Connected = true } };
        view1.Model.PickImportFileAsync = () =>
        {
            picks++;
            return Task.FromResult<(string, string)?>(null);   // 用户取消选择
        };
        var window1 = Show(view1);
        try
        {
            Click(window1, Find<Button>(view1, "ConfigCreateButton"));
            Click(window1, Find<Button>(view1, "ConfigFormPickFile"));
            Check(picks == 1, $"one click asks the platform picker exactly once (got {picks})");
            Check(view1.Model.Form is not null, "cancelling the picker keeps the create form open");

            // ⑩ Esc 关闭模态（焦点在遮罩上）
            Key(window1, Avalonia.Input.Key.Escape, PhysicalKey.Escape);
            Pump();
            Check(view1.Model.Form is null, "Escape closes the modal form");
        }
        finally { window1.Close(); }

        // ② 在途导出：按钮禁用、重复点击不再发读取
        var gate = new TaskCompletionSource();
        var two = new FakeBackend { ReadGate = gate };
        two.Instances.Add(new ConfigInstanceInfo("demo", "en", "1", "running"));
        var view2 = new ConfigManagerPage(two) { Model = { Connected = true } };
        var written = new List<string>();
        view2.Model.ExportFileAsync = async (name, content) => { await content(); written.Add(name); };
        var window2 = Show(view2);
        try
        {
            Click(window2, Find<Button>(view2, "ConfigExportButton"));
            Check(two.ReadCalls == 1, $"the export starts exactly one read (got {two.ReadCalls})");
            Check(!Find<Button>(view2, "ConfigExportButton").IsEnabled,
                "the export button is disabled while the operation is pending");
            Click(window2, Find<Button>(view2, "ConfigExportButton"));
            Check(two.ReadCalls == 1, $"a repeated click does not start a second read (got {two.ReadCalls})");
            gate.SetResult();
            WaitUntil(() => written.Count == 1 && view2.Model.Notice.Contains("已导出"));
            Check(written.Count == 1 && view2.Model.Notice.Contains("已导出"),
                "the export completes once and only then reports success");
        }
        finally { window2.Close(); }

        // ③ 旧后端的列表晚到，不得覆盖新后端
        var slow = new FakeBackend { ListGate = new TaskCompletionSource() };
        slow.Instances.Add(new ConfigInstanceInfo("old-backend", "en", "1", "running"));
        var fast = new FakeBackend();
        fast.Instances.Add(new ConfigInstanceInfo("new-backend", "en", "2", "running"));
        var view3 = new ConfigManagerPage(slow) { Model = { Connected = true } };
        var window3 = Show(view3);
        try
        {
            Click(window3, Find<Button>(view3, "ConfigCreateButton"));
            view3.Model.Backend = fast;          // 切换：旧列表还在路上
            Pump();
            Check(!Find<Border>(view3, "ConfigFormOverlay").IsVisible, "switching the backend hides the old form");
            slow.ListGate!.SetResult();
            Pump();
            Check(view3.Model.Instances.Count == 1 && view3.Model.Instances[0].Name == "new-backend",
                $"a late list from the old backend is ignored (got {string.Join(",", view3.Model.Instances.Select(i => i.Name))})");
        }
        finally { window3.Close(); }

        // ④ 删除在途时切换后端：不得把 revision 发往旧后端，也不得写到新后端
        var slowDelete = new FakeBackend { ReadGate = new TaskCompletionSource() };
        slowDelete.Instances.Add(new ConfigInstanceInfo("victim", "en", "1", "running"));
        var other = new FakeBackend();
        var view4 = new ConfigManagerPage(slowDelete) { Model = { Connected = true } };
        var window4 = Show(view4);
        try
        {
            Click(window4, Find<Button>(view4, "ConfigDeleteButton"));   // 进入确认
            Click(window4, Find<Button>(view4, "ConfigDeleteButton"));   // 确认 → 卡在 ReadConfig
            Check(slowDelete.Deleted.Count == 0, "no delete is sent while the config is still being read");
            view4.Model.Backend = other;
            slowDelete.ReadGate!.SetResult();
            Pump();
            Check(slowDelete.Deleted.Count == 0, "a backend switch stops the pending delete from writing to the old backend");
            Check(other.Deleted.Count == 0, "the new backend does not receive the stale delete either");
        }
        finally { window4.Close(); }

        // ⑤ 归一化名导航 + ⑨ 名称规则正反例
        var back = new FakeBackend();
        var opened = new List<string>();
        var view5 = new ConfigManagerPage(back) { Model = { Connected = true } };
        view5.Model.OpenOverview = opened.Add;
        var window5 = Show(view5);
        try
        {
            Click(window5, Find<Button>(view5, "ConfigCreateButton"));
            var form = view5.Model.Form!;
            Check(!ConfigCreateFormViewModel.IsValidName("bad:name"), "a colon is rejected by the upstream name rule");
            Check(!ConfigCreateFormViewModel.IsValidName(".leading"), "a leading dot is rejected");
            Check(ConfigCreateFormViewModel.IsValidName("实例.名称-1 空格"),
                "CJK, dot, hyphen and space are accepted");
            form.Name = "demo ";                  // 后端会归一化首尾空白
            Check(form.CanSubmit, "the normalizable name can be submitted");
            Click(window5, Find<Button>(view5, "ConfigFormSubmit"));
            Pump();
            Check(opened.Count == 1 && opened[0] == "demo",
                $"navigation uses the backend-normalized name (got {string.Join(",", opened)})");

            // ⑥ 选择「使用默认配置」必须清掉导入选择（两个来源互斥）
            form.ChooseImport("imported");
            Check(form.ImportFile == "imported", "choosing an import records it");
            form.ChooseImport(string.Empty);
            Check(form.ImportFile.Length == 0, "choosing the default source clears the import");
        }
        finally { window5.Close(); }

        // ⑦ 导入成功但重读可导入源失败：保留原输入、显示真实错误
        var failing = new FakeBackend { ImportListError = new InvalidOperationException("可导入源读取失败") };
        var view6 = new ConfigManagerPage(failing) { Model = { Connected = true } };
        view6.Model.PickImportFileAsync = () => Task.FromResult<(string, string)?>(("broken.json", "{"));
        var window6 = Show(view6);
        try
        {
            Click(window6, Find<Button>(view6, "ConfigImportButton"));
            var form = view6.Model.Form!;
            form.Name = "kept";
            form.Source = "existing";
            Click(window6, Find<Button>(view6, "ConfigFormPickFile"));
            Pump();
            Check(form.HasError && form.Error.Contains("可导入源读取失败"),
                $"a failed re-read surfaces the real error (got {form.Error})");
            Check(form.Name == "kept" && form.Source == "existing", "a failed re-read keeps the user input");
            Check(form.ImportFile.Length == 0, "a failed re-read does not select the imported source");
        }
        finally { window6.Close(); }
    }

    // ① 未接入 Core：真实错误态、无实例行、新建/导入不可用（不伪造列表）
    private static void Disconnected()
    {
        var view = new ConfigManagerPage();
        var window = Show(view);
        try
        {
            Check(view.Model.HasError, "disconnected config manager surfaces a real error");
            Check(view.Model.Instances.Count == 0, "no instances are invented while disconnected");
            Check(!Find<Button>(view, "ConfigCreateButton").IsEnabled && !Find<Button>(view, "ConfigImportButton").IsEnabled,
                "create and import stay disabled while disconnected");
            Check(!Find<ItemsControl>(view, "ConfigList").IsVisible || Find<ItemsControl>(view, "ConfigList").ItemCount == 0,
                "the instance list renders nothing while disconnected");
        }
        finally { window.Close(); }
    }

    // ② 实例行：名称 / 服务器·序列号 / 状态；server=disabled 时不显示服务器名
    private static void ListsInstances()
    {
        var backend = new FakeBackend
        {
            Instances =
            {
                new ConfigInstanceInfo("demo-main", "en", "emulator-5554", "running"),
                new ConfigInstanceInfo("solo", "disabled", "127.0.0.1:5555", "waiting"),
            },
        };
        var view = new ConfigManagerPage(backend) { Model = { Connected = true } };
        var window = Show(view);
        try
        {
            Check(view.Model.Instances.Count == 2, $"instances are listed (got {view.Model.Instances.Count})");
            var texts = VisibleTexts(view);
            Check(texts.Any(text => text == "demo-main") && texts.Any(text => text == "solo"),
                "each row shows its instance name");
            Check(texts.Any(text => text.Contains("en") && text.Contains("emulator-5554")),
                "a row shows server and serial together");
            Check(texts.Any(text => text == "127.0.0.1:5555"),
                "a disabled server is not shown (only the serial)");
            // 状态徽标按上游做本地化展示（StatusBadge + status.* 文案），并保留状态类供样式复用。
            Check(texts.Any(text => text == "运行中") && texts.Any(text => text == "待命"),
                "each row shows its localized status label");
            var stateClasses = view.GetVisualDescendants().OfType<Border>()
                .Where(border => border.Classes.Contains("task-state")).ToList();
            Check(stateClasses.Count == 2 && stateClasses.Any(border => border.Classes.Contains("running")),
                "the status badges keep their state classes");
            Check(!view.Model.IsEmpty, "the empty state is off when instances exist");
            Check(Find<Button>(view, "ConfigCreateButton").IsEnabled, "create is enabled once connected");
        }
        finally { window.Close(); }
    }

    // ③ 点击“新建配置”打开表单；名称校验：必填、≤64、不含路径分隔符
    private static void CreateFormValidation()
    {
        var view = new ConfigManagerPage(new FakeBackend()) { Model = { Connected = true } };
        var window = Show(view);
        try
        {
            Click(window, Find<Button>(view, "ConfigCreateButton"));
            Check(view.Model.Form is not null, "clicking create opens the form");
            Check(Find<Border>(view, "ConfigForm").IsVisible, "the form is visible");

            var submit = Find<Button>(view, "ConfigFormSubmit");
            var nameBox = Find<TextBox>(view, "ConfigFormName");
            Check(!submit.IsEnabled, "an empty name cannot be submitted");

            nameBox.Text = "ok-name";
            Pump();
            Check(submit.IsEnabled, "a valid name enables submit");

            nameBox.Text = new string('长', 65);
            Pump();
            Check(!submit.IsEnabled, "a name longer than 64 characters is rejected");

            nameBox.Text = "bad/name";
            Pump();
            Check(!submit.IsEnabled, "a name containing a path separator is rejected");
        }
        finally { window.Close(); }
    }

    // ④ 导入源与初始配置互斥；选择导入源会自动填实例名
    private static void ImportSourceExclusive()
    {
        var backend = new FakeBackend
        {
            Instances = { new ConfigInstanceInfo("existing", "en", "1", "waiting") },
            Imports = { new ConfigImportSource("imported", DateTimeOffset.UnixEpoch) },
        };
        var view = new ConfigManagerPage(backend) { Model = { Connected = true } };
        var window = Show(view);
        try
        {
            Click(window, Find<Button>(view, "ConfigImportButton"));
            Pump();
            var form = view.Model.Form!;
            Check(form.ImportFirst, "entering from import lists the importable sources right away");
            Check(form.HasImports, "the importable source is listed");

            form.ChooseImport("imported");
            Pump();
            Check(form.ImportFile == "imported", "choosing an import records it");
            Check(form.Name == "imported", "choosing an import fills the instance name");
            Check(form.Source.Length == 0, "choosing an import clears the initial config (mutually exclusive)");

            form.Source = "existing";
            Pump();
            Check(form.Source == "existing" && form.ImportFile.Length == 0,
                "choosing an initial config clears the import (mutually exclusive)");
        }
        finally { window.Close(); }
    }

    // ⑤ 导入失败：显示真实错误且**保留已填输入**
    private static void ImportFailureKeepsInput()
    {
        var backend = new FakeBackend { ImportError = new InvalidOperationException("配置无法读取") };
        var view = new ConfigManagerPage(backend) { Model = { Connected = true } };
        view.Model.PickImportFileAsync = () => Task.FromResult<(string, string)?>(("broken.json", "{"));
        var window = Show(view);
        try
        {
            Click(window, Find<Button>(view, "ConfigImportButton"));
            Pump();
            var form = view.Model.Form!;
            form.Name = "kept-name";
            Click(window, Find<Button>(view, "ConfigFormPickFile"));
            Pump();
            Check(form.HasError && form.Error.Contains("配置无法读取"),
                $"an import failure surfaces the real error (got {form.Error})");
            Check(form.Name == "kept-name", "a failed import keeps what the user already typed");
            Check(!form.HasImports, "a failed import does not invent importable sources");
        }
        finally { window.Close(); }
    }

    // ⑥⑦ 删除：第一次点击只二次确认（零调用），第二次才带 revision 删除
    private static void DeleteNeedsConfirmation()
    {
        var backend = new FakeBackend
        {
            Instances = { new ConfigInstanceInfo("victim", "en", "1", "waiting") },
        };
        backend.Revisions["victim"] = "rev-42";
        var view = new ConfigManagerPage(backend) { Model = { Connected = true } };
        var window = Show(view);
        try
        {
            Click(window, Find<Button>(view, "ConfigDeleteButton"));
            Pump();
            Check(backend.Deleted.Count == 0, "the first click only asks for confirmation (no call)");
            Check(view.Model.IsConfirming("victim"), "the row switches to the confirm state");
            Check(Find<Button>(view, "ConfigDeleteButton").Content?.ToString() == "确认删除",
                "the button label switches to 确认删除");

            Click(window, Find<Button>(view, "ConfigDeleteButton"));
            Pump();
            Check(backend.Deleted.Count == 1, "confirming deletes exactly once");
            Check(backend.Deleted[0] == ("victim", "rev-42"),
                $"the delete carries the revision read from the config (got {backend.Deleted[0]})");
        }
        finally { window.Close(); }
    }

    // ⑧ 导出：只有平台委托完成才提示“已导出”；委托缺失或失败都不报成功
    private static void ExportOnlyNotifiesAfterDelegate()
    {
        var backend = new FakeBackend
        {
            Instances = { new ConfigInstanceInfo("demo", "en", "1", "waiting") },
        };
        backend.Values["demo"] = "{\"Alas\":{}}";
        var view = new ConfigManagerPage(backend) { Model = { Connected = true } };
        var window = Show(view);
        try
        {
            // (a) 没有平台委托：必须报错，不能提示已导出
            Click(window, Find<Button>(view, "ConfigExportButton"));
            Pump();
            Check(view.Model.HasError && !view.Model.HasNotice,
                "export without a platform delegate reports an error, not success");

            // (b) 委托失败：同样不报成功
            view.Model.ExportFileAsync = (_, _) => Task.FromException(new InvalidOperationException("磁盘只读"));
            Click(window, Find<Button>(view, "ConfigExportButton"));
            Pump();
            Check(view.Model.HasError && !view.Model.HasNotice, "a failing export never claims success");

            // (c) 委托成功：提示已导出，且文件名与内容来自 config.values
            var written = new List<(string Name, string Text)>();
            view.Model.ExportFileAsync = async (name, content) => { written.Add((name, await content())); };
            Click(window, Find<Button>(view, "ConfigExportButton"));
            Pump();
            Check(written.Count == 1 && written[0].Name == "demo.json",
                $"the export writes <instance>.json (got {written.FirstOrDefault().Name})");
            Check(written.Count == 1 && written[0].Text == "{\"Alas\":{}}",
                "the exported text is the config values");
            Check(view.Model.Notice.Contains("已导出") && !view.Model.HasError,
                "success is announced only after the platform delegate completed");
        }
        finally { window.Close(); }
    }

    // ⑨ 概览：点击某一行的概览，必须把**该行**的实例名交给外壳
    private static void OverviewPassesInstance()
    {
        var backend = new FakeBackend
        {
            Instances =
            {
                new ConfigInstanceInfo("first", "en", "1", "waiting"),
                new ConfigInstanceInfo("second", "en", "2", "waiting"),
            },
        };
        var view = new ConfigManagerPage(backend) { Model = { Connected = true } };
        var opened = new List<string>();
        view.Model.OpenOverview = opened.Add;
        var window = Show(view);
        try
        {
            var buttons = view.GetVisualDescendants().OfType<Button>()
                .Where(button => button.Name == "ConfigOverviewButton").ToList();
            Check(buttons.Count == 2, $"each row has an overview action (got {buttons.Count})");
            Click(window, buttons[1]);
            Pump();
            Check(opened.Count == 1 && opened[0] == "second",
                $"overview receives the clicked instance (got {string.Join(",", opened)})");
        }
        finally { window.Close(); }
    }

    private static Window Show(Control view)
    {
        var window = new Window { Width = 1280, Height = 820, Content = view };
        window.Show();
        Pump();
        return window;
    }

    /// <summary>真实指针点击：移动到按钮中心 → 按下 → 松开（这样才会走 Button 的命令路径）。</summary>
    private static void Click(Window window, Button button)
    {
        button.BringIntoView();
        Pump();
        var origin = button.TranslatePoint(new Point(0, 0), window) ?? default;
        var center = new Point(origin.X + button.Bounds.Width / 2, origin.Y + button.Bounds.Height / 2);
        Check(new Rect(window.ClientSize).Contains(center), $"{button.Name} is reachable in the viewport");
        window.MouseMove(center);
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Pump();
    }

    private static void Key(Window window, Key key, PhysicalKey physical, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, physical, null);
        window.KeyRelease(key, modifiers, physical, null);
        Pump();
    }

    private static void ModalInput()
    {
        var page = new ConfigManagerPage(new FakeBackend()) { Model = { Connected = true } };
        var window = Show(page);
        try
        {
            var opener = Find<Button>(page, "ConfigCreateButton");
            var before = opener.TranslatePoint(default, window);
            Click(window, opener);
            var overlay = Find<Border>(window, "ConfigFormOverlay");
            var form = Find<Border>(window, "ConfigForm");
            var input = Find<TextBox>(window, "ConfigFormName");
            var center = form.TranslatePoint(new Point(form.Bounds.Width / 2, form.Bounds.Height / 2), window)!.Value;
            Check(Math.Abs(center.Y - window.ClientSize.Height / 2) < 2 && Math.Abs(center.X - window.ClientSize.Width / 2) < 2,
                "create modal is centered in the viewport");
            Check(before == opener.TranslatePoint(default, window), "opening does not reflow the content");
            window.KeyTextInput("配置"); Pump(); window.KeyTextInput("A"); Pump();
            Check(input.IsFocused && input.Text == "配置A" && page.Model.Form?.Name == "配置A",
                "typing preserves focus and updates the current form");
            var current = page.Model.Form;
            Click(window, opener);
            Check(ReferenceEquals(current, page.Model.Form), "modal shields the underlying create button");
            input.Focus();
            foreach (var modifiers in new[] { RawInputModifiers.None, RawInputModifiers.Shift })
                for (int i = 0; i < 12; i++)
                {
                    Key(window, Avalonia.Input.Key.Tab, PhysicalKey.Tab, modifiers);
                    Check(window.FocusManager?.GetFocusedElement() is Visual focused &&
                        (focused == overlay || focused.GetVisualAncestors().Contains(overlay)), "Tab stays within the modal");
                }
            Key(window, Avalonia.Input.Key.Escape, PhysicalKey.Escape);
            Check(page.Model.Form is null && !overlay.IsVisible && opener.IsFocused,
                "Escape closes and returns focus to the opener");
        }
        finally { window.Close(); }
    }

    private static void StaleOperations()
    {
        var backend = new FakeBackend();
        var page = new ConfigManagerPage(backend) { Model = { Connected = true } };
        var window = Show(page);
        try
        {
            var firstList = new TaskCompletionSource<IReadOnlyList<ConfigInstanceInfo>>();
            var secondList = new TaskCompletionSource<IReadOnlyList<ConfigInstanceInfo>>();
            backend.ListOperation = () => firstList.Task;
            var firstRefresh = page.Model.RefreshAsync();
            backend.ListOperation = () => secondList.Task;
            var secondRefresh = page.Model.RefreshAsync();
            secondList.SetResult([new("new-list", "", "", "stopped")]);
            Check(WaitUntil(() => secondRefresh.IsCompleted), "latest list completes");
            firstList.SetResult([new("old-list", "", "", "stopped")]);
            Check(WaitUntil(() => firstRefresh.IsCompleted) && page.Model.Instances.Single().Name == "new-list",
                "same-backend delayed list cannot overwrite the latest response");
            backend.ListOperation = null;

            var oldRead = new TaskCompletionSource();
            backend.ReadGate = oldRead;
            page.Model.ExportFileAsync = async (_, read) => { await read(); };
            var oldExport = page.Model.ExportAsync("same-name");
            var newRead = new TaskCompletionSource();
            var replacement = new FakeBackend { ReadGate = newRead };
            page.Backend = replacement;
            var newExport = page.Model.ExportAsync("same-name");
            oldRead.SetResult();
            Check(WaitUntil(() => oldExport.IsCompleted) && page.Model.IsBusy("same-name"),
                "old operation cleanup does not clear the new backend's busy row");
            newRead.SetResult();
            Check(WaitUntil(() => newExport.IsCompleted) && !page.Model.IsBusy("same-name"), "new operation cleans its own busy row");

            var pick = new TaskCompletionSource<(string, string)?>();
            page.Model.PickImportFileAsync = () => pick.Task;
            page.Model.OpenCreateForm(false);
            var oldForm = page.Model.Form!;
            var oldPick = oldForm.PickLocalFileAsync();
            page.Model.CloseForm(); page.Model.OpenCreateForm(false);
            var newForm = page.Model.Form!; newForm.Name = "keep-me";
            pick.SetResult(("obsolete.json", "{}"));
            Check(WaitUntil(() => oldPick.IsCompleted) && replacement.Imported.Count == 0 &&
                ReferenceEquals(newForm, page.Model.Form) && newForm.Name == "keep-me",
                "closing a pending picker prevents upload and leaves a new form untouched");

            replacement.CreateGate = new TaskCompletionSource<string>();
            int navigated = 0; page.Model.OpenOverview = _ => navigated++;
            var creation = newForm.SubmitAsync();
            page.Model.CloseForm(); page.Model.OpenCreateForm(false);
            var retained = page.Model.Form!;
            replacement.CreateGate.SetResult("created");
            Check(WaitUntil(() => creation.IsCompleted) && ReferenceEquals(retained, page.Model.Form) && navigated == 0,
                "a completed old creation neither closes nor navigates away from the new form");
        }
        finally { window.Close(); }
    }

    private static void ShellIntegration(string output)
    {
        var backend = new CoreUiBackendChecks.FixtureBackend
        {
            Listed = [new() { Instance = "fixture", Revision = "rev-fixture", Server = "en", Status = "running" }],
        };
        var files = new MemoryUiFiles();
        var view = new Alas.UI.Views.MainView(new Alas.UI.Theming.MemoryThemeStore(), backend, files);
        var window = new Window { Width = 1280, Height = 820, Content = view };
        window.Show(); Pump();
        try
        {
            Click(window, view.GetVisualDescendants().OfType<Button>().Single(button => button.CommandParameter as string == "configs"));
            var page = Find<ConfigManagerPage>(window, "ConfigManagerPage");
            Check(page.IsEffectivelyVisible && !Find<ScrollViewer>(window, "MainScroll").IsVisible,
                "config route owns the visible viewport and suppresses the other pages");
            using (var frame = window.CaptureRenderedFrame()) frame?.Save(Path.Combine(output, "config-manager-1280.png"), PngBitmapEncoderOptions.Default);
            Click(window, Find<Button>(page, "ConfigExportButton"));
            Check(files.Exports.Single().Name == "fixture.json" &&
                System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(files.Exports[0].Bytes), backend.ConfigValues),
                "shell export reaches Core adapter and writes only config values through the injected file capability");
            Click(window, Find<Button>(page, "ConfigCreateButton"));
            var overlay = Find<Border>(window, "ConfigFormOverlay");
            Check(overlay.Parent == Find<Grid>(window, "Root") && overlay.Bounds.Size == window.ClientSize,
                "integrated create modal covers the whole shell including the sidebar");
            Click(window, Find<Button>(window, "ConfigFormPickFile"));
            Check(backend.Imported is { Name: "upload", Content: "{\"Alas\":{}}" } && page.Model.Form?.ImportFile == "upload",
                "file picker import goes through the backend then selects the reread source");
            Click(window, Find<Button>(window, "ConfigFormSubmit"));
            Check(backend.Created is { Instance: "upload", Source: null, ImportFile: "upload" } &&
                view.Model.InstanceName == "upload" && view.Model.IsOverviewActive && !overlay.IsVisible,
                "created instance navigates to its overview and closes the shell modal");

            var export = new Alas.UI.Statistics.StatisticsExport("chart.png", "image/png", [137, 80, 78, 71]);
            view.Model.Statistics.ExportAsync(export).GetAwaiter().GetResult();
            Check(files.Exports.Last().Name == "chart.png" && files.Exports.Last().Bytes.SequenceEqual(export.Content),
                "statistics shares the binary-capable platform export");
            view.Model.GoHomeCommand.Execute(null); Pump();
            Click(window, Find<Button>(window, "NewInstanceButton"));
            Check(view.Model.IsHomeActive && overlay.IsVisible && page.Model.Form is { ImportFirst: false },
                "home create opens the same shell modal without leaving home");
            Click(window, Find<Button>(window, "ConfigFormCancel"));
            Click(window, Find<Button>(window, "ImportInstanceButton"));
            Check(view.Model.IsHomeActive && page.Model.Form is { ImportFirst: true, HasImports: true },
                "home import opens the shared form and lists import candidates");
            Click(window, Find<Button>(window, "ConfigFormCancel"));
            view.Model.SelectNavCommand.Execute("configs");
            window.Width = 390; Pump();
            var row = Find<Grid>(window, "ConfigInstanceRow");
            Check(Grid.GetRow(row.Children[2]) == 2, "narrow configuration row stacks actions below the instance and status");
            Click(window, Find<Button>(page, "ConfigExportButton"));
            Check(files.Exports.Last().Name == "fixture.json", "narrow instance export is reachable with a real pointer");
            using (var frame = window.CaptureRenderedFrame()) frame?.Save(Path.Combine(output, "config-manager-390.png"), PngBitmapEncoderOptions.Default);
            Click(window, Find<Button>(page, "ConfigCreateButton"));
            var card = Find<Border>(window, "ConfigForm");
            var origin = card.TranslatePoint(default, window)!.Value;
            Check(origin.X >= 0 && origin.X + card.Bounds.Width <= window.ClientSize.Width,
                "narrow create modal stays within the viewport");
            using (var frame = window.CaptureRenderedFrame()) frame?.Save(Path.Combine(output, "config-create-390.png"), PngBitmapEncoderOptions.Default);
        }
        finally { window.Close(); }
        Console.WriteLine("PASS: configuration routes, Core adapter, shared file operations, modal input and stale response isolation");
    }

    private sealed class MemoryUiFiles : Alas.UI.Platform.IUiFiles
    {
        public List<(string Name, byte[] Bytes)> Exports = [];
        public Task<(string Name, string Content)?> OpenJsonAsync() =>
            Task.FromResult<(string, string)?>(new("upload.json", "{\"Alas\":{}}"));
        public async Task SaveAsync(string name, string mediaType, Func<Task<byte[]>> content, CancellationToken cancellationToken = default) =>
            Exports.Add((name, await content()));
    }

    private static T Find<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
        ?? throw new Exception("Missing control: " + name);

    private static List<string> VisibleTexts(Visual root) =>
        root.GetVisualDescendants().OfType<TextBlock>()
            .Where(block => block.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(block.Text))
            .Select(block => block.Text!).ToList();

    /// <summary>
    /// 有界等待：延迟任务（TaskCompletionSource）完成后，续体先落在线程池、再回投到 UI 线程，
    /// 紧循环的 Pump 可能在池线程跑完之前就结束了，于是断言看到"还没完成"。
    /// 这里按条件轮询并配合 Pump，超时即返回 false（由调用方的断言报错）。
    /// </summary>
    private static bool WaitUntil(Func<bool> condition, int milliseconds = 3000)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < deadline)
        {
            Pump();
            if (condition()) return true;
            System.Threading.Thread.Sleep(5);
        }
        return condition();
    }

    private static void Pump()
    {
        for (var i = 0; i < 16; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void Check(bool value, string label)
    {
        if (!value) throw new Exception("FAIL: " + label);
    }

    /// <summary>页面局部合同的假后端：只用于离屏检查，不访问服务、设备或真实配置。</summary>
    private sealed class FakeBackend : IConfigInstancesBackend
    {
        public List<ConfigInstanceInfo> Instances { get; } = new();
        public List<ConfigImportSource> Imports { get; } = new();
        public Dictionary<string, string> Revisions { get; } = new();
        public Dictionary<string, string> Values { get; } = new();
        public List<(string Name, string Source, string Import)> Created { get; } = new();
        public List<(string Instance, string Revision)> Deleted { get; } = new();
        public List<(string Name, string Content)> Imported { get; } = new();
        public Exception? ListError { get; init; }
        public Exception? ImportError { get; init; }
        public Exception? DeleteError { get; init; }

        /// <summary>用于区分两个后端（切换后端时断言当前列表来自哪一个）。</summary>
        public string Tag { get; set; } = string.Empty;

        public int ListCalls { get; private set; }
        public int ReadCalls { get; private set; }
        public int ImportListCalls { get; private set; }

        /// <summary>非空时 ListInstances 会一直等它完成——用来制造"旧后端的列表晚到"。</summary>
        public TaskCompletionSource? ListGate { get; set; }

        /// <summary>非空时 ReadConfig 会一直等它完成——用来制造"操作在途"（按钮应禁用、重复点击应被忽略）。</summary>
        public TaskCompletionSource? ReadGate { get; set; }
        public TaskCompletionSource<string>? CreateGate { get; set; }
        public Func<Task<IReadOnlyList<ConfigInstanceInfo>>>? ListOperation { get; set; }

        public async Task<IReadOnlyList<ConfigInstanceInfo>> ListInstancesAsync(
            CancellationToken cancellationToken = default)
        {
            ListCalls++;
            if (ListOperation is not null) return await ListOperation();
            if (ListGate is not null) await ListGate.Task.ConfigureAwait(false);
            if (ListError is not null) throw ListError;
            return Instances.ToList();
        }

        public async Task<ConfigContent> ReadConfigAsync(string instance, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            if (ReadGate is not null) await ReadGate.Task.ConfigureAwait(false);
            return new ConfigContent(instance, Revisions.GetValueOrDefault(instance, "rev-1"),
                Values.GetValueOrDefault(instance, "{}"));
        }

        public Task<string> CreateInstanceAsync(string name, string? source, string? importFile,
            CancellationToken cancellationToken = default)
        {
            Created.Add((name, source ?? string.Empty, importFile ?? string.Empty));
            return CreateGate?.Task ?? Task.FromResult(name.Trim());
        }

        public Task ImportConfigAsync(string name, string content, CancellationToken cancellationToken = default)
        {
            if (ImportError is not null) return Task.FromException(ImportError);
            Imported.Add((name, content));
            Imports.Add(new ConfigImportSource(name, DateTimeOffset.UnixEpoch));
            return Task.CompletedTask;
        }

        /// <summary>非空时 ListImports 会抛错——用来制造"导入成功但重读可导入源失败"。</summary>
        public Exception? ImportListError { get; set; }

        public Task<IReadOnlyList<ConfigImportSource>> ListImportsAsync(CancellationToken cancellationToken = default)
        {
            ImportListCalls++;
            return ImportListError is null
                ? Task.FromResult<IReadOnlyList<ConfigImportSource>>(Imports.ToList())
                : Task.FromException<IReadOnlyList<ConfigImportSource>>(ImportListError);
        }
        public Task DeleteInstanceAsync(string instance, string revision, CancellationToken cancellationToken = default)
        {
            if (DeleteError is not null) return Task.FromException(DeleteError);
            Deleted.Add((instance, revision));
            Instances.RemoveAll(item => item.Name == instance);
            return Task.CompletedTask;
        }
    }
}
