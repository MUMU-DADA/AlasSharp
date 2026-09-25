using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Nodes;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Alas.UI.Overview;
using Alas.UI.ViewModels;
using Alas.UI.Views;

namespace Alas.UI.Headless;

/// <summary>
/// 概览页「资源卡片设置」（上游 resource.settings / resource.cardsHint）的离屏检查：
/// ① 未接 Core → 显示不可用原因、不渲染任何卡片、开关与保存都不可用（不伪造卡片清单）；
/// ② 接入数据 → 列出全部可选卡片、当前启用项为勾选状态；
/// ③ 切换后保存 → 提交的启用顺序与界面一致，保存成功有提示；
/// ④ 保存失败 → 显示真实错误且不谎称已保存。
/// </summary>
internal static class ResourceCardSettingsChecks
{
    internal static void Run()
    {
        Disconnected();
        ListsOptions();
        SavesSelection();
        ConnectedOverview();
    }

    private static void Disconnected()
    {
        var view = new ResourceCardSettingsPanel();
        var window = Show(view);
        try
        {
            var model = view.Model;
            Check(model.HasError, "disconnected card settings surface the missing capability");
            Check(model.Error.Contains("不可用"), "disconnected card settings explain why they are unavailable");
            Check(model.Options.Count == 0 && Find<ItemsControl>(view, "CardSettingsList").ItemCount == 0,
                "no card rows are rendered while unconnected");
            Check(!model.CanEdit && !model.CanSave, "editing and saving stay unavailable while unconnected");
            Check(!Find<Button>(view, "CardSettingsSaveButton").IsEnabled, "save button is disabled while unconnected");
            Check(Find<TextBlock>(view, "CardSettingsHint").Text!.Contains("上移或下移"),
                "the hint describes the implemented ordering controls");
        }
        finally { window.Close(); }
    }

    private static void ListsOptions()
    {
        var backend = new FakeSettings(
            new ResourceCardSettingsSnapshot(
                new[] { new ResourceCardOption("Oil", "石油"), new ResourceCardOption("Coin", "物资") },
                new[] { "Oil", "Coin" }));
        var view = new ResourceCardSettingsPanel(backend);
        var window = Show(view);
        try
        {
            var model = view.Model;
            Check(!model.HasError && model.Options.Count == 2, $"options are listed (got {model.Options.Count})");
            Check(model.Options.All(choice => choice.IsEnabled), "enabled cards start checked");
            var boxes = view.GetVisualDescendants().OfType<CheckBox>().ToList();
            Check(boxes.Count == 2 && boxes.All(box => box.IsChecked == true), "each option renders a checked box");
            Check(!Find<TextBlock>(view, "CardSettingsEmpty").IsVisible, "no empty state when options exist");
        }
        finally { window.Close(); }

        // 已连接但没有任何可配置卡片：显示真实空态，且不允许保存（不伪造"已保存"）。
        var empty = new ResourceCardSettingsPanel(new FakeSettings(
            new ResourceCardSettingsSnapshot(Array.Empty<ResourceCardOption>(), Array.Empty<string>())));
        var emptyWindow = Show(empty);
        try
        {
            Check(!empty.Model.HasError && empty.Model.IsEmpty, "a connected-but-empty backend yields the empty state");
            Check(Find<TextBlock>(empty, "CardSettingsEmpty").IsVisible, "the empty state is shown");
            Check(!empty.Model.CanEdit && !empty.Model.CanSave, "nothing can be edited or saved without cards");
            Check(!Find<Button>(empty, "CardSettingsSaveButton").IsEnabled, "the save button stays disabled");
        }
        finally { emptyWindow.Close(); }
    }

