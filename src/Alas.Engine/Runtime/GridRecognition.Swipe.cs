using Alas.Engine.Imaging;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed partial class GridRecognition
{
    private async ValueTask<double> MeasureAsync(ScreenFrame frame, PixelArea area, int width, int height,
        PatchMeasure measure, PatchProcessing processing = PatchProcessing.Color, PixelColor color = default,
        AssetRule? template = null, int minimum = 0, HsvBounds hsv = default, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var bytes = template is null ? ReadOnlyMemory<byte>.Empty : await assets.ReadAsync(template.For(server), token);
        var value = await vision.MeasurePatchAsync(frame, new(area, width, height, measure, processing, color, bytes, minimum, hsv), token);
        if (value.FrameSequence != frame.Sequence || !double.IsFinite(value.Value) ||
            (measure == PatchMeasure.Template ? value.Value is < -1 or > 1 : value.Value < 0 || value.Value > width * height || value.Value != Math.Truncate(value.Value)))
            throw new InvalidDataException("Grid measurement has invalid frame identity or value");
        return value.Value;
    }

    private async ValueTask<bool> PredictFleetAsync(ScreenFrame frame, Projection projection, CancellationToken token)
        => await MeasureAsync(frame, projection.Crop(-1, -2, -0.5, -1.5), 50, 50,
            PatchMeasure.Template, PatchProcessing.ColorSimilarity, new(255, 255, 255),
            UiAssets.Template.TEMPLATE_FLEET_AMMO, token: token) > 0.85;

    private async ValueTask<bool> PredictCurrentFleetAsync(ScreenFrame frame, Projection projection, CancellationToken token)
    {
        var area = projection.Crop(-0.5, -3.5, 0.5, -2.5);
        return await MeasureAsync(frame, area, 50, 50, PatchMeasure.HsvCount, hsv: new(138, 151), token: token) >= 600 &&
            await MeasureAsync(frame, area, 60, 60, PatchMeasure.Template, PatchProcessing.ColorSimilarity,
                new(24, 255, 107), UiAssets.Template.TEMPLATE_FLEET_CURRENT, token: token) > 0.85;
    }

    /// <summary>Independent native predict_fleet/current_fleet, without submarine suppression from predict().</summary>
    public async ValueTask<FleetMarker> RawFleetAsync(MapViewFrame view, VisibleGrid grid, CancellationToken token = default)
    {
        rules.Validate();
        var projection = new Projection(grid.Corners, rules.ImageScale);
        bool fleet = await PredictFleetAsync(view.Frame, projection, token);
        bool current = await PredictCurrentFleetAsync(view.Frame, projection, token);
        return new(fleet, current);
    }

    internal PixelArea SimilarityArea(VisibleGrid grid, bool full = false)
    {
        rules.Validate();
        double extent = full ? 0.6 : 0.5;
        return new Projection(grid.Corners, rules.ImageScale).Crop(-extent, -extent, extent, extent);
    }
}

/// <summary>Numeric CV adapter; mask selection and all thresholds stay in C#.</summary>
public sealed class MapSwipeEvidence(GridRecognition recognition, IVision colors, IImagePairVision pairs,
    ScreenFrame detectionMask, PixelPoint maskOrigin) : IMapSwipeEvidence
{
    public static readonly SourceFile MaskSource = new("module/map_detection/utils_assets.py",
        "45bf68025782c9daee80603662977c4e379c5b2eae97743cb0af1ef29d2c4882");
    public ValueTask<FleetMarker> FleetAsync(MapViewFrame view, VisibleGrid grid, CancellationToken token)
        => recognition.RawFleetAsync(view, grid, token);

    public async ValueTask<double?> SimilarityAsync(MapViewFrame before, VisibleGrid oldGrid,
        MapViewFrame after, VisibleGrid newGrid, CancellationToken token)
    {
        var oldArea = recognition.SimilarityArea(oldGrid);
        var newArea = recognition.SimilarityArea(newGrid);
        async ValueTask<bool> Inside(PixelArea area)
        {
            // Native GridPredictor adds the global DETECTING_AREA origin before mask cropping.
            var value = await colors.MeanColorAsync(detectionMask, area with
                { X = checked(area.X + maskOrigin.X), Y = checked(area.Y + maskOrigin.Y) }, token);
            if (value.FrameSequence != detectionMask.Sequence || !double.IsFinite(value.R) || value.R is < 0 or > 255)
                throw new InvalidDataException("Invalid map mask measurement");
            return value.R > 235;
        }
        if (!await Inside(oldArea) || !await Inside(newArea)) return null;
        var measured = await pairs.CompareAsync(before.Frame, after.Frame,
            new(oldArea, new(60, 60), recognition.SimilarityArea(newGrid, full: true), new(72, 72)), token);
        if (measured.FirstSequence != before.Frame.Sequence || measured.SecondSequence != after.Frame.Sequence ||
            !double.IsFinite(measured.Value) || measured.Value is < -1 or > 1)
            throw new InvalidDataException("Invalid paired image measurement");
        return measured.Value;
    }
}
