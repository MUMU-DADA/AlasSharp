namespace Alas.Engine.Imaging;

public sealed record ImagePairRequest(PixelArea FirstArea, PixelPoint FirstSize, PixelArea SecondArea, PixelPoint SecondSize);
public sealed record ImagePairObservation(long FirstSequence, long SecondSequence, double Value);

/// <summary>Crop, cubic resize, native grayscale and normalized correlation of two image patches.</summary>
public interface IImagePairVision
{
    ValueTask<ImagePairObservation> CompareAsync(ScreenFrame first, ScreenFrame second,
        ImagePairRequest request, CancellationToken token = default);
}

public sealed partial class PythonTemplateVision : IImagePairVision
{
    public async ValueTask<ImagePairObservation> CompareAsync(ScreenFrame first, ScreenFrame second,
        ImagePairRequest request, CancellationToken token = default)
    {
        ValidateFrame(first); ValidateFrame(second);
        ValidateArea(request.FirstArea); ValidateArea(request.SecondArea);
        foreach (var size in new[] { request.FirstSize, request.SecondSize })
            if (size.X < 1 || size.Y < 1 || (long)size.X * size.Y > 1024 * 1024)
                throw new ArgumentOutOfRangeException(nameof(request));
        if (request.FirstSize.X > request.SecondSize.X || request.FirstSize.Y > request.SecondSize.Y)
            throw new ArgumentException("First patch must fit inside second patch", nameof(request));
        return await ExchangeAsync(first, id => new
        {
            protocol = "alas-cv/1", id, operation = "image_pair", frame = first.Sequence,
            image = Convert.ToBase64String(first.Png.Span), area = AreaValues(request.FirstArea),
            size = new[] { request.FirstSize.X, request.FirstSize.Y }, second_frame = second.Sequence,
            second_image = Convert.ToBase64String(second.Png.Span), second_area = AreaValues(request.SecondArea),
            second_size = new[] { request.SecondSize.X, request.SecondSize.Y }
        }, response =>
        {
            double value = response.GetProperty("value").GetDouble();
            if (response.GetProperty("second_frame").GetInt64() != second.Sequence || !double.IsFinite(value) || value is < -1 or > 1)
                throw new InvalidDataException("Invalid paired image response");
            return new ImagePairObservation(first.Sequence, second.Sequence, value);
        }, token);
    }
}
