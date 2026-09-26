using System.Buffers.Binary;
using System.Text.Json;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Imaging;

public sealed record LineFeatures(IReadOnlyList<PolarLine> InnerHorizontal, IReadOnlyList<PolarLine> InnerVertical,
    IReadOnlyList<PolarLine> EdgeHorizontal, IReadOnlyList<PolarLine> EdgeVertical);
public sealed record CorrelationFeatures(double Maximum, PixelPoint Location, IReadOnlyList<PixelPoint> AboveThreshold);
public sealed record WarpedFeatures(ScreenFrame Edges, IReadOnlyList<PolarLine> Lines);
public interface IGridFeatureVision
{
    ValueTask<ScreenFrame> MaskAsync(ScreenFrame frame, ReadOnlyMemory<byte> mask, PixelPoint origin, CancellationToken token);
    ValueTask<LineFeatures> LinesAsync(ScreenFrame frame, ReadOnlyMemory<byte> mask, MapDetectionRules rules, CancellationToken token);
    ValueTask<WarpedFeatures> WarpAsync(ScreenFrame frame, ReadOnlyMemory<byte> mask, ProjectiveTransform transform,
        PixelPoint size, MapDetectionRules rules, CancellationToken token);
    ValueTask<CorrelationFeatures> CorrelateAsync(ScreenFrame frame, ReadOnlyMemory<byte> template, double threshold, int? flip, CancellationToken token);
    ValueTask<IReadOnlyList<IReadOnlyList<PixelArea>>> RectanglesAsync(ScreenFrame frame, CancellationToken token);
}