    private static void SavesSelection()
    {
        var backend = new FakeSettings(
            new ResourceCardSettingsSnapshot(
                new[] { new ResourceCardOption("Oil", "石油"), new ResourceCardOption("Coin", "物资") },
                new[] { "Oil", "Coin" }));
        var view = new ResourceCardSettingsPanel(backend);
        var window = Show(view);
        try
        {
            // 取消勾选第二张卡后保存：提交的清单必须与界面一致。
            var boxes = view.GetVisualDescendants().OfType<CheckBox>().ToList();
            boxes[1].IsChecked = false;
            Pump();
            Check(view.Model.EnabledKeys.SequenceEqual(new[] { "Oil" }),
                $"local edits follow the toggles (got {string.Join(",", view.Model.EnabledKeys)})");
            view.Model.SaveCommand.Execute(null);
            Pump();
            Check(backend.LastSaved.SequenceEqual(new[] { "Oil" }),
                $"the saved list matches the toggles (got {string.Join(",", backend.LastSaved)})");
            Check(view.Model.Saved && Find<TextBlock>(view, "CardSettingsSaved").IsVisible,
                "a successful save is reported");

            // 排序：上游是拖拽，这里用上移/下移按钮（可访问等价物）。顺序必须真的变化并被提交。
            // （上面刚取消勾选了第二张卡，这里先恢复，否则启用顺序里只剩一张卡、谈不上排序。）
            boxes[1].IsChecked = true;
            Pump();
            Check(view.Model.EnabledKeys.SequenceEqual(new[] { "Oil", "Coin" }),
                $"both cards are enabled again (got {string.Join(",", view.Model.EnabledKeys)})");
            Check(!view.Model.MoveUp("Oil"), "the first card cannot move up (boundary)");
            Check(view.Model.MoveDown("Oil"), "a card can move down");
            Pump();
            Check(view.Model.EnabledKeys.SequenceEqual(new[] { "Coin", "Oil" }),
                $"moving changes the order (got {string.Join(",", view.Model.EnabledKeys)})");
            Check(!view.Model.Saved, "reordering clears the saved flag until it is saved again");
            view.Model.SaveCommand.Execute(null);
            Pump();
            Check(backend.LastSaved.SequenceEqual(new[] { "Coin", "Oil" }),
                $"the reordered list is what gets saved (got {string.Join(",", backend.LastSaved)})");
            Check(!view.Model.MoveDown("Oil"), "the last card cannot move down (boundary)");

            // 「恢复默认」（上游 ResourceCards 的 defaultResourceKeys）：把选择重置为上游常量那几张。
            // 这条同时**证明卡片开关的绑定修复生效**：模型改了之后勾选框必须跟着变
            // （此前是"创建时赋值一次"，恢复默认改了模型而界面不动 —— 第 174 轮断言查出的缺陷）。
            boxes.Single(box => box.Content?.ToString() == "物资").IsChecked = false;
            Pump();
            Check(!view.Model.AllAdded, "AllAdded is false while a card is removed");
            Check(!Find<TextBlock>(view, "CardSettingsAllAdded").IsVisible,
                "the all-added hint hides while a card is removed");
            view.Model.RestoreDefault();
            Pump();
            Check(view.Model.Options.All(choice => view.Model.EnabledKeys.Contains(choice.Key)),
                $"restore-default selects every available card (got {string.Join(",", view.Model.EnabledKeys)})");
            // 勾选框要**重新查一遍**再断言：列表会回收容器，最早抓到的那批可能已经换了 DataContext
            // （这正是本步第一次运行失败的原因），按标签重新取才可靠。
            var afterRestore = view.GetVisualDescendants().OfType<CheckBox>()
                .Where(box => box.Content?.ToString() is "石油" or "物资")
                .ToList();
            Check(afterRestore.Count == 2 && afterRestore.All(box => box.IsChecked == true),
                $"restore-default re-checks every box through the binding " +
                $"(got {string.Join("|", afterRestore.Select(box => $"{box.Content}={box.IsChecked}"))})");
            Check(view.Model.AllAdded && Find<TextBlock>(view, "CardSettingsAllAdded").IsVisible,
                "the all-added hint appears once everything is selected");
            Check(!view.Model.Saved, "restore-default only changes local state until it is saved");
        }
        finally { window.Close(); }

        // 保存失败：显示真实错误，且不显示"已保存"。
        var failing = new FakeSettings(
            new ResourceCardSettingsSnapshot(
                new[] { new ResourceCardOption("Oil", "石油") },
                new[] { "Oil" }),
            saveError: "卡片清单写入失败");
        var failingView = new ResourceCardSettingsPanel(failing);
        var failingWindow = Show(failingView);
        try
        {
            failingView.Model.SaveCommand.Execute(null);
            Pump();
            Check(failingView.Model.Error == "卡片清单写入失败",
                $"a failing save surfaces the real error (got {failingView.Model.Error})");
            Check(!failingView.Model.Saved, "a failing save does not claim success");
        }
        finally { failingWindow.Close(); }
    }

