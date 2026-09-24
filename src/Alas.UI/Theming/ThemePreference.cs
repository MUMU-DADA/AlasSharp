using System.Text.Json;
using System.Text.Json.Serialization;

namespace Alas.UI.Theming;

/// <summary>
/// 界面主题偏好：对应上游同时写入 localStorage 的四个键
/// （azurpilot.theme / azurpilot.palette / azurpilot.color-mode / azurpilot.custom-palettes）。
/// </summary>
public sealed record ThemePreference(
    UiTheme Theme,
    string Palette,
    UiColorMode ColorMode,
    IReadOnlyList<UiPalette> CustomPalettes)
{
    public static ThemePreference Default { get; } =
        new(UiTheme.Light, "ocean", UiColorMode.Auto, Array.Empty<UiPalette>());

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>把越界输入夹回上游允许的范围：配色 id 与颜色格式非法、超过 32 个都按丢弃处理。</summary>
    public ThemePreference Normalize()
    {
        var custom = new List<UiPalette>();
        foreach (var item in CustomPalettes ?? Array.Empty<UiPalette>())
        {
            if (custom.Count >= UiPalettes.MaxCustom) break;
            if (!UiPalettes.IsValidCustomId(item.Id)) continue;
            if (!IsUsable(item.Light) || !IsUsable(item.Dark)) continue;
            custom.Add(item);
        }
        var palette = Palette ?? "ocean";
        if (!UiPalettes.IsValidCustomId(palette) && UiPalettes.Find(palette, custom).Id != palette) palette = "ocean";
        var mode = Enum.IsDefined(ColorMode) ? ColorMode : UiColorMode.Auto;
        return this with { Palette = palette, ColorMode = mode, CustomPalettes = custom };
    }

    private static bool IsUsable(PaletteColors colors) =>
        UiPalettes.IsValidColor(colors.Accent) && UiPalettes.IsValidColor(colors.Secondary);

    public string ToJson() => JsonSerializer.Serialize(new Persisted(
        Theming.UiThemes.ToId(Theme), Palette, ColorMode.ToString().ToLowerInvariant(),
        (CustomPalettes ?? Array.Empty<UiPalette>()).Select(item => new PersistedPalette(
            item.Id,
            new PersistedColors(item.Light.Accent, item.Light.Secondary, item.Light.AccentHover, item.Light.AccentSoft, item.Light.SecondarySoft, item.Light.OnAccent),
            new PersistedColors(item.Dark.Accent, item.Dark.Secondary, item.Dark.AccentHover, item.Dark.AccentSoft, item.Dark.SecondarySoft, item.Dark.OnAccent))).ToArray()), Options);

    public static ThemePreference FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Default;
        try
        {
            var persisted = JsonSerializer.Deserialize<Persisted>(json, Options);
            if (persisted is null) return Default;
            if (!UiThemes.TryParse(persisted.Theme, out var theme)) theme = UiTheme.Light;
            var mode = persisted.ColorMode switch
            {
                "light" => UiColorMode.Light,
                "dark" => UiColorMode.Dark,
                _ => UiColorMode.Auto,
            };
            var custom = new List<UiPalette>();
            foreach (var item in persisted.CustomPalettes ?? Array.Empty<PersistedPalette>())
            {
                custom.Add(new UiPalette(item.Id, item.Id,
                    ToColors(item.Light, item.Primary, item.Secondary),
                    ToColors(item.Dark, item.Primary, item.Secondary)));
            }
            return new ThemePreference(theme, persisted.Palette ?? "ocean", mode, custom).Normalize();
        }
        catch (JsonException)
        {
            return Default;
        }
    }

    private static PaletteColors ToColors(PersistedColors? colors, string? primary, string? secondary)
    {
        var accent = colors?.Accent ?? primary ?? "#245DBE";
        var second = colors?.Secondary ?? secondary ?? "#13777C";
        return new PaletteColors(
            accent,
            colors?.AccentHover ?? accent,
            colors?.AccentSoft ?? accent,
            second,
            colors?.SecondarySoft ?? second,
            colors?.OnAccent ?? "#FFFFFF");
    }

    private sealed record Persisted(string Theme, string? Palette, string? ColorMode, PersistedPalette[]? CustomPalettes);

    private sealed record PersistedPalette(string Id, PersistedColors? Light, PersistedColors? Dark)
    {
        // 兼容上游旧格式 {id, light:{primary,secondary}}：缺少细项时由 ToColors 回填。
        public string? Primary { get; init; }
        public string? Secondary { get; init; }
    }

    private sealed record PersistedColors(string? Accent, string? Secondary, string? AccentHover, string? AccentSoft, string? SecondarySoft, string? OnAccent);
}
