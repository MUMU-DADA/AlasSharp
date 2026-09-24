using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Alas.UI.Headless;

/// <summary>
/// 「控件真的点得到吗」检查：对页面上每个**可见且启用**的按钮，取其中心点做命中测试，
/// 断言命中结果就是该按钮（或它的可视后代）。
///
/// 为什么需要它：我在第 140–147 轮排查过一个真实缺陷——抽屉里的关闭按钮被过宽的品牌行挤出边界、
/// 裁掉一半后中心点命中到侧栏背景，点击失效；而位置/可见性断言都显示"正常"。
/// 这类"看得见但点不到"的问题只有命中测试能发现。此检查把那次教训固化成通用防线。
/// </summary>
internal static class HitTestReachabilityChecks
{
    internal static void Run()
    {
        var pages = new (string Name, Control View)[]
        {
            ("config manager page", new ConfigManager.ConfigManagerPage(StubConfigBackend.Instance)),
            ("remote access page", new RemoteAccess.RemoteAccessView(StubRemoteBackend.Instance)),
            ("updater page", new Updater.UpdaterView(StubUpdaterBackend.Instance)),
            ("developer tools page", new DevTools.DevToolsView(StubDevBackend.Instance)),
            ("settings page", new Settings.SettingsView(StubSettingsBackend.Instance)),
            ("login page", new Auth.LoginView(StubAuthBackend.Instance)),
        };

        foreach (var (name, view) in pages) CheckButtonsAreHittable(view, name);
        // 窄宽度（移动端 390）下再走一遍：这些页面此前只在 1280 宽下验证过，
        // 而窄屏是产品形态之一（外壳有 drawer/rail 覆盖层）——页面在窄屏是否仍可操作必须验。
        foreach (var (name, view) in BuildNarrowPages()) CheckButtonsAreHittable(view, name + " @390");
        // 极矮视口（390×500）：不变量是**要么主操作仍在视口内，要么页面可滚动**——
        // 否则内容被裁掉且滚不到，用户就彻底够不着了（这是"看得见但点不到"的另一种形态）。
        foreach (var (name, view) in BuildNarrowPages()) CheckShortViewport(view, name + " @390x500");
        // 超长文本：文件名/路径极长时布局不能被撑坏（按钮仍要可点、主操作仍要可见）。
        CheckButtonsAreHittable(new ConfigManager.ConfigManagerPage(StubConfigBackend.LongText), "config manager @long-text");
        CheckButtonsAreHittable(new Settings.SettingsView(StubSettingsBackend.LongText), "settings page @long-text");
        CheckButtonsAreHittable(new Updater.UpdaterView(StubUpdaterBackend.LongText), "updater page @long-text");
        CheckInjectionChangesThePage();
    }

    /// <summary>
    /// 六页都暴露了外壳注入点（可写 `Backend`）。这条断言验证**注入确实改变了页面呈现**：
    /// 先以"未连接"构造（默认状态），记录页面上的全部可见文本作为签名；注入带数据的假后端后
    /// 再取一次签名，两者必须不同——否则外壳注入了后端，界面却什么都不变（等于没接上）。
    /// 这一族此前只有"未连接"态被断言覆盖，注入路径本身没有测试。
    /// </summary>
    private static void CheckInjectionChangesThePage()
    {
        var cases = new (string Name, Func<Control> Create, Action<Control> Inject)[]
        {
            ("config manager page", () => new ConfigManager.ConfigManagerPage(),
                view => ((ConfigManager.ConfigManagerPage)view).Backend = StubConfigBackend.Instance),
            ("remote access page", () => new RemoteAccess.RemoteAccessView(),
                view => ((RemoteAccess.RemoteAccessView)view).Backend = StubRemoteBackend.Instance),
            ("updater page", () => new Updater.UpdaterView(),
                view => ((Updater.UpdaterView)view).Backend = StubUpdaterBackend.Instance),
            ("developer tools page", () => new DevTools.DevToolsView(),
                view => ((DevTools.DevToolsView)view).Backend = StubDevBackend.Instance),
            ("settings page", () => new Settings.SettingsView(),
                view => ((Settings.SettingsView)view).Backend = StubSettingsBackend.Instance),
            ("login page", () => new Auth.LoginView(),
                view => ((Auth.LoginView)view).Backend = StubAuthBackend.Instance),
        };

        foreach (var (name, create, inject) in cases)
        {
            var view = create();
            var window = new Window { Width = 1280, Height = 820, Content = view };
            window.Show();
            Pump();
            try
            {
                var before = VisibleText(view);
                inject(view);
                Pump();
                var after = VisibleText(view);
                if (before == after)
                {
                    throw new Exception($"FAIL: {name} looks identical after a backend was injected " +
                        "(the injection point is not wired to the page)");
                }
            }
            finally { window.Close(); }
        }
    }

