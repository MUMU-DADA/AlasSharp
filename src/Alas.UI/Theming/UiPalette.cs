namespace Alas.UI.Theming;

/// <summary>一套配色在某个明暗模式下的实际颜色（取自上游 minimal 皮肤的内联令牌）。</summary>
public sealed record PaletteColors(
    string Accent,
    string AccentHover,
    string AccentSoft,
    string Secondary,
    string SecondarySoft,
    string OnAccent);

/// <summary>配色预设或自定义配色；id 与上游 azurpilot.palette 的取值一致。</summary>
public sealed record UiPalette(string Id, string Name, PaletteColors Light, PaletteColors Dark)
{
    public PaletteColors For(bool dark) => dark ? Dark : Light;
}

public static class UiPalettes
{
    public const int MaxCustom = 32;

    /// <summary>自定义配色 id 规则（上游 ^custom:[\w-]{1,80}$）。</summary>
    public static bool IsValidCustomId(string? id) =>
        !string.IsNullOrEmpty(id) && System.Text.RegularExpressions.Regex.IsMatch(id, @"^custom:[\w-]{1,80}$");

    /// <summary>自定义配色颜色规则（上游 #RRGGBB）。</summary>
    public static bool IsValidColor(string? color) =>
        !string.IsNullOrEmpty(color) && System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9a-fA-F]{6}$");

    public static readonly UiPalette Ocean = new("ocean", "海洋",
        new PaletteColors("#245DBE", "#1F4FA2", "#F0F4FA", "#13777C", "#EEF5F6", "#FFFFFF"),
        new PaletteColors("#8AB4FF", "#9CBFFF", "#2D3646", "#71CBC9", "#2A3940", "#17202B"));

    public static readonly UiPalette Forest = new("forest", "森林",
        new PaletteColors("#286747", "#22583C", "#F0F4F2", "#956020", "#F8F4EF", "#FFFFFF"),
        new PaletteColors("#86CBA3", "#98D3B1", "#2C393B", "#DFB472", "#373635", "#17202B"));

    public static readonly UiPalette Violet = new("violet", "紫罗兰",
        new PaletteColors("#7050A3", "#5F448B", "#F5F3F9", "#A14865", "#F8F2F4", "#FFFFFF"),
        new PaletteColors("#C1A4EE", "#CAB2F1", "#333444", "#EFA2BB", "#39343E", "#17202B"));

    public static readonly UiPalette Sand = new("sand", "沙丘",
        new PaletteColors("#915A2B", "#7B4D25", "#F7F3F0", "#426B70", "#F2F5F5", "#FFFFFF"),
        new PaletteColors("#E1B082", "#E6BC95", "#373637", "#92C3C8", "#2E3840", "#17202B"));

    public static readonly UiPalette Slate = new("slate", "石板",
        new PaletteColors("#45566B", "#3B495B", "#F2F3F5", "#96553B", "#F8F3F1", "#FFFFFF"),
        new PaletteColors("#ACBDD2", "#B8C7D9", "#313741", "#DFAA8E", "#373539", "#17202B"));

    public static readonly UiPalette[] Presets = [Ocean, Forest, Violet, Sand, Slate];

    /// <summary>按 id 查找预设，找不到时回落 ocean（上游删除当前配色后的行为）。</summary>
    public static UiPalette Find(string? id, IReadOnlyList<UiPalette>? custom = null)
    {
        foreach (var preset in Presets)
        {
            if (string.Equals(preset.Id, id, StringComparison.Ordinal)) return preset;
        }
        if (custom is not null)
        {
            foreach (var item in custom)
            {
                if (string.Equals(item.Id, id, StringComparison.Ordinal)) return item;
            }
        }
        return Ocean;
    }
}
