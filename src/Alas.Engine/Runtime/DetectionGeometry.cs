using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed class ProjectiveTransform
{
    private readonly double[] _matrix, _inverse;
    public IReadOnlyList<double> Matrix => Array.AsReadOnly(_matrix);
    public ProjectiveTransform(IReadOnlyList<ScreenPoint> source, IReadOnlyList<ScreenPoint> destination)
    {
        if (source.Count != 4 || destination.Count != 4 || source.Concat(destination).Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y)))
            throw new ArgumentException("A perspective transform requires four finite point pairs");
        static ScreenPoint Float(ScreenPoint p) => new((float)p.X, (float)p.Y);
        _matrix = GridGeometry.Solve(source.Select(Float).ToArray(), destination.Select(Float).ToArray());
        _inverse = GridGeometry.Invert(_matrix);
    }
    public ScreenPoint Forward(ScreenPoint point) => GridGeometry.Transform(point, _matrix);
    public ScreenPoint Inverse(ScreenPoint point) => GridGeometry.Transform(point, _inverse);
}

public readonly record struct PolarLine(double Rho, double Theta)
{
    public double X(double y) => (Rho - y * Math.Sin(Theta)) / Math.Cos(Theta);
    public double Y(double x) => (Rho - x * Math.Cos(Theta)) / Math.Sin(Theta);
    public double Distance(ScreenPoint point) => Rho - point.X * Math.Cos(Theta) - point.Y * Math.Sin(Theta);
    public static PolarLine Link(ScreenPoint from, ScreenPoint to)
    {
        double theta = -Math.Atan((from.X - to.X) / (from.Y - to.Y));
        return new(from.X * Math.Cos(theta) + from.Y * Math.Sin(theta), theta);
    }
}

internal sealed class DetectionLines(IEnumerable<PolarLine> values, bool horizontal, bool singlePrecision = false)
{
    public PolarLine[] Values { get; } = values.ToArray();
    public bool Horizontal => horizontal;
    private double Mid(PolarLine line) => horizontal ? line.Rho : singlePrecision
        ? ((float)line.Rho - 360 * MathF.Sin((float)line.Theta)) / MathF.Cos((float)line.Theta) : line.X(360);
    public double[] Mids => Values.Select(Mid).ToArray();
    public DetectionLines Add(DetectionLines other) => new(Values.Concat(other.Values), horizontal, singlePrecision && other.SinglePrecision);
    private bool SinglePrecision => singlePrecision;
    public DetectionLines Move(double x, double y) => new(Values.Select(l => l with { Rho = singlePrecision
        ? (float)l.Rho + (horizontal ? (float)y : (float)x * MathF.Cos((float)l.Theta) + (float)y * MathF.Sin((float)l.Theta))
        : l.Rho + (horizontal ? y : x * Math.Cos(l.Theta) + y * Math.Sin(l.Theta)) }), horizontal, singlePrecision);
    public DetectionLines Delete(DetectionLines other, double threshold)
    {
        var mids = other.Mids;
        return new(Values.Where((l, i) => !mids.Any(m => Math.Abs(m - (horizontal ? l.Rho : l.X(360))) < threshold)), horizontal);
    }
    public DetectionLines Group(double threshold = 3)
    {
        var sorted = Values.OrderBy(Mid).ToArray();
        var output = new List<PolarLine>();
        int begin = 0;
        for (int i = 1; i <= sorted.Length; i++)
        {
            if (i < sorted.Length && Mid(sorted[i]) - Mid(sorted[i - 1]) <= threshold) continue;
            var group = sorted[begin..i];
            double theta = group.Average(l => l.Theta);
            output.Add(new(horizontal ? group.Average(l => l.Rho) : group.Average(l => l.X(360)) * Math.Cos(theta) + 360 * Math.Sin(theta), theta));
            begin = i;
        }
        return new(output, horizontal);
    }
    public ScreenPoint[] Cross(DetectionLines other) => Values.SelectMany(a => other.Values.Select(b =>
    {
        double ca = Math.Cos(a.Theta), sa = Math.Sin(a.Theta), cb = Math.Cos(b.Theta), sb = Math.Sin(b.Theta);
        double d = ca * sb - sa * cb;
        if (Math.Abs(d) < 1e-15) throw new MapGeometryException("Parallel map grid lines");
        return new ScreenPoint((a.Rho * sb - sa * b.Rho) / d, (ca * b.Rho - a.Rho * cb) / d);
    })).ToArray();
}

