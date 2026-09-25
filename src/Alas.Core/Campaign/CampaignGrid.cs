namespace Alas.Campaign;

/// <summary>
/// 地图格子的 C# 侧模型：只保留**目标选择**需要的属性，逐项对应上游
/// `module/map_detection/grid_info.py` 的 <c>GridInfo</c>：
/// <list type="bullet">
///   <item><c>is_accessible = cost &lt; 9999</c>、<c>is_nearby = cost &lt; 20</c>（原样移植）；</item>
///   <item><c>str</c>：上游 <c>encode()</c> —— **全部分支已移植**（见 <see cref="Encode"/>）：
///         陆地 <c>++</c>、boss <c>BO</c>、塞壬（按 <c>enemy_genre</c> 解析）、敌人
///         <c>"{enemy_scale}{enemy_genre 首字母大写或 E}"</c>（如 <c>3L</c>/<c>2M</c>/<c>1E</c>）、
///         以及 <c>FL</c>/<c>Fc</c>/<c>Fl</c>/<c>ss</c>/<c>MY</c>/<c>AM</c>/<c>FR</c>/<c>MI</c>/<c>BE</c>/<c>==</c>/<c>--</c>；</item>
///   <item><c>enemy_scale</c>（0–3）、<c>enemy_genre</c>（Light/Main/Carrier/Treasure/Enemy 或空）。</item>
/// </list>
/// 编码已由全库逐格对拍验证（1370 张声明地图 / 275,934 格，见 <c>r5_path_sweep.py</c> 的
/// "逐格编码不一致 0"）。识别来源的标志（<c>FL</c>/<c>Fc</c>/<c>ss</c>/<c>MI</c> 等）只在识别叠加后命中，
/// 离线对拍覆盖不到，如实标注。
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
    bool MayEnemy = false,
    bool MayMystery = false,
    bool MaySiren = false,
    bool MayAmbush = false,
    bool MayBouncingEnemy = false,
    bool IsCaughtBySiren = false,
    bool IsFleet = false,
    bool IsCurrentFleet = false,
    bool IsSubmarine = false,
    bool IsMissileAttack = false,
    bool IsFlare = false,
    bool IsCleared = false,
    bool IsLand = false,
    bool IsMechanismTrigger = false,
    bool IsMechanismBlock = false,
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

    /// <summary>
    /// 上游 <c>GridInfo.encode()</c>（`Filter` 用的 `grid.str`）的**完整移植**，判定顺序照抄：
    /// 陆地 <c>++</c> → boss <c>BO</c> → 塞壬（按 `enemy_genre` 解析，形如 `Siren_xxx`）→
    /// 敌人 <c>{scale}{genre首字母或E}</c> → 舰队/被抓/潜艇/神秘/弹药/要塞/导弹/巡逻/已清 →
    /// 都不是则 <c>--</c>。注意塞壬名字长度为 1 时上游会补一个**空格**（`f'{name.upper()} '`）。
    /// </summary>
    public string FilterKey => Encode();

    /// <summary>上游 <c>GridInfo.encode()</c>。</summary>
    public string Encode()
    {
        if (IsLand) return "++";
        if (IsBoss) return "BO";

        if (IsSiren)
        {
            if (string.IsNullOrEmpty(EnemyGenre)) return "SU";
            // enemy_genre 形如 "Siren_xxx"：去掉前 6 个字符，再有下划线就取最后一段，然后取前两个字符
            string name = EnemyGenre.Length > 6 ? EnemyGenre[6..] : "";
            int underscore = name.LastIndexOf('_');
            if (underscore >= 0) name = name[(underscore + 1)..];
            name = name.Length > 2 ? name[..2] : name;
            return name.Length switch
            {
                2 => name.ToUpperInvariant(),
                1 => $"{name.ToUpperInvariant()} ",
                _ => "SU",
            };
        }

        if (IsEnemy)
        {
            return $"{EnemyScale}" +
                   (string.IsNullOrEmpty(EnemyGenre) ? "E" : char.ToUpperInvariant(EnemyGenre[0]).ToString());
        }

        if (IsCurrentFleet) return "FL";
        if (IsCaughtBySiren) return "Fc";
        if (IsFleet) return "Fl";
        if (IsSubmarine) return "ss";
        if (IsMystery) return "MY";
        if (IsAmmo) return "AM";
        if (IsFortress) return "FR";
        if (IsMissileAttack) return "MI";
        if (MayBouncingEnemy) return "BE";
        if (IsCleared) return "==";
        return "--";
    }
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
        if (x is < 0 or > 25) throw new NotSupportedException(
            $"列 {x} 超出 A–Z：上游 `location2node` 是 Excel 式的（AA/AB…，也支持负数列），" +
            "这里只移植到单字母。战役地图宽度远小于 26（全库逐格对拍里没有一例越界）；真要支持时在这里显式加。");
        return $"{(char)('A' + x)}{y + 1}";
    }

    /// <summary>上游 <c>node2location()</c> 的逆变换：<c>B8</c> → <c>(1, 7)</c>。</summary>
    public static (int X, int Y) ToCoordinates(string node)
    {
        if (string.IsNullOrEmpty(node)) throw new NotSupportedException("格子节点名为空");
        char column = char.ToUpperInvariant(node[0]);
        if (column is < 'A' or > 'Z' || !int.TryParse(node[1..], out int row) || row < 1)
        {
            throw new NotSupportedException($"无法解析格子节点名：{node}");
        }
        return (column - 'A', row - 1);
    }

    /// <summary>越界坐标返回 false（用于四邻接判定，不抛异常）。</summary>
    public static bool TryToNode(int x, int y, out string node)
    {
        if (x is < 0 or > 25 || y < 0)
        {
            node = "";
            return false;
        }
        node = $"{(char)('A' + x)}{y + 1}";
        return true;
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
    bool? MayBouncingEnemy = null,
    bool? IsMechanismTrigger = null,
    bool? IsMechanismBlock = null,
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
        (MayBouncingEnemy is null || grid.MayBouncingEnemy == MayBouncingEnemy) &&
        (IsMechanismTrigger is null || grid.IsMechanismTrigger == IsMechanismTrigger) &&
        (IsMechanismBlock is null || grid.IsMechanismBlock == IsMechanismBlock) &&
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
                throw new NotSupportedException(
                    $"排序键 {attribute} 不在已移植的白名单里（weight/cost/cost_1/cost_2）。" +
                    "上游用 attrgetter 支持任意属性；已核对全库用法，战役只用到这四个" +
                    "（默认 ('weight','cost')、FLEET_2 下 ('weight','cost_2')、" +
                    "fleet_2_protect 的 ('cost_2','cost_1')、clear_all_mystery 的 ('cost',)、" +
                    "movable 分支的 ('cost_2',)）。真要新增键时在这里显式加，不猜语义。");
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
