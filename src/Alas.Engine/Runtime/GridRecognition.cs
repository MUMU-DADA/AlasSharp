using Alas.Engine.Imaging;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public readonly record struct ScreenPoint(double X, double Y);
/// <summary>Geometry from line detection: top-left, top-right, bottom-left, bottom-right.</summary>
public sealed record GridCorners(ScreenPoint TopLeft, ScreenPoint TopRight, ScreenPoint BottomLeft, ScreenPoint BottomRight);
public sealed record VisibleGrid(ViewCell LocalCell, GridCorners Corners);

/// <summary>Native GridPredictor decisions in C#. The CV service returns numeric measurements only.</summary>
public sealed class GridRecognition(IImagePatchVision vision, AssetFiles assets, GameServer server, GridRecognitionRules rules)
{
    public static readonly SourceFile Source = new("module/map_detection/grid_predictor.py",
        "7e191b0c48ceb89ecb453742e559b7c4b26fdb920c80e1e128d4e79331d26caa");

    /// <summary>The input frame must already have the upstream map UI mask applied.</summary>
    public async ValueTask<MapObservation> ObserveAsync(ScreenFrame frame, IReadOnlyList<VisibleGrid> grids,
        Cell camera, ViewCell center, MapScanMode mode = MapScanMode.Normal, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(grids);
        rules.Validate();
        if (!Enum.IsDefined(mode) || grids.Any(g => g is null) || grids.Select(g => g.LocalCell).Distinct().Count() != grids.Count)
            throw new ArgumentException("Invalid visual grid layout", nameof(grids));
        // Construct a whole view before exposing it to authoritative map state.
        var observations = new List<MapCellObservation>();
        foreach (var grid in grids)
            observations.Add(new(grid.LocalCell, await PredictAsync(frame, grid.Corners, token)));
        return new(observations.AsReadOnly(), camera, center, mode);
    }

