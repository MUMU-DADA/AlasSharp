using Alas.MapDetection;

namespace Alas.Campaign;

/// <summary>
/// 「识别结果 → 引擎状态」适配器：把**关卡声明的静态地图**与**运行期识别结果**合成引擎要吃的
/// <see cref="CampaignGrid"/> 集合。
///
/// 为什么单独一层：最终引擎需要"只依赖上游静态规则"，同时又要吃真机识别；这两件事的交界就是这里。
/// 上游对应的是 `CampaignMap.map_data`（声明）+ `GridInfo.decode()`（静态标志）+
/// 地图识别写回的运行期标志（is_enemy / is_siren / is_fleet …）+ `find_path_initial_multi_fleet`
/// 算出的 `cost` / `cost_1` / `cost_2`。
///
/// 边界（如实记录）：
/// <list type="bullet">
///   <item>识别只覆盖**它认得的格子**；没认到的格子保持声明状态（不猜成"没有敌人"）；</item>
///   <item>识别给出的标志名若不认识，收进 <c>unknownFlags</c> 由调用方报出，不静默丢弃、也不猜语义；</item>
///   <item>成本场按上游 `find_path_initial_multi_fleet` 的顺序算：**非当前舰队先算、当前舰队最后算**，
///         因此 `cost` 最终是当前舰队的成本，`cost_1` / `cost_2` 各留一份。</item>
/// </list>
/// </summary>
public static class CampaignMapState
{
    /// <summary>识别结果里认得、并会写进引擎状态的运行期标志。</summary>
    private static readonly HashSet<string> KnownFlags = new(StringComparer.Ordinal)
    {
        "is_enemy", "is_boss", "is_siren", "is_fortress", "is_mystery", "is_ammo",
        "is_fleet", "is_submarine", "is_cleared", "is_caught_by_siren",
        "may_bouncing_enemy", "is_mechanism_block", "is_spawn_point",
        "is_submarine_spawn_point",
        "is_current_fleet", "is_missile_attack",
        "may_enemy", "may_boss", "may_mystery", "may_ammo", "may_siren", "may_ambush",
        "is_flare", "is_land", "is_mechanism_trigger",
    };

    /// <summary>
    /// 从关卡计划构造引擎状态：声明令牌 → 格子；两支舰队所在格标 <c>is_fleet</c>；
    /// 再按上游多舰队顺序算 <c>cost</c> / <c>cost_1</c> / <c>cost_2</c>。
    /// </summary>
    public static IReadOnlyList<CampaignGrid> FromPlan(CampaignPlan plan, string? fleet1Location = null,
                                                      string? fleet2Location = null, bool hasAmbush = false,
                                                      int currentFleet = 1)
    {
        var grids = CampaignGridTokens.FromText(plan.Map?.MapData?.GetValue<string>()).ToList();
        if (grids.Count == 0) return grids;

        var byLocation = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int index = 0; index < grids.Count; index++) byLocation[grids[index].Location] = index;

        if (!string.IsNullOrEmpty(fleet1Location) && byLocation.TryGetValue(fleet1Location, out int one)
            && !grids[one].IsFleet)
        {
            grids[one] = grids[one] with { IsFleet = true };
        }
        if (!string.IsNullOrEmpty(fleet2Location) && byLocation.TryGetValue(fleet2Location, out int two)
            && !grids[two].IsFleet)
        {
            grids[two] = grids[two] with { IsFleet = true };
        }

        // 上游：sorted(key=(location == current,)) → 非当前舰队先算，当前舰队最后算（cost 留当前舰队的）。
        var order = new List<(int Fleet, string Location)>();
        if (!string.IsNullOrEmpty(fleet2Location)) order.Add((2, fleet2Location));
        if (!string.IsNullOrEmpty(fleet1Location)) order.Add((1, fleet1Location));
        order = order.OrderBy(item => item.Fleet == currentFleet ? 1 : 0).ToList();

