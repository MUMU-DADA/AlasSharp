namespace Alas.UI.Theming;

/// <summary>上游六种主题值（与 azurpilot.theme 的取值一一对应）。</summary>
public enum UiTheme
{
    Light,
    Dark,
    Minimal,
    Extreme,
    LegacyLight,
    LegacyDark,
}

/// <summary>配色模式；auto 仅在 minimal/extreme 上有意义（随系统明暗）。</summary>
public enum UiColorMode
{
    Auto,
    Light,
    Dark,
}

/// <summary>皮肤：决定加载哪一套样式表（上游 style[data-azurpilot-skin]）。</summary>
public enum UiSkin
{
    Classic,
    Minimal,
    Legacy,
}

public static class UiThemes
{
    /// <summary>上游 VALID_THEMES 的顺序，界面设置里的下拉顺序必须与之一致。</summary>
    public static readonly UiTheme[] All =
    [
        UiTheme.Light, UiTheme.Dark, UiTheme.Minimal, UiTheme.Extreme, UiTheme.LegacyLight, UiTheme.LegacyDark,
    ];

    public static string ToId(UiTheme theme) => theme switch
    {
        UiTheme.Light => "light",
        UiTheme.Dark => "dark",
        UiTheme.Minimal => "minimal",
        UiTheme.Extreme => "extreme",
        UiTheme.LegacyLight => "legacy-light",
        UiTheme.LegacyDark => "legacy-dark",
        _ => "light",
    };

    public static bool TryParse(string? id, out UiTheme theme)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(ToId(candidate), id, StringComparison.Ordinal))
            {
                theme = candidate;
                return true;
            }
        }
        theme = UiTheme.Light;
        return false;
    }

    public static UiSkin SkinOf(UiTheme theme) => theme switch
    {
        UiTheme.Minimal or UiTheme.Extreme => UiSkin.Minimal,
        UiTheme.LegacyLight or UiTheme.LegacyDark => UiSkin.Legacy,
        _ => UiSkin.Classic,
    };

    /// <summary>上游 MATERIAL_THEMES：只有 light/dark 走 public/theme.css 那套材质令牌。</summary>
    public static bool IsMaterial(UiTheme theme) => theme is UiTheme.Light or UiTheme.Dark;

    /// <summary>只有 minimal/extreme 显示配色偏好，且只有它们把 color-mode 写进 &lt;html&gt;。</summary>
    public static bool UsesPalettePreferences(UiTheme theme) => SkinOf(theme) == UiSkin.Minimal;

    /// <summary>上游 resolvedMode：legacy-dark 是独立主题值，不参与解析；light 恒亮、dark 恒暗。</summary>
    public static bool IsDark(UiTheme theme, UiColorMode preference, bool systemDark) => theme switch
    {
        UiTheme.Dark or UiTheme.LegacyDark => true,
        UiTheme.Light or UiTheme.LegacyLight => false,
        UiTheme.Minimal or UiTheme.Extreme => preference switch
        {
            UiColorMode.Light => false,
            UiColorMode.Dark => true,
            _ => systemDark,
        },
        _ => false,
    };

    /// <summary>
    /// 皮肤字典文件名。注意 extreme 与 minimal 虽然是同一个皮肤（skin=minimal），
    /// 但上游 compact.css 另有自己的令牌（例如卡片圆角 0），所以极端主题要加载 Extreme.* 而不是 Minimal.*。
    /// </summary>
    public static string ResourceName(UiTheme theme, bool dark) => theme switch
    {
        UiTheme.Extreme => dark ? "Extreme.Dark" : "Extreme.Light",
        UiTheme.Minimal => dark ? "Minimal.Dark" : "Minimal.Light",
        UiTheme.LegacyLight or UiTheme.LegacyDark => dark ? "Legacy.Dark" : "Legacy.Light",
        _ => dark ? "Classic.Dark" : "Classic.Light",
    };

    public static string ResourceUri(UiTheme theme, bool dark) =>
        $"avares://Alas.UI/Styles/Themes/{ResourceName(theme, dark)}.axaml";
}
