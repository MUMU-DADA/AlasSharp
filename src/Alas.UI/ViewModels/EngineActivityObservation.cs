using System.Globalization;
using System.Text.Json.Nodes;

namespace Alas.UI.ViewModels;

/// <summary>Display Engine activity observations verbatim; this model never schedules tasks.</summary>
public static class EngineActivityObservation
{
    public static IReadOnlyList<RailTaskViewModel> Tasks(JsonObject? snapshot, bool running)
    {
        var result = new List<RailTaskViewModel>();
        bool engineActivity = Text(snapshot?["source"]) == "engine-queue";
        string? current = running && Text(snapshot?["phase"]) == "running"
            ? Text(snapshot?["task"]) : null;
        if (!string.IsNullOrEmpty(current))
            result.Add(Task(current, Text(snapshot?["next_run"]), "running", engineActivity));
        if (snapshot?["tasks"] is JsonArray engineTasks)
            foreach (var item in engineTasks.OfType<JsonObject>())
            {
                string? name = Text(item["kind"]);
                string? state = Text(item["state"]);
                if (name is null || name == current || state is null || state == "completed") continue;
                result.Add(Task(name, Text(item["next_run"]), state switch
                {
                    "failed" => "failed",
                    "waiting" => "waiting",
                    "running" => "running",
                    _ => "pending",
                }, engineActivity));
            }
        foreach (string state in new[] { "pending", "waiting" })
            if (snapshot?[state] is JsonArray tasks)
                foreach (var item in tasks.OfType<JsonObject>())
                    if (Text(item["name"]) is { Length: > 0 } name && name != current)
                        result.Add(Task(name, Text(item["next_run"]), state, engineActivity));
        return result;
    }

    private static RailTaskViewModel Task(string key, string? nextRun, string state, bool engineActivity)
    {
        string name = engineActivity ? EngineTaskLabels.GetValueOrDefault(key, key) : LegacyTaskLabel(key);
        return new RailTaskViewModel(name, nextRun ?? "", state,
            state == "running" ? "运行中" : state == "pending" ? "待运行" : state == "failed" ? "失败" : "等待中");
    }

    private static string LegacyTaskLabel(string key)
    {
        foreach (var (_, _, _, tasks, labels) in EngineTaskCatalog.Groups)
        {
            int index = Array.IndexOf(tasks, key);
            if (index >= 0) return labels[index];
        }
        return key;
    }

    private static readonly IReadOnlyDictionary<string, string> EngineTaskLabels =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["observe"] = "读取页面状态", ["navigate"] = "页面导航",
            ["data_key"] = "领取数据", ["map_observe"] = "读取地图状态",
            ["campaign_stages"] = "读取关卡", ["campaign_select"] = "选择关卡",
            ["campaign_fleet_prepare"] = "准备舰队", ["campaign_run"] = "执行战役",
            ["campaign_resume"] = "继续战役"
        };

    // Labels and image mapping follow ResourceCards.tsx; unknown keys remain visible.
    private static readonly Dictionary<string, (string Label, string? Image)> ResourceNames = new()
    {
        ["Oil"] = ("石油", "oil"), ["Coin"] = ("物资", "gold"),
        ["Gem"] = ("钻石", "diamond"), ["Cube"] = ("心智魔方", "cube"),
        ["Pt"] = ("活动 PT", "pt"), ["ActionPoint"] = ("行动力", "guild_coin"),
        ["YellowCoin"] = ("作战补给凭证", "supply_token"), ["PurpleCoin"] = ("特别兑换凭证", "special_token"),
        ["Core"] = ("核心数据", "core_data"), ["Medal"] = ("荣誉勋章", "honor_medal"),
        ["Merit"] = ("功勋", "merit"), ["GuildCoin"] = ("舰队币", "stamina"),
        ["Chip"] = ("心智单元", null),
    };

    public static IReadOnlyList<ResourceCardViewModel> Resources(JsonObject? snapshot, IReadOnlyList<string> selected)
    {
        var available = snapshot?["resources"] is JsonArray resources ? resources.OfType<JsonObject>().ToArray() : [];
        return selected.Select((key, index) =>
        {
            var item = available.FirstOrDefault(value => Text(value["name"]) == key);
            var record = Text(item?["record"]);
            bool recorded = !string.IsNullOrEmpty(record) && !record.StartsWith("2020-01-01", StringComparison.Ordinal);
            var (label, icon) = ResourceNames.GetValueOrDefault(key, (Text(item?["label"]) ?? key, null));
            double? value = Number(item?["value"]), limit = Number(item?["limit"]), total = Number(item?["total"]);
            string? suffix = recorded && limit > 0 ? "/ " + Format(limit.Value)
                : recorded && key == "ActionPoint" && total > value ? "/ 总行动力 " + Format(total.Value) : null;
            string display = recorded && value.HasValue ? Format(value.Value) : "—";
            string time = (record ?? "").Replace('T', ' ');
            string foot = recorded ? "记录于 " + (time.Length >= 19 ? time[5..19] : time) : "等待游戏内资源同步";
            return new ResourceCardViewModel(label, icon is null ? null : "Resources/" + icon, display, suffix, foot, index);
        }).ToArray();
    }

    private static string Format(double value) => value.ToString("N0", CultureInfo.CurrentCulture);
    private static string? Text(JsonNode? value) => value is JsonValue text && text.TryGetValue<string>(out var result) ? result : null;
    private static double? Number(JsonNode? value) => value is JsonValue number && number.TryGetValue<double>(out var result)
        && double.IsFinite(result) ? result : null;
}
