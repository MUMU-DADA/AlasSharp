using Alas.Engine.Imaging;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed record PerspectiveLayout(IReadOnlyList<VisibleGrid> Grids, MapEdges Edges,
    ScreenPoint VanishPoint, ScreenPoint DistantPoint, HomographyStorage Storage);

public static class PerspectiveDetection
{
    public static readonly SourceFile Source = new("module/map_detection/perspective.py",
        "43b5f8214796ee2ee6fbe4c1589711df602aee7fefd77fc4afd8b2508bb890b2");

    internal static DetectionLines Filter(IEnumerable<PolarLine> values, bool horizontal, double threshold)
    {
        double radians = threshold * Math.PI / 180;
        return new(values.Where(l => horizontal ? Math.PI / 2 - radians < l.Theta && l.Theta < Math.PI / 2 + radians :
                l.Theta < radians || Math.PI - radians < l.Theta)
            .Select(l => !horizontal && l.Rho < 0 ? new PolarLine(-l.Rho, (float)l.Theta - (float)Math.PI) : l), horizontal, singlePrecision: true);
    }

    public static PerspectiveLayout Detect(LineFeatures measured, MapDetectionRules rules, CancellationToken token = default)
    {
        rules.Validate();
        DetectionLines Lines(IReadOnlyList<PolarLine> values, bool horizontal) =>
            Filter(values, horizontal, horizontal ? rules.HorizontalTheta : rules.VerticalTheta).Move(rules.Area.X, rules.Area.Y);
        var ih = Lines(measured.InnerHorizontal, true); var iv = Lines(measured.InnerVertical, false);
        var eh = Lines(measured.EdgeHorizontal, true); var ev = Lines(measured.EdgeVertical, false);
        var horizontal = ih.Add(eh).Group(); var vertical = iv.Add(ev).Group();
        eh = eh.Group(); ev = ev.Group();
        if (!rules.TrustEdgeLines) { eh = eh.Delete(ih, rules.TrustEdgeThreshold); ev = ev.Delete(iv, rules.TrustEdgeThreshold); }
        if (horizontal.Values.Length == 0) throw new MapGeometryException("No horizontal line detected");
        if (vertical.Values.Length == 0) throw new MapGeometryException("No vertical line detected");
        var crossings = horizontal.Cross(vertical);
        var vanish = DetectionFit.Minimize(p => vertical.Values.Sum(l => Math.Log10(Math.Abs(l.Distance(new(p[0], p[1]))) + 0.001)),
            [rules.VanishX, rules.VanishY], token);
        var v = new ScreenPoint(vanish[0], vanish[1]);
        var distance = DetectionFit.Minimize(p =>
        {
            var mids = crossings.Select(c => PolarLine.Link(c, new(p[0], v.Y)).X(360)).Order().ToArray();
            return mids.Skip(1).Select((value, index) => Math.Log10(value - mids[index] + 0.001)).Sum();
        }, [rules.DistantX], token);
        var d = new ScreenPoint(distance[0], v.Y);
        if (double.Hypot(v.X - d.X, v.Y - d.Y) < 10) throw new MapGeometryException("Vanish point and distant point too close");
        var inner = new ScreenPoint(crossings.Average(p => p.X), crossings.Average(p => p.Y));

        (DetectionLines Lines, double? Lower, double? Upper) Cleanse(DetectionLines lines, DetectionLines internalLines, DetectionLines edge)
        {
            var clean = CleanMids(lines.Mids, lines.Horizontal, v, d, rules, token);
            var internalClean = internalLines.Mids.Where(m => clean.Any(c => Math.Abs(m - c) < 5)).ToArray();
            var edges = edge.Mids;
            if (internalClean.Length > 0)
                edges = edges.Where(e => e > internalClean.Max() - 3 || e < internalClean.Min() + 3).ToArray();
            edges = clean.Where(c => edges.Any(e => Math.Abs(c - e) < 5)).ToArray();
            var (lower, upper) = DetectionFit.Separate(edges, lines.Horizontal ? inner.Y : inner.X);
            // Python truthiness treats a measured zero edge as absent.
            if (lower is not null and not 0) clean = clean.Where(c => c > lower - 3).ToArray();
            if (upper is not null and not 0) clean = clean.Where(c => c < upper + 3).ToArray();
            var output = new DetectionLines(clean.Select(c => lines.Horizontal ? new PolarLine(c, Math.PI / 2) :
                PolarLine.Link(new(c, rules.ScreenCenter.Y), v)), lines.Horizontal);
            return (output, lower, upper);
        }
        var h = Cleanse(horizontal, ih.Group(), eh); var w = Cleanse(vertical, iv.Group(), ev);
        if (h.Lines.Values.Length < 2 || w.Lines.Values.Length < 2) throw new MapGeometryException("Insufficient cleaned grid lines");
        var points = h.Lines.Cross(w.Lines);
        var grids = Generate(points, w.Lines.Values.Length, h.Lines.Values.Length);
        int width = w.Lines.Values.Length, height = h.Lines.Values.Length;
        var corners = new GridCorners(points[0], points[width - 1], points[(height - 1) * width], points[^1]);
        return new(grids, new(w.Lower is not null and not 0, w.Upper is not null and not 0,
            h.Lower is not null and not 0, h.Upper is not null and not 0), v, d, new(new(width - 1, height - 1), corners));
    }

