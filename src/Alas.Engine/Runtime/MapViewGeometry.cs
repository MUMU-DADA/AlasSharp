using System.Collections.Immutable;
using Alas.Engine.Imaging;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed class MapGeometryException(string message) : Exception(message);
public sealed class CameraOutsideViewException(ViewCell offset, ViewCell center)
    : Exception($"Camera outside detected view: offset=({offset.X}, {offset.Y})")
{
    public ViewCell Offset { get; } = offset;
    public ViewCell Center { get; } = center;
}
public readonly record struct MapEdges(bool Left = false, bool Right = false, bool Lower = false, bool Upper = false);

/// <summary>Projection of a detected quadrilateral. Grid coordinates start at its upper-left corner.</summary>
public sealed class GridGeometry
{
    public static readonly SourceFile Source = new("module/map_detection/grid.py",
        "a4272524f1abf82273d10f6b3675cb2899acdce82aee65ac7ef01f78b5cb641b");
    public static readonly SourceFile AreaSource = new("module/map_detection/utils.py",
        "7a0c274e6aa752512ab584f418991b275397c735033687cf11da16155c7f479a");
    private readonly double[] _forward, _inverse;
    private readonly PixelPoint _tile;
    public GridCorners Corners { get; }
    public PixelArea Bounds { get; }
    public PixelArea Inner { get; }
    public PixelArea Outer { get; }

