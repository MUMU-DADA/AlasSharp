// 由 Assets/Catalog/generate-task-catalog.mjs 依据上游静态目录生成，请勿手改。
// 来源：上游 AzurPilot f67259dcd 的 module/config/argument/menu.json 与
//       module/config/i18n/zh-CN.json（Menu.<组>.name / Task.<任务>.name）。

namespace Alas.UI.ViewModels;

/// <summary>侧栏任务分组的静态目录：分组顺序与任务顺序与上游 menu.json 一致。</summary>
internal static class TaskCatalog
{
    public static IReadOnlyList<(string Group, string GroupLabel, string Icon, string[] Tasks, string[] TaskLabels)> Groups { get; } =
        new (string, string, string, string[], string[])[]
        {
            ("Alas", "系统", "Settings2",
                new[] { "Alas", "General", "Restart" },
                new[] { "系统设置", "通用设置", "重启设置" }),
            ("Farm", "出击Plus", "Swords",
                new[] { "Main", "Main2", "Main3", "GemsFarming", "ThreeOilLowCost", "Ambush11" },
                new[] { "主线图-1Plus", "主线图-2Plus", "主线图-3Plus", "紧急委托Plus", "三油低耗Plus", "1-1刷伏击(尚不完善)" }),
            ("Event", "活动Plus", "Sparkles",
                new[] { "EventGeneral", "Event", "Event2", "Event3", "Raid", "RaidScuttle", "Hospital", "Coalition", "CoalitionScuttle", "MaritimeEscort", "EventShop", "WarArchives" },
                new[] { "活动通用设置", "活动图-1Plus", "活动图-2Plus", "活动图-3Plus", "共斗活动Plus", "共斗沉船Plus", "深谷来信", "夜勤病栋", "联盟沉船", "商路护航", "活动商店", "作战档案" }),
            ("EventDaily", "活动-每日任务", "CalendarDays",
                new[] { "EventA", "EventB", "EventC", "EventD", "EventSp", "RaidDaily", "CoalitionSp" },
                new[] { "每日A图", "每日B图", "每日C图", "每日D图", "每日SP图", "共斗活动每日", "怪谈纪实：逃离白夜山庄SP" }),
            ("Reward", "自动收获", "Gift",
                new[] { "Commission", "Tactical", "Research", "Dorm", "Meowfficer", "Guild", "Reward", "Awaken", "Secretary", "OperationHandover" },
                new[] { "委托", "战术学院", "科研", "后宅", "指挥喵", "大舰队", "收获", "认知觉醒", "秘书舰", "作战委托" }),
            ("DailyMission", "每日任务", "CalendarDays",
                new[] { "Daily", "Hard", "Exercise", "ShopFrequent", "ShopOnce", "Shipyard", "Gacha", "Freebies", "Minigame", "PrivateQuarters" },
                new[] { "每日任务", "主线-困难图", "演习", "军火商商店", "其他商店", "开发船坞", "每日抽卡", "白嫖奖励", "小游戏", "宿舍计划" }),
            ("Opsi", "大世界Plus", "Compass",
                new[] { "OpsiGeneral", "OpsiAshBeacon", "OpsiAshAssist", "OpsiExplore", "OpsiShop", "OpsiVoucher", "OpsiDaily", "OpsiObscure", "OpsiAbyssal", "OpsiArchive", "OpsiStronghold", "OpsiMonthBoss", "OpsiMeowfficerFarming", "OpsiHazard1Leveling", "OpsiScheduling", "OpsiPreventActionPointOverflow", "OpsiCrossMonth", "OpsiSimulator" },
                new[] { "通用设置", "META作战", "META支援", "每月开荒Plus", "大世界商店Plus", "白票商店", "大世界每日Plus", "隐秘海域", "深渊坐标", "档案坐标", "塞壬要塞", "月度Boss", "耄耋相接", "侵蚀1练级", "智能调度Plus", "防止行动力溢出", "跨月每日", "大世界模拟器 Alpha" }),
            ("Island", "赤石计划", "Palmtree",
                new[] { "IslandPlan", "IslandBusiness", "IslandFarm", "IslandRancher", "IslandMineForest", "IslandRestaurant", "IslandTeahouse", "IslandGrill", "IslandJuuEatery", "IslandJuuCoffee", "IslandManufacture", "IslandDailyGather", "IslandAirDrop", "IslandCargoPreparation", "IslandDailyOrder", "IslandDailyInteract", "IslandPearlSell" },
                new[] { "全局配置", "经营模块", "农田", "牧场", "矿山林场", "有鱼餐馆", "白熊饮品", "乌鱼烤肉", "啾啾简餐", "啾咖啡", "制造业", "每日采集", "每日补给", "货物筹备", "每日订单", "每日/周任务", "每周珍珠采购与售卖" }),
            ("FleetManagement", "舰队管理", "Ship",
                new[] { "FleetInfo" },
                new[] { "舰队信息" }),
            ("Tool", "工具Plus", "Wrench",
                new[] { "FleetScan", "Daemon", "OpsiDaemon", "EventStory", "BoxDisassemble", "AutoEquip", "Benchmark", "OcrBenchmark", "AzurLaneUncensored", "GameManager", "EmulatorManager", "MeowfficerScore" },
                new[] { "舰队扫描", "半自动点击", "大世界半自动", "活动剧情", "拆装备箱", "自动装备", "性能测试", "OCR性能测试", "反和谐", "游戏管理器(未完成)", "模拟器管理器", "指挥喵评分" }),
        };
}
