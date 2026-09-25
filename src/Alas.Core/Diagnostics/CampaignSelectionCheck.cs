using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Alas.Campaign;

namespace Alas.Core.Diagnostics;

/// <summary>
/// R5 目标选择的离线对拍入口：`Alas.Server r5-select --fixture &lt;fixture.json&gt;`。
///
/// 它把夹具里的地图格子喂给 C# 移植的 <see cref="CampaignTargetSelector"/>，输出每个用例的选中结果
/// （JSON），供 `tools/diagnostics/verify_r5_selection.py` 与上游算法对拍。
/// **不连设备、不执行任何游戏动作**：只做"选哪个格子"的决策。
///
/// 用法：
///   Alas.Server r5-select --fixture data/fixtures/r5-selection.json
/// </summary>
internal static class CampaignSelectionCheck
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static int Run(string fixturePath)
    {
        if (!File.Exists(fixturePath))
        {
            return Fail($"找不到夹具：{fixturePath}");
        }
        SelectionFixture fixture;
        try
        {
            fixture = JsonSerializer.Deserialize<SelectionFixture>(File.ReadAllText(fixturePath), Options)
                      ?? throw new JsonException("夹具为空");
        }
        catch (JsonException error)
        {
            return Fail($"夹具解析失败：{error.Message}");
        }

        var results = new JsonArray();
        foreach (var testCase in fixture.Cases)
        {
            try
            {
            var grids = new CampaignGridSet((testCase.Grids ?? []).Select(Grid));
            var options = new CampaignTargetOptions(
                Nearby: testCase.Options?.Nearby ?? false,
                IsAccessible: testCase.Options?.IsAccessible ?? true,
                Scale: testCase.Options?.Scale,
                ScaleInOrder: testCase.Options?.ScaleInOrder ?? false,
                Genre: testCase.Options?.Genre,
                GenreInOrder: testCase.Options?.GenreInOrder ?? false,
                Strongest: testCase.Options?.Strongest ?? false,
                Weakest: testCase.Options?.Weakest ?? false,
                Sort: testCase.Options?.Sort,
                Ignore: null);

            CampaignTargetDecision decision;
            JsonArray? actions = null;
            JsonArray? logs = null;
            switch (testCase.Kind)
            {
                case "select_grids":
                    var selected = CampaignTargetSelector.SelectGrids(grids, options);
                    decision = new CampaignTargetDecision(selected.IsEmpty ? null : selected[0],
                                                          $"select_grids（{selected.Count} 个）");
                    break;
                case "clear_filter_enemy":
                    decision = CampaignTargetSelector.SelectFilterEnemyTarget(
                        grids, testCase.Filter ?? "", testCase.Preserve,
                        testCase.EnemyPriority, testCase.HasMovableNormalEnemy);
                    break;
                // 复合原语：直接把 C# 原语跑在**记录宿主**上，从记录到的动作里取"打了哪一格"。
                // 这样对拍的是原语的真实判定（含配置分支），而不只是选择器。
                case "primitive_clear_enemy":
                case "primitive_clear_any_enemy":
                case "primitive_clear_siren":
                case "primitive_clear_boss":
                case "primitive_clear_roadblocks":
                case "primitive_clear_potential_roadblocks":
                case "primitive_clear_first_roadblocks":
                case "primitive_pick_up_ammo":
                case "primitive_fleet_2_push_forward":
                case "primitive_fleet_2_protect":
                case "primitive_brute_clear_boss":
                case "primitive_brute_fleet_meet":
                case "primitive_clear_potential_boss":
                case "primitive_clear_filter_enemy":
                case "primitive_pick_up_flare":
                case "primitive_fleet_2_rescue":
                    var config = new CampaignRuntimeConfig(
                        EnemyPriority: testCase.EnemyPriority,
                        MapClearAllThisTime: testCase.MapClearAllThisTime,
                        MapHasSiren: testCase.MapHasSiren ?? false,
                        MapHasFortress: testCase.MapHasFortress ?? false,
                        Fleet2: testCase.Fleet2 ?? false,
                        FleetBoss: testCase.FleetBoss ?? false,
                        MapHasMovableEnemy: testCase.MapHasMovableEnemy ?? false,
                        MapHasMovableNormalEnemy: testCase.MapHasMovableNormalEnemy ?? false);
                    var host = new RecordingCampaignHost(grids.Grids, config)
                    {
                        FleetCurrentIndex = testCase.FleetCurrentIndex ?? 1,
                        Fleet1Location = testCase.Fleet1Location ?? "",
                        Fleet2Location = testCase.Fleet2Location ?? "",
                        AmmoCount = testCase.AmmoCount ?? 3,
                    };
                    var roads = (testCase.Roads ?? [])
                        .Select(road => new CampaignRoad(road)).ToArray();
                    bool result = testCase.Kind switch
                    {
                        "primitive_pick_up_ammo" => CampaignPrimitives.PickUpAmmo(host),
                        "primitive_fleet_2_push_forward" => CampaignPrimitives.Fleet2PushForward(host),
                        "primitive_fleet_2_protect" => CampaignPrimitives.Fleet2Protect(host),
                        "primitive_pick_up_flare" =>
                            CampaignPrimitives.PickUpFlare(host, TargetGrid(grids, testCase)),
                        "primitive_fleet_2_rescue" =>
                            CampaignPrimitives.Fleet2Rescue(host, TargetGrid(grids, testCase)),
                        "primitive_clear_filter_enemy" =>
                            CampaignPrimitives.ClearFilterEnemy(host, testCase.Filter ?? "", testCase.Preserve),
                        "primitive_brute_clear_boss" => CampaignPrimitives.BruteClearBoss(host),
                        "primitive_brute_fleet_meet" => CampaignPrimitives.BruteFleetMeet(host),
                        "primitive_clear_potential_boss" => CampaignPrimitives.ClearPotentialBoss(host),
                        "primitive_clear_first_roadblocks" =>
                            CampaignPrimitives.ClearFirstRoadblocks(host, roads, options),
                        "primitive_clear_roadblocks" => CampaignPrimitives.ClearRoadblocks(host, roads, options),
                        "primitive_clear_potential_roadblocks" =>
                            CampaignPrimitives.ClearPotentialRoadblocks(host, roads, options),
                        "primitive_clear_any_enemy" => CampaignPrimitives.ClearAnyEnemy(host, options),
                        "primitive_clear_siren" => CampaignPrimitives.ClearSiren(host, options),
                        "primitive_clear_boss" => CampaignPrimitives.ClearBoss(host),
                        _ => CampaignPrimitives.ClearEnemy(host, options),
                    };
                    actions = new JsonArray(host.Actions.Select(text => (JsonNode)text!).ToArray());
                    logs = new JsonArray(host.Logs.Select(text => (JsonNode)text!).ToArray());
                    decision = new CampaignTargetDecision(TargetFromActions(host.Actions, grids),
                                                          $"{testCase.Kind} → {result}" +
                                                          $"（动作 {host.Actions.Count} 条）");
                    break;
                default:
                    // **未知的 `primitive_*` kind 必须显式报错**，不能落到"选敌人"这个默认分支：
                    // 实测踩过一次——新增原语时只接了替身侧，C# 侧忘了加 kind，诊断命令于是静默去跑
                    // "选一个敌人"，把一次"命令没接线"误判成"引擎与上游行为不同"。
                    if (testCase.Kind.StartsWith("primitive_", StringComparison.Ordinal))
                    {
                        return Fail($"未知的原语 kind：{testCase.Kind}（要在 CampaignSelectionCheck 的 " +
                                    "switch 里显式接线，不能用默认分支兜底）");
                    }
                    decision = CampaignTargetSelector.SelectEnemyTarget(
                        grids, testCase.EnemyPriority, testCase.MapClearAllThisTime, options);
                    break;
            }

            results.Add(new JsonObject
            {
                ["name"] = testCase.Name,
                ["branch"] = decision.Branch,
                ["selected"] = decision.Target?.Location,
                ["selected_str"] = decision.Target?.FilterKey,
                ["actions"] = actions,
                ["logs"] = logs,
                ["unsupported"] = decision.Unsupported,
            });
            }
            catch (Exception error)
            {
                // **逐例捕获**：某个用例撞到未移植分支/异常时，只让这一例失败并如实报出原因，
                // 不能让整份夹具崩掉（实测踩过：一条 NotSupportedException 让扫描整体退出）。
                results.Add(new JsonObject
                {
                    ["name"] = testCase.Name,
                    ["branch"] = "异常",
                    ["selected"] = null,
                    ["selected_str"] = null,
                    ["actions"] = null,
                    ["unsupported"] = $"{error.GetType().Name}: {error.Message}",
                });
            }
        }

        Console.WriteLine(new JsonObject
        {
            ["fixture"] = Path.GetFileName(fixturePath),
            ["cases"] = results,
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    /// <summary>
    /// 取用例声明的目标格（`target` 字段，如 `C1`）——给那些**收一个格子参数**的原语用
    /// （`pick_up_flare` / `fleet_2_rescue` 等）。缺字段或找不到就报错，不猜一个默认格子。
    /// </summary>
    private static CampaignGrid TargetGrid(CampaignGridSet grids, SelectionCase testCase)
    {
        if (string.IsNullOrEmpty(testCase.Target))
        {
            throw new ArgumentException($"用例 {testCase.Name} 的 kind {testCase.Kind} 需要 `target` 字段");
        }
        return grids.Grids.FirstOrDefault(grid => grid.Location == testCase.Target)
               ?? throw new ArgumentException($"用例 {testCase.Name} 的 target {testCase.Target} 不在 grids 里");
    }

    private static CampaignGrid Grid(SelectionGrid grid) => new(
        grid.Location ?? "",
        IsEnemy: grid.IsEnemy,
        IsBoss: grid.IsBoss,
        IsSiren: grid.IsSiren,
        IsMystery: grid.IsMystery,
        IsAmmo: grid.IsAmmo,
        MayAmmo: grid.MayAmmo,
        IsFortress: grid.IsFortress,
        MayEnemy: grid.MayEnemy,
        MayBoss: grid.MayBoss,
        MayMystery: grid.MayMystery,
        MaySiren: grid.MaySiren,
        MayAmbush: grid.MayAmbush,
        MayBouncingEnemy: grid.MayBouncingEnemy,
        IsCaughtBySiren: grid.IsCaughtBySiren,
        IsFleet: grid.IsFleet,
        IsLand: grid.IsLand,
        IsMechanismBlock: grid.IsMechanismBlock,
        IsCleared: grid.IsCleared,
        EnemyScale: grid.EnemyScale,
        EnemyGenre: grid.EnemyGenre,
        Weight: grid.Weight,
        Cost: grid.Cost,
        Cost1: grid.Cost1 ?? 9999,
        Cost2: grid.Cost2 ?? 9999);

    /// <summary>
    /// 从记录到的动作里取"打了哪一格"：优先 `clear_chosen_enemy(X …)`，其次 `goto(X …)`。
    /// 找不到就返回 null（不猜）。只用于诊断对拍，不参与引擎判定。
    /// </summary>
    private static CampaignGrid? TargetFromActions(IReadOnlyList<string> actions, CampaignGridSet grids)
    {
        foreach (string action in actions)
        {
            foreach (string prefix in new[] { "clear_chosen_enemy(", "goto(" })
            {
                if (!action.StartsWith(prefix, StringComparison.Ordinal)) continue;
                int end = action.IndexOfAny([',', ')'], prefix.Length);
                string location = end > prefix.Length
                    ? action[prefix.Length..end].Trim()
                    : action[prefix.Length..].TrimEnd(')').Trim();
                var match = grids.Grids.FirstOrDefault(grid => grid.Location == location);
                if (match is not null) return match;
            }
        }
        return null;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"[错误   ] {message}");
        return 1;
    }

    private sealed class SelectionFixture
    {
        [JsonPropertyName("cases")] public List<SelectionCase> Cases { get; init; } = [];
    }

    private sealed class SelectionCase
    {
        [JsonPropertyName("name")] public string Name { get; init; } = "";
        [JsonPropertyName("kind")] public string Kind { get; init; } = "clear_enemy";
        [JsonPropertyName("grids")] public List<SelectionGrid>? Grids { get; init; }
        [JsonPropertyName("options")] public SelectionOptions? Options { get; init; }
        [JsonPropertyName("enemy_priority")] public string? EnemyPriority { get; init; }
        [JsonPropertyName("map_clear_all_this_time")] public bool MapClearAllThisTime { get; init; }
        [JsonPropertyName("filter")] public string? Filter { get; init; }
        [JsonPropertyName("preserve")] public int Preserve { get; init; }

        /// <summary>目标格（节点名，如 <c>C1</c>）：给 `pick_up_flare` / `fleet_2_rescue` 这类收格子参数的原语用。</summary>
        [JsonPropertyName("target")] public string? Target { get; init; }

        /// <summary>路段：`roads[road][block] = [节点名…]`（与上游 `RoadGrids` 同构）。</summary>
        [JsonPropertyName("roads")] public List<List<List<string>>>? Roads { get; init; }
        [JsonPropertyName("has_movable_normal_enemy")] public bool HasMovableNormalEnemy { get; init; }
        [JsonPropertyName("map_has_siren")] public bool? MapHasSiren { get; init; }
        [JsonPropertyName("map_has_fortress")] public bool? MapHasFortress { get; init; }
        [JsonPropertyName("fleet_2")] public bool? Fleet2 { get; init; }
        [JsonPropertyName("fleet_boss")] public bool? FleetBoss { get; init; }
        [JsonPropertyName("ammo_count")] public int? AmmoCount { get; init; }
        [JsonPropertyName("map_has_movable_enemy")] public bool? MapHasMovableEnemy { get; init; }
        [JsonPropertyName("map_has_movable_normal_enemy")] public bool? MapHasMovableNormalEnemy { get; init; }
        [JsonPropertyName("fleet_current_index")] public int? FleetCurrentIndex { get; init; }
        [JsonPropertyName("fleet_1_location")] public string? Fleet1Location { get; init; }
        [JsonPropertyName("fleet_2_location")] public string? Fleet2Location { get; init; }
    }

    private sealed class SelectionOptions
    {
        [JsonPropertyName("nearby")] public bool Nearby { get; init; }
        [JsonPropertyName("is_accessible")] public bool? IsAccessible { get; init; }
        [JsonPropertyName("scale")] public List<int>? Scale { get; init; }
        [JsonPropertyName("scale_in_order")] public bool ScaleInOrder { get; init; }
        [JsonPropertyName("genre")] public List<string>? Genre { get; init; }
        [JsonPropertyName("genre_in_order")] public bool GenreInOrder { get; init; }
        [JsonPropertyName("strongest")] public bool Strongest { get; init; }
        [JsonPropertyName("weakest")] public bool Weakest { get; init; }
        [JsonPropertyName("sort")] public string[]? Sort { get; init; }
    }

    private sealed class SelectionGrid
    {
        [JsonPropertyName("location")] public string? Location { get; init; }
        [JsonPropertyName("is_enemy")] public bool IsEnemy { get; init; }
        [JsonPropertyName("is_boss")] public bool IsBoss { get; init; }
        [JsonPropertyName("is_siren")] public bool IsSiren { get; init; }
        [JsonPropertyName("is_mystery")] public bool IsMystery { get; init; }
        [JsonPropertyName("is_ammo")] public bool IsAmmo { get; init; }
        [JsonPropertyName("may_ammo")] public bool MayAmmo { get; init; }
        [JsonPropertyName("is_fortress")] public bool IsFortress { get; init; }
        [JsonPropertyName("may_enemy")] public bool MayEnemy { get; init; }
        [JsonPropertyName("may_boss")] public bool MayBoss { get; init; }
        [JsonPropertyName("may_mystery")] public bool MayMystery { get; init; }
        [JsonPropertyName("may_siren")] public bool MaySiren { get; init; }
        [JsonPropertyName("may_ambush")] public bool MayAmbush { get; init; }
        [JsonPropertyName("may_bouncing_enemy")] public bool MayBouncingEnemy { get; init; }
        [JsonPropertyName("is_caught_by_siren")] public bool IsCaughtBySiren { get; init; }
        [JsonPropertyName("is_fleet")] public bool IsFleet { get; init; }
        [JsonPropertyName("is_land")] public bool IsLand { get; init; }
        [JsonPropertyName("is_mechanism_block")] public bool IsMechanismBlock { get; init; }
        [JsonPropertyName("is_cleared")] public bool IsCleared { get; init; }
        [JsonPropertyName("enemy_scale")] public int EnemyScale { get; init; }
        [JsonPropertyName("enemy_genre")] public string? EnemyGenre { get; init; }
        [JsonPropertyName("weight")] public int Weight { get; init; }
        [JsonPropertyName("cost")] public int Cost { get; init; }
        /// <summary>`cost_<舰队>`：多舰队成本场（`sort` 支持按它们排序；缺省 9999 = 不可达）。</summary>
        [JsonPropertyName("cost_1")] public int? Cost1 { get; init; }
        [JsonPropertyName("cost_2")] public int? Cost2 { get; init; }
    }
}
