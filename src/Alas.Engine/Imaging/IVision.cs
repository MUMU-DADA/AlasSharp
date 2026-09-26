namespace Alas.Engine.Imaging;

public readonly record struct PixelPoint(int X, int Y);
public readonly record struct PixelArea(int X, int Y, int Width, int Height);
public sealed record ScreenFrame(long Sequence, DateTimeOffset CapturedAt, ReadOnlyMemory<byte> Png);
public enum TemplatePreprocessing { Color, Luma, Binary }
public sealed record TemplateRequest(ReadOnlyMemory<byte> TemplatePng, PixelArea SearchArea, double Similarity,
    TemplatePreprocessing Preprocessing = TemplatePreprocessing.Color, PixelArea? TemplateArea = null);
public sealed record TemplateObservation(long FrameSequence, bool Matched, double Similarity, PixelPoint? Location);
public sealed record MeanColorObservation(long FrameSequence, double R, double G, double B);
public sealed record ColorBandRequest(PixelArea Area, int R, int G, int B, int ClosingSize,
    int RowThreshold, double PeakHeight, double PeakWidth, double PeakDistance, double RelativeHeight);
public sealed record ColorBand(int Top, int Bottom);
public sealed record ColorBandObservation(long FrameSequence, IReadOnlyList<ColorBand> Bands);
public enum OcrPreprocessing { Letters, Luma, Grayscale }
public sealed record OcrRequest(PixelArea Area, string Language, string? Alphabet,
    int LetterR = 255, int LetterG = 255, int LetterB = 255, int Threshold = 128,
    OcrPreprocessing Preprocessing = OcrPreprocessing.Letters);
public sealed record OcrObservation(long FrameSequence, string Text, double? Confidence);

/// <summary>Pure image computation. No devices, campaign ids, task names, or business RPC.</summary>
public interface IVision : IAsyncDisposable
{
    ValueTask<TemplateObservation> MatchAsync(ScreenFrame frame, TemplateRequest request, CancellationToken token = default);
    ValueTask<MeanColorObservation> MeanColorAsync(ScreenFrame frame, PixelArea area, CancellationToken token = default);
    ValueTask<ColorBandObservation> ColorBandsAsync(ScreenFrame frame, ColorBandRequest request, CancellationToken token = default);
    ValueTask<OcrObservation> ReadTextAsync(ScreenFrame frame, OcrRequest request, CancellationToken token = default);
}