    private static Window Show(ResourceCardSettingsPanel view)
    {
        var window = new Window { Width = 900, Height = 700, Content = view };
        window.Show();
        Pump();
        return window;
    }

    private static void ConnectedOverview()
    {
        var store = new TestStore();
        store.Write("one", "[\"Oil\",\"Coin\"]");
        store.Write("two", "[]");
        var model = new OverviewViewModel(resourceStore: store);
        model.SetInstance("one");
        model.ApplyState(JsonNode.Parse("""
            {"active":{"status":"idle"},"overview":{"instance":"one","resources":[
              {"name":"Oil","value":12,"record":"2026-09-24T12:00:00"},
              {"name":"Coin","value":34,"record":"2026-09-24T12:00:00"},
              {"name":"Gem","value":5,"record":"2026-09-24T12:00:00"}]}}
            """)!.AsObject());
        var view = new OverviewView { DataContext = model };
        var window = new Window { Width = 900, Height = 700, Content = view };
        window.Show(); Pump();
        try
        {
            var trigger = Find<Button>(view, "InstanceSettingsButton");
            var flyout = (Flyout)trigger.Flyout!;
            flyout.ShowAt(trigger); Pump();
            var panel = (ResourceSelectionPanel)flyout.Content!;
            Check(flyout.IsOpen && ReferenceEquals(panel.Selection, model.Selection),
                "the real overview flyout edits its current selection");
            Check(Find<StackPanel>(panel, "CardSettingsSelected").Children.Count == 2
                && Find<StackPanel>(panel, "CardSettingsAvailable").Children.Count == 1,
                "selected and available rows come from the real observation");
            Check(Find<Button>(panel, "CardSettingsMoveUp").IsVisible,
                "keyboard sorting controls remain visible and reachable");
            Press(panel, "CardSettingsAdd", "添加钻石");
            Check(model.Selection.Keys.SequenceEqual(new[] { "Oil", "Coin", "Gem" })
                && model.Resources.Count == 3 && store.Read("one") == "[\"Oil\",\"Coin\",\"Gem\"]",
                "adding a card immediately updates the overview and instance preference");
            var moveUp = panel.GetVisualDescendants().OfType<Button>().Single(control =>
                control.Name == "CardSettingsMoveUp" &&
                Avalonia.Automation.AutomationProperties.GetName(control) == "上移物资");
            moveUp.Focus(); Pump();
            Check(moveUp.IsFocused, "sorting button receives keyboard focus inside the flyout");
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); Pump();
            Check(model.Selection.Keys.SequenceEqual(new[] { "Coin", "Oil", "Gem" })
                && model.Resources[0].Name == "物资" && store.Read("one") == "[\"Coin\",\"Oil\",\"Gem\"]",
                "keyboard-reachable sorting persists and reorders cards immediately");
            Press(panel, "CardSettingsRemove", "移除石油");
            Check(model.Selection.Keys.SequenceEqual(new[] { "Coin", "Gem" }) && model.Resources.Count == 2,
                "removing a card immediately updates the overview");
            Press(panel, "CardSettingsRestoreButton", null);
            Check(model.Selection.Keys.SequenceEqual(ResourceCardSettingsPanel.DefaultKeys)
                && model.Resources.Count == 4 && store.Read("one") == "[\"Oil\",\"Coin\",\"Gem\",\"Cube\"]",
                "restoring defaults persists all upstream default cards");

            model.SetInstance("two"); Pump();
            Check(model.Selection.Keys.Count == 0 && model.Resources.Count == 0
                && Find<TextBlock>(panel, "CardSettingsEmpty").IsVisible
                && Find<StackPanel>(panel, "CardSettingsSelected").Children.Count == 0,
                "an open flyout follows the new instance's empty selection");
            Check(store.Read("one") == "[\"Oil\",\"Coin\",\"Gem\",\"Cube\"]"
                && store.Read("two") == "[]", "instance switching does not mutate the previous preference");
            flyout.Hide(); Pump();
            model.SetInstance("one"); Pump();
            flyout.ShowAt(trigger); Pump();
            Check(Find<StackPanel>(panel, "CardSettingsSelected").Children.Count == 4,
                "reopening after a detached instance switch refreshes the visible controls");

            store.FailWrites = true;
            Press(panel, "CardSettingsRemove", "移除石油");
            Check(model.Resources.Count == 3 && model.Selection.StorageError.Length > 0
                && Find<TextBlock>(panel, "CardSettingsError").IsVisible,
                "storage denial keeps the current in-memory cards and exposes the error");
            store.FailWrites = false;
            Press(panel, "CardSettingsRestoreButton", null);
            Check(model.Selection.StorageError.Length == 0 && !Find<TextBlock>(panel, "CardSettingsError").IsVisible,
                "successful retry clears the storage error");
            var replacement = new OverviewViewModel(resourceStore: store);
            replacement.SetInstance("two");
            view.DataContext = replacement; Pump();
            Check(ReferenceEquals(panel.Selection, replacement.Selection)
                && Find<StackPanel>(panel, "CardSettingsSelected").Children.Count == 0,
                "replacing the overview model rebinds the open flyout to its new selection");
            model.Selection.Remove("Coin"); Pump();
            Check(Find<StackPanel>(panel, "CardSettingsSelected").Children.Count == 0,
                "events from the old selection cannot modify the replacement flyout");
            view.DataContext = model; Pump();
            window.Width = 390; window.Height = 844; window.UpdateLayout(); Pump();
            Check(panel.Bounds.Width <= 390 && panel.GetVisualDescendants().OfType<Button>()
                .Where(button => button.Name is "CardSettingsMoveUp" or "CardSettingsMoveDown"
                    or "CardSettingsRemove").All(button => button.IsEffectivelyVisible),
                "narrow flyout retains every sorting and removal control");
            flyout.Hide();
        }
        finally { window.Close(); }
    }

    internal static void RunPerformance(string output)
    {
        var store = new MemoryResourceSelectionStore();
        var model = new OverviewViewModel(resourceStore: store);
        model.SetInstance("perf");
        model.ApplyState(JsonNode.Parse("""
            {"active":{"status":"idle"},"overview":{"instance":"perf","resources":[
              {"name":"Oil","value":12,"record":"2026-09-24T12:00:00"},
              {"name":"Coin","value":34,"record":"2026-09-24T12:00:00"},
              {"name":"Gem","value":5,"record":"2026-09-24T12:00:00"},
              {"name":"Cube","value":6,"record":"2026-09-24T12:00:00"}]}}
            """)!.AsObject());
        var view = new OverviewView { DataContext = model };
        var window = new Window { Width = 900, Height = 700, Content = view };
        window.Show(); Pump();
        try
        {
            var trigger = Find<Button>(view, "InstanceSettingsButton");
            var flyout = (Flyout)trigger.Flyout!;
            var panel = (ResourceSelectionPanel)flyout.Content!;
            var legacy = new Flyout
            {
                Content = new ResourceCardSettingsPanel(new FakeSettings(
                    new ResourceCardSettingsSnapshot(
                        new[] { new ResourceCardOption("Oil", "石油"), new ResourceCardOption("Coin", "物资"),
                            new ResourceCardOption("Gem", "钻石"), new ResourceCardOption("Cube", "心智魔方") },
                        ResourceCardSettingsPanel.DefaultKeys))),
            };
            var openings = new List<Sample>();
            var legacyOpenings = new List<Sample>();
            for (int i = 0; i < 20; i++)
            {
                legacyOpenings.Add(Measure(() => legacy.ShowAt(trigger), window));
                legacy.Hide(); Pump();
                openings.Add(Measure(() => flyout.ShowAt(trigger), window));
                Check(panel.GetVisualDescendants().OfType<Button>().Count(button => button.Name == "CardSettingsRemove") == 4,
                    "performance opening keeps all four default cards and controls");
                flyout.Hide(); Pump();
            }
            flyout.ShowAt(trigger); Pump();
            var sorting = new List<Sample>();
            for (int i = 0; i < 20; i++)
            {
                var label = i % 2 == 0 ? "下移石油" : "上移石油";
                sorting.Add(Measure(() => ClickAction(panel, "CardSettingsMove" + (i % 2 == 0 ? "Down" : "Up"), label), window));
            }
            Check(model.Selection.Keys.SequenceEqual(ResourceCardSettingsPanel.DefaultKeys),
                "sorting performance returns to the original complete card order");
            var report = new
            {
                schema = "ui-resource-settings-perf/1",
                field_count = model.Selection.Selected.Count,
                legacy_open_reference = Summary(legacyOpenings), open = Summary(openings), sort = Summary(sorting),
                environment = "Release Avalonia Headless, offscreen Skia; four visible cards in both panels; legacy open is a nonfunctional reference, not an operation baseline",
            };
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "resource-settings-perf.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"resource settings: legacy open={report.legacy_open_reference.MedianMs} ms/{report.legacy_open_reference.MedianBytes} B "
                + $"live open={report.open.MedianMs} ms/{report.open.MedianBytes} B "
                + $"sort={report.sort.MedianMs} ms/{report.sort.MedianBytes} B");
            flyout.Hide();
        }
        finally { window.Close(); }
    }

    private readonly record struct Sample(double ActionMs, double LayoutMs, double RenderMs, long Bytes)
    {
        public double Ms => ActionMs + LayoutMs + RenderMs;
    }
    private sealed record Metric(double MedianMs, double P95Ms, double ActionMs, double LayoutMs,
        double RenderMs, long MedianBytes);

    private static Sample Measure(Action action, Window window)
    {
        PumpFrame();
        long allocation = GC.GetAllocatedBytesForCurrentThread();
        var clock = Stopwatch.StartNew();
        action();
        double actionMs = clock.Elapsed.TotalMilliseconds;
        window.UpdateLayout();
        double layoutMs = clock.Elapsed.TotalMilliseconds - actionMs;
        PumpFrame();
        return new Sample(actionMs, layoutMs, clock.Elapsed.TotalMilliseconds - actionMs - layoutMs,
            GC.GetAllocatedBytesForCurrentThread() - allocation);
    }

    private static void PumpFrame()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static Metric Summary(List<Sample> samples)
    {
        var milliseconds = samples.Select(sample => sample.Ms).Order().ToArray();
        var bytes = samples.Select(sample => sample.Bytes).Order().ToArray();
        return new Metric(Math.Round(milliseconds[9], 3), Math.Round(milliseconds[18], 3),
            Math.Round(samples.Select(sample => sample.ActionMs).Order().ElementAt(9), 3),
            Math.Round(samples.Select(sample => sample.LayoutMs).Order().ElementAt(9), 3),
            Math.Round(samples.Select(sample => sample.RenderMs).Order().ElementAt(9), 3), bytes[9]);
    }

    private static void ClickAction(ResourceSelectionPanel panel, string name, string automationName)
    {
        var button = panel.GetVisualDescendants().OfType<Button>().First(control => control.Name == name &&
            Avalonia.Automation.AutomationProperties.GetName(control) == automationName);
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    }

    private static void Press(ResourceSelectionPanel panel, string name, string? automationName)
    {
        var button = panel.GetVisualDescendants().OfType<Button>().First(control =>
            control.Name == name && (automationName is null ||
                Avalonia.Automation.AutomationProperties.GetName(control) == automationName));
        Check(button.IsEffectivelyVisible && button.IsEnabled && button.Focusable,
            "setting action is visible and keyboard reachable: " + automationName);
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Pump();
    }

    private sealed class TestStore : IResourceSelectionStore
    {
        private readonly MemoryResourceSelectionStore _memory = new();
        public bool FailWrites { get; set; }
        public string? Read(string instance) => _memory.Read(instance);
        public void Write(string instance, string json)
        {
            if (FailWrites) throw new IOException("write denied");
            _memory.Write(instance, json);
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

    /// <summary>局部接口的假实现：只用于离屏检查，不访问服务、设备或真实配置。</summary>
    private sealed class FakeSettings(ResourceCardSettingsSnapshot snapshot, string? saveError = null) : IResourceCardSettings
    {
        public IReadOnlyList<string> LastSaved { get; private set; } = Array.Empty<string>();

        public Task<ResourceCardSettingsSnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot);

        public Task<ResourceCardSettingsSnapshot> SaveAsync(IReadOnlyList<string> enabledKeys, CancellationToken cancellationToken = default)
        {
            LastSaved = enabledKeys;
            return Task.FromResult(saveError is null
                ? snapshot with { EnabledKeys = enabledKeys }
                : snapshot with { Error = saveError });
        }
    }
}
