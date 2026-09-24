using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Alas.UI.Theming;

/// <summary>主题偏好的持久化位置由平台提供（桌面写文件、浏览器写 localStorage）。</summary>
public interface IThemeStore
{
    ThemePreference Load();

    void Save(ThemePreference preference);

    /// <summary>可读的存储状态；正常时为 null（读失败或写失败都会在这里给出原因，界面需如实展示）。</summary>
    string? Status { get; }
}

/// <summary>不落盘的实现：Headless 验收与单元测试使用。</summary>
public sealed class MemoryThemeStore : IThemeStore
{
    private ThemePreference _preference = ThemePreference.Default;

    public ThemePreference Load() => _preference;

    public void Save(ThemePreference preference) => _preference = preference;

    public string? Status => null;
}

/// <summary>
/// 六主题切换：按「皮肤（classic/minimal/legacy）× 明暗」换整套资源字典，再叠加配色层。
/// 对应上游的 style[data-azurpilot-skin]（皮肤样式节点）+ 内联 --* 令牌（简约/紧凑的配色覆盖）。
/// </summary>
public sealed class ThemeService
{
    /// <summary>配色层覆盖的键；这套键在四个皮肤字典里都有定义，这里按当前配色重算。</summary>
    private static readonly string[] PaletteKeys =
    [
        "AlasAccentBrush", "AlasAccentHoverBrush", "AlasAccentSoftBrush",
        "AlasSecondaryBrush", "AlasSecondarySoftBrush", "AlasOnAccentBrush",
    ];

    private readonly IThemeStore _store;

    // 皮肤字典挂在 Application.Resources 上，是**应用级**状态：多个 ThemeService 实例
    // （外壳、预览页、离屏验收各建一个）必须共用同一份引用，否则每次切换都会把上一份留在
    // MergedDictionaries 里累积（后合并者优先所以不炸，但会越积越多）。
    private static ResourceDictionary? _skin;
    private static ResourceDictionary? _palette;
    private static string? _skinName;

    /// <summary>当前生效的皮肤字典名（如 Classic.Light）；没有合并任何皮肤时为 null。</summary>
    public static string? MergedSkinName => _skinName;

    public ThemeService(IThemeStore store)
    {
        _store = store;
        Preference = store.Load().Normalize();
    }

    public ThemePreference Preference { get; private set; }

    /// <summary>本地首选项的存储状态；读取或保存失败时给出可读原因，界面设置页会如实展示。</summary>
    public string? Status => _store.Status;

    /// <summary>当前解析出的明暗（minimal/extreme 受 color-mode 影响，legacy-dark 恒暗）。</summary>
    public bool IsDark { get; private set; }

    public event EventHandler? Changed;

    /// <summary>应用持久化的偏好；systemDark 只在 color-mode=auto 的简约/紧凑主题上起作用。</summary>
    public void ApplyStored(bool systemDark = false) => Apply(Preference, persist: false, systemDark);

    public void Apply(ThemePreference preference, bool persist = true, bool systemDark = false)
    {
        var normalized = preference.Normalize();
        var dark = UiThemes.IsDark(normalized.Theme, normalized.ColorMode, systemDark);
        var palette = UiPalettes.Find(normalized.Palette, normalized.CustomPalettes);

        Preference = normalized;
        IsDark = dark;
        if (persist) _store.Save(normalized);

        var resources = Application.Current?.Resources;
        if (resources is not null)
        {
            if (_skin is not null) resources.MergedDictionaries.Remove(_skin);
            if (_palette is not null) resources.MergedDictionaries.Remove(_palette);

            // 皮肤字典用编译期 XAML 类型实例化（不用运行时 ResourceInclude(uri)）：
            // 后者依赖 AvaloniaXamlLoader 动态加载程序集资源，裁剪/ AOT 下会丢资源（IL2026）。
            _skin = SkinDictionary(normalized.Theme, dark);
            _skinName = UiThemes.ResourceName(normalized.Theme, dark);
            resources.MergedDictionaries.Add(_skin);

            // 简约/紧凑皮肤的配色由用户选择决定；经典/旧版皮肤的强调色写死在皮肤里（上游同此）。
            if (UiThemes.UsesPalettePreferences(normalized.Theme))
            {
                _palette = BuildPalette(palette.For(dark));
                resources.MergedDictionaries.Add(_palette);
            }
            else
            {
                _palette = null;
            }
        }

        if (Application.Current is { } application)
        {
            // 保留 Avalonia 内置控件的明暗，避免下拉/滚动条与皮肤脱节。
            application.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 按主题与明暗返回编译期 XAML 字典实例。刻意不用 ResourceInclude(uri)：
    /// 那会走 AvaloniaXamlLoader 动态加载，裁剪/ AOT 下资源可能被裁掉（IL2026）。
    /// </summary>
    private static ResourceDictionary SkinDictionary(UiTheme theme, bool dark) =>
        UiThemes.ResourceName(theme, dark) switch
        {
            "Classic.Light" => new Styles.Themes.ClassicLight(),
            "Classic.Dark" => new Styles.Themes.ClassicDark(),
            "Minimal.Light" => new Styles.Themes.MinimalLight(),
            "Minimal.Dark" => new Styles.Themes.MinimalDark(),
            "Extreme.Light" => new Styles.Themes.ExtremeLight(),
            "Extreme.Dark" => new Styles.Themes.ExtremeDark(),
            "Legacy.Light" => new Styles.Themes.LegacyLight(),
            _ => new Styles.Themes.LegacyDark(),
        };

    private static ResourceDictionary BuildPalette(PaletteColors colors)
    {
        var dictionary = new ResourceDictionary();
        var values = new[]
        {
            colors.Accent, colors.AccentHover, colors.AccentSoft,
            colors.Secondary, colors.SecondarySoft, colors.OnAccent,
        };
        for (var index = 0; index < PaletteKeys.Length; index++)
        {
            dictionary[PaletteKeys[index]] = new SolidColorBrush(Color.Parse(values[index]));
        }
        return dictionary;
    }
}
