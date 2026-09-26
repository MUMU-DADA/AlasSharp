namespace Alas.Engine.Rules;

public sealed record PeakParameters(double HeightMin, double HeightMax, double? WidthMin,
    double? WidthMax, double Prominence, double Distance, int? WindowLength = null);

/// <summary>Explicit chapter overrides, not a claim that all upstream CV defaults are ported.</summary>
public sealed record MapVisionOverrides(PeakParameters InternalPeaks, PeakParameters EdgePeaks,
    (int Low, int High) Canny, (int Low, int High) EdgeColor,
    int InternalHough, int EdgeHough, int HomographyEdgeHough);

public enum EnemyScalePriority { Default, StrongestFirst, WeakestFirst }

public sealed record CampaignConfiguration
{
    public bool PoorMapData { get; init; }
    public bool ClearAllThisTime { get; init; }
    public bool HasMovableNormalEnemy { get; init; }
    public bool HasMovableEnemy { get; init; }
    public bool HasMaze { get; init; }
    public bool HasWall { get; init; }
    public bool HasPortal { get; init; }
    public bool HasLandBased { get; init; }
    public bool HasFortress { get; init; }
    public bool HasBouncingEnemy { get; init; }
    public bool HasSiren { get; init; }
    public bool IsClearMode { get; init; }
    public bool HasAmbush { get; init; } = true;
    public bool HandleError { get; init; }
    public EnemyScalePriority EnemyPriority { get; init; }
    public int Fleet2 { get; init; }
    public int Submarine { get; init; }
    public MapVisionOverrides? Vision { get; init; }
}

/// <summary>Port of campaign_main/campaign_1_1.py Config, also imported by 1-2/1-3/1-4.</summary>
internal static class ChapterOneConfiguration
{
    public static CampaignConfiguration Apply(CampaignConfiguration input) => input with
    {
        Fleet2 = 0,
        Submarine = 0,
        Vision = new MapVisionOverrides(
            new PeakParameters(120, 255 - 49, 1.5, 10, 10, 35),
            new PeakParameters(255 - 49, 255, null, null, 10, 50, 1000),
            (75, 100), (0, 49), 40, 40, 80)
    };
}
