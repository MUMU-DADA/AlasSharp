namespace Alas.Campaign;

/// <summary>暴力寻路障的结果：需要先清掉哪些敌人才能让目标格可达。</summary>
public sealed record CampaignRoadblockSearch(
    IReadOnlyList<CampaignGrid> Roadblocks,
    bool AlreadyAccessible,
    bool Exhausted,
    int TriedCombinations)
{
    public bool Found => Roadblocks.Count > 0;
}

/// <summary>
/// 在成本场之上做"暴力找路障"的 C# 移植，逐条对应上游 <c>module/map/fleet.py</c> 的
/// <c>Fleet.brute_find_roadblocks(grid, fleet)</c>：
/// <list type="number">
///   <item>先按目标舰队算一次成本场：目标格可达就直接返回空（上游 <c>grid.is_accessible</c>）；</item>
///   <item>否则枚举敌人**可重复子集**（<c>itertools.product(enemies, repeat=r)</c>，r 从 1 到敌人数），
///         每次把该子集临时标成非敌人、重算成本场，看目标格是否变为可达；</item>
///   <item>第一个让目标可达的子集就是路障；全部枚举完仍不可达则记
///         <c>Enemy roadblock try exhausted.</c>。</item>
/// </list>
///
/// 与上游的两点差异（如实记录）：① 上游用临时改写 <c>grid.is_enemy</c> 再改回来，这里用不可变副本；
/// ② 上游没有枚举上限，这里保留 <paramref name="maxTries"/> 上限（默认 20 万）以防指数爆炸，
/// 触顶时 <see cref="CampaignRoadblockSearch.Exhausted"/> 为真并**明确报出**，不静默当成"没有路障"。
/// </summary>
public static class CampaignBruteFinder
{
    public const int DefaultMaxTries = 200_000;

    /// <param name="liveCost">
    /// 目标格在**地图现成成本场**里的代价（`CampaignGrid.Cost`）。上游 `brute_find_roadblocks` 的入口判断
    /// 用的就是地图上已有的 `grid.is_accessible`（即成本场里的值），而**不是**重算一遍——两者在
    /// "上一轮寻路之后地图状态又变了"的情况下会不同。传 null 时才回退到重算（旧行为）。
    /// </param>
    public static CampaignRoadblockSearch FindRoadblocks(IReadOnlyList<CampaignGrid> grids, string target,
                                                         string fleetStart, bool hasAmbush,
                                                         int maxTries = DefaultMaxTries,
                                                         int? liveCost = null)
    {
        if (liveCost is { } cost)
        {
            if (cost < CampaignPathfinder.Unreachable)
            {
                return new CampaignRoadblockSearch([], AlreadyAccessible: true, Exhausted: false, 0);
            }
        }
        else
        {
            var initial = CampaignPathfinder.FindPathInitial(grids, fleetStart, hasAmbush, hasEnemy: true);
            if (initial.CostOf(target) < CampaignPathfinder.Unreachable)
            {
                return new CampaignRoadblockSearch([], AlreadyAccessible: true, Exhausted: false, 0);
            }
        }

        var enemies = grids.Where(grid => grid.IsEnemy).ToArray();
        int tried = 0;
        for (int repeat = 1; repeat <= enemies.Length; repeat++)
        {
            foreach (var subset in Product(enemies, repeat))
            {
                if (++tried > maxTries)
                {
                    return new CampaignRoadblockSearch([], AlreadyAccessible: false, Exhausted: true, tried);
                }
                var blocked = subset.Select(grid => grid.Location).ToHashSet(StringComparer.Ordinal);
                var candidate = grids
                    .Select(grid => blocked.Contains(grid.Location) ? grid with { IsEnemy = false } : grid)
                    .ToArray();
                var field = CampaignPathfinder.FindPathInitial(candidate, fleetStart, hasAmbush, hasEnemy: true);
                if (field.CostOf(target) < CampaignPathfinder.Unreachable)
                {
                    return new CampaignRoadblockSearch(subset.ToArray(), false, false, tried);
                }
            }
        }

        return new CampaignRoadblockSearch([], false, Exhausted: true, tried);
    }

    /// <summary>上游 <c>itertools.product(objs, repeat=r)</c>：长度 r 的有序可重复组合。</summary>
    private static IEnumerable<CampaignGrid[]> Product(CampaignGrid[] objs, int repeat)
    {
        if (repeat <= 0) yield break;
        var indexes = new int[repeat];
        while (true)
        {
            var current = new CampaignGrid[repeat];
            for (int i = 0; i < repeat; i++) current[i] = objs[indexes[i]];
            yield return current;

            int position = repeat - 1;
            while (position >= 0)
            {
                indexes[position]++;
                if (indexes[position] < objs.Length) break;
                indexes[position] = 0;
                position--;
            }
            if (position < 0) yield break;
        }
    }
}
