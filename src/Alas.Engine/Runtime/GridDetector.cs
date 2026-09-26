using System.Buffers.Binary;
using Alas.Engine.Imaging;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed record HomographyLayout(ProjectiveTransform Transform, PixelPoint Size);
public sealed record DetectedGrid(IReadOnlyList<VisibleGrid> Grids, MapEdges Edges, ScreenPoint Offset,
    ScreenPoint Interior, int VerticalEdges, int HorizontalEdges);

/// <summary>Screenshot to local map geometry. All fitting, fallback selection and grid state stay in C#.</summary>
public sealed class GridDetector(IGridFeatureVision vision, AssetFiles assets, MapDetectionRules rules)
{
    public static readonly SourceFile Source = new("module/map_detection/homography.py",
        "9003f6a59c0dd9a7e66e2a5df213a9c00063ecb3bc6b164fe7edb8922f6fcc8a");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HomographyLayout? _homography;
    public HomographyLayout? Calibration => _homography;
    public async ValueTask<MapViewFrame> DetectAsync(ScreenFrame raw, CancellationToken token = default)
    {
        rules.Validate();
        var png = raw.Png.Span;
        if (png.Length < 24 || !png.Slice(12, 4).SequenceEqual("IHDR"u8) ||
            BinaryPrimitives.ReadInt32BigEndian(png.Slice(16, 4)) != 1280 || BinaryPrimitives.ReadInt32BigEndian(png.Slice(20, 4)) != 720)
            throw new NotSupportedException("Grid detection requires a normalized 1280x720 PNG frame");
        await _gate.WaitAsync(token);
        try
        {
            var mask = await assets.ReadAsync(rules.OperationSiren ? MapDetectionAssets.OsMask : MapDetectionAssets.Mask, token);
            // Perspective always uses the main stroke mask upstream; only View masking
            // and Homography's warped mask select the Operation Siren variant.
            var lineMask = rules.OperationSiren ? await assets.ReadAsync(MapDetectionAssets.Mask, token) : mask;
            var frame = await vision.MaskAsync(raw, mask, MapDetectionAssets.MaskOrigin, token);
            if (frame.Sequence != raw.Sequence) throw new InvalidDataException("Masked image identity differs");
            IReadOnlyList<VisibleGrid> grids; MapEdges edges;
            if (rules.Backend == GridDetectionBackend.Perspective)
            {
                var perspective = PerspectiveDetection.Detect(await vision.LinesAsync(frame, lineMask, rules, token), rules, token);
                grids = perspective.Grids; edges = perspective.Edges;
            }
            else
            {
                if (_homography is null)
                {
                    var storage = rules.Storage ?? PerspectiveDetection.Detect(await vision.LinesAsync(frame, lineMask, rules, token), rules, token).Storage;
                    _homography = Calibrate(storage, rules);
                }
                var features = await vision.WarpAsync(frame, mask, _homography.Transform, _homography.Size, rules, token);
                if (features.Edges.Sequence != frame.Sequence) throw new InvalidDataException("Warped image identity differs");
                var center = await vision.CorrelateAsync(features.Edges, await assets.ReadAsync(MapDetectionAssets.Center, token), rules.CenterThreshold, null, token);
                ScreenPoint location, interior;
                if (center.Maximum > rules.CenterGoodThreshold)
                { location = new(center.Location.X - rules.CenterOffset.X, center.Location.Y - rules.CenterOffset.Y); interior = new(center.Location.X, center.Location.Y); }
                else if (center.Maximum > rules.CenterThreshold)
                {
                    var points = center.AboveThreshold.Select(p => new ScreenPoint(p.X, p.Y)).ToArray();
                    location = DetectionFit.FitPoints(points, new(rules.Tile.X, rules.Tile.Y), 1, token);
                    location = new(location.X - rules.CenterOffset.X, location.Y - rules.CenterOffset.Y); interior = Mean(points);
                }
                else
                {
                    var points = new List<ScreenPoint>();
                    double maximum = 0;
                    int?[] flips = [-1, 0, 1, null];
                    var corner = await assets.ReadAsync(MapDetectionAssets.Corner, token);
                    for (int i = 0; i < 4; i++)
                    {
                        var value = await vision.CorrelateAsync(features.Edges, corner, rules.CornerThreshold, flips[i], token);
                        maximum = Math.Max(maximum, value.Maximum);
                        points.AddRange(value.AboveThreshold.Select(p => new ScreenPoint(p.X - rules.CornerOffsets[i].X, p.Y - rules.CornerOffsets[i].Y)));
                    }
                    if (maximum > rules.CornerThreshold)
                    {
                        location = DetectionFit.FitPoints(points, new(rules.Tile.X, rules.Tile.Y), 1, token);
                        location = new(location.X - rules.CenterOffset.X, location.Y - rules.CenterOffset.Y); interior = Mean(points);
                    }
                    else
                    {
                        points.Clear();
                        foreach (var group in await vision.RectanglesAsync(features.Edges, token))
                        {
                            // Native clears previous candidates when a kernel produced no contours.
                            if (group.Count == 0) { points.Clear(); continue; }
                            points.AddRange(group.Where(r => r.Width > 100 && r.Height > 100 &&
                                Math.Abs(r.Width - Math.Round((double)r.Width / rules.Tile.X) * rules.Tile.X) < 5.1 &&
                                Math.Abs(r.Height - Math.Round((double)r.Height / rules.Tile.Y) * rules.Tile.Y) < 5.1)
                                .Select(r => new ScreenPoint(r.X, r.Y)));
                        }
                        if (points.Count <= rules.RectangleThreshold) throw new MapGeometryException("Failed to find a free tile");
                        location = DetectionFit.FitPoints(points, new(rules.Tile.X, rules.Tile.Y), 5.1, token); interior = Mean(points);
                    }
                }
                location = new(DetectionFit.Mod(location.X, rules.Tile.X), DetectionFit.Mod(location.Y, rules.Tile.Y));
                var detected = Complete(_homography, rules, location, interior, features.Lines);
                grids = detected.Grids; edges = detected.Edges;
            }
            token.ThrowIfCancellationRequested();
            return new(frame, new(grids, rules.Area, rules.ScreenCenter, rules.Tile, edges));
        }
        finally { _gate.Release(); }
    }