    private static double[] CleanMids(double[] mids, bool horizontal, ScreenPoint vanish, ScreenPoint distant,
        MapDetectionRules rules, CancellationToken token)
    {
        var right = new ScreenPoint(vanish.X * 2 - distant.X, distant.Y);
        double ToX(double y) => PolarLine.Link(new(rules.ScreenCenter.X, y), right).X(360);
        double ToY(double x) => PolarLine.Link(new(x, rules.ScreenCenter.Y), right).Y(rules.ScreenCenter.X);
        if (horizontal) mids = mids.Select(ToX).ToArray();
        var lines = new List<PolarLine>();
        for (int i = 0; i < mids.Length; i++)
            for (int n = rules.ErrorTolerance.X; n <= rules.ErrorTolerance.Y; n++)
            {
                double theta = Math.Atan(i + n);
                lines.Add(new(mids[i] * Math.Cos(theta), theta));
            }
        var diff = horizontal ? rules.MidHorizontal : rules.MidVertical;
        var fit = DetectionFit.Minimize(p => lines.Sum(l => DetectionFit.Activation(Math.Abs(p[0] - l.X(p[1])),
                rules.CoincidentEncourage * rules.CoincidentEncourage)),
            [new(-Math.Abs(rules.ErrorTolerance.X) * diff.High, 200), diff], token);
        double left = horizontal ? ToX(rules.Area.Y) : PolarLine.Link(new(rules.Area.X, rules.Area.Y), vanish).X(360);
        double rightBorder = horizontal ? ToX(rules.Area.Y + rules.Area.Height) :
            PolarLine.Link(new(rules.Area.X + rules.Area.Width, rules.Area.Y), vanish).X(360);
        var clean = Enumerable.Range(-25, 50).Select(i => i * fit[1] + fit[0]).Where(m => m > left - 3 && m < rightBorder + 3);
        return (horizontal ? clean.Select(ToY) : clean).ToArray();
    }

    internal static IReadOnlyList<VisibleGrid> Generate(IReadOnlyList<ScreenPoint> points, int columns, int rows)
    {
        var grids = new List<VisibleGrid>();
        for (int y = 0; y < rows - 1; y++)
            for (int x = 0; x < columns - 1; x++)
                grids.Add(new(new(x, y), new(points[y * columns + x], points[y * columns + x + 1],
                    points[(y + 1) * columns + x], points[(y + 1) * columns + x + 1])));
        return grids.AsReadOnly();
    }
}