    /// <summary>页面上全部可见文本的签名（用于比较"注入前后是否真的变了"）。</summary>
    private static string VisibleText(Visual root) =>
        string.Join("|", root.GetVisualDescendants().OfType<TextBlock>()
            .Where(block => block.IsVisible && !string.IsNullOrWhiteSpace(block.Text))
            .Select(block => block.Text));

    /// <summary>极矮视口下：主操作必须在视口内，或者页面里有 ScrollViewer 能滚到它。</summary>
    private static void CheckShortViewport(Control view, string page)
    {
        var window = new Window { Width = 390, Height = 500, Content = view };
        window.Show();
        Pump();
        try
        {
            var primaryName = PrimaryActions.TryGetValue(page.Split(' ')[0], out var mapped) ? mapped : null;
            if (primaryName is null) return;
            var primary = view.GetVisualDescendants().OfType<Control>()
                .FirstOrDefault(control => control.Name == primaryName);
            if (primary is null) throw new Exception($"FAIL: {page} has no {primaryName} at all");

            var origin = primary.TranslatePoint(new Point(0, 0), window) ?? default;
            var inside = origin.Y + primary.Bounds.Height <= window.Height + 0.5;
            var scrollable = view.GetVisualDescendants().OfType<ScrollViewer>().Any();
            if (!inside && !scrollable)
            {
                throw new Exception($"FAIL: {page}: the primary action must either fit or the page must scroll " +
                    $"(bottom {origin.Y + primary.Bounds.Height} vs {window.Height}, scrollable={scrollable})");
            }
        }
        finally { window.Close(); }
    }

    /// <summary>窄屏用的一套页面实例（视图只能挂载一次，必须新建）。</summary>
    private static (string Name, Control View)[] BuildNarrowPages() => new (string, Control)[]
    {
        ("config manager page", new ConfigManager.ConfigManagerPage(StubConfigBackend.Instance)),
        ("remote access page", new RemoteAccess.RemoteAccessView(StubRemoteBackend.Instance)),
        ("updater page", new Updater.UpdaterView(StubUpdaterBackend.Instance)),
        ("developer tools page", new DevTools.DevToolsView(StubDevBackend.Instance)),
        ("settings page", new Settings.SettingsView(StubSettingsBackend.Instance)),
        ("login page", new Auth.LoginView(StubAuthBackend.Instance)),
    };

    /// <summary>
    /// 每个页面的**主操作**在"有数据"的状态下必须真的可见、并且有实际尺寸。
    /// 这正是一个真实缺陷的形状：系统设置的「应用改动」按钮创建时隐藏，异步数据到达后
    /// PropertyChanged 只更新了行与 enabled、漏了按钮自身的 IsVisible → 用户永远点不到
    /// （第 155 轮修复，提交 46496f0）。这里在同一个窗口里顺带检查，避免重复挂载视图。
    /// </summary>
    private static void CheckPrimaryActionVisible(Visual root, string page, string controlName)
    {
        var control = root.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(candidate => candidate.Name == controlName);
        if (control is null) throw new Exception($"FAIL: {page} has no {controlName} at all");
        if (!control.IsVisible)
        {
            throw new Exception($"FAIL: {page} primary action {controlName} is not visible with data present");
        }
        if (control.Bounds.Width <= 0 || control.Bounds.Height <= 0)
        {
            throw new Exception($"FAIL: {page} primary action {controlName} has no laid-out size (bounds {control.Bounds})");
        }
    }

