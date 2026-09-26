namespace Alas.Engine.Imaging;

public readonly record struct PixelColor(int R, int G, int B);
public readonly record struct HsvBounds(double HLow, double HHigh, double SLow = 0, double SHigh = 100,
    double VLow = 0, double VHigh = 100);
public enum PatchMeasure { Template, SimilarityCount, HsvCount }
public enum PatchProcessing { Color, Gray, ColorSimilarity }
public sealed record ImagePatchRequest(PixelArea Area, int Width, int Height, PatchMeasure Measure,
    PatchProcessing Processing = PatchProcessing.Color, PixelColor Color = default,
    ReadOnlyMemory<byte> Template = default, int MinimumSimilarity = 0, HsvBounds Hsv = default);
public sealed record ImagePatchObservation(long FrameSequence, double Value);

/// <summary>Crop/resize and numeric image measurements only; no map state or asset names.</summary>
public interface IImagePatchVision
{
    ValueTask<ImagePatchObservation> MeasurePatchAsync(ScreenFrame frame, ImagePatchRequest request,
        CancellationToken token = default);
}
