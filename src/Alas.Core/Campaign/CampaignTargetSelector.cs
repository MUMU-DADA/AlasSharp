namespace Alas.Campaign;

/// <summary>选择链路里的可选条件，对应上游 <c>Map.select_grids(**kwargs)</c> 的形参。</summary>
public sealed record CampaignTargetOptions(
    bool Nearby = false,
    bool IsAccessible = true,
    IReadOnlyList<int>? Scale = null,
    bool ScaleInOrder = false,
    IReadOnlyList<string>? Genre = null,
    bool GenreInOrder = false,
    bool Strongest = false,
    bool Weakest = false,
    string[]? Sort = null,
    CampaignGridSet? Ignore = null);

/// <summary>
/// 一次目标选择的结论：选中的格子（无则 null）、走的分支、以及"未接线分支"的说明。
/// <see cref="Unsupported"/> **当前没有生产者**（各分支都已移植）；字段保留给决策层新增分支时显式报出，
/// 免得悄悄返回一个错的目标。
/// </summary>
public sealed record CampaignTargetDecision(CampaignGrid? Target, string Branch, string? Unsupported = null)
{
    public bool Selected => Target is not null;
}

/// <summary>
/// 目标选择的 C# 移植，逐条对应上游 <c>module/map/map.py</c>：
/// <list type="bullet">
///   <item><c>Map.select_grids(...)</c>（第 103 行起）：nearby / is_accessible / ignore / scale / genre /
///         strongest / weakest / sort 的处理顺序与循环取值完全照抄；</item>
///   <item><c>Map.clear_enemy(**kwargs)</c>（第 191 行起）：<c>map.select(is_enemy=True, is_boss=False)</c>
///         → 按 <c>EnemyPriority_EnemyScaleBalanceWeight</c> / <c>MAP_CLEAR_ALL_THIS_TIME</c> 决定
///         strongest 或 weakest → <c>select_grids</c> → 取第一个；</item>
///   <item><c>Map.clear_filter_enemy(string, preserve)</c>（第 663 行起）：按优先级改写过滤串、
///         <c>is_enemy/is_accessible</c> 过滤、<c>sort('weight','cost')</c>、文本过滤器按优先级取、
///         <c>preserve</c> 截断 → 取第一个。</item>
/// </list>
/// **边界说明**：`MAP_HAS_MOVABLE_NORMAL_ENEMY` 分支**不是**本层的事——上游在
/// `clear_filter_enemy` 里就忽略过滤串、直接委托 `clear_any_enemy(sort=('cost_2',))`，
/// C# 侧同样在 `CampaignPrimitives.ClearFilterEnemy` 里委托（见那里的注释）。
/// 本层只负责"选哪一格"；点击/移动属设备动作，在宿主层。
/// </summary>
public static class CampaignTargetSelector
{
    /// <summary>上游 <c>clear_filter_enemy</c> 在 <c>S3_enemy_first</c> 下替换的过滤串。</summary>
    public const string S3EnemyFirstFilter =
        "3L > 3M > 3E > 3C > 2L > 2M > 2E > 2C > 1L > 1M > 1E > 1C";

    /// <summary>上游 <c>clear_filter_enemy</c> 在 <c>S1_enemy_first</c> 下替换的过滤串。</summary>
    public const string S1EnemyFirstFilter =
        "1L > 1M > 1E > 1C > 2L > 2M > 2E > 2C > 3L > 3M > 3E > 3C";

    /// <summary>上游 <c>Map.select_grids(...)</c>。</summary>
    public static CampaignGridSet SelectGrids(CampaignGridSet grids, CampaignTargetOptions? options = null)
    {
        options ??= new CampaignTargetOptions();
        if (options.Nearby) grids = grids.Select(new CampaignGridFilter(IsNearby: true));
        if (options.IsAccessible) grids = grids.Select(new CampaignGridFilter(IsAccessible: true));
        grids = grids.Delete(options.Ignore);

        if (options.Scale is { Count: > 0 } scale)
        {
            var enemy = CampaignGridSet.Empty;
            foreach (int value in scale)
            {
                enemy = enemy.Add(grids.Select(new CampaignGridFilter(EnemyScale: value)));
                if (options.ScaleInOrder && !enemy.IsEmpty) break;
            }
            grids = enemy;
        }

        if (options.Genre is { Count: > 0 } genre)
        {
            var enemy = CampaignGridSet.Empty;
            foreach (string value in genre)
            {
                // 上游：enemy_genre 首字母小写时改成大写（camel case）
                string normalized = value.Length > 0 && char.IsLower(value[0])
                    ? char.ToUpperInvariant(value[0]) + value[1..]
                    : value;
                enemy = enemy.Add(grids.Select(new CampaignGridFilter(EnemyGenre: normalized)));
                if (options.GenreInOrder && !enemy.IsEmpty) break;
            }
            grids = enemy;
        }

        if (options.Strongest)
        {
            foreach (int value in new[] { 3, 2, 1, 0 })
            {
                var enemy = grids.Select(new CampaignGridFilter(EnemyScale: value));
                if (!enemy.IsEmpty)
                {
                    grids = enemy;
                    break;
                }
            }
        }

        if (options.Weakest)
        {
            foreach (int value in new[] { 1, 2, 3, 0 })
            {
                var enemy = grids.Select(new CampaignGridFilter(EnemyScale: value));
                if (!enemy.IsEmpty)
                {
                    grids = enemy;
                    break;
                }
            }
        }

        if (!grids.IsEmpty) grids = grids.Sort(options.Sort ?? ["weight", "cost"]);
        return grids;
    }

