using System.Collections.Immutable;
using Alas.Engine.Imaging;
using Alas.Engine.Runtime;

namespace Alas.Engine.Rules;

public readonly record struct NumberRange(double Low, double High);
public sealed record LinePeakParameters(NumberRange Height, NumberRange? Width = null, double Prominence = 10,
    double Distance = 35, int? Window = null);
public sealed record HomographyStorage(PixelPoint GridSize, GridCorners Corners);
public enum GridDetectionBackend { Homography, Perspective }

/// <summary>Typed upstream detector configuration. Chapter overrides must supply these ordinary C# values.</summary>
public sealed record MapDetectionRules
{
    public static readonly SourceFile Source = GridRecognitionRules.Source;
    public PixelArea Area { get; init; } = new(123, 55, 1157, 665);
    public ScreenPoint ScreenCenter { get; init; } = new(640, 360);
    public PixelPoint Tile { get; init; } = new(140, 140);
    public GridDetectionBackend Backend { get; init; } = GridDetectionBackend.Homography;
    public HomographyStorage? Storage { get; init; }
    public PixelPoint CenterOffset { get; init; } = new(48, 48);
    public ImmutableArray<PixelPoint> CornerOffsets { get; init; } = [new(-42, -42), new(68, -42), new(-42, 69), new(69, 69)];
    public NumberRange Canny { get; init; } = new(100, 150);
    public double CenterGoodThreshold { get; init; } = 0.9;
    public double CenterThreshold { get; init; } = 0.8;
    public double CornerThreshold { get; init; } = 0.8;
    public int RectangleThreshold { get; init; } = 10;
    public bool DetectEdges { get; init; } = true;
    public int EdgeHoughThreshold { get; init; } = 180;
    public NumberRange EdgeColor { get; init; } = new(0, 33);
    public LinePeakParameters InternalPeaks { get; init; } = new(new(150, 222), new(0.9, 10));
    public LinePeakParameters EdgePeaks { get; init; } = new(new(222, 255), Distance: 50, Window: 1000);
    public int InternalLinesThreshold { get; init; } = 75;
    public int EdgeLinesThreshold { get; init; } = 75;
    public double HorizontalTheta { get; init; } = 0.005;
    public double VerticalTheta { get; init; } = 18;
    public bool TrustEdgeLines { get; init; }
    public double TrustEdgeThreshold { get; init; } = 5;
    public NumberRange VanishX { get; init; } = new(540, 740);
    public NumberRange VanishY { get; init; } = new(-3000, -1000);
    public NumberRange DistantX { get; init; } = new(-3200, -1600);
    public double CoincidentEncourage { get; init; } = 3;
    public PixelPoint ErrorTolerance { get; init; } = new(-10, 10);
    public NumberRange MidHorizontal { get; init; } = new(126, 132);
    public NumberRange MidVertical { get; init; } = new(126, 132);
    public bool OperationSiren { get; init; }

    public MapDetectionRules WithChapter(MapVisionOverrides? overrides)
    {
        if (overrides is null) return this;
        static LinePeakParameters Peaks(PeakParameters value) => new(new(value.HeightMin, value.HeightMax),
            value.WidthMin is { } low && value.WidthMax is { } high ? new(low, high) : null,
            value.Prominence, value.Distance, value.WindowLength);
        return this with { InternalPeaks = Peaks(overrides.InternalPeaks), EdgePeaks = Peaks(overrides.EdgePeaks),
            Canny = new(overrides.Canny.Low, overrides.Canny.High), EdgeColor = new(overrides.EdgeColor.Low, overrides.EdgeColor.High),
            InternalLinesThreshold = overrides.InternalHough, EdgeLinesThreshold = overrides.EdgeHough,
            EdgeHoughThreshold = overrides.HomographyEdgeHough };
    }

