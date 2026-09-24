using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Alas.UI.Headless;

/// <summary>
/// 命令可达性检查：把页面 ViewModel 上所有公开的 <see cref="ICommand"/> 属性与
/// 可视树里控件的 <c>Command</c> 做对照，找出**写了却没有界面入口**的命令。
///
/// 为什么需要它：我已经遇到两次同类缺陷（配置管理的保存、界面设置的删除配色）——
/// 先写 VM 再写视图时漏挂命令，而编译与既有断言都不会发现。
/// 这个检查是防住同类问题的一般化手段。
/// </summary>
internal static class CommandReachabilityChecks
{
    internal static void Run()
    {
        ConfigManagerPage();
        // 其余六页：用各自的未接能力构造（控件存在但禁用，仍能检查"命令是否挂了入口"）。
        Reachable(new Auth.LoginView(), "login page");
        Reachable(new RemoteAccess.RemoteAccessView(), "remote access page");
        Reachable(new Updater.UpdaterView(), "updater page");
        Reachable(new DevTools.DevToolsView(), "developer tools page");
        Reachable(new Settings.SettingsView(), "settings page");
        Reachable(new Overview.ResourceCardSettingsPanel(), "resource card settings panel");
    }

    /// <summary>对单个视图做可达性检查（DataContext 即其 VM）。</summary>
    private static void Reachable(Control view, string page, IReadOnlyDictionary<string, string>? exempt = null)
    {
        var window = Show(view);
        try
        {
            if (view.DataContext is null) throw new Exception($"FAIL: {page} has no view model");
            CheckAllReachable(view, view.DataContext, page, exempt ?? NoExemptions);
        }
        finally { window.Close(); }
    }

    private static readonly IReadOnlyDictionary<string, string> NoExemptions =
        new Dictionary<string, string>();

    private static void ConfigManagerPage()
    {
        var backend = new StubConfigBackend();
        var view = new ConfigManager.ConfigManagerPage(backend) { Model = { Connected = true } };
        var window = Show(view);
        try
        {
            // 新配置管理页（028 的 A）五个命令都挂在真实控件上：新建/导入在工具栏，
            // 概览/导出/删除在每一行（行命令用 CommandParameter 传实例名）。
            CheckAllReachable(view, view.Model, "config manager", NoExemptions);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// 断言 <paramref name="view"/> 可视树里有控件的 Command 指向 VM 的每个公开命令；
    /// <paramref name="exempt"/> 里的命令需要写明理由（表示"暂不暴露"，不是遗漏）。
    /// </summary>
    private static void CheckAllReachable(Visual view, object model, string page, IReadOnlyDictionary<string, string> exempt)
    {
        var commands = model.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => typeof(ICommand).IsAssignableFrom(property.PropertyType))
            .Select(property => (property.Name, Value: property.GetValue(model) as ICommand))
            .Where(item => item.Value is not null)
            .ToList();
        if (commands.Count == 0) throw new Exception($"FAIL: {page} exposes no commands at all");

        var wired = view.GetVisualDescendants().OfType<Button>()
            .Select(button => button.Command)
            .Concat(view.GetVisualDescendants().OfType<MenuItem>().Select(item => item.Command))
            .Where(command => command is not null)
            .ToHashSet();

        var unreachable = commands.Where(item => !wired.Contains(item.Value)).Select(item => item.Name).ToList();
        var missingReason = unreachable.Where(name => !exempt.ContainsKey(name)).ToList();
        if (missingReason.Count > 0)
        {
            throw new Exception($"FAIL: {page} commands without a UI entry and without a reason: " +
                string.Join(",", missingReason));
        }
    }

    private static Window Show(Control view)
    {
        var window = new Window { Width = 1280, Height = 820, Content = view };
        window.Show();
        Pump();
        return window;
    }

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

    /// <summary>最小假后端：只用于把页面渲染出来（不含任何服务/设备访问）。</summary>
    private sealed class StubConfigBackend : ConfigManager.IConfigInstancesBackend
    {
        public Task<IReadOnlyList<ConfigManager.ConfigInstanceInfo>> ListInstancesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConfigManager.ConfigInstanceInfo>>(
                new[] { new ConfigManager.ConfigInstanceInfo("demo-main", "en", "emulator-5554", "running") });

        public Task<ConfigManager.ConfigContent> ReadConfigAsync(string instance, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConfigManager.ConfigContent(instance, "rev-1", "{}"));

        public Task<string> CreateInstanceAsync(string name, string? source, string? importFile,
            CancellationToken cancellationToken = default) => Task.FromResult(name);

        public Task ImportConfigAsync(string name, string content, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<ConfigManager.ConfigImportSource>> ListImportsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConfigManager.ConfigImportSource>>(
                new[] { new ConfigManager.ConfigImportSource("imported", DateTimeOffset.UnixEpoch) });

        public Task DeleteInstanceAsync(string instance, string revision, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
