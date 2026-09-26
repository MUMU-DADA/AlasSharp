using Alas.Engine.Imaging;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Devices;

/// <summary>Native grid-button point placement; action transport remains in the C# device.</summary>
public sealed class MapGridInput(IGameDevice device, Random? random = null) : IMapGridInput
{
    public static readonly SourceFile Source = MapSwipeInput.Source;
    private readonly Random _random = random ?? Random.Shared;

    public ValueTask TapAsync(PixelArea area, CancellationToken token)
        => device.TapAsync(Place(area, _random), token);

    public static PixelPoint Place(PixelArea area, Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (area.X < 0 || area.Y < 0 || area.Width <= 0 || area.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(area));
        int right = checked(area.X + area.Width), bottom = checked(area.Y + area.Height);
        static int Coordinate(int low, int high, Random random)
        {
            long sum = 0;
            for (int i = 0; i < 3; i++) sum += random.NextInt64(low, (long)high + 1);
            return checked((int)Math.Round(sum / 3.0));
        }
        return new(Coordinate(area.X, right, random), Coordinate(area.Y, bottom, random));
    }
}