    /// <summary>
    /// 上游 <c>clear_enemy(**kwargs)</c> 的决策部分：返回要清的敌人格子；<c>null</c> 表示没有目标。
    /// </summary>
    public static CampaignTargetDecision SelectEnemyTarget(
        CampaignGridSet grids,
        string? enemyPriority,
        bool mapClearAllThisTime,
        CampaignTargetOptions? options = null)
    {
        options ??= new CampaignTargetOptions();
        var enemy = grids.Select(new CampaignGridFilter(IsEnemy: true, IsBoss: false));

        string branch = "default";
        if (enemyPriority == "S3_enemy_first")
        {
            options = options with { Strongest = true };
            branch = "S3_enemy_first → strongest";
        }
        else if (enemyPriority == "S1_enemy_first")
        {
            options = options with { Weakest = true };
            branch = "S1_enemy_first → weakest";
        }
        else if (mapClearAllThisTime)
        {
            options = options with { Strongest = true };
            branch = "MAP_CLEAR_ALL_THIS_TIME → strongest";
        }

        var selected = SelectGrids(enemy, options);
        return new CampaignTargetDecision(selected.IsEmpty ? null : selected[0], branch);
    }

    /// <summary>
    /// 上游 <c>clear_filter_enemy(string, preserve)</c> 的决策部分（**不含** movable 分支：
    /// 那条分支在 <c>CampaignPrimitives.ClearFilterEnemy</c> 里就委托给 `clear_any_enemy(sort=('cost_2',))` 了）。
    /// <paramref name="hasMovableNormalEnemy"/> 只用于"误传时有明确结果"：为真时返回空目标并说明原因。
    /// </summary>
    public static CampaignTargetDecision SelectFilterEnemyTarget(
        CampaignGridSet grids,
        string filter,
        int preserve,
        string? enemyPriority = null,
        bool hasMovableNormalEnemy = false)
    {
        if (hasMovableNormalEnemy)
        {
            // 保留这个入口参数只为"传错就有明确结果"：真正的 movable 分支在 `ClearFilterEnemy` 里
            // 直接委托给 `clear_any_enemy(sort=('cost_2',))`（上游就是这么写的），不再报未移植。
            return new CampaignTargetDecision(null, "MAP_HAS_MOVABLE_NORMAL_ENEMY（已在 ClearFilterEnemy 委托 clear_any_enemy）");
        }

        string branch = "filter";
        if (enemyPriority == "S3_enemy_first")
        {
            filter = S3EnemyFirstFilter;
            preserve = 0;
            branch = "S3_enemy_first → 覆盖过滤串、preserve=0";
        }
        else if (enemyPriority == "S1_enemy_first")
        {
            filter = S1EnemyFirstFilter;
            branch = "S1_enemy_first → 覆盖过滤串";
        }

        var enemy = grids.Select(new CampaignGridFilter(IsEnemy: true, IsAccessible: true));
        if (enemy.IsEmpty) return new CampaignTargetDecision(null, branch + "（无敌人）");

        var sorted = enemy.Sort("weight", "cost");
        var filtered = new CampaignTextFilter().Load(filter).Apply(sorted.Grids);
        var remaining = preserve > 0 && filtered.Count > preserve
            ? filtered.Skip(preserve).ToList()
            : preserve > 0 ? [] : filtered;

        return new CampaignTargetDecision(remaining.Count == 0 ? null : remaining[0],
                                          branch + $"（过滤后 {filtered.Count} 个，preserve={preserve}）");
    }
}