        foreach (var (fleet, location) in order)
        {
            if (byLocation.GetValueOrDefault(location, -1) < 0) continue;
            var field = CampaignPathfinder.FindPathInitial(grids, location, hasAmbush, hasEnemy: true);
            for (int index = 0; index < grids.Count; index++)
            {
                int cost = field.Costs.GetValueOrDefault(grids[index].Location, CampaignPathfinder.Unreachable);
                grids[index] = fleet switch
                {
                    1 => grids[index] with { Cost1 = cost, Cost = cost },
                    _ => grids[index] with { Cost2 = cost, Cost = cost },
                };
            }
        }
        return grids;
    }

    /// <summary>
    /// 把**上游地图的实时状态**（`s3_campaign_grids` 的返回）解析成 C# 的格子模型。
    /// 每项形如 <c>{"loca": [x, y], "flags": [...], "cost": n, "cost_1": n, "cost_2": n, "weight": n,
    /// "enemy_scale": n, "enemy_genre": "…"}</c>。标志走与识别叠加**同一张表**（未知标志收进
    /// <paramref name="unknownFlags"/>，不静默丢）；成本场直接用上游的值，不重算。
    /// 坐标、标志、成本、权重与敌人字段都是必填项；缺项、类型错误或重复坐标均拒绝整份响应。
    /// </summary>
    public static IReadOnlyList<CampaignGrid> FromUpstream(System.Text.Json.Nodes.JsonNode? payload,
                                                           out IReadOnlyList<string> unknownFlags)
    {
        var unknown = new SortedSet<string>(StringComparer.Ordinal);
        var grids = new List<CampaignGrid>();
        if (payload is not System.Text.Json.Nodes.JsonObject root
            || root["grids"] is not System.Text.Json.Nodes.JsonArray items)
        {
            throw new InvalidDataException("上游地图响应缺少 grids 数组");
        }

        var flagMap = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var values = new Dictionary<string, (int Cost, int Cost1, int Cost2, int Weight, int Scale, string? Genre)>(
            StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item is not System.Text.Json.Nodes.JsonObject entry
                || entry["loca"] is not System.Text.Json.Nodes.JsonArray loca || loca.Count != 2)
            {
                throw new InvalidDataException("上游地图格子缺少二维坐标");
            }
            int x = ReadRequiredInt(loca[0], "loca[0]");
            int y = ReadRequiredInt(loca[1], "loca[1]");
            if (!CampaignLocations.TryToNode(x, y, out string node) || y == int.MaxValue)
                throw new InvalidDataException("上游地图含非法坐标");
            if (values.ContainsKey(node)) throw new InvalidDataException($"上游地图含重复坐标 {node}");
            if (!entry.ContainsKey("flags") || entry["flags"] is not System.Text.Json.Nodes.JsonArray list)
                throw new InvalidDataException($"上游地图格子 {node} 缺少 flags 数组");
            if (!entry.ContainsKey("cost") || !entry.ContainsKey("cost_1") || !entry.ContainsKey("cost_2")
                || !entry.ContainsKey("weight") || !entry.ContainsKey("enemy_scale")
                || !entry.ContainsKey("enemy_genre"))
                throw new InvalidDataException($"上游地图格子 {node} 缺少成本、权重或敌人字段");
            var flags = new List<string>();
            foreach (var flag in list)
            {
                if (flag is not System.Text.Json.Nodes.JsonValue flagValue
                    || !flagValue.TryGetValue<string>(out string? text) || string.IsNullOrEmpty(text))
                    throw new InvalidDataException($"上游地图格子 {node} 的 flags 含非字符串或空值");
                flags.Add(text);
            }
            flagMap[$"{x},{y}"] = flags;
            string? genre = null;
            if (entry["enemy_genre"] is { } genreNode
                && (genreNode is not System.Text.Json.Nodes.JsonValue genreValue
                    || !genreValue.TryGetValue<string>(out genre)))
                throw new InvalidDataException($"上游地图格子 {node} 的 enemy_genre 不是字符串或 null");
            values[node] = (
                ReadRequiredInt(entry["cost"], $"{node}.cost"),
                ReadRequiredInt(entry["cost_1"], $"{node}.cost_1"),
                ReadRequiredInt(entry["cost_2"], $"{node}.cost_2"),
                ReadRequiredInt(entry["weight"], $"{node}.weight"),
                ReadRequiredInt(entry["enemy_scale"], $"{node}.enemy_scale"),
                genre);
        }

        var baseGrids = values.Keys.Select(node => new CampaignGrid(node)).ToArray();
        var overlaid = OverlayDetection(baseGrids, flagMap, out var overlayUnknown);
        foreach (string flag in overlayUnknown) unknown.Add(flag);
        foreach (var grid in overlaid)
        {
            var extra = values[grid.Location];
            grids.Add(grid with
            {
                Cost = extra.Cost,
                Cost1 = extra.Cost1,
                Cost2 = extra.Cost2,
                Weight = extra.Weight,
                EnemyScale = extra.Scale,
                EnemyGenre = extra.Genre,
            });
        }
        unknownFlags = unknown.ToArray();
        return grids;
    }

    private static int ReadRequiredInt(System.Text.Json.Nodes.JsonNode? node, string name) =>
        node is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<int>(out int result)
            ? result
            : throw new InvalidDataException($"上游地图字段 {name} 不是 int32 整数");

    /// <summary>
    /// 叠加地图识别的运行期标志（`MapDetectResult.GridFlags` 的形态：`"x,y"` → 标志名列表）。
    /// 认不出的标志名收进 <paramref name="unknownFlags"/>；认不出的格子坐标直接忽略。
    /// </summary>
    public static IReadOnlyList<CampaignGrid> OverlayDetection(IReadOnlyList<CampaignGrid> grids,
                                                              IReadOnlyDictionary<string, List<string>>? gridFlags,
                                                              out IReadOnlyList<string> unknownFlags)
    {
        var unknown = new SortedSet<string>(StringComparer.Ordinal);
        if (gridFlags is null || gridFlags.Count == 0)
        {
            unknownFlags = [];
            return grids;
        }

        var updated = grids.ToArray();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < updated.Length; i++) index[updated[i].Location] = i;

        foreach (var (key, flags) in gridFlags)
        {
            string[] parts = key.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !int.TryParse(parts[0], out int x) || !int.TryParse(parts[1], out int y))
            {
                continue;
            }
            if (!CampaignLocations.TryToNode(x, y, out string location) || !index.TryGetValue(location, out int at))
            {
                continue;
            }
            var grid = updated[at];
            foreach (string flag in flags)
            {
                if (!KnownFlags.Contains(flag))
                {
                    unknown.Add(flag);
                    continue;
                }
                grid = flag switch
                {
                    "is_enemy" => grid with { IsEnemy = true },
                    "is_land" => grid with { IsLand = true },
                    "is_flare" => grid with { IsFlare = true },
                    "is_mechanism_trigger" => grid with { IsMechanismTrigger = true },
                    "may_enemy" => grid with { MayEnemy = true },
                    "may_boss" => grid with { MayBoss = true },
                    "may_mystery" => grid with { MayMystery = true },
                    "may_ammo" => grid with { MayAmmo = true },
                    "may_siren" => grid with { MaySiren = true },
                    "may_ambush" => grid with { MayAmbush = true },
                    "is_boss" => grid with { IsBoss = true },
                    "is_siren" => grid with { IsSiren = true },
                    "is_fortress" => grid with { IsFortress = true },
                    "is_mystery" => grid with { IsMystery = true },
                    "is_ammo" => grid with { IsAmmo = true },
                    "is_fleet" => grid with { IsFleet = true },
                    "is_cleared" => grid with { IsCleared = true },
                    "is_caught_by_siren" => grid with { IsCaughtBySiren = true },
                    "may_bouncing_enemy" => grid with { MayBouncingEnemy = true },
                    "is_mechanism_block" => grid with { IsMechanismBlock = true },
                    "is_spawn_point" => grid with { IsSpawnPoint = true },
                    "is_submarine_spawn_point" => grid with { IsSubmarineSpawnPoint = true },
                    // 这三个只影响 `encode()`（`Filter` 用的 `grid.str`）与识别展示，
                    // 不影响寻路/选择判定；但既然上游 encode 有分支，识别给了就照实写下来
                    "is_current_fleet" => grid with { IsCurrentFleet = true },
                    "is_submarine" => grid with { IsSubmarine = true },
                    "is_missile_attack" => grid with { IsMissileAttack = true },
                    _ => grid,
                };
            }
            updated[at] = grid;
        }

        unknownFlags = unknown.ToArray();
        return updated;
    }
}
