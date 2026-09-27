namespace Alas.UI.ViewModels;

/// <summary>
/// The task navigation is a projection of the runners registered by Alas.Engine.
/// It intentionally contains no upstream menu/config task names.  Queue input is
/// kept as JSON so the Engine remains the owner of task validation and semantics.
/// </summary>
internal static class EngineTaskCatalog
{
    public static IReadOnlyList<(string Group, string GroupLabel, string Icon, string[] Tasks, string[] TaskLabels)> Groups { get; } =
        new (string, string, string, string[], string[])[]
        {
            ("Navigation", "页面与导航", "Compass",
                ["observe", "navigate"], ["读取页面状态", "页面导航"]),
            ("Data", "数据任务", "Database",
                ["data_key"], ["领取资料密钥"]),
            ("Map", "地图观察", "Map",
                ["map_observe"], ["读取地图状态"]),
            ("Campaign", "战役队列", "Swords",
                ["campaign_stages", "campaign_select", "campaign_fleet_prepare", "campaign_run", "campaign_resume"],
                ["读取关卡", "选择关卡", "准备舰队", "执行战役", "继续战役"]),
        };

    public static bool Contains(string kind) => Groups.Any(group => group.Tasks.Contains(kind, StringComparer.Ordinal));

    public static string Label(string kind)
    {
        foreach (var (_, _, _, tasks, labels) in Groups)
        {
            int index = Array.IndexOf(tasks, kind);
            if (index >= 0) return labels[index];
        }
        return kind;
    }
}
