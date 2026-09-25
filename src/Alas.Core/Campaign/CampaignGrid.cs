namespace Alas.Campaign;

/// <summary>
/// 地图格子的 C# 侧模型：只保留**目标选择**需要的属性，逐项对应上游
/// `module/map_detection/grid_info.py` 的 <c>GridInfo</c>：
/// <list type="bullet">
///   <item><c>is_accessible = cost &lt; 9999</c>、<c>is_nearby = cost &lt; 20</c>（原样移植）；</item>
///   <item><c>str</c>：上游 <c>encode()</c> 的敌人分支 <c>"{enemy_scale}{enemy_genre 首字母大写或 E}"</c>
///         （如 <c>3L</c>/<c>2M</c>/<c>1E</c>）——敌方格子的编码是文本过滤器唯一匹配的属性；</item>
///   <item><c>enemy_scale</c>（0–3）、<c>enemy_genre</c>（Light/Main/Carrier/Treasure/Enemy 或空）。</item>
/// </list>
/// 非敌方格子的 <c>encode()</c>（<c>SP</c>/<c>ME</c>/<c>++</c>/<c>MY</c> 等）未移植：选择链路只对
/// <c>is_enemy=True</c> 的格子做文本过滤，移植面按实际需要收窄，不做多余猜测。
/// </summary>
public sealed record CampaignGrid(
    string Location,
    bool IsEnemy = false,
    bool IsBoss = false,
    bool IsSiren = false,
    bool IsMystery = false,
    bool IsAmmo = false,
    bool IsFortress = false,
    bool MayBoss = false,
    bool MayAmmo = false,
    bool IsCaughtBySiren = false,
    bool IsFleet = false,
    bool IsCleared = false,
    bool IsLand = false,
    int EnemyScale = 0,
    string? EnemyGenre = null,
    int Weight = 0,
    int Cost = 0,
    int Cost1 = 9999,
    int Cost2 = 9999)
{
    /// <summary>上游 <c>is_accessible = cost &lt; 9999</c>。</summary>
    public bool IsAccessible => Cost < 9999;

    /// <summary>上游 <c>is_accessible_1 = cost_1 &lt; 9999</c>。</summary>
    public bool IsAccessible1 => Cost1 < 9999;

    /// <summary>上游 <c>is_accessible_2 = cost_2 &lt; 9999</c>（第二舰队的可达性）。</summary>
    public bool IsAccessible2 => Cost2 < 9999;

    /// <summary>上游 <c>is_sea</c>：不是陆地、敌人、塞壬、要塞、boss 就是海。</summary>
    public bool IsSea => !(IsLand || IsEnemy || IsSiren || IsFortress || IsBoss);

    /// <summary>上游 <c>is_nearby = cost &lt; 20</c>。</summary>
    public bool IsNearby => Cost < 20;

    /// <summary>上游 <c>GridInfo.str</c> 的敌人分支（非敌方格子返回空串，见类型注释）。</summary>
    public string FilterKey => IsEnemy
        ? $"{EnemyScale}{(string.IsNullOrEmpty(EnemyGenre) ? "E" : char.ToUpperInvariant(EnemyGenre[0]).ToString())}"
        : "";
}

/// <summary>
/// 上游 <c>RoadGrids</c> 的 C# 侧模型：一条路段由若干 <b>block</b> 组成，每个 block 是一组格子
/// （`RoadGrids([B8])` 是单格 block，`RoadGrids([[H8, I8, J9]])` 是三格 block）。
/// 三个判定方法逐条对应上游 <c>module/map/map_grids.py</c> 的同名方法。
/// </summary>
public sealed class CampaignRoad
{
    public CampaignRoad(IEnumerable<IEnumerable<string>> blocks) =>
        Blocks = blocks.Select(block => (IReadOnlyList<string>)block.ToArray()).ToArray();

    /// <summary>每个 block 的格子位置（节点名，如 <c>B8</c>）。</summary>
    public IReadOnlyList<IReadOnlyList<string>> Blocks { get; }

    private IReadOnlyList<CampaignGrid> Block(IReadOnlyList<string> block, CampaignGridSet all) =>
        all.Grids.Where(grid => block.Contains(grid.Location, StringComparer.Ordinal)).ToArray();

