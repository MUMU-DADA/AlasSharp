using Alas.Engine.Imaging;
using Alas.Engine.Rules;

namespace Alas.Engine.Navigation;

/// <summary>offset=0 uses color; integer offsets expand x by 3 and y by the given value.</summary>
public readonly record struct ButtonOffset(bool MatchTemplate, int Left, int Top, int Right, int Bottom)
{
    public static ButtonOffset Color => default;
    public static ButtonOffset Vertical(int value) => new(true, -3, -value, 3, value);
    public static ButtonOffset Expand(int x, int y) => new(true, -x, -y, x, y);
    public static ButtonOffset Bounds(int left, int top, int right, int bottom) => new(true, left, top, right, bottom);
}

public sealed class AssetMatcher(GameServer server, IVision vision, AssetFiles files)
{
    private readonly Dictionary<string, (int X, int Y)> _offsets = new(StringComparer.Ordinal);
    public Rectangle ClickArea(AssetRule asset)
    {
        var rectangle = asset.For(server).ClickArea ?? throw new InvalidOperationException("Asset has no clickable area");
        return _offsets.TryGetValue(asset.Id, out var offset) ? rectangle.Offset(offset.X, offset.Y) : rectangle;
    }
    public void ClearOffset(AssetRule asset) => _offsets.Remove(asset.Id);
    public async ValueTask<bool> AppearsAsync(ScreenFrame frame, AssetRule asset, ButtonOffset offset,
        double similarity = 0.85, int colorThreshold = 10, TemplatePreprocessing preprocessing = TemplatePreprocessing.Color,
        CancellationToken token = default)
    {
        if (asset.Kind != AssetKind.Button) throw new NotSupportedException("Button recognition requires a Button rule");
        var variant = asset.For(server);
        var area = variant.Area ?? throw new InvalidDataException("Button has no recognition area");
        if (!offset.MatchTemplate)
        {
            var expected = variant.Color ?? throw new InvalidDataException("Button has no color rule");
            var color = await vision.MeanColorAsync(frame, area.Area, token);
            if (color.FrameSequence != frame.Sequence) throw new InvalidDataException("Color observation belongs to a different frame");
            return ColorSimilar(color, expected, colorThreshold);
        }
        var search = new Rectangle(checked(area.Left + offset.Left), checked(area.Top + offset.Top),
            checked(area.Right + offset.Right), checked(area.Bottom + offset.Bottom));
        var observation = await vision.MatchAsync(frame, new TemplateRequest(await files.ReadAsync(variant, token),
            search.Area, similarity, preprocessing, area.Area), token);
        if (observation.FrameSequence != frame.Sequence || observation.Location is not { } location)
            throw new InvalidDataException("Template observation has no location for this frame");
        // Upstream updates the click offset even on a failed match, but not when interval gating skipped it.
        _offsets[asset.Id] = (location.X - area.Left, location.Y - area.Top);
        return observation.Matched;
    }
    public static bool ColorSimilar(MeanColorObservation color, Rgb expected, int threshold)
    {
        if (threshold < 0) throw new ArgumentOutOfRangeException(nameof(threshold));
        double r = color.R - expected.R, g = color.G - expected.G, b = color.B - expected.B;
        return Math.Max(0, Math.Max(r, Math.Max(g, b))) - Math.Min(0, Math.Min(r, Math.Min(g, b))) <= threshold;
    }
}