    public static HomographyLayout Calibrate(HomographyStorage storage, MapDetectionRules rules, bool overflow = true)
    {
        rules.Validate();
        if (storage.GridSize.X < 1 || storage.GridSize.Y < 1) throw new ArgumentException("Invalid homography grid size");
        var (a, b, c, d) = storage.Corners;
        var source = new[] { a, b, c, d }.Select(p => new ScreenPoint(p.X - rules.Area.X, p.Y - rules.Area.Y)).ToArray();
        double width = (double)storage.GridSize.X * rules.Tile.X, height = (double)storage.GridSize.Y * rules.Tile.Y;
        ScreenPoint[] target = [source[0], new(source[0].X + width, source[0].Y), new(source[0].X, source[0].Y + height), new(source[0].X + width, source[0].Y + height)];
        var first = new ProjectiveTransform(source, target);
        ScreenPoint[] area = [new(0, 0), new(rules.Area.Width, 0), new(0, rules.Area.Height), new(rules.Area.Width, rules.Area.Height)];
        var transformed = area.Select(first.Forward).ToArray();
        double left = overflow ? transformed.Min(p => p.X) : Math.Max(transformed[0].X, transformed[2].X);
        double top = overflow ? transformed.Min(p => p.Y) : Math.Max(transformed[0].Y, transformed[1].Y);
        double right = overflow ? transformed.Max(p => p.X) : Math.Min(transformed[1].X, transformed[3].X);
        double bottom = overflow ? transformed.Max(p => p.Y) : Math.Min(transformed[2].Y, transformed[3].Y);
        var size = new PixelPoint(checked((int)Math.Ceiling(right - left)), checked((int)Math.Ceiling(bottom - top)));
        if (size.X < 1 || size.Y < 1 || (long)size.X * size.Y > 16 * 1024 * 1024) throw new MapGeometryException("Unbounded homography output");
        return new(new(area, transformed.Select(p => new ScreenPoint(p.X - left, p.Y - top)).ToArray()), size);
    }

    public static DetectedGrid Complete(HomographyLayout layout, MapDetectionRules rules, ScreenPoint location,
        ScreenPoint interior, IReadOnlyList<PolarLine> rawLines)
    {
        double? left = null, right = null, lower = null, upper = null;
        int verticalCount = 0, horizontalCount = 0;
        if (rules.DetectEdges)
        {
            var horizontal = PerspectiveDetection.Filter(rawLines, true, 0.005).Group();
            var vertical = PerspectiveDetection.Filter(rawLines, false, 0.005);
            double midLeft = layout.Transform.Forward(new(0, rules.Area.Height / 2.0)).X;
            double midRight = layout.Transform.Forward(new(rules.Area.Width, rules.Area.Height / 2.0)).X;
            vertical = new DetectionLines(vertical.Values.Where(l => midLeft < l.Rho && l.Rho < midRight), false).Group();
            horizontalCount = horizontal.Values.Length; verticalCount = vertical.Values.Length;
            double[] Filter(DetectionLines lines, double offset, int tile) => lines.Values.Select(l => l.Rho)
                .Where(r => DetectionFit.Mod(r - offset, tile) < 9 || DetectionFit.Mod(r - offset, tile) > tile - 9).ToArray();
            (lower, upper) = DetectionFit.Separate(Filter(horizontal, location.Y, rules.Tile.Y), interior.Y);
            (left, right) = DetectionFit.Separate(Filter(vertical, location.X, rules.Tile.X), interior.X);
        }
        static bool Present(double? v) => v is not null and not 0;
        double x0 = Present(left) ? left!.Value - 9 : 0, y0 = Present(lower) ? lower!.Value - 9 : 0;
        double x1 = Present(right) ? right!.Value + 9 : layout.Size.X, y1 = Present(upper) ? upper!.Value + 9 : layout.Size.Y;
        var xs = Enumerable.Range(-25, 50).Select(i => i * rules.Tile.X + location.X).Where(x => x > x0 && x < x1).ToArray();
        var ys = Enumerable.Range(-25, 50).Select(i => i * rules.Tile.Y + location.Y).Where(y => y > y0 && y < y1).ToArray();
        var points = ys.SelectMany(y => xs.Select(x =>
        {
            var p = layout.Transform.Inverse(new(x, y)); return new ScreenPoint(p.X + rules.Area.X, p.Y + rules.Area.Y);
        })).ToArray();
        return new(PerspectiveDetection.Generate(points, xs.Length, ys.Length), new(Present(left), Present(right), Present(lower), Present(upper)),
            location, interior, verticalCount, horizontalCount);
    }
    private static ScreenPoint Mean(IReadOnlyCollection<ScreenPoint> points)
    {
        if (points.Count == 0) throw new MapGeometryException("No matched grid locations");
        return new(points.Average(p => p.X), points.Average(p => p.Y));
    }
}

public sealed class MapViewSource(Func<CancellationToken, ValueTask<ScreenFrame>> capture, GridDetector detector) : IMapViewSource
{
    public async ValueTask<MapViewFrame> CaptureAsync(CancellationToken token) => await detector.DetectAsync(await capture(token), token);
}