    /// <summary>上游 <c>roadblocks()</c>：整块都是敌人时，整块算路障。</summary>
    public CampaignGridSet Roadblocks(CampaignGridSet all)
    {
        var grids = new List<CampaignGrid>();
        foreach (var block in Blocks)
        {
            var cells = Block(block, all);
            if (cells.Count > 0 && cells.Count == cells.Count(grid => grid.IsEnemy)) grids.AddRange(cells);
        }
        return new CampaignGridSet(grids);
    }

    /// <summary>上游 <c>potential_roadblocks()</c>：跳过含舰队或已清格子的块；只剩一个非敌人时，取该块的敌人。</summary>
    public CampaignGridSet PotentialRoadblocks(CampaignGridSet all)
    {
        var grids = new List<CampaignGrid>();
        foreach (var block in Blocks)
        {
            var cells = Block(block, all);
            if (cells.Count == 0) continue;
            if (cells.Any(grid => grid.IsFleet) || cells.Any(grid => grid.IsCleared)) continue;
            if (cells.Count - cells.Count(grid => grid.IsEnemy) == 1)
            {
                grids.AddRange(cells.Where(grid => grid.IsEnemy));
            }
        }
        return new CampaignGridSet(grids);
    }

    /// <summary>上游 <c>first_roadblocks()</c>：跳过含舰队或已清格子的块；块里有敌人就取敌人。</summary>
    public CampaignGridSet FirstRoadblocks(CampaignGridSet all)
    {
        var grids = new List<CampaignGrid>();
        foreach (var block in Blocks)
        {
            var cells = Block(block, all);
            if (cells.Count == 0) continue;
            if (cells.Any(grid => grid.IsFleet) || cells.Any(grid => grid.IsCleared)) continue;
            if (cells.Count(grid => grid.IsEnemy) >= 1) grids.AddRange(cells.Where(grid => grid.IsEnemy));
        }
        return new CampaignGridSet(grids);
    }
}

/// <summary>上游 <c>location2node()</c> 的命名约定：列字母（A=0）+ 行号（从 1 起）。</summary>
public static class CampaignLocations
{
    public static string ToNode(int x, int y)
    {
        if (x is < 0 or > 25) throw new NotSupportedException($"列 {x} 超出 A–Z（未移植多字母列名）");
        return $"{(char)('A' + x)}{y + 1}";
    }
}

/// <summary>
/// 上游 <c>SelectedGrids.select(**kwargs)</c> 的等价过滤条件：**字段相等即匹配**（含类型）。
/// 只登记选择链路会用到的属性，避免把整张 GridInfo 表搬过来。
/// </summary>
public sealed record CampaignGridFilter(
    bool? IsEnemy = null,
    bool? IsBoss = null,
    bool? IsSiren = null,
    bool? IsFortress = null,
    bool? IsMystery = null,
    bool? MayBoss = null,
    bool? MayAmmo = null,
    bool? IsCaughtBySiren = null,
    bool? IsFleet = null,
    bool? IsCleared = null,
    bool? IsLand = null,
    bool? IsSea = null,
    bool? IsAccessible = null,
    bool? IsAccessible1 = null,
    bool? IsAccessible2 = null,
    bool? IsNearby = null,
    int? EnemyScale = null,
    int? Cost2 = null,
    string? EnemyGenre = null)
{
    public bool Matches(CampaignGrid grid) =>
        (IsEnemy is null || grid.IsEnemy == IsEnemy) &&
        (IsBoss is null || grid.IsBoss == IsBoss) &&
        (IsSiren is null || grid.IsSiren == IsSiren) &&
        (IsFortress is null || grid.IsFortress == IsFortress) &&
        (IsMystery is null || grid.IsMystery == IsMystery) &&
        (MayBoss is null || grid.MayBoss == MayBoss) &&
        (MayAmmo is null || grid.MayAmmo == MayAmmo) &&
        (IsCaughtBySiren is null || grid.IsCaughtBySiren == IsCaughtBySiren) &&
        (IsFleet is null || grid.IsFleet == IsFleet) &&
        (IsCleared is null || grid.IsCleared == IsCleared) &&
        (IsLand is null || grid.IsLand == IsLand) &&
        (IsSea is null || grid.IsSea == IsSea) &&
        (IsAccessible is null || grid.IsAccessible == IsAccessible) &&
        (IsAccessible1 is null || grid.IsAccessible1 == IsAccessible1) &&
        (IsAccessible2 is null || grid.IsAccessible2 == IsAccessible2) &&
        (IsNearby is null || grid.IsNearby == IsNearby) &&
        (EnemyScale is null || grid.EnemyScale == EnemyScale) &&
        (Cost2 is null || grid.Cost2 == Cost2) &&
        (EnemyGenre is null || string.Equals(grid.EnemyGenre, EnemyGenre, StringComparison.Ordinal));
}

