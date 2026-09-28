using Alas.Engine.Imaging;
using Alas.Engine.Rules;

namespace Alas.Engine.Navigation;

/// <summary>Native image_color_count and match_template_color using existing pure pixel operations.</summary>
public sealed class UiVisuals(IImagePatchVision vision, Func<ScreenFrame> current)
{
    public static readonly SourceFile Source = ImageStability.Source;
    public async ValueTask<bool> ColorCountAsync(Rectangle area, PixelColor color, int threshold, int count,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (threshold is < 0 or > 255 || count < 0) throw new ArgumentOutOfRangeException(nameof(threshold));
        var frame = current();
        var value = await vision.MeasurePatchAsync(frame, new(area.Area, area.Width, area.Height,
            PatchMeasure.SimilarityCount, PatchProcessing.ColorSimilarity, color, MinimumSimilarity: 255 - threshold), token);
        token.ThrowIfCancellationRequested();
        if (value.FrameSequence != frame.Sequence || current().Sequence != frame.Sequence)
            throw new InvalidDataException("Color count belongs to a changed frame");
        return value.Value > count;
    }

    public static async ValueTask<bool> MatchTemplateColorAsync(IUiDriver ui, AssetRule asset, ButtonOffset offset,
        double interval, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ui.ObserveProgress(asset);
        if (interval > 0 && !ui.Timer(asset, interval, renew: true).Reached()) return false;
        if (!await ui.AppearsAsync(asset, offset, preprocessing: TemplatePreprocessing.Luma, token: token)) return false;
        var variant = asset.For(ui.Server);
        var original = variant.ClickArea ?? throw new InvalidDataException("Button has no click rectangle");
        var actual = ui.ButtonArea(asset);
        var area = (variant.Area ?? throw new InvalidDataException("Button has no area"))
            .Offset(actual.Left - original.Left, actual.Top - original.Top);
        if (!AssetMatcher.ColorSimilar(await ui.ColorAsync(area, token),
                variant.Color ?? throw new InvalidDataException("Button has no color"), 30)) return false;
        if (interval > 0) ui.Timer(asset).Reset();
        return true;
    }
}
