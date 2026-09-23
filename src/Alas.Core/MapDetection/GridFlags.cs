namespace Alas.MapDetection;

/// <summary>
/// 网格 token 的语义（S2 数据半边的第二块）：把一个格子标记解成引擎要用的布尔量。
///
/// 表来自上游 `GridInfo.decode()`，**逐条照抄**，包括两个容易看漏的点：
///   1. 先 `text.upper()` —— 所以 `Me` 与 `ME` 同义（数据里 `Me` 有 4728 个！）；
///   2. `--` **不在表里**，于是八个查表标志为假；"海"是由 <see cref="IsSea"/> 反推的，
///      不是查表查出来的。<see cref="MayAmbush"/> 与 <see cref="MayCarrier"/> 是推导值，
///      对未知 token 仍可能为真。
///
/// 上游表（print_name → property）：
/// | ++ | is_land | 舰队不能进 |
/// | -- | is_sea（推导） | 海 |
/// | __ | is_submarine_spawn_point | 潜艇出生点 |
/// | SP | is_spawn_point | 舰队可能出生 |
/// | ME | may_enemy | 可能有敌人 |
/// | MB | may_boss | 可能有 BOSS |
/// | MM | may_mystery | 可能有神秘事件 |
/// | MA | may_ammo | 可补给弹药 |
/// | MS | may_siren | 塞壬/精英可能出生 |
/// 其余（`SI`、`-`）不在表里 → 八个查表标志为假；推导值仍按上游公式计算。
/// </summary>
public readonly record struct GridFlags(
    bool IsLand, bool IsSpawnPoint, bool IsSubmarineSpawnPoint,
    bool MayEnemy, bool MayBoss, bool MayMystery, bool MayAmmo, bool MaySiren,
    bool MayAmbush)
{
    /// <summary>
    /// 上游：`is_sea = False if (is_land or is_enemy or is_siren or is_fortress or is_boss) else True`。
    /// **在地图数据阶段**，is_enemy / is_siren / is_fortress / is_boss 都是运行期标记（尚未识别），
    /// 一律为假，所以此时 `is_sea == !is_land`。别把这两件事混起来 —— 等识别跑过之后再算 is_sea，
    /// 语义就不同了。
    /// </summary>
    public bool IsSea => !IsLand;

    /// <summary>上游：`may_carrier = is_sea and not may_enemy`。</summary>
    public bool MayCarrier => IsSea && !MayEnemy;

    /// <summary>
    /// 单字符指纹：跨语言对照用。每个 token 最多命中一个标志（上游那张表是一对一的），
    /// 所以这里不存在优先级歧义；`.` 表示"没有标志"（海/未知 token 都落这里）。
    /// </summary>
    public char Fingerprint => IsLand ? 'L'
        : IsSpawnPoint ? 'S'
        : IsSubmarineSpawnPoint ? 'U'
        : MayEnemy ? 'E'
        : MayBoss ? 'B'
        : MayMystery ? 'M'
        : MayAmmo ? 'A'
        : MaySiren ? 'R'
        : '.';

    /// <summary>上游 `GridInfo.decode(text)` 的移植。</summary>
    public static GridFlags Decode(string token)
    {
        string t = (token ?? "").ToUpperInvariant();     // ← 上游在这里 upper()，别漏
        bool land = t == "++";
        bool spawn = t == "SP";
        bool subSpawn = t == "__";
        bool enemy = t == "ME";
        bool boss = t == "MB";
        bool mystery = t == "MM";
        bool ammo = t == "MA";
        bool siren = t == "MS";
        // 上游：may_ambush = not (may_enemy or may_boss or may_mystery or may_mystery)
        // 注意源码里 may_mystery 写了两遍（疑似笔误，本应含 may_siren）——
        // 这里**照抄现状**，不自作主张"修好"，否则两边行为会不一致。
        bool ambush = !(enemy || boss || mystery || mystery);
        return new GridFlags(land, spawn, subSpawn, enemy, boss, mystery, ammo, siren, ambush);
    }
}