/// <summary>
/// 上游 <c>SelectedGrids</c> 的最小移植：<c>select</c> / <c>delete</c> / <c>add</c> / <c>sort</c> / 索引。
///
/// 与上游的差异（如实记录）：<c>add</c> 在上游用 <c>set()</c> 去重，顺序不确定；这里按**首次出现顺序**去重，
/// 结果确定。选择链路在 <c>add</c> 之后都会重新 <c>sort</c>，因此最终顺序与上游一致。
/// </summary>
public sealed class CampaignGridSet
{
    private readonly List<CampaignGrid> _grids;

    public CampaignGridSet(IEnumerable<CampaignGrid> grids) => _grids = [.. grids];

    public static CampaignGridSet Empty { get; } = new([]);

    public IReadOnlyList<CampaignGrid> Grids => _grids;

    public int Count => _grids.Count;

    public bool IsEmpty => _grids.Count == 0;

    public CampaignGrid this[int index] => _grids[index];

    /// <summary>上游 <c>select(**kwargs)</c>：字段全等即保留。</summary>
    public CampaignGridSet Select(CampaignGridFilter filter) =>
        new(_grids.Where(filter.Matches));

    /// <summary>上游 <c>delete(grids=...)</c>：按位置（等价于格子身份）删除。</summary>
    public CampaignGridSet Delete(CampaignGridSet? ignore)
    {
        if (ignore is null || ignore.IsEmpty) return this;
        var removed = ignore.Grids.Select(grid => grid.Location).ToHashSet(StringComparer.Ordinal);
        return new CampaignGridSet(_grids.Where(grid => !removed.Contains(grid.Location)));
    }

    /// <summary>上游 <c>add(grids)</c>：并集，按位置去重（顺序取首次出现，见类型注释）。</summary>
    public CampaignGridSet Add(CampaignGridSet other)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<CampaignGrid>(_grids.Count + other.Count);
        foreach (var grid in _grids.Concat(other.Grids))
        {
            if (seen.Add(grid.Location)) result.Add(grid);
        }
        return new CampaignGridSet(result);
    }

    /// <summary>
    /// 上游 <c>sort(*args)</c>：按属性升序（<c>attrgetter</c> 的元组键）。已移植 <c>weight</c> / <c>cost</c> /
    /// <c>cost_2</c>；遇到其它排序键直接报错，不静默用错语义。
    /// </summary>
    public CampaignGridSet Sort(params string[] attributes)
    {
        if (attributes.Length == 0 || _grids.Count == 0) return this;
        foreach (string attribute in attributes)
        {
            if (attribute is not ("weight" or "cost" or "cost_1" or "cost_2"))
                throw new NotSupportedException($"排序键 {attribute} 尚未移植（当前支持 weight/cost/cost_1/cost_2）");
        }
        IOrderedEnumerable<CampaignGrid>? ordered = null;
        foreach (string attribute in attributes)
        {
            Func<CampaignGrid, int> key = attribute switch
            {
                "weight" => grid => grid.Weight,
                "cost" => grid => grid.Cost,
                "cost_1" => grid => grid.Cost1,
                _ => grid => grid.Cost2,
            };
            ordered = ordered is null ? _grids.OrderBy(key) : ordered.ThenBy(key);
        }
        return new CampaignGridSet(ordered!);
    }
}