public sealed partial class PythonTemplateVision : IGridFeatureVision
{
    public ValueTask<ScreenFrame> MaskAsync(ScreenFrame frame, ReadOnlyMemory<byte> mask, PixelPoint origin, CancellationToken token)
    {
        ValidateFrame(frame); ValidateImage(mask);
        var size = PngSize(frame.Png);
        return ExchangeAsync(frame, id => new { protocol = "alas-cv/1", id, frame = frame.Sequence, operation = "image_mask",
            image = Convert.ToBase64String(frame.Png.Span), mask = Convert.ToBase64String(mask.Span), origin = new[] { origin.X, origin.Y } },
            result => DecodeFrame(frame, result.GetProperty("image"), size), token, 24 * 1024 * 1024);
    }
    public ValueTask<LineFeatures> LinesAsync(ScreenFrame frame, ReadOnlyMemory<byte> mask, MapDetectionRules rules, CancellationToken token)
    {
        ValidateFrame(frame); ValidateImage(mask); rules.Validate();
        static object Peaks(LinePeakParameters p) => new { height = new[] { p.Height.Low, p.Height.High },
            width = p.Width is { } w ? new[] { w.Low, w.High } : null, prominence = p.Prominence, distance = p.Distance, wlen = p.Window };
        return ExchangeAsync(frame, id => new { protocol = "alas-cv/1", id, frame = frame.Sequence, operation = "line_features",
            image = Convert.ToBase64String(frame.Png.Span), mask = Convert.ToBase64String(mask.Span), area = AreaValues(rules.Area),
            inner_peaks = Peaks(rules.InternalPeaks), edge_peaks = Peaks(rules.EdgePeaks), inner_hough = rules.InternalLinesThreshold,
            edge_hough = rules.EdgeLinesThreshold }, r => new LineFeatures(ReadLines(r.GetProperty("inner_h")), ReadLines(r.GetProperty("inner_v")),
                ReadLines(r.GetProperty("edge_h")), ReadLines(r.GetProperty("edge_v"))), token, 4 * 1024 * 1024);
    }
    public ValueTask<WarpedFeatures> WarpAsync(ScreenFrame frame, ReadOnlyMemory<byte> mask, ProjectiveTransform transform,
        PixelPoint size, MapDetectionRules rules, CancellationToken token)
    {
        ValidateFrame(frame); ValidateImage(mask); rules.Validate();
        if (size.X < 1 || size.Y < 1 || (long)size.X * size.Y > 16 * 1024 * 1024) throw new ArgumentException("Invalid warped image size");
        return ExchangeAsync(frame, id => new { protocol = "alas-cv/1", id, frame = frame.Sequence, operation = "warp_features",
            image = Convert.ToBase64String(frame.Png.Span), mask = Convert.ToBase64String(mask.Span), area = AreaValues(rules.Area),
            matrix = transform.Matrix, size = new[] { size.X, size.Y }, canny = new[] { rules.Canny.Low, rules.Canny.High },
            edge_color = new[] { rules.EdgeColor.Low, rules.EdgeColor.High }, hough = rules.DetectEdges ? rules.EdgeHoughThreshold : (int?)null },
            r => new WarpedFeatures(DecodeFrame(frame, r.GetProperty("image"), size), ReadLines(r.GetProperty("lines"))), token, 24 * 1024 * 1024);
    }
    public ValueTask<CorrelationFeatures> CorrelateAsync(ScreenFrame frame, ReadOnlyMemory<byte> template, double threshold, int? flip, CancellationToken token)
    {
        ValidateFrame(frame); ValidateImage(template);
        var size = PngSize(frame.Png); var templateSize = PngSize(template);
        var bounds = new PixelPoint(size.X - templateSize.X + 1, size.Y - templateSize.Y + 1);
        if (bounds.X < 1 || bounds.Y < 1) throw new ArgumentException("Template exceeds the correlation image");
        if (!double.IsFinite(threshold) || threshold is < -1 or > 1 || flip is < -1 or > 1) throw new ArgumentException("Invalid correlation parameters");
        return ExchangeAsync(frame, id => new { protocol = "alas-cv/1", id, frame = frame.Sequence, operation = "correlation_features",
            image = Convert.ToBase64String(frame.Png.Span), template = Convert.ToBase64String(template.Span), threshold, flip }, r =>
            {
                double maximum = r.GetProperty("maximum").GetDouble();
                if (!double.IsFinite(maximum) || maximum is < -1 or > 1) throw new InvalidDataException("Invalid correlation maximum");
                var values = r.GetProperty("points");
                if (values.GetArrayLength() > Math.Min(1_000_000L, (long)bounds.X * bounds.Y))
                    throw new InvalidDataException("Too many correlation locations");
                var points = values.EnumerateArray().Select(p => ReadPoint(p, bounds)).ToArray();
                if ((maximum > threshold) != (points.Length > 0) || points.Distinct().Count() != points.Length)
                    throw new InvalidDataException("Inconsistent correlation locations");
                return new CorrelationFeatures(maximum, ReadPoint(r.GetProperty("location"), bounds), points);
            }, token, 24 * 1024 * 1024);
    }
    public ValueTask<IReadOnlyList<IReadOnlyList<PixelArea>>> RectanglesAsync(ScreenFrame frame, CancellationToken token)
    {
        ValidateFrame(frame);
        var size = PngSize(frame.Png);
        return ExchangeAsync<IReadOnlyList<IReadOnlyList<PixelArea>>>(frame, id => new { protocol = "alas-cv/1", id, frame = frame.Sequence,
            operation = "contour_features", image = Convert.ToBase64String(frame.Png.Span), kernels = new[] { 5, 10, 15, 20, 25 } }, r =>
            {
                var groups = r.GetProperty("rectangles");
                if (groups.GetArrayLength() != 5) throw new InvalidDataException("Incorrect contour groups");
                return groups.EnumerateArray().Select(group => (IReadOnlyList<PixelArea>)group.EnumerateArray().Select(a =>
                {
                    if (a.GetArrayLength() != 4) throw new InvalidDataException("Invalid contour box");
                    var area = new PixelArea(a[0].GetInt32(), a[1].GetInt32(), a[2].GetInt32(), a[3].GetInt32());
                    ValidateArea(area);
                    if (area.X < 0 || area.Y < 0 || (long)area.X + area.Width > size.X || (long)area.Y + area.Height > size.Y)
                        throw new InvalidDataException("Contour box exceeds image");
                    return area;
                }).ToArray()).ToArray();
            }, token, 24 * 1024 * 1024);
    }
    private static void ValidateImage(ReadOnlyMemory<byte> image)
    { if (image.IsEmpty || image.Length > 16 * 1024 * 1024) throw new ArgumentException("Invalid image bytes"); }
    private static PixelPoint PngSize(ReadOnlyMemory<byte> image)
    {
        var bytes = image.Span;
        if (bytes.Length < 33 || !bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            BinaryPrimitives.ReadInt32BigEndian(bytes[8..12]) != 13 || !bytes[12..16].SequenceEqual("IHDR"u8))
            throw new InvalidDataException("Feature image must be PNG");
        var size = new PixelPoint(BinaryPrimitives.ReadInt32BigEndian(bytes[16..20]), BinaryPrimitives.ReadInt32BigEndian(bytes[20..24]));
        if (size.X < 1 || size.Y < 1 || (long)size.X * size.Y > 16 * 1024 * 1024)
            throw new InvalidDataException("Invalid feature image dimensions");
        return size;
    }
    private static ScreenFrame DecodeFrame(ScreenFrame identity, JsonElement value, PixelPoint size)
    {
        byte[] bytes = value.GetBytesFromBase64();
        ValidateImage(bytes);
        if (PngSize(bytes) != size) throw new InvalidDataException("CV image dimensions differ");
        return identity with { Png = bytes };
    }
    private static PixelPoint ReadPoint(JsonElement point, PixelPoint bounds)
    {
        if (point.GetArrayLength() != 2) throw new InvalidDataException("Invalid feature point");
        var value = new PixelPoint(point[0].GetInt32(), point[1].GetInt32());
        if (value.X < 0 || value.Y < 0 || value.X >= bounds.X || value.Y >= bounds.Y)
            throw new InvalidDataException("Correlation point exceeds image");
        return value;
    }
    private static PolarLine[] ReadLines(JsonElement lines) => lines.EnumerateArray().Select(line =>
    {
        if (line.GetArrayLength() != 2) throw new InvalidDataException("Invalid Hough line");
        double rho = line[0].GetDouble(), theta = line[1].GetDouble();
        if (!double.IsFinite(rho) || !double.IsFinite(theta) || theta is < 0 or > Math.PI)
            throw new InvalidDataException("Invalid Hough measurement");
        return new PolarLine(rho, theta);
    }).ToArray();
}
