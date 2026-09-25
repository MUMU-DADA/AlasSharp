using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using Alas.UI.Overview;
using Alas.UI.Simulation;
using Alas.UI.TaskEditor;
using Alas.UI.Theming;
using Alas.UI.ViewModels;
using Alas.UI.Views;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Alas.UI.Headless;

internal static class TaskEditorCacheChecks
{
    private static readonly FieldInfo PropertyChangedField = typeof(EditorObservable).GetField(
        nameof(EditorObservable.PropertyChanged), BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("无法检查任务字段事件订阅。");

    public static void Run()
    {
        using var backend = new SimulatedUiBackend();
        var shell = new MainView(new MemoryThemeStore(), backend,
            resourceStore: new MemoryResourceSelectionStore());
        var window = new Window { Width = 1280, Height = 820, Content = shell };
        window.Show();
        try
        {
            shell.Model.SelectInstance("demo-main");
            Pump(window);
            var tasks = shell.Model.TaskGroups.SelectMany(group => group.Tasks)
                .Where(task => task.Key != "MeowfficerScore")
                .DistinctBy(task => task.Key)
                .Take(4).ToArray();
            Check(tasks.Length == 4, "模拟目录不足四个普通任务");

            Select(shell, window, tasks[0]);
            var firstModel = shell.Model.TaskEditor;
            firstModel.AutoSave = false;
            var draft = firstModel.Fields.Single(field => field.Argument == "Count");
            draft.SetText("9");
            firstModel.Search = "模拟数量";
            Pump(window);
            Check(firstModel.HasChanges && firstModel.Fields.Count(firstModel.Matches) == 1,
                "初始草稿和筛选未建立");
            var weakPage = WeakActivePage(shell);
            int modelSubscriptions = Subscriptions(firstModel);
            int fieldSubscriptions = Subscriptions(draft);
            Check(modelSubscriptions >= 2 && fieldSubscriptions >= 2, "首个任务页未订阅模型或字段");

            Select(shell, window, tasks[1]);
            var secondPage = ActivePage(shell);
            Select(shell, window, tasks[2]);
            Check(TaskHost(shell).Children.OfType<TaskEditorView>().Count() == 3,
                "前三个任务页未完整缓存");
            Select(shell, window, tasks[1]);
            Check(ReferenceEquals(secondPage, ActivePage(shell)), "缓存页切换时重建了第二页");

            shell.Model.ApplyTheme(ThemePreference.Default with { Theme = UiTheme.LegacyDark });
            window.Width = 390;
            Pump(window);
            Check(shell.Model.IsNarrow && ReferenceEquals(secondPage, ActivePage(shell)),
                "窄屏布局丢失缓存任务页");
            Check(secondPage.GetVisualDescendants().OfType<Control>()
                .Count(control => control.Name?.StartsWith("Field_", StringComparison.Ordinal) == true)
                == shell.Model.TaskEditor.Fields.Count(), "窄屏未保留完整字段控件");
            window.Width = 1280;
            shell.Model.ApplyTheme(ThemePreference.Default);
            Pump(window);
            Check(!shell.Model.IsNarrow && ReferenceEquals(secondPage, ActivePage(shell)),
                "主题与宽屏往返重建了缓存任务页");

            Select(shell, window, tasks[2]);
            Select(shell, window, tasks[3]);
            Check(TaskHost(shell).Children.OfType<TaskEditorView>().Count() == 3,
                "第四页后缓存未保持三页上限");
            Check(Subscriptions(firstModel) == modelSubscriptions - 1
                && Subscriptions(draft) == fieldSubscriptions - 2,
                "淘汰页未退订模型与两个字段事件");
            Pump(window);
            Collect();
            Check(!weakPage.TryGetTarget(out _), "淘汰页仍被保留");
            GC.KeepAlive(firstModel);
            GC.KeepAlive(draft);

            Select(shell, window, tasks[0]);
            Check(ReferenceEquals(firstModel, shell.Model.TaskEditor)
                && firstModel.Search == "模拟数量" && draft.Text == "9" && draft.IsDirty,
                "重建后草稿或筛选状态丢失");
            var restored = ActivePage(shell);
            Check(restored.GetVisualDescendants().OfType<TextBox>()
                .Single(control => control.Name == "TaskConfigSearch").Text == firstModel.Search,
                "重建页未显示保留的筛选词");
            Check(Subscriptions(firstModel) == modelSubscriptions
                && Subscriptions(draft) == fieldSubscriptions,
                "重建页事件订阅未恢复到原数量");
        }
        finally { window.Close(); Pump(); }
        Console.WriteLine("PASS: task-page LRU eviction, subscriptions, weak collection, drafts, filter, theme and narrow layout");
    }

    private static void Select(MainView shell, Window window, TaskEntry task)
    {
        shell.Model.SelectTaskCommand.Execute(task);
        for (int attempt = 0; attempt < 100; attempt++)
        {
            Pump(window);
            if (shell.Model.TaskEditor.IsLoaded && shell.Model.TaskEditor.TaskName == task.Key) return;
            Thread.Sleep(1);
        }
        throw new InvalidOperationException("缓存检查：任务加载失败：" + task.Key + " / " + shell.Model.TaskEditor.Error);
    }

    private static Panel TaskHost(MainView shell) => shell.FindControl<Panel>("TaskEditorHost")
        ?? throw new InvalidOperationException("找不到任务页宿主。");

    private static TaskEditorView ActivePage(MainView shell) => TaskHost(shell).Children
        .OfType<TaskEditorView>().Single(page => page.IsVisible);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<TaskEditorView> WeakActivePage(MainView shell) => new(ActivePage(shell));

    private static int Subscriptions(EditorObservable model) =>
        (PropertyChangedField.GetValue(model) as PropertyChangedEventHandler)?.GetInvocationList().Length ?? 0;

    private static void Collect()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private static void Pump(Window? window = null)
    {
        for (int index = 0; index < 4; index++)
        {
            Dispatcher.UIThread.RunJobs();
            window?.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Task cache: " + message);
    }
}
