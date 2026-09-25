namespace Alas.Campaign;

/// <summary>
/// 一次成本场计算的结果：每个格子的 <c>cost</c> 与 <c>connection</c>（上游 <c>GridInfo.cost</c> /
/// <c>GridInfo.connection</c>），逐项对应上游 <c>CampaignMap.find_path_initial()</c>。
/// </summary>
public sealed record CampaignCostField(
    IReadOnlyDictionary<string, int> Costs,
    IReadOnlyDictionary<string, string?> Connections)
{
    public int CostOf(string location) => Costs.TryGetValue(location, out int cost) ? cost : 9999;

    public string? ConnectionOf(string location) =>
        Connections.TryGetValue(location, out string? connection) ? connection : null;
}

/// <summary>
/// 寻路的 C# 移植，逐条对应上游 <c>module/map/map_base.py</c>：
/// <list type="bullet">
///   <item><c>grid_connection_initial()</c>：四邻接（上下左右），只连形状范围内的格子；</item>
///   <item><c>find_path_initial(location, has_ambush, has_enemy)</c>：以起点 cost=0 做最小成本扩散，
///         进入格子的代价是 <c>1</c>（或 <c>may_ambush</c> 时的 <c>ambush_cost</c>），
///         陆地与机关阻挡不可进入，**非海域格子不继续扩散**（除非 <c>has_enemy=False</c>），
///         等代价时若横向相邻（x 差 1）则改写 connection（上游的确定性 tie-break）；</item>
///   <item><c>_find_path(location)</c>：沿 connection 回溯出路线。</item>
/// </list>
///
/// `may_ambush` 按上游 <c>GridInfo.decode()</c> 的规则：令牌不是 <c>ME/MB/MM/MA</c> 之一就为真。
/// 只做地图上的算术，**不连设备、不读识别结果**。
///
/// **一处有意的偏离（已文档化）**：上游用"前沿不再增长就停"（`len(new) == len(visited)`）作为终止条件，
/// 而它的 `visited` / `grid_connection` 都是 `set`（对象身份哈希）→ 收敛到哪个不动点取决于迭代序，
/// 会出现"某个格的 cost 比它的 connection 链还贵"这种自身不自洽（实测：`campaign_13_2` 上
/// G4=42 而链路只值 33）。本实现改成**relax 到不动点**：每轮都对所有已发现格 relax，
/// 直到"没有新的更小代价、也没有新格子"才停。代价规则与 tie-break 与上游完全一致，
/// 差别只在"更充分"——因此 C# 的 cost 场恒为真正的最短距离，且与自己的 connection 链自洽。
/// 全库对拍（1370 关 × 3 配置 / 275,934 格）显示：无伏击配置逐格完全相同，有伏击配置的差异
/// 全部是"上游偏大"这一方向。
/// </summary>
public static class CampaignPathfinder
{
    /// <summary>上游的不可达代价常量。</summary>
    public const int Unreachable = 9999;

    /// <summary>上游 <c>find_path_initial()</c>：从起点算出全图成本场。</summary>
    public static CampaignCostField FindPathInitial(IReadOnlyList<CampaignGrid> grids, string start,
                                                    bool hasAmbush = true, bool hasEnemy = true)
    {
        var byLocation = grids.ToDictionary(grid => grid.Location, StringComparer.Ordinal);
        if (!byLocation.ContainsKey(start))
        {
            throw new NotSupportedException($"起点 {start} 不在格子集合里");
        }
        int ambushCost = hasAmbush ? 10 : 1;

        var costs = grids.ToDictionary(grid => grid.Location, _ => Unreachable, StringComparer.Ordinal);
        var connections = grids.ToDictionary(grid => grid.Location, _ => (string?)null, StringComparer.Ordinal);
        costs[start] = 0;
        var visited = new HashSet<string>(StringComparer.Ordinal) { start };

        while (true)
        {
            var next = new HashSet<string>(visited, StringComparer.Ordinal);
            bool improved = false;
            foreach (string location in visited)
            {
                foreach (string neighbour in Neighbours(location, byLocation))
                {
                    var grid = byLocation[neighbour];
                    if (grid.IsLand || grid.IsMechanismBlock) continue;
                    int cost = (grid.MayAmbush ? ambushCost : 1) + costs[location];
                    if (cost < costs[neighbour])
                    {
                        costs[neighbour] = cost;
                        connections[neighbour] = location;
                        improved = true;
                    }
                    else if (cost == costs[neighbour]
                             && Math.Abs(Column(neighbour) - Column(location)) == 1)
                    {
                        connections[neighbour] = location;
                    }
                    if (grid.IsSea || !hasEnemy) next.Add(neighbour);
                }
            }
            // **relax 到不动点**：只有"没有更小代价、也没有新格子"才停。
            // 上游只判断"前沿不再增长"，因此可能在代价还没传播完时就停（见类注释的偏离说明）。
            // tie-break 只改 connection 不改代价，不会让这个循环打转。
            if (!improved && next.Count == visited.Count) break;
            visited = next;
        }

        return new CampaignCostField(costs, connections);
    }

    /// <summary>上游 <c>_find_path(location)</c>：沿 connection 回溯出路线；到不了返回空。</summary>
    public static IReadOnlyList<string> FindPath(CampaignCostField field, string destination, int maxLength = 30)
    {
        if (field.CostOf(destination) == 0) return [destination];
        if (field.ConnectionOf(destination) is null) return [];

        var route = new List<string> { destination };
        string? current = field.ConnectionOf(destination);
        while (current is not null)
        {
            route.Add(current);
            if (route.Count > maxLength)
            {
                // 上游只记 warning（Route too long）并继续，这里保留同样行为但避免无界增长
                if (route.Count > maxLength * 4) break;
            }
            current = field.ConnectionOf(current);
        }
        route.Reverse();
        return route;
    }

    /// <summary>上游四邻接（`[(0,-1),(0,1),(-1,0),(1,0)]`），只连存在于形状内的格子。</summary>
    private static IEnumerable<string> Neighbours(string location, IReadOnlyDictionary<string, CampaignGrid> byLocation)
    {
        var (x, y) = CampaignLocations.ToCoordinates(location);
        foreach (var (dx, dy) in new[] { (0, -1), (0, 1), (-1, 0), (1, 0) })
        {
            // 越界（列 < A 或行 < 1）不是邻居——上游用 `if arr in total` 天然过滤掉。
            if (!CampaignLocations.TryToNode(x + dx, y + dy, out string candidate)) continue;
            if (byLocation.ContainsKey(candidate)) yield return candidate;
        }
    }

    private static int Column(string location) => CampaignLocations.ToCoordinates(location).X;
}