    public GridGeometry(GridCorners corners, PixelPoint tile)
    {
        ArgumentNullException.ThrowIfNull(corners);
        if (tile.X <= 0 || tile.Y <= 0) throw new ArgumentOutOfRangeException(nameof(tile));
        var points = new[] { corners.TopLeft, corners.TopRight, corners.BottomLeft, corners.BottomRight };
        if (points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y) || Math.Abs(p.X) > 1e7 || Math.Abs(p.Y) > 1e7))
            throw new ArgumentException("Non-finite or unbounded grid corners", nameof(corners));
        var perimeter = new[] { points[0], points[1], points[3], points[2] };
        for (int i = 0; i < 4; i++)
        {
            var a = perimeter[i]; var b = perimeter[(i + 1) % 4]; var c = perimeter[(i + 2) % 4];
            if ((b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X) <= 0)
                throw new ArgumentException("Grid corners must form a convex clockwise quadrilateral", nameof(corners));
        }
        Corners = corners;
        _tile = tile;
        Bounds = Area(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));
        Inner = PaddedArea(corners, 5);
        Outer = PaddedArea(corners, -5);
        // getPerspectiveTransform receives float32 corners in the upstream GridPredictor.
        _forward = Solve(points.Select(p => new ScreenPoint((float)p.X, (float)p.Y)).ToArray(),
            [new(0, 0), new(tile.X, 0), new(0, tile.Y), new(tile.X, tile.Y)]);
        _inverse = Invert(_forward);
    }

    public ScreenPoint ScreenToGrid(ScreenPoint point)
    {
        var result = Transform(point, _forward);
        return new(result.X / _tile.X, result.Y / _tile.Y);
    }
    public ScreenPoint GridToScreen(ScreenPoint point) => Transform(new(point.X * _tile.X, point.Y * _tile.Y), _inverse);

    public static PixelArea PaddedArea(GridCorners corners, int padding)
    {
        ArgumentNullException.ThrowIfNull(corners);
        var (a, b, c, d) = corners;
        var area = padding > 0 ? Area(Math.Max(a.X, c.X), Math.Max(a.Y, b.Y), Math.Min(b.X, d.X), Math.Min(c.Y, d.Y)) :
            padding < 0 ? Area(Math.Min(a.X, c.X), Math.Min(a.Y, b.Y), Math.Max(b.X, d.X), Math.Max(c.Y, d.Y)) :
            Area(Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X)), Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y)),
                Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X)), Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y)));
        return Pad(area, padding);
    }

    private static PixelArea Area(double left, double top, double right, double bottom)
    {
        int x = checked((int)Math.Round(left)), y = checked((int)Math.Round(top));
        return new(x, y, checked((int)Math.Round(right) - x), checked((int)Math.Round(bottom) - y));
    }
    private static PixelArea Pad(PixelArea area, int pad) => new(checked(area.X + pad), checked(area.Y + pad),
        checked(area.Width - 2 * pad), checked(area.Height - 2 * pad));
    private static ScreenPoint Transform(ScreenPoint point, double[] matrix)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) throw new ArgumentException("Invalid projection point", nameof(point));
        double divisor = matrix[6] * point.X + matrix[7] * point.Y + matrix[8];
        double x = (matrix[0] * point.X + matrix[1] * point.Y + matrix[2]) / divisor;
        double y = (matrix[3] * point.X + matrix[4] * point.Y + matrix[5]) / divisor;
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new MapGeometryException("Projection intersects the horizon");
        return new(x, y);
    }
    private static double[] Solve(ScreenPoint[] source, ScreenPoint[] destination)
    {
        var equations = new double[8, 9];
        for (int i = 0; i < 4; i++)
        {
            var (x, y) = source[i]; var (u, v) = destination[i];
            // Keep all x equations before y, including float32 products, as in the CV solver.
            equations[i, 0] = x; equations[i, 1] = y; equations[i, 2] = 1;
            equations[i, 6] = -(float)x * (float)u; equations[i, 7] = -(float)y * (float)u; equations[i, 8] = u;
            equations[i + 4, 3] = x; equations[i + 4, 4] = y; equations[i + 4, 5] = 1;
            equations[i + 4, 6] = -(float)x * (float)v; equations[i + 4, 7] = -(float)y * (float)v; equations[i + 4, 8] = v;
        }
        for (int column = 0; column < 8; column++)
        {
            int pivot = column;
            for (int row = column + 1; row < 8; row++)
                if (Math.Abs(equations[row, column]) > Math.Abs(equations[pivot, column])) pivot = row;
            if (Math.Abs(equations[pivot, column]) < 1e-12) throw new MapGeometryException("Degenerate grid projection");
            for (int j = 0; j <= 8; j++)
                (equations[column, j], equations[pivot, j]) = (equations[pivot, j], equations[column, j]);
            double reciprocal = -1 / equations[column, column];
            for (int row = column + 1; row < 8; row++)
            {
                double factor = equations[row, column] * reciprocal;
                for (int j = column + 1; j <= 8; j++) equations[row, j] += factor * equations[column, j];
            }
        }
        for (int row = 7; row >= 0; row--)
        {
            double result = equations[row, 8];
            for (int column = row + 1; column < 8; column++) result -= equations[row, column] * equations[column, 8];
            equations[row, 8] = result / equations[row, row];
        }
        return [equations[0, 8], equations[1, 8], equations[2, 8], equations[3, 8], equations[4, 8],
            equations[5, 8], equations[6, 8], equations[7, 8], 1];
    }
    private static double[] Invert(double[] m)
    {
        double[] adjugate = [m[4] * m[8] - m[5] * m[7], m[2] * m[7] - m[1] * m[8], m[1] * m[5] - m[2] * m[4],
            m[5] * m[6] - m[3] * m[8], m[0] * m[8] - m[2] * m[6], m[2] * m[3] - m[0] * m[5],
            m[3] * m[7] - m[4] * m[6], m[1] * m[6] - m[0] * m[7], m[0] * m[4] - m[1] * m[3]];
        double determinant = m[0] * adjugate[0] + m[1] * adjugate[3] + m[2] * adjugate[6];
        if (determinant == 0 || !double.IsFinite(determinant)) throw new MapGeometryException("Singular grid projection");
        return adjugate.Select(value => value / determinant).ToArray();
    }
}

