using System.Text.Json;

namespace Alas.Engine.Imaging;

public enum ProfileProcessing { Gray, Letters }
public sealed record TemplatePointsRequest(PixelArea Area, ReadOnlyMemory<byte> Template, double Similarity = .85,
    ProfileProcessing Processing = ProfileProcessing.Gray, PixelColor Letter = default, int Threshold = 153);
public sealed record TemplatePoints(PixelPoint TemplateSize, IReadOnlyList<PixelPoint> Points);
public interface IImageProfileVision
{
    ValueTask<IReadOnlyList<int>> ColorRowPeaksAsync(ScreenFrame frame, PixelArea area, PixelColor color,
        double height, double prominence, double distance, CancellationToken token);
    ValueTask<TemplatePoints> TemplatePointsAsync(ScreenFrame frame, TemplatePointsRequest request, CancellationToken token);
    ValueTask<IReadOnlyList<double>> LetterColumnMeansAsync(ScreenFrame frame, PixelArea area, PixelColor letter, int threshold, CancellationToken token);
}

public sealed partial class PythonTemplateVision : IImageProfileVision
{
    public ValueTask<IReadOnlyList<int>> ColorRowPeaksAsync(ScreenFrame frame, PixelArea area, PixelColor color,
        double height, double prominence, double distance, CancellationToken token)
    {
        ValidateFrame(frame); ValidateArea(area); ValidateColor(color);
        if (new[] { height, prominence, distance }.Any(v => !double.IsFinite(v)) || height is < 0 or > 255 || prominence < 0 || distance < 1)
            throw new ArgumentException("Invalid profile peak parameters");
        return ExchangeAsync<IReadOnlyList<int>>(frame, id => new { protocol = "alas-cv/1", id, frame = frame.Sequence,
            operation = "color_profile_peaks", image = Convert.ToBase64String(frame.Png.Span), area = AreaValues(area),
            color = new[] { color.R, color.G, color.B }, height, prominence, distance }, r =>
            {
                var peaks = r.GetProperty("peaks").EnumerateArray().Select(p => p.GetInt32()).ToArray();
                if (peaks.Length > area.Height || peaks.Any(p => p < 0 || p >= area.Height) || !peaks.SequenceEqual(peaks.Distinct().Order()))
                    throw new InvalidDataException("Invalid row peaks");
                return peaks;
            }, token);
    }
    public ValueTask<TemplatePoints> TemplatePointsAsync(ScreenFrame frame, TemplatePointsRequest request, CancellationToken token)
    {
        ValidateFrame(frame); ValidateArea(request.Area); ValidateImage(request.Template); ValidateColor(request.Letter);
        if (!Enum.IsDefined(request.Processing) || !double.IsFinite(request.Similarity) || request.Similarity is < -1 or > 1 || request.Threshold is < 1 or > 255)
            throw new ArgumentException("Invalid template profile parameters");
        return ExchangeAsync(frame, id => new { protocol = "alas-cv/1", id, frame = frame.Sequence, operation = "template_points",
            image = Convert.ToBase64String(frame.Png.Span), area = AreaValues(request.Area), template = Convert.ToBase64String(request.Template.Span),
            preprocessing = request.Processing.ToString().ToLowerInvariant(), letter = new[] { request.Letter.R, request.Letter.G, request.Letter.B },
            threshold = request.Threshold, similarity = request.Similarity }, r =>
            {
                var size = r.GetProperty("size");
                if (size.GetArrayLength() != 2) throw new InvalidDataException("Invalid template dimensions");
                var value = new PixelPoint(size[0].GetInt32(), size[1].GetInt32());
                if (value.X < 1 || value.Y < 1 || value.X > request.Area.Width || value.Y > request.Area.Height)
                    throw new InvalidDataException("Template exceeds profile image");
                var points = r.GetProperty("points");
                if (points.GetArrayLength() > 1_000_000) throw new InvalidDataException("Too many template points");
                return new TemplatePoints(value, points.EnumerateArray().Select(p => ReadPoint(p,
                    new(request.Area.Width - value.X + 1, request.Area.Height - value.Y + 1))).ToArray());
            }, token, 24 * 1024 * 1024);
    }
    public ValueTask<IReadOnlyList<double>> LetterColumnMeansAsync(ScreenFrame frame, PixelArea area, PixelColor letter, int threshold, CancellationToken token)
    {
        ValidateFrame(frame); ValidateArea(area); ValidateColor(letter);
        if (threshold is < 1 or > 255) throw new ArgumentException("Invalid letter threshold");
        return ExchangeAsync<IReadOnlyList<double>>(frame, id => new { protocol = "alas-cv/1", id, frame = frame.Sequence,
            operation = "letter_column_means", image = Convert.ToBase64String(frame.Png.Span), area = AreaValues(area),
            letter = new[] { letter.R, letter.G, letter.B }, threshold }, r =>
            {
                var values = r.GetProperty("means").EnumerateArray().Select(p => p.GetDouble()).ToArray();
                if (values.Length != area.Width || values.Any(v => !double.IsFinite(v) || v is < 0 or > 255))
                    throw new InvalidDataException("Invalid column means");
                return values;
            }, token);
    }
    private static void ValidateColor(PixelColor color)
    { if (new[] { color.R, color.G, color.B }.Any(v => v is < 0 or > 255)) throw new ArgumentException("Invalid profile color"); }
}