/// <summary>Upstream grid-search followed by the standard non-adaptive Nelder-Mead minimizer.</summary>
internal static class DetectionFit
{
    public static double[] Minimize(Func<double[], double> objective, NumberRange[] ranges, CancellationToken token)
    {
        int n = ranges.Length;
        if (n is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(ranges));
        double[] best = new double[n]; double minimum = double.PositiveInfinity;
        double Evaluate(double[] p)
        {
            token.ThrowIfCancellationRequested();
            double value = objective(p);
            if (!double.IsFinite(value)) throw new MapGeometryException("Non-finite grid fitting objective");
            return value;
        }
        for (int i = 0; i < 20; i++)
            for (int j = 0; j < (n == 1 ? 1 : 20); j++)
            {
                double[] point = n == 1 ? [ranges[0].Low + i * ((ranges[0].High - ranges[0].Low) / 19)] :
                    [ranges[0].Low + i * ((ranges[0].High - ranges[0].Low) / 19), ranges[1].Low + j * ((ranges[1].High - ranges[1].Low) / 19)];
                double value = Evaluate(point);
                if (value < minimum) { minimum = value; best = point; }
            }
        var vertices = new double[n + 1][]; var scores = new double[n + 1];
        vertices[0] = best;
        for (int i = 0; i < n; i++)
        { vertices[i + 1] = (double[])best.Clone(); vertices[i + 1][i] = best[i] == 0 ? 0.00025 : best[i] * 1.05; }
        int evaluations = 0, iterations = 1, limit = n * 200;
        double Eval(double[] p) { if (evaluations >= limit) throw new FitLimitException(); evaluations++; return Evaluate(p); }
        void Sort()
        {
            var order = Enumerable.Range(0, n + 1).OrderBy(i => scores[i]).ToArray();
            vertices = order.Select(i => vertices[i]).ToArray(); scores = order.Select(i => scores[i]).ToArray();
        }
        for (int i = 0; i <= n; i++) scores[i] = Eval(vertices[i]);
        Sort();
        while (evaluations < limit && iterations < limit)
        {
            try
            {
                if (vertices.Skip(1).Max(v => v.Zip(vertices[0], (a, b) => Math.Abs(a - b)).Max()) <= 1e-4 &&
                    scores.Skip(1).Max(s => Math.Abs(scores[0] - s)) <= 1e-4) break;
                var center = Enumerable.Range(0, n).Select(d => vertices.Take(n).Sum(v => v[d]) / n).ToArray();
                double[] Mix(double a, double b) => center.Zip(vertices[n], (c, w) => a * c + b * w).ToArray();
                var reflect = Mix(2, -1); double reflected = Eval(reflect); bool shrink = false;
                if (reflected < scores[0])
                {
                    var expand = Mix(3, -2); double expanded = Eval(expand);
                    vertices[n] = expanded < reflected ? expand : reflect; scores[n] = Math.Min(expanded, reflected);
                }
                else if (reflected < scores[n - 1]) { vertices[n] = reflect; scores[n] = reflected; }
                else if (reflected < scores[n])
                {
                    var contract = Mix(1.5, -0.5); double contracted = Eval(contract);
                    if (contracted <= reflected) { vertices[n] = contract; scores[n] = contracted; } else shrink = true;
                }
                else
                {
                    var contract = Mix(0.5, 0.5); double contracted = Eval(contract);
                    if (contracted < scores[n]) { vertices[n] = contract; scores[n] = contracted; } else shrink = true;
                }
                if (shrink)
                    for (int i = 1; i <= n; i++)
                    { vertices[i] = vertices[0].Zip(vertices[i], (a, b) => a + 0.5 * (b - a)).ToArray(); scores[i] = Eval(vertices[i]); }
                iterations++;
            }
            catch (FitLimitException) { break; }
            finally { Sort(); }
        }
        return vertices[0];
    }
    private sealed class FitLimitException : Exception;
    public static double Mod(double value, double size) => value - Math.Floor(value / size) * size;
    public static ScreenPoint FitPoints(IReadOnlyList<ScreenPoint> points, ScreenPoint mod, double encourage, CancellationToken token)
    {
        if (points.Count == 0) throw new MapGeometryException("No grid match points");
        var normalized = points.Select(p => new ScreenPoint(Mod(p.X, mod.X), Mod(p.Y, mod.Y))).ToArray();
        var doubled = normalized.Select(p => new ScreenPoint(p.X - mod.X, p.Y - mod.Y)).Concat(normalized).ToArray();
        var fit = Minimize(p => doubled.Sum(q => Activation(double.Hypot(q.X - p[0], q.Y - p[1]), encourage * encourage)),
            [new(-mod.X - 10, mod.X + 10), new(-mod.Y - 10, mod.Y + 10)], token);
        return new(Mod(fit[0], mod.X), Mod(fit[1], mod.Y));
    }
    public static double Activation(double distance, double encourageSquared)
        => distance == 0 ? 0 : 1 / (1 + Math.Exp(encourageSquared / distance) / distance);
    public static (double? Lower, double? Upper) Separate(double[] edges, double inner)
    {
        if (edges.Length == 0) return (null, null);
        if (edges.Length == 1) return edges[0] > inner ? (null, edges[0]) : (edges[0], null);
        var lower = edges.Where(e => e < inner).ToArray(); var upper = edges.Where(e => e > inner).ToArray();
        return (lower.Length == 0 ? null : lower[0], upper.Length == 0 ? null : upper[^1]);
    }
}