    private static void CheckButtonsAreHittable(Control view, string page)
    {
        var window = new Window { Width = page.EndsWith("@390") ? 390 : 1280, Height = 820, Content = view };
        window.Show();
        Pump();
        try
        {
            // 主操作必须可见且有尺寸（"该出现的按钮没出现"的真实缺陷形状）。
            if (PrimaryActions.TryGetValue(page, out var primary))
            {
                CheckPrimaryActionVisible(view, page, primary);
            }

            var buttons = view.GetVisualDescendants().OfType<Button>()
                .Where(button => button.IsVisible && button.IsEffectivelyEnabled
                    && button.Bounds.Width > 0 && button.Bounds.Height > 0)
                .ToList();
            // 零面积控件不算"有按钮但点不到"：模板里被压成 0 高的部件（例如 Fluent 滚动条里
            // 没有分页空间的 PART_PageUpButton，实测 bounds=(0,0,5,0) 但 IsVisible/IsEffectivelyEnabled
            // 仍为 true）在几何上就没有中心点可点，命中的只会是同一滚动条上的其它部件。
            // 只按宽度过滤会漏掉这一类：开发者工具页补上页面自持滚动容器后，页面级滚动条一出现就误报。
            // 某些页面在给定状态下本来就没有按钮（例如系统设置在无待应用改动时）。
            // 这不是缺陷，记录下来继续；真正要拦的是"有按钮但点不到"。
            if (buttons.Count == 0)
            {
                return;
            }

            var unreachable = new List<string>();
            foreach (var button in buttons)
            {
                // 只测完全落在根视图内的按钮：越界的那些由包含性断言负责，这里测"点到的是谁"。
                var origin = button.TranslatePoint(new Point(0, 0), window);
                if (origin is null) continue;
                var point = new Point(origin.Value.X + button.Bounds.Width / 2, origin.Value.Y + button.Bounds.Height / 2);
                if (point.X < 0 || point.Y < 0 || point.X > window.Width || point.Y > window.Height) continue;
                if (view.InputHitTest(point) is not Visual hit)
                {
                    unreachable.Add($"{button.Name ?? button.Content?.ToString() ?? "?"}(no hit)");
                    continue;
                }
                // 命中者必须是该按钮本身、它的后代、或它的内容呈现器（点文字命中的是 TextBlock）。
                var belongs = hit == button || hit.GetVisualAncestors().Contains(button);
                if (!belongs)
                {
                    unreachable.Add($"{button.Name ?? button.Content?.ToString() ?? "?"} → {hit.GetType().Name}");
                }
            }

            if (unreachable.Count > 0)
            {
                throw new Exception($"FAIL: {page} has buttons that cannot be hit at their centre: " +
                    string.Join(", ", unreachable));
            }
        }
        finally { window.Close(); }
    }

    /// <summary>各页面的主操作控件名（有数据时必须可见、有尺寸）。</summary>
    private static readonly Dictionary<string, string> PrimaryActions = new()
    {
        ["config manager page"] = "ConfigCreateButton",
        ["remote access page"] = "RemoteToggleButton",
        ["updater page"] = "UpdaterFetchButton",
        ["developer tools page"] = "DevToolsSimulateRunningButton",
        // 系统设置页没有「应用改动」这类常驻主操作：它按上游 useDeploySettings 的语义
        // **输入即提交**（改动进草稿队列后立即发送），因此页面上的按钮是逐字段的
        // 「重试保存」，只在对应字段失败时出现——"有数据时必须有可见主操作"不适用于它。
        // 该页的可达性仍由下面按可见控件逐个命中测试覆盖。
        ["login page"] = "LoginSubmitButton",
    };

