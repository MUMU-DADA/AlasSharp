namespace Alas.Engine.Imaging;

public sealed record ColorBarRequest(PixelArea Area, PixelColor Color, bool Reverse = false, int Starter = 0, int Threshold = 30);
public interface IColorBarVision
{
    ValueTask<IReadOnlyList<double>> ColorBarsAsync(ScreenFrame frame, IReadOnlyList<ColorBarRequest> bars, CancellationToken token);
}

public sealed partial class PureVisionWorker : IColorBarVision
{
    public ValueTask<IReadOnlyList<double>> ColorBarsAsync(ScreenFrame frame, IReadOnlyList<ColorBarRequest> bars, CancellationToken token)
    {
        ValidateFrame(frame);
        if (bars.Count is < 1 or > 128) throw new ArgumentException("Invalid number of color bars");
        // Snapshot the request before asynchronous transport; callers cannot change its dimensions in flight.
        var requests = bars.ToArray();
        foreach (var bar in requests)
        {
            ValidateArea(bar.Area); ValidateColor(bar.Color);
            if (bar.Area.Width * (long)bar.Area.Height > 30000 || bar.Starter < 0 || bar.Starter >= bar.Area.Width || bar.Threshold is < 1 or > 255)
                throw new ArgumentException("Invalid color bar parameters");
        }
        return ExchangeAsync<IReadOnlyList<double>>(frame, id => new {
            protocol = "alas-cv/1", id, frame = frame.Sequence, operation = "color_bars",
            image = Convert.ToBase64String(frame.Png.Span),
            bars = requests.Select(bar => new { area = AreaValues(bar.Area), color = new[] { bar.Color.R, bar.Color.G, bar.Color.B },
                reverse = bar.Reverse, starter = bar.Starter, threshold = bar.Threshold }).ToArray()
        }, response => {
            var values = response.GetProperty("values").EnumerateArray().Select(item => item.GetDouble()).ToArray();
            if (values.Length != requests.Length || values.Any(value => !double.IsFinite(value) || value is < 0 or >= 1))
                throw new InvalidDataException("Invalid color bar measurements");
            return values;
        }, token);
    }
}
