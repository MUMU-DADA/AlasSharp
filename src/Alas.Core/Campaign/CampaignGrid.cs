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
    bool IsCaughtBySiren = false,
    int EnemyScale = 0,
    string? EnemyGenre = null,
    int Weight = 0,
    int Cost = 0,
    int Cost2 = 9999)
{
    /// <summary>上游 <c>is_accessible = cost &lt; 9999</c>。</summary>
    public bool IsAccessible => Cost < 9999;

    /// <summary>上游 <c>is_accessible_2 = cost_2 &lt; 9999</c>（第二舰队的可达性）。</summary>
    public bool IsAccessible2 => Cost2 < 9999;

    /// <summary>上游 <c>is_nearby = cost &lt; 20</c>。</summary>
    public bool IsNearby => Cost < 20;

    /// <summary>上游 <c>GridInfo.str</c> 的敌人分支（非敌方格子返回空串，见类型注释）。</summary>
    public string FilterKey => IsEnemy
        ? $"{EnemyScale}{(string.IsNullOrEmpty(EnemyGenre) ? "E" : char.ToUpperInvariant(EnemyGenre[0]).ToString())}"
        : "";
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
    bool? IsCaughtBySiren = null,
    bool? IsAccessible = null,
    bool? IsNearby = null,
    int? EnemyScale = null,
    string? EnemyGenre = null)
{
    public bool Matches(CampaignGrid grid) =>
        (IsEnemy is null || grid.IsEnemy == IsEnemy) &&
        (IsBoss is null || grid.IsBoss == IsBoss) &&
        (IsSiren is null || grid.IsSiren == IsSiren) &&
        (IsFortress is null || grid.IsFortress == IsFortress) &&
        (IsMystery is null || grid.IsMystery == IsMystery) &&
        (MayBoss is null || grid.MayBoss == MayBoss) &&
        (IsCaughtBySiren is null || grid.IsCaughtBySiren == IsCaughtBySiren) &&
        (IsAccessible is null || grid.IsAccessible == IsAccessible) &&
        (IsNearby is null || grid.IsNearby == IsNearby) &&
        (EnemyScale is null || grid.EnemyScale == EnemyScale) &&
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
            if (attribute is not ("weight" or "cost" or "cost_2"))
                throw new NotSupportedException($"排序键 {attribute} 尚未移植（当前支持 weight/cost/cost_2）");
        }
        IOrderedEnumerable<CampaignGrid>? ordered = null;
        foreach (string attribute in attributes)
        {
            Func<CampaignGrid, int> key = attribute switch
            {
                "weight" => grid => grid.Weight,
                "cost" => grid => grid.Cost,
                _ => grid => grid.Cost2,
            };
            ordered = ordered is null ? _grids.OrderBy(key) : ordered.ThenBy(key);
        }
        return new CampaignGridSet(ordered!);
    }
}