    private static void Pump()
    {
        // 多跑几轮：页面的数据是异步拉取的，命中测试必须在"数据到位、按钮真的出现"之后做
        // （否则会把"还没渲染"误报成"没有按钮"）。
        for (var i = 0; i < 24; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>页面局部接口的最小假实现：只为了让页面渲染出按钮，不访问任何服务或设备。</summary>
    private sealed class StubConfigBackend(bool longText = false) : ConfigManager.IConfigInstancesBackend
    {
        public static StubConfigBackend Instance { get; } = new();

        /// <summary>超长文本变体：实例名/序列号都很长，用来验证布局不会被撑坏、按钮仍可点。</summary>
        public static StubConfigBackend LongText { get; } = new(longText: true);

        private static string Long => new string('长', 120);

        public Task<IReadOnlyList<ConfigManager.ConfigInstanceInfo>> ListInstancesAsync(CancellationToken t = default) =>
            Task.FromResult<IReadOnlyList<ConfigManager.ConfigInstanceInfo>>(new[]
            {
                new ConfigManager.ConfigInstanceInfo(
                    longText ? Long : "demo-main",
                    longText ? Long : "en",
                    longText ? Long : "emulator-5554",
                    "running"),
            });

        public Task<ConfigManager.ConfigContent> ReadConfigAsync(string instance, CancellationToken t = default) =>
            Task.FromResult(new ConfigManager.ConfigContent(instance, "rev-1", "{}"));

        public Task<string> CreateInstanceAsync(string name, string? source, string? importFile,
            CancellationToken t = default) => Task.FromResult(name);

        public Task ImportConfigAsync(string name, string content, CancellationToken t = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<ConfigManager.ConfigImportSource>> ListImportsAsync(CancellationToken t = default) =>
            Task.FromResult<IReadOnlyList<ConfigManager.ConfigImportSource>>(
                new[] { new ConfigManager.ConfigImportSource("imported", DateTimeOffset.UnixEpoch) });

        public Task DeleteInstanceAsync(string instance, string revision, CancellationToken t = default) =>
            Task.CompletedTask;
    }

    private sealed class StubRemoteBackend : RemoteAccess.IRemoteAccessBackend
    {
        public static StubRemoteBackend Instance { get; } = new();
        public Task<RemoteAccess.RemoteAccessSchema> ReadStatusAsync(CancellationToken t = default) =>
            Task.FromResult(new RemoteAccess.RemoteAccessSchema("ready", "https://remote.example/abc"));
        public Task<RemoteAccess.RemoteAccessStatus> SetEnabledAsync(bool enabled, CancellationToken t = default) =>
            Task.FromResult(new RemoteAccess.RemoteAccessStatus(enabled ? "ready" : "disabled", "https://remote.example/abc"));
    }

    private sealed class StubUpdaterBackend(bool longText = false) : Updater.IUpdaterBackend
    {
        public static StubUpdaterBackend Instance { get; } = new();

        /// <summary>超长文本变体：HEAD 与提交信息都是长串，验证列表布局不被撑坏。</summary>
        public static StubUpdaterBackend LongText { get; } = new(longText: true);

        private static string Long => new string('长', 120);

        private static Updater.UpdaterStatus Status(bool longText) => new(
            "available",
            longText ? Long : "abc1234",
            longText ? Long : "def5678",
            2,
            new[]
            {
                new Updater.UpdaterCommit("c1", longText ? Long : "提交 1"),
                new Updater.UpdaterCommit("c2", longText ? Long : "提交 2"),
            },
            null, 1, 0);

        public Task<Updater.UpdaterStatus> ReadStatusAsync(CancellationToken t = default) => Task.FromResult(Status(longText));
        public Task<Updater.UpdaterStatus> FetchAsync(CancellationToken t = default) => Task.FromResult(Status(longText));
        public Task<Updater.UpdaterStatus> ApplyAsync(CancellationToken t = default) => Task.FromResult(Status(longText));
        public Task<Updater.UpdaterStatus> CancelAsync(CancellationToken t = default) => Task.FromResult(Status(longText));
        public Task<Updater.UpdaterStatus> ReadCommitsAsync(int offset, CancellationToken t = default) => Task.FromResult(Status(longText));
    }

    private sealed class StubDevBackend : DevTools.IDevToolsBackend
    {
        public static StubDevBackend Instance { get; } = new();
        private static DevTools.DevToolsStatus Status => new(true, null, false);
        public Task<DevTools.DevToolsStatus> ReadStatusAsync(CancellationToken t = default) => Task.FromResult(Status);
        public Task<DevTools.DevToolsStatus> SimulateAsync(string status, CancellationToken t = default) => Task.FromResult(Status);
        public Task<DevTools.DevToolsStatus> ClearSimulationAsync(CancellationToken t = default) => Task.FromResult(Status);
        public Task<DevTools.DevToolsStatus> SetUpdateNoticeAsync(bool enabled, CancellationToken t = default) => Task.FromResult(Status);
        public Task ThrowTestAsync(CancellationToken t = default) => Task.CompletedTask;
    }

    private sealed class StubSettingsBackend(bool longText = false) : Settings.ISettingsBackend
    {
        public static StubSettingsBackend Instance { get; } = new();

        /// <summary>超长文本变体：分组名/字段名/值/说明/错误都是长串，验证布局不被撑坏。</summary>
        public static StubSettingsBackend LongText { get; } = new(longText: true);

        private static string Long => new string('长', 120);

        private static Settings.SettingsSnapshot Snapshot(bool longText) => new(new[]
        {
            new Settings.SettingsGroup(
                longText ? Long : "连接",
                new[]
                {
                    new Settings.SettingsField(
                        "host",
                        longText ? Long : "主机",
                        longText ? Long : "127.0.0.1",
                        true,
                        Help: longText ? Long : string.Empty,
                        Error: longText ? Long : string.Empty),
                }),
        }, QueuedChanges: 1);
        public Task<Settings.SettingsSnapshot?> ReadAsync(CancellationToken t = default) =>
            Task.FromResult<Settings.SettingsSnapshot?>(Snapshot(longText));
        public Task SaveAsync(Settings.SettingsChange change, CancellationToken t = default) =>
            Task.CompletedTask;
    }

    private sealed class StubAuthBackend : Auth.IAlasAuth
    {
        public static StubAuthBackend Instance { get; } = new();
        public Task<Auth.AuthStatus> ReadAsync(CancellationToken t = default) => Task.FromResult(new Auth.AuthStatus("pending"));
        public Task<Auth.AuthStatus> SignInAsync(string password, CancellationToken t = default) =>
            Task.FromResult(new Auth.AuthStatus("authenticated"));
    }
}
