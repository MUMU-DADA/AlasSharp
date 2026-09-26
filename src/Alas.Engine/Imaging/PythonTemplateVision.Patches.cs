namespace Alas.Engine.Imaging;

public sealed partial class PythonTemplateVision : IImagePatchVision
{
    public async ValueTask<ImagePatchObservation> MeasurePatchAsync(ScreenFrame frame, ImagePatchRequest request,
        CancellationToken token = default)
    {
        ValidateFrame(frame);
        ValidateArea(request.Area);
        if (request.Width < 1 || request.Height < 1 || (long)request.Width * request.Height > 1024 * 1024 ||
            !Enum.IsDefined(request.Measure) || !Enum.IsDefined(request.Processing) ||
            request.MinimumSimilarity is < 0 or > 255 ||
            new[] { request.Color.R, request.Color.G, request.Color.B }.Any(v => v is < 0 or > 255) ||
            (request.Measure == PatchMeasure.Template && (request.Template.IsEmpty || request.Template.Length > 16 * 1024 * 1024)))
            throw new ArgumentException("Invalid image patch parameters", nameof(request));
        var hsv = request.Hsv;
        if (new[] { hsv.HLow, hsv.HHigh, hsv.SLow, hsv.SHigh, hsv.VLow, hsv.VHigh }.Any(v => !double.IsFinite(v)) ||
            hsv.HLow > hsv.HHigh || hsv.SLow > hsv.SHigh || hsv.VLow > hsv.VHigh)
            throw new ArgumentException("Invalid HSV bounds", nameof(request));
        return await ExchangeAsync(frame, id => new
        {
            protocol = "alas-cv/1", id, operation = "image_patch", frame = frame.Sequence,
            image = Convert.ToBase64String(frame.Png.Span), area = AreaValues(request.Area),
            size = new[] { request.Width, request.Height }, measure = request.Measure.ToString().ToLowerInvariant(),
            processing = request.Processing.ToString().ToLowerInvariant(), color = new[] { request.Color.R, request.Color.G, request.Color.B },
            template = Convert.ToBase64String(request.Template.Span), minimum = request.MinimumSimilarity,
            lower = new[] { hsv.HLow / 2, hsv.SLow * 2.55, hsv.VLow * 2.55 },
            upper = new[] { hsv.HHigh / 2 + 1, hsv.SHigh * 2.55 + 1, hsv.VHigh * 2.55 + 1 }
        }, response =>
        {
            double value = response.GetProperty("value").GetDouble();
            if (!double.IsFinite(value) || (request.Measure == PatchMeasure.Template ? value is < -1 or > 1 :
                value < 0 || value > (long)request.Width * request.Height || value != Math.Truncate(value)))
                throw new InvalidDataException("Invalid image patch measurement");
            return new ImagePatchObservation(frame.Sequence, value);
        }, token);
    }
}
