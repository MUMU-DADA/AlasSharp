using Alas.Engine.Imaging;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Devices;

public sealed record SwipePath(PixelPoint Start, PixelPoint End, bool AvoidanceExhausted);

/// <summary>Native vector placement and ADB gesture timing, entirely in C#.</summary>
public sealed class MapSwipeInput(IGameDevice device, Random? random = null) : IMapSwipeInput
{
    public static readonly SourceFile Source = new("module/base/utils.py",
        "54a8096f8a91d9c8b5ed1ec5993c78e956bb64076de7c08df1f0fed71367260b");
    public static readonly SourceFile ControlSource = new("module/device/control.py",
        "6c66e68c85fb7daf86d86f7623512776227dc05a53dcdfcb45592697f55fe6b8");
    private readonly Random _random = random ?? Random.Shared;
    public SwipePath? LastPath { get; private set; }
    public async ValueTask SwipeAsync(MapSwipeGesture gesture, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var path = Place(gesture, _random);
        LastPath = path;
        int milliseconds = Normal(100, 200, _random);
        if (double.Hypot(path.Start.X - path.End.X, path.Start.Y - path.End.Y) < 10) return;
        // Native ADB swipes multiply duration by 2.5; other backends need their own transport.
        await device.SwipeAsync(path.Start, path.End, TimeSpan.FromMilliseconds(milliseconds * 2.5), token);
    }

    public static SwipePath Place(MapSwipeGesture gesture, Random random, int padding = 15)
    {
        if (!double.IsFinite(gesture.Pixels.X) || !double.IsFinite(gesture.Pixels.Y) || gesture.Box.Width <= 0 ||
            gesture.Box.Height <= 0 || gesture.Box.X < 0 || gesture.Box.Y < 0 || padding < 0)
            throw new ArgumentException("Invalid swipe geometry", nameof(gesture));
        int vx = checked((int)Math.Round(gesture.Pixels.X)), vy = checked((int)Math.Round(gesture.Pixels.Y));
        int hx = checked((int)Math.Round(vx / 2.0)), hy = checked((int)Math.Round(vy / 2.0));
        var box = gesture.Box;
        int right = checked(box.X + box.Width), bottom = checked(box.Y + box.Height);
        int leftPad = checked(box.X + Math.Abs(hx) + padding + hx), topPad = checked(box.Y + Math.Abs(hy) + padding + hy);
        int rightPad = checked(right - Math.Abs(hx) - padding + hx), bottomPad = checked(bottom - Math.Abs(hy) - padding + hy);
        int segments = checked((int)(double.Hypot(vx, vy) / 70) + 1);
        bool Blocked(PixelPoint end)
        {
            if (gesture.ForbiddenAreas is null || gesture.ForbiddenAreas.Count == 0) return false;
            for (int i = 0; i <= segments; i++)
            {
                double x = end.X - (double)vx * i / segments, y = end.Y - (double)vy * i / segments;
                if (gesture.ForbiddenAreas.Any(a => a.X < x && x < (long)a.X + a.Width && a.Y < y && y < (long)a.Y + a.Height)) return true;
            }
            return false;
        }
        SwipePath Result(PixelPoint end, bool exhausted = false) => new(
            new((int)Math.Clamp((long)end.X - vx, box.X, right), (int)Math.Clamp((long)end.Y - vy, box.Y, bottom)),
            new(Math.Clamp(end.X, box.X, right), Math.Clamp(end.Y, box.Y, bottom)), exhausted);
        // Native limit_in keeps the lower bound when the available interval is inverted.
        static int Clip(long value, int low, int high) => (int)Math.Max(Math.Min(value, high), low);
        if (gesture.PreferredEnds is not null)
            foreach (var area in gesture.PreferredEnds)
            {
                int left = Clip(area.X, leftPad, rightPad), top = Clip(area.Y, topPad, bottomPad);
                int r = Clip((long)area.X + area.Width, leftPad, rightPad), b = Clip((long)area.Y + area.Height, topPad, bottomPad);
                if (r <= left || b <= top) continue;
                var end = new PixelPoint(Normal(left, r, random), Normal(top, b, random));
                // Native's ten checks reuse the same point; deterministic geometry needs one check.
                if (!Blocked(end)) return Result(end);
            }
        for (int attempt = 0; attempt < 100; attempt++)
        {
            var end = new PixelPoint(Normal(leftPad, rightPad, random), Normal(topPad, bottomPad, random));
            if (!Blocked(end)) return Result(end);
        }
        return Result(new(Normal(leftPad, rightPad, random), Normal(topPad, bottomPad, random)), exhausted: true);
    }
    private static int Normal(int low, int high, Random random)
    {
        if (low >= high) return high;
        long sum = 0;
        for (int i = 0; i < 3; i++) sum += random.NextInt64(low, (long)high + 1);
        return checked((int)Math.Round(sum / 3.0));
    }
}