/// <summary>Native View.load geometry, owned in C#; detector inputs contain polygons and edge measurements only.</summary>
public sealed class MapViewGeometry
{
    public static readonly SourceFile Source = new("module/map_detection/view.py",
        "95edd921ea68fc636a352dad57461a3901730dfea5bee8f60559660db46ce7ce");
    public ImmutableArray<VisibleGrid> Grids { get; }
    public ImmutableDictionary<ViewCell, GridGeometry> Projections { get; }
    public ViewCell Shape { get; }
    public ViewCell Center { get; }
    public ViewCell Origin { get; }
    public ScreenPoint CenterOffset { get; }
    public ScreenPoint SwipeBase { get; }
    public MapEdges Edges { get; }

    public MapViewGeometry(IEnumerable<VisibleGrid> candidates, PixelArea detectingArea, ScreenPoint screenCenter,
        PixelPoint tileSize, MapEdges edges = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (detectingArea.Width <= 0 || detectingArea.Height <= 0) throw new ArgumentOutOfRangeException(nameof(detectingArea));
        var seen = new HashSet<ViewCell>();
        var accepted = new List<(VisibleGrid Grid, GridGeometry Projection)>();
        foreach (var grid in candidates)
        {
            if (grid is null || !seen.Add(grid.LocalCell)) throw new ArgumentException("Duplicate or missing detected grid", nameof(candidates));
            var area = GridGeometry.PaddedArea(grid.Corners, 0);
            // Native area_in_area includes a five-pixel margin, using rounded corner bounds.
            if (area.X >= (long)detectingArea.X - 5 && area.Y >= (long)detectingArea.Y - 5 &&
                (long)area.X + area.Width <= (long)detectingArea.X + detectingArea.Width + 5 &&
                (long)area.Y + area.Height <= (long)detectingArea.Y + detectingArea.Height + 5)
                accepted.Add((grid, new GridGeometry(grid.Corners, tileSize)));
        }
        if (accepted.Count == 0) throw new MapGeometryException("No map grids found");
        Origin = new(accepted.Min(g => g.Grid.LocalCell.X), accepted.Min(g => g.Grid.LocalCell.Y));
        Grids = accepted.Select(g => g.Grid with { LocalCell = new(checked(g.Grid.LocalCell.X - Origin.X), checked(g.Grid.LocalCell.Y - Origin.Y)) }).ToImmutableArray();
        Projections = Grids.Select((grid, index) => new KeyValuePair<ViewCell, GridGeometry>(grid.LocalCell, accepted[index].Projection)).ToImmutableDictionary();
        Shape = new(Grids.Max(g => g.LocalCell.X), Grids.Max(g => g.LocalCell.Y));
        var first = accepted[0].Projection;
        var offset = first.ScreenToGrid(screenCenter);
        // numpy.astype(int) truncates toward zero, including negative coordinates.
        int dx = checked((int)offset.X), dy = checked((int)offset.Y);
        Center = new(checked(Grids[0].LocalCell.X + dx), checked(Grids[0].LocalCell.Y + dy));
        if (!Projections.TryGetValue(Center, out var center))
            throw new CameraOutsideViewException(new(Center.X > 0 ? Math.Max(Center.X - Shape.X, 0) : Center.X,
                Center.Y > 0 ? Math.Max(Center.Y - Shape.Y, 0) : Center.Y), Center);
        CenterOffset = center.ScreenToGrid(screenCenter);
        static double Distance(ScreenPoint a, ScreenPoint b) => double.Hypot(a.X - b.X, a.Y - b.Y);
        SwipeBase = new(Distance(first.GridToScreen(new(dx + 0.5, dy)), first.GridToScreen(new(dx - 0.5, dy))),
            Distance(first.GridToScreen(new(dx, dy + 0.5)), first.GridToScreen(new(dx, dy - 0.5))));
        if (SwipeBase.X <= 0 || SwipeBase.Y <= 0) throw new MapGeometryException("Invalid projected swipe distance");
        Edges = edges;
    }
}
