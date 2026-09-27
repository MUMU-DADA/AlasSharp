using System.Text.RegularExpressions;
using Alas.Engine.Runtime;

namespace Alas.Engine.Rules;

public enum MapAchievement { NonStop, FullyCleared, ThreeStars, ThreatSafe, ThreatSafeWithoutStars }

/// <summary>FastForwardHandler.map_get_info, triggered_map_stop and campaign_name_increase.</summary>
public static class CampaignObjectives
{
    public static readonly SourceFile Source = CampaignFleetLock.Source;
    public static string Name(this MapAchievement value) => value switch
    {
        MapAchievement.NonStop => "non_stop", MapAchievement.FullyCleared => "100_percent_clear",
        MapAchievement.ThreeStars => "map_3_stars", MapAchievement.ThreatSafe => "threat_safe",
        MapAchievement.ThreatSafeWithoutStars => "threat_safe_without_3_stars",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
    public static MapAchievement Parse(string value) => value switch
    {
        "non_stop" => MapAchievement.NonStop, "100_percent_clear" => MapAchievement.FullyCleared,
        "map_3_stars" => MapAchievement.ThreeStars, "threat_safe" => MapAchievement.ThreatSafe,
        "threat_safe_without_3_stars" => MapAchievement.ThreatSafeWithoutStars,
        _ => throw new ArgumentException("Unknown map achievement: " + value)
    };
    public static CampaignConfiguration Apply(CampaignConfiguration configuration)
    {
        _ = configuration.MapAchievement.Name();
        if (configuration.AllEnemiesStar is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(configuration));
        if (configuration.PreparationInfo is not { } info) return configuration;
        bool achieved = configuration.AllEnemiesStar switch { 1 => info.Star1, 2 => info.Star2, 3 => info.Star3, _ => true };
        return configuration with
        {
            HasMapStory = configuration.HasMapStory && !info.Star1,
            ClearAllThisTime = !achieved && configuration.MapAchievement is MapAchievement.ThreeStars or MapAchievement.ThreatSafe
        };
    }
    public static bool Reached(MapAchievement achievement, CampaignMapInfo info)
    {
        _ = achievement.Name();
        return info.FullyCleared && achievement switch
        {
            MapAchievement.FullyCleared => true, MapAchievement.ThreeStars => info.ThreeStars,
            MapAchievement.ThreatSafeWithoutStars => info.ThreatSafe,
            MapAchievement.ThreatSafe => info.ThreeStars && info.ThreatSafe, _ => false
        };
    }
    public static string InputName(string name)
    {
        name = Regex.Replace(name, "[ \\t\\n]", "").ToLowerInvariant();
        var match = Regex.Match(name, @"([a-zA-Z])+[- ]+(\d+)");
        if (match.Success && match.Index == 0) name = match.Groups[1].Value + match.Groups[2].Value;
        return name.ToUpperInvariant().Replace("CAMPAIGN_", "", StringComparison.Ordinal).Replace('_', '-');
    }
    public static string NextStage(string name, string folder, CampaignConfiguration configuration, IEnumerable<string> availableFiles)
    {
        name = InputName(name);
        var chains = new List<string>();
        if (configuration.StageIncreaseCustom.IsDefault) throw new ArgumentException("Invalid stage increase declarations");
        chains.AddRange(configuration.StageIncreaseCustom);
        if (configuration.StageIncreaseAcrossAB) chains.Add("A1 > A2 > A3 > B1 > B2 > B3");
        chains.Add(string.Join(" > ", Enumerable.Range(1, 16).SelectMany(chapter => Enumerable.Range(1, 4).Select(stage => $"{chapter}-{stage}"))));
        chains.AddRange(["A1 > A2 > A3", "B1 > B2 > B3", "C1 > C2 > C3", "D1 > D2 > D3",
            "SP1 > SP2 > SP3 > SP4 > SP5", "T1 > T2 > T3 > T4 > T5 > T6", "HT1 > HT2 > HT3 > HT4 > HT5 > HT6"]);
        var files = availableFiles.ToHashSet(StringComparer.Ordinal);
        foreach (string chain in chains)
        {
            if (chain is null) throw new ArgumentException("Null stage increase declaration");
            var stages = chain.Split('>').Select(stage => stage.Trim(' ', '\t', '\r', '\n')).ToArray();
            int index = Array.IndexOf(stages, name);
            if (index < 0) continue;
            return index + 1 < stages.Length && (folder == "campaign_main" || files.Contains(stages[index + 1].ToLowerInvariant()))
                ? stages[index + 1] : name;
        }
        return name;
    }
}
