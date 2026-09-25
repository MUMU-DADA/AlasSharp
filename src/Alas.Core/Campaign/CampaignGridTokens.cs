namespace Alas.Campaign;

/// <summary>
/// 上游地图令牌 → 格子状态的解析，逐条对应上游 <c>GridInfo.decode()</c>：
/// <c>++</c> 陆地、<c>SP</c> 出生点、<c>__</c> 潜艇出生点、<c>ME</c> 可能有敌、<c>MB</c> 可能 boss、
/// <c>MM</c> 可能有神秘、<c>MA</c> 可能有弹药、<c>MS</c> 可能有塞壬；
/// <c>may_ambush = not (may_enemy or may_boss or may_mystery or may_mystery)</c>。
///
/// 用途：从**导出的真实地图**构造干跑用的地图状态（真机的 <c>is_enemy</c> / <c>is_boss</c> 等
/// 识别结果不在导出里，需要时由调用方另行覆盖）。
/// </summary>
public static class CampaignGridTokens
{
    private static readonly Dictionary<string, string> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        ["++"] = "is_land",
        ["SP"] = "is_spawn_point",
        ["__"] = "is_submarine_spawn_point",
        ["ME"] = "may_enemy",
        ["MB"] = "may_boss",
        ["MM"] = "may_mystery",
        ["MA"] = "may_ammo",
        ["MS"] = "may_siren",
    };

    /// <summary>把多行令牌文本解成格子集合（行优先，列从 A 起、行从 1 起）。</summary>
    public static IReadOnlyList<CampaignGrid> FromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var rows = text.Replace("\r", "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(row => row.Trim().Length > 0)
            .ToArray();
        return FromRows(rows);
    }

    public static IReadOnlyList<CampaignGrid> FromRows(IReadOnlyList<string> rows)
    {
        var grids = new List<CampaignGrid>();
        for (int y = 0; y < rows.Count; y++)
        {
            string[] tokens = rows[y].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            for (int x = 0; x < tokens.Length; x++)
            {
                grids.Add(FromToken(CampaignLocations.ToNode(x, y), tokens[x]));
            }
        }
        return grids;
    }

    /// <summary>单个令牌 → 格子（未知令牌按"无声明"处理，与上游 decode 的 <c>valid=False</c> 一致）。</summary>
    public static CampaignGrid FromToken(string location, string token)
    {
        string flag = Table.TryGetValue(token, out string? mapped) ? mapped : "";
        bool mayEnemy = flag == "may_enemy";
        bool mayBoss = flag == "may_boss";
        bool mayMystery = flag == "may_mystery";
        return new CampaignGrid(
            location,
            IsLand: flag == "is_land",
            MayEnemy: mayEnemy,
            MayBoss: mayBoss,
            MayMystery: mayMystery,
            MayAmmo: flag == "may_ammo",
            MaySiren: flag == "may_siren",
            MayAmbush: !(mayEnemy || mayBoss || mayMystery || mayMystery));
    }
}
