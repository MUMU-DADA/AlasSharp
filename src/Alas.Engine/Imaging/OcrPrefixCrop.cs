namespace Alas.Engine.Imaging;

/// <summary>Pure pixel preprocessing parameters: brighten a dim crop, subtract its background,
/// then remove a prefix located by a dark vertical stroke. Contains no game or server identity.</summary>
public sealed record OcrPrefixCrop(int MaskRows, int MaskRedMaximum, double MaskScale,
    PixelColor Background, double LumaR, double LumaG, double LumaB,
    int SearchTop, int SearchBottom, int SearchThreshold, int PrefixWidth,
    int RightLimit, int MinimumRemaining, int Border)
{
    public void Validate(PixelArea area)
    {
        if (MaskRows < 1 || MaskRows > area.Height || MaskRedMaximum is < 0 or > 255 ||
            !double.IsFinite(MaskScale) || MaskScale <= 0 || MaskScale > 255 ||
            new[] { Background.R, Background.G, Background.B }.Any(v => v is < 0 or > 255) ||
            new[] { LumaR, LumaG, LumaB }.Any(v => !double.IsFinite(v) || v < 0) ||
            Math.Abs(LumaR + LumaG + LumaB - 1) > 1e-12 ||
            Background.R * LumaR + Background.G * LumaG + Background.B * LumaB >= 255 ||
            SearchTop < 0 || SearchBottom <= SearchTop || SearchBottom > area.Height ||
            SearchThreshold is < 1 or > 255 || PrefixWidth < 0 || PrefixWidth >= area.Width ||
            RightLimit < 1 || RightLimit > area.Width + 2 || MinimumRemaining < 0 || MinimumRemaining >= area.Width ||
            Border is < 0 or > 32)
            throw new ArgumentException("Invalid OCR prefix crop parameters");
    }
    internal object Protocol() => new { mask_rows = MaskRows, mask_red_maximum = MaskRedMaximum, mask_scale = MaskScale,
        background = new[] { Background.R, Background.G, Background.B }, luma = new[] { LumaR, LumaG, LumaB },
        search_top = SearchTop, search_bottom = SearchBottom, search_threshold = SearchThreshold,
        prefix_width = PrefixWidth, right_limit = RightLimit, minimum_remaining = MinimumRemaining, border = Border };
}