    public async ValueTask<CellObservation> PredictAsync(ScreenFrame frame, GridCorners corners, CancellationToken token = default)
    {
        rules.Validate();
        var projection = new Projection(corners, rules.ImageScale);
        async ValueTask<double> Measure(double x0, double y0, double x1, double y1, int width, int height,
            PatchMeasure measure, PatchProcessing processing = PatchProcessing.Color, PixelColor color = default,
            AssetRule? template = null, int minimum = 0, HsvBounds hsv = default)
        {
            token.ThrowIfCancellationRequested();
            var bytes = template is null ? ReadOnlyMemory<byte>.Empty : await assets.ReadAsync(template.For(server), token);
            var value = await vision.MeasurePatchAsync(frame, new(projection.Crop(x0, y0, x1, y1), width, height,
                measure, processing, color, bytes, minimum, hsv), token);
            if (value.FrameSequence != frame.Sequence || !double.IsFinite(value.Value) ||
                (measure == PatchMeasure.Template ? value.Value is < -1 or > 1 : value.Value < 0 || value.Value > width * height || value.Value != Math.Truncate(value.Value)))
                throw new InvalidDataException("Grid measurement has invalid frame identity or value");
            return value.Value;
        }
        async ValueTask<bool> Match(double x0, double y0, double x1, double y1, int width, int height,
            AssetRule template, double threshold = 0.85, PixelColor? color = null)
            => await Measure(x0, y0, x1, y1, width, height, PatchMeasure.Template,
                color.HasValue ? PatchProcessing.ColorSimilarity : PatchProcessing.Gray, color ?? default, template) > threshold;
        ValueTask<double> Hsv(double x0, double y0, double x1, double y1, int width, int height, double low, double high)
            => Measure(x0, y0, x1, y1, width, height, PatchMeasure.HsvCount, hsv: new(low, high));
        ValueTask<double> Rgb(double x0, double y0, double x1, double y1, int width, int height, PixelColor color, int minimum = 221)
            => Measure(x0, y0, x1, y1, width, height, PatchMeasure.SimilarityCount, PatchProcessing.ColorSimilarity, color, minimum: minimum);

        int scale = await Match(-0.415 - 0.7, -0.62 - 0.7, -0.415, -0.62, 50, 50, UiAssets.Template.TEMPLATE_ENEMY_L, 0.75, new(255, 130, 132)) ? 3 :
            await Match(-0.415 - 0.7, -0.62 - 0.7, -0.415, -0.62, 50, 50, UiAssets.Template.TEMPLATE_ENEMY_M, color: new(255, 235, 156)) ? 2 :
            await Match(-0.415 - 0.7, -0.62 - 0.7, -0.415, -0.62, 50, 50, UiAssets.Template.TEMPLATE_ENEMY_S, color: new(255, 235, 156)) ? 1 : 0;

        async ValueTask<string?> Genre()
        {
            if (rules.SirenHasBossIcon)
            {
                if (scale != 0) return "";
                if (await Rgb(-0.55, -0.2, 0.45, 0.2, 50, 20, new(255, 150, 24), minimum: 222) > 200 &&
                    await Match(-0.55, -0.2, 0.45, 0.2, 50, 20, UiAssets.Template.TEMPLATE_ENEMY_BOSS, 0.6, new(255, 150, 24)))
                    return "Siren_Siren";
            }
            if (rules.SirenHasSmallBossIcon && await Hsv(0.03, -0.15, 0.63, 0.15, 50, 20, 29, 35) > 100 &&
                await Match(0.03, -0.15, 0.63, 0.15, 50, 20, UiAssets.Template.TEMPLATE_ENEMY_BOSS, 0.7, new(255, 150, 33)))
                return "Siren_Siren";
            foreach (var entry in rules.Enemies.Concat(rules.HasSiren ? rules.Sirens : []))
                foreach (double factor in entry.Scales)
                {
                    int size = checked((int)Math.Round(60 * factor));
                    if (await Match(-0.5, -1, 0.5, 0, size, size, entry.Template, rules.GenreSimilarity)) return entry.Genre;
                }
            return null;
        }

        string? genre = await Genre();
        bool boss = genre != "Siren_Siren" &&
            (await Match(-0.55, -0.2, 0.45, 0.2, 50, 20, UiAssets.Template.TEMPLATE_ENEMY_BOSS, 0.75, new(255, 77, 82)) ||
             await Hsv(0.03, -0.15, 0.63, 0.15, 50, 20, 355, 361) > 100 &&
             await Match(0.03, -0.15, 0.63, 0.15, 50, 20, UiAssets.Template.TEMPLATE_ENEMY_BOSS, 0.7, new(255, 77, 82)));
        bool submarine = await Match(-0.86, 0.08, -0.36, 0.58, 50, 50, UiAssets.Template.TEMPLATE_SUBMARINE, color: new(255, 243, 156));
        bool fleet = !submarine && await Match(-1, -2, -0.5, -1.5, 50, 50, UiAssets.Template.TEMPLATE_FLEET_AMMO, color: new(255, 255, 255));
        bool mystery = rules.HasMystery && await Rgb(-0.3, -2, 0.3, -0.6, 20, 50, new(148, 255, 247)) > 50;
        bool current = await Hsv(-0.5, -3.5, 0.5, -2.5, 50, 50, 138, 151) >= 600 &&
            await Match(-0.5, -3.5, 0.5, -2.5, 60, 60, UiAssets.Template.TEMPLATE_FLEET_CURRENT, color: new(24, 255, 107));
        bool missile = rules.HasMissileAttack && await Rgb(-0.5, -1, 0.5, 0, 50, 50, new(255, 255, 60)) > 35;
        bool enemy = !string.IsNullOrEmpty(genre) || scale != 0;
        if (enemy && string.IsNullOrEmpty(genre)) genre = "Enemy";
        bool siren = rules.HasSiren && genre?.StartsWith("Siren", StringComparison.Ordinal) == true;
        return new(IsSubmarine: submarine, IsFleet: fleet, IsCurrentFleet: current, IsBoss: boss, IsSiren: siren,
            IsEnemy: enemy, IsMystery: mystery, IsMissileAttack: missile, EnemyScale: siren ? 0 : scale, EnemyGenre: genre);
    }

    private sealed class Projection
    {
        private readonly double _x, _y, _a;
        public Projection(GridCorners corners, double scale)
        {
            ArgumentNullException.ThrowIfNull(corners);
            var (p0, p1, p2, p3) = corners;
            if (new[] { p0.X, p0.Y, p1.X, p1.Y, p2.X, p2.Y, p3.X, p3.Y }.Any(v => !double.IsFinite(v)) ||
                p0.X >= p1.X || p2.X >= p3.X || p0.Y >= p2.Y || p1.Y >= p3.Y)
                throw new ArgumentException("Invalid grid corners", nameof(corners));
            double divisor = p0.X - p1.X + p2.X - p3.X;
            _x = (p0.X * p2.X - p1.X * p3.X) / divisor;
            _y = (p0.X * p2.Y - p1.X * p2.Y + p2.X * p0.Y - p3.X * p0.Y) / divisor;
            _a = (-p0.X * p2.X + p0.X * p3.X + p1.X * p2.X - p1.X * p3.X) / divisor * scale;
            if (!double.IsFinite(_x) || !double.IsFinite(_y) || !double.IsFinite(_a) || _a <= 0)
                throw new ArgumentException("Degenerate grid projection", nameof(corners));
        }
        public PixelArea Crop(double x0, double y0, double x1, double y1)
        {
            int left = checked((int)Math.Round(_x + x0 * _a)), top = checked((int)Math.Round(_y + y0 * _a));
            int right = checked((int)Math.Round(_x + x1 * _a)), bottom = checked((int)Math.Round(_y + y1 * _a));
            return new(left, top, checked(right - left), checked(bottom - top));
        }
    }
}