    public void Validate()
    {
        if (!Enum.IsDefined(Backend) || Area.X < 0 || Area.Y < 0 || Area.Width < 1 || Area.Height < 1 ||
            (long)Area.X + Area.Width > 1280 || (long)Area.Y + Area.Height > 720 ||
            Tile.X < 1 || Tile.Y < 1 || Tile.X > 4096 || Tile.Y > 4096 || CornerOffsets.IsDefault || CornerOffsets.Length != 4 ||
            !double.IsFinite(ScreenCenter.X) || !double.IsFinite(ScreenCenter.Y) || RectangleThreshold < 0 ||
            EdgeHoughThreshold < 1 || InternalLinesThreshold < 1 || EdgeLinesThreshold < 1 ||
            ErrorTolerance.X > ErrorTolerance.Y || (long)ErrorTolerance.Y - ErrorTolerance.X > 200 ||
            new[] { HorizontalTheta, VerticalTheta, TrustEdgeThreshold, CoincidentEncourage }.Any(v => !double.IsFinite(v) || v <= 0) ||
            new[] { CenterGoodThreshold, CenterThreshold, CornerThreshold }.Any(v => !double.IsFinite(v) || v is < -1 or > 1))
            throw new ArgumentException("Invalid map detection rules");
        foreach (var range in new[] { Canny, EdgeColor, VanishX, VanishY, DistantX, MidHorizontal, MidVertical })
            if (!double.IsFinite(range.Low) || !double.IsFinite(range.High) || range.Low > range.High)
                throw new ArgumentException("Invalid detector range");
        if (MidHorizontal.Low <= 0 || MidVertical.Low <= 0) throw new ArgumentException("Invalid grid spacing");
        if (Canny.Low < 0 || EdgeColor.Low < 0 || EdgeColor.High > 255 || CenterGoodThreshold < CenterThreshold)
            throw new ArgumentException("Invalid detector thresholds");
        if (Storage is { } storage)
        {
            var (a, b, c, d) = storage.Corners;
            if (storage.GridSize.X < 1 || storage.GridSize.Y < 1 ||
                new[] { a, b, c, d }.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y)))
                throw new ArgumentException("Invalid homography storage");
        }
        foreach (var peaks in new[] { InternalPeaks, EdgePeaks })
            if (peaks is null || !double.IsFinite(peaks.Height.Low) || !double.IsFinite(peaks.Height.High) || peaks.Height.Low > peaks.Height.High ||
                peaks.Width is { } width && (!double.IsFinite(width.Low) || !double.IsFinite(width.High) || width.Low > width.High || width.Low < 0) ||
                !double.IsFinite(peaks.Distance) || peaks.Distance < 1 || !double.IsFinite(peaks.Prominence) || peaks.Prominence < 0 || peaks.Window is <= 1)
                throw new ArgumentException("Invalid line peak parameters");
    }
}

public static class MapDetectionAssets
{
    public static readonly SourceFile Source = MapSwipeEvidence.MaskSource;
    public static readonly AssetVariant Mask = new(null, null, null, "assets/mask/MASK_MAP_UI.png",
        "8b5708bc4bcd1443fbfcfb8fc34889df64f1e7231d1d71480c20d389cc77e80e");
    public static readonly AssetVariant OsMask = new(null, null, null, "assets/mask/MASK_OS_MAP_UI.png",
        "5a16f1498b745e0da0ce769d6f77edfd39119a4e594712c21fc2a1fdf2de84fc");
    public static readonly AssetVariant Center = new(null, null, null, "assets/map_detection/TILE_CENTER.png",
        "508dba7c9fabe472e16475bca3ce8d4aeea4e2b638c7f6ea785064842d7714d9");
    public static readonly AssetVariant Corner = new(null, null, null, "assets/map_detection/TILE_CORNER.png",
        "603253ed59b479ab9762c6263474060bf87346c0bac70a824f5f580f36023a8d");
    public static readonly PixelPoint MaskOrigin = new(123, 55);
}
