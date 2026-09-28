namespace Alas.Engine.Rules;

public sealed record PeakParameters(double HeightMin, double HeightMax, double? WidthMin,
    double? WidthMax, double Prominence, double Distance, int? WindowLength = null);

public sealed record MapSwipeMultipliers(Alas.Engine.Runtime.ScreenPoint Adb,
    Alas.Engine.Runtime.ScreenPoint Minitouch, Alas.Engine.Runtime.ScreenPoint MaaTouch);

/// <summary>Explicit chapter overrides, not a claim that all upstream CV defaults are ported.</summary>
public sealed record MapVisionOverrides(PeakParameters InternalPeaks, PeakParameters EdgePeaks,
    (int Low, int High) Canny, (int Low, int High) EdgeColor,
    int InternalHough, int EdgeHough, int HomographyEdgeHough)
{
    public static MapVisionOverrides Default { get; } = new(new(150, 222, .9, 10, 10, 35),
        new(222, 255, null, null, 10, 50, 1000), (100, 150), (0, 33), 75, 75, 180);
    public double? CoincidentEncourage { get; init; }
    public NumberRange? MidHorizontal { get; init; }
    public NumberRange? MidVertical { get; init; }
    public HomographyStorage? Storage { get; init; }
    public GridDetectionBackend? Backend { get; init; }
}

public enum EnemyScalePriority { Default, StrongestFirst, WeakestFirst }
public enum CampaignEmotionMode { Calculate, Ignore, CalculateIgnore, Nothing }
public enum FleetFormation { LineAhead, DoubleLine, Diamond }
public enum SubmarineMode { DoNotUse, HuntOnly, BossOnly, HuntAndBoss, EveryCombat }
public enum FleetOrder { Fleet1MobFleet2Boss, Fleet1BossFleet2Mob, Fleet1AllFleet2Standby, Fleet1StandbyFleet2All }

public sealed record CampaignConfiguration
{
    public bool PoorMapData { get; init; }
    public bool ClearAllThisTime { get; init; }
    public MapAchievement MapAchievement { get; init; }
    public bool StageIncrease { get; init; }
    public bool StageIncreaseAcrossAB { get; init; }
    public System.Collections.Immutable.ImmutableArray<string> StageIncreaseCustom { get; init; } = [];
    public int AllEnemiesStar { get; init; } = 3;
    public bool HasMapStory { get; init; }
    public Alas.Engine.Runtime.CampaignMapInfo? PreparationInfo { get; init; }
    public bool HasMovableNormalEnemy { get; init; }
    public bool HasMovableEnemy { get; init; }
    public System.Collections.Immutable.ImmutableArray<int> MovableEnemyTurns { get; init; } = [2];
    public System.Collections.Immutable.ImmutableArray<int> MovableNormalEnemyTurns { get; init; } = [1];
    public int MovableEnemyStep { get; init; } = 2;
    public double SirenMoveWait { get; init; } = 1.5;
    public bool HasMaze { get; init; }
    public bool WalkUseCurrentFleet { get; init; }
    // Retained source declaration. Native Fleet.goto currently uses HasAmbush instead.
    public bool WalkTurningOptimize { get; init; } = true;
    public bool SwipePredictWithSeaGrids { get; init; }
    public (int X, int Y)? BossAppearRefocusSwipe { get; init; } = (0, 0);
    public bool HasWall { get; init; }
    public bool HasPortal { get; init; }
    public bool HasLandBased { get; init; }
    public bool HasFortress { get; init; }
    public bool HasBouncingEnemy { get; init; }
    public bool HasSiren { get; init; }
    public bool HasMystery { get; init; } = true;
    public bool IsClearMode { get; init; }
    public bool UseClearMode { get; init; } = true;
    public bool UseDoubleBook { get; init; }
    public bool IsDoubleBook { get; init; }
    public bool HasClearPercentage { get; init; } = true;
    public bool ClearPercentageShort { get; init; }
    public bool IsOneTimeStage { get; init; }
    public bool HasFleetStep { get; init; }
    public int Fleet1Step { get; init; } = 3;
    public int Fleet2Step { get; init; } = 2;
    public bool HasDecoyEnemy { get; init; }
    public bool MysteryHasCarrier { get; init; }
    public bool HasAmbush { get; init; } = true;
    public bool AmbushEvade { get; init; } = true;
    public bool HandleError { get; init; }
    public EnemyScalePriority EnemyPriority { get; init; }
    public int Fleet2 { get; init; }
    public int? BossFleet { get; init; }
    public FleetOrder FleetOrder { get; init; } = FleetOrder.Fleet1MobFleet2Boss;
    public bool WaitForFleetSwitchInfoBar { get; init; }
    public int Submarine { get; init; }
    public FleetFormation Fleet1Formation { get; init; } = FleetFormation.DoubleLine;
    public FleetFormation Fleet2Formation { get; init; } = FleetFormation.DoubleLine;
    public SubmarineMode SubmarineMode { get; init; } = SubmarineMode.DoNotUse;
    public bool UseFleetLock { get; init; } = true;
    public FleetHealthOptions Health { get; init; } = new();
    public FleetLevelOptions Levels { get; init; } = new();
    public RetirementOptions Retirement { get; init; } = new();
    public CampaignEmotionMode EmotionMode { get; init; } = CampaignEmotionMode.Calculate;
    public MapVisionOverrides? Vision { get; init; }
    public MapSwipeMultipliers? SwipeMultipliers { get; init; }
    public string? MapEdgeCorner { get; init; }
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
