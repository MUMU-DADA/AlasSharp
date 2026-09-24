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
using Alas.UI.Overview;

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
