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
                default:
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
                ["unsupported"] = decision.Unsupported,
            });
        }

        Console.WriteLine(new JsonObject
        {
            ["fixture"] = Path.GetFileName(fixturePath),
            ["cases"] = results,
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static CampaignGrid Grid(SelectionGrid grid) => new(
        grid.Location ?? "",
        IsEnemy: grid.IsEnemy,
        IsBoss: grid.IsBoss,
        IsSiren: grid.IsSiren,
        IsMystery: grid.IsMystery,
        IsAmmo: grid.IsAmmo,
        IsFortress: grid.IsFortress,
        EnemyScale: grid.EnemyScale,
        EnemyGenre: grid.EnemyGenre,
        Weight: grid.Weight,
        Cost: grid.Cost);

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
        [JsonPropertyName("has_movable_normal_enemy")] public bool HasMovableNormalEnemy { get; init; }
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
        [JsonPropertyName("is_fortress")] public bool IsFortress { get; init; }
        [JsonPropertyName("enemy_scale")] public int EnemyScale { get; init; }
        [JsonPropertyName("enemy_genre")] public string? EnemyGenre { get; init; }
        [JsonPropertyName("weight")] public int Weight { get; init; }
        [JsonPropertyName("cost")] public int Cost { get; init; }
    }
}
