using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Avalonia.Media;
using Alas.UI.Theming;

namespace Alas.UI.ViewModels;

/// <summary>界面设置页：上游 /interface。六主题 + 配色方案（含自定义配色增删改）+ 自定义背景 + 界面语言。</summary>
public sealed class InterfaceSettingsViewModel : System.ComponentModel.INotifyPropertyChanged
{
    private readonly ThemeService _theme;
    private readonly PreviewCommand _applyTheme;

    public InterfaceSettingsViewModel(ThemeService theme)
    {
        _theme = theme;
        _applyTheme = new PreviewCommand(_ => ApplyCurrent());
        AddPaletteCommand = new PreviewCommand(_ => OpenPaletteDialog(null));
        SelectPaletteCommand = new PreviewCommand(parameter => SelectPalette(parameter as PaletteOption));
        EditPaletteCommand = new PreviewCommand(parameter => OpenPaletteDialog(parameter as PaletteOption));
        DeletePaletteCommand = new PreviewCommand(parameter => DeletePalette(parameter as PaletteOption));
        SavePaletteCommand = new PreviewCommand(_ => SavePalette());
        CancelPaletteCommand = new PreviewCommand(_ => ClosePaletteDialog());
        ApplyBackgroundCommand = new PreviewCommand(_ => ApplyBackground());
        ReloadPalettes();
    }

    /// <summary>页面标题取上游 settings.uiPreferences=界面偏好（侧栏导航项仍是 nav.interface=界面设置）。</summary>
    public string Title => "界面偏好";

    public IReadOnlyList<ThemeOption> Themes { get; } =
    [
        new("light", "浅色"), new("dark", "深色"), new("minimal", "简约"),
        new("legacy-light", "旧版·浅色"), new("legacy-dark", "旧版·深色"), new("extreme", "紧凑"),
    ];

    public IReadOnlyList<LabeledOption> ColorModes { get; } =
    [
        new("auto", "跟随系统"), new("light", "浅色"), new("dark", "深色"),
    ];

    public IReadOnlyList<LabeledOption> BackgroundKinds { get; } =
    [
        new("default", "默认随机图片"), new("url", "填写 URL"), new("upload", "上传文件"),
    ];

    public IReadOnlyList<LabeledOption> Languages { get; } =
    [
        new("zh-CN", "简体中文"), new("zh-TW", "繁體中文"), new("en-US", "English"), new("ja-JP", "日本語"),
    ];

    public ObservableCollection<PaletteOption> Palettes { get; } = new();

    /// <summary>上游只有简约/紧凑皮肤显示配色偏好。</summary>
    public bool ShowPalettePreferences => UiThemes.UsesPalettePreferences(_theme.Preference.Theme);

    /// <summary>上游只有 light/dark 显示自定义背景（简约主题会隐藏背景）。</summary>
    public bool ShowBackgroundPreferences => UiThemes.IsMaterial(_theme.Preference.Theme);

    public string SelectedThemeId
    {
        get => UiThemes.ToId(_theme.Preference.Theme);
        set
        {
            if (!UiThemes.TryParse(value, out var theme) || theme == _theme.Preference.Theme) return;
            _applyTheme.Execute(null);
            ApplyPreference(_theme.Preference with { Theme = theme });
        }
    }

    public string SelectedColorMode
    {
        get => _theme.Preference.ColorMode.ToString().ToLowerInvariant();
        set
        {
            var mode = value switch
            {
                "light" => UiColorMode.Light,
                "dark" => UiColorMode.Dark,
                _ => UiColorMode.Auto,
            };
            if (mode == _theme.Preference.ColorMode) return;
            ApplyPreference(_theme.Preference with { ColorMode = mode });
        }
    }

    public string SelectedPaletteId
    {
        get => _theme.Preference.Palette;
        set
        {
            if (string.IsNullOrEmpty(value) || value == _theme.Preference.Palette) return;
            ApplyPreference(_theme.Preference with { Palette = value });
        }
    }

    public string SelectedLanguage
    {
        get => _language;
        set
        {
            // 上游：连接未就绪时语言下拉禁用；这里如实反映「未接服务」而不是假装切换成功。
            if (IsConnected) SetField(ref _language, value);
        }
    }

    public string BackgroundKindId
    {
        get => _backgroundKind;
        set
        {
            if (!SetField(ref _backgroundKind, value)) return;
            Notify(nameof(IsBackgroundUrlInput));
            Notify(nameof(IsBackgroundUpload));
        }
    }

    public bool IsBackgroundUrlInput => BackgroundKindId == "url";
    public bool IsBackgroundUpload => BackgroundKindId == "upload";

    public string BackgroundUrl
    {
        get => _backgroundUrl;
        set => SetField(ref _backgroundUrl, value);
    }

    /// <summary>背景/语言依赖服务与平台能力；未就绪时禁用并给出原因，不伪造成功。</summary>
    public bool IsConnected => false;

    public string BackgroundNotice => IsConnected
        ? "支持浏览器可播放的图片和视频，文件最大 200 MB。"
        : "未连接控制服务：自定义背景需要由服务保存素材，当前不可用。";

    public string BackgroundStatus => IsBackgroundUrlInput && BackgroundUrl.Length == 0
        ? "填写图片或视频地址后再应用。"
        : "简约主题会隐藏背景。";

    public string LanguageNotice => IsConnected ? string.Empty : "未连接控制服务：语言切换在连接后可用。";

    /// <summary>本地首选项的落盘状态；读取或保存失败时如实显示原因（空串表示正常）。</summary>
    public string PersistenceStatus => _theme.Status ?? string.Empty;

    public bool HasPersistenceProblem => !string.IsNullOrEmpty(_theme.Status);

    public ICommand AddPaletteCommand { get; }

    /// <summary>选中某个配色（上游 palette-options 里的 radio）——选中即写偏好并落盘。</summary>
    public ICommand SelectPaletteCommand { get; }
    public ICommand EditPaletteCommand { get; }
    public ICommand DeletePaletteCommand { get; }
    public ICommand SavePaletteCommand { get; }
    public ICommand CancelPaletteCommand { get; }
    public ICommand ApplyBackgroundCommand { get; }

    public bool IsPaletteDialogOpen
    {
        get => _isDialogOpen;
        private set => SetField(ref _isDialogOpen, value);
    }

    public string DraftTitle => _editing is null ? "添加自定义配色" : "编辑方案";
    public string DraftName { get => _draftName; set { if (SetField(ref _draftName, value)) BumpDraftVersion(); } }
    public string DraftPrimary
    {
        get => _draftPrimary;
        set
        {
            if (!SetField(ref _draftPrimary, value)) return;
            Notify(nameof(DraftPreview));
            Notify(nameof(IsPaletteDraftValid));
            BumpDraftVersion();
        }
    }

    public string DraftSecondary
    {
        get => _draftSecondary;
        set
        {
            if (!SetField(ref _draftSecondary, value)) return;
            Notify(nameof(DraftPreview));
            Notify(nameof(IsPaletteDraftValid));
            BumpDraftVersion();
        }
    }

    /// <summary>
    /// 草稿版本号：每次草稿变化都 +1，界面据此**无条件重算**「保存」的可用状态。
    /// 只 raise <c>IsPaletteDraftValid</c> 是不够的——绑定在草稿对象的属性变化上时不会重新求值
    /// （实测：非法十六进制没有让「保存配色」变灰）。
    /// </summary>
    public int DraftVersion => _draftVersion;

    private void BumpDraftVersion()
    {
        _draftVersion++;
        Notify(nameof(DraftVersion));
        Notify(nameof(CanSavePalette));
    }

    /// <summary>
    /// 上游 CustomPaletteEditor：两个颜色都合法（<c>/^#[\da-f]{6}$/i</c>）时「保存配色」才可用。
    /// 名称非法由保存时的 DRAFT_ERROR 文案说明（上游 id 由随机值生成，本项目的 id 需要名称）。
    ///
    /// 这里同时看**草稿里已有的文本**与取色控件报来的非法标记：十六进制框里打了非法值时，
    /// 取色控件不会用非法文本覆盖颜色（颜色仍是上一个合法值），因此只比颜色会让保存键保持可用。
    /// </summary>
    public bool IsPaletteDraftValid =>
        UiPalettes.IsValidColor(DraftPrimary) && UiPalettes.IsValidColor(DraftSecondary)
        && !HasInvalidColorText;

    /// <summary>取色控件报告「十六进制框内容非法」（弹窗打开期间由代码后置维护）。</summary>
    public bool HasInvalidColorText
    {
        get => _hasInvalidColorText;
        set
        {
            if (!SetField(ref _hasInvalidColorText, value)) return;
            Notify(nameof(IsPaletteDraftValid));
            Notify(nameof(CanSavePalette));
        }
    }

    /// <summary>
    /// 「保存配色」的可用状态：把草稿版本号也作为输入，任何草稿编辑都会触发重算。
    /// 只绑 <see cref="IsPaletteDraftValid"/> 时绑定不会重新求值（实测：非法十六进制没让保存变灰）。
    /// </summary>
    public bool CanSavePalette => DraftVersion >= 0 && IsPaletteDraftValid;

    public IBrush DraftPreview => new SolidColorBrush(
        UiPalettes.IsValidColor(DraftPrimary) ? Color.Parse(DraftPrimary) : Colors.Transparent);

    public string DraftError
    {
        get => _draftError;
        private set => SetField(ref _draftError, value);
    }

    public string PaletteLimitNotice =>
        $"最多 {UiPalettes.MaxCustom} 个自定义配色，当前 {CustomPalettes.Count} 个。";

    /// <summary>上游 palette-add 在达到 32 个上限时禁用。</summary>
    public bool CanAddPalette => CustomPalettes.Count < UiPalettes.MaxCustom;

    private List<UiPalette> CustomPalettes => _theme.Preference.CustomPalettes.ToList();

    private void ApplyCurrent() => ApplyPreference(_theme.Preference);

    /// <summary>唯一入口：写偏好 + 落盘 + 通知界面（ThemeService.Changed 会驱动外壳刷新）。</summary>
    private void ApplyPreference(ThemePreference preference)
    {
        _theme.Apply(preference, persist: true);
        Notify(nameof(SelectedThemeId));
        Notify(nameof(SelectedPaletteId));
        Notify(nameof(SelectedColorMode));
        Notify(nameof(ShowPalettePreferences));
        Notify(nameof(ShowBackgroundPreferences));
        Notify(nameof(PersistenceStatus));
        Notify(nameof(HasPersistenceProblem));
        ReloadPalettes();
    }

    private void ReloadPalettes()
    {
        var selected = _theme.Preference.Palette;
        Palettes.Clear();
        foreach (var preset in UiPalettes.Presets)
        {
            Palettes.Add(PaletteOption.From(preset, _theme.IsDark, isCustom: false, selected == preset.Id));
        }
        foreach (var custom in _theme.Preference.CustomPalettes)
        {
            Palettes.Add(PaletteOption.From(custom, _theme.IsDark, isCustom: true, selected == custom.Id));
        }
        Notify(nameof(PaletteLimitNotice));
        Notify(nameof(CanAddPalette));
    }

    private void OpenPaletteDialog(PaletteOption? option)
    {
        _editing = option?.IsCustom == true ? option.Id : null;
        DraftName = option?.Id.Replace("custom:", string.Empty) ?? string.Empty;
        DraftPrimary = option?.Accent ?? "#245DBE";
        DraftSecondary = option?.Secondary ?? "#13777C";
        DraftError = string.Empty;
        HasInvalidColorText = false;
        IsPaletteDialogOpen = true;
        Notify(nameof(DraftTitle));
        Notify(nameof(DraftPreview));
        Notify(nameof(CanSavePalette));
    }

    private void ClosePaletteDialog()
    {
        // 上游：取消必须丢弃草稿，不写回偏好。
        IsPaletteDialogOpen = false;
        _editing = null;
        DraftName = string.Empty;
        DraftPrimary = string.Empty;
        DraftSecondary = string.Empty;
        HasInvalidColorText = false;
        DraftError = string.Empty;
        Notify(nameof(DraftTitle));
    }

    private void SavePalette()
    {
        if (!IsPaletteDraftValid)
        {
            DraftError = "请使用 #RRGGBB 格式的颜色。";
            return;
        }
        var slug = Slug(DraftName);
        if (slug.Length == 0)
        {
            DraftError = "请填写配色名称（字母、数字、下划线或连字符）。";
            return;
        }
        var id = $"custom:{slug}";
        if (_editing is null && CustomPalettes.Count >= UiPalettes.MaxCustom)
        {
            DraftError = "自定义配色已达上限。";
            return;
        }
        var palette = new UiPalette(id, DraftName.Trim(),
            BuildColors(DraftPrimary, DraftSecondary, dark: false),
            BuildColors(DraftPrimary, DraftSecondary, dark: true));
        var rest = CustomPalettes.Where(item => item.Id != (_editing ?? id)).ToList();
        var next = rest.Append(palette).ToList();
        // 上游：保存后立即选中新配色。
        _theme.Apply(_theme.Preference with { CustomPalettes = next, Palette = id }, persist: true);
        ClosePaletteDialog();
        ApplyPreference(_theme.Preference);
    }

    /// <summary>选中一个配色（上游 radio 的 onChange → setPalette）。</summary>
    private void SelectPalette(PaletteOption? option)
    {
        if (option is null || option.Id == _theme.Preference.Palette) return;
        ApplyPreference(_theme.Preference with { Palette = option.Id });
    }

    private void DeletePalette(PaletteOption? option)    {
        if (option?.IsCustom != true) return;
        var rest = CustomPalettes.Where(item => item.Id != option.Id).ToList();
        // 上游：删除当前配色时回落到 ocean。
        var selected = _theme.Preference.Palette == option.Id ? UiPalettes.Ocean.Id : _theme.Preference.Palette;
        ApplyPreference(_theme.Preference with { CustomPalettes = rest, Palette = selected });
    }

    private void ApplyBackground()
    {
        // 背景素材要由服务保存；未连接时只如实说明，不写假状态。
        Notify(nameof(BackgroundStatus));
    }

    private static string Slug(string value) =>
        Regex.Replace(value.Trim().ToLowerInvariant(), @"[^a-z0-9_-]+", "-").Trim('-');

    /// <summary>由用户给的主/副色推导 hover/soft/on-accent，规则与上游内联令牌一致（hover 偏 15%，soft 混 7%）。</summary>
    private static PaletteColors BuildColors(string primary, string secondary, bool dark)
    {
        var accent = Color.Parse(primary);
        var second = Color.Parse(secondary);
        var toward = dark ? Colors.White : Colors.Black;
        string Mix(Color color, Color target, double ratio)
        {
            byte Channel(byte from, byte to) => (byte)Math.Round(from + (to - from) * ratio);
            return $"#{Channel(color.R, target.R):X2}{Channel(color.G, target.G):X2}{Channel(color.B, target.B):X2}";
        }
        string Soft(Color color) => dark
            ? Mix(color, Color.Parse("#171C24"), 0.19)
            : Mix(color, Colors.White, 0.93);
        return new PaletteColors(primary.ToUpperInvariant(), Mix(accent, toward, 0.15).ToUpperInvariant(),
            Soft(accent).ToUpperInvariant(), secondary.ToUpperInvariant(), Soft(second).ToUpperInvariant(),
            dark ? "#17202B" : "#FFFFFF");
    }

    private string _language = "zh-CN";
    private string _backgroundKind = "default";
    private string _backgroundUrl = string.Empty;
    private bool _isDialogOpen;
    private string? _editing;
    private string _draftName = string.Empty;
    private string _draftPrimary = string.Empty;
    private string _draftSecondary = string.Empty;
    private int _draftVersion;
    private bool _hasInvalidColorText;
    private string _draftError = string.Empty;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void Notify(string propertyName) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));

    private bool SetField<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(propertyName!);
        return true;
    }
}

public sealed record ThemeOption(string Id, string Label);

public sealed record LabeledOption(string Id, string Label);

/// <summary>
/// 配色格子：显示主/副两个色块。上游是 radio + swatch 的结构，
/// 因此选中状态与「是否显示行内编辑/删除」都由这里给出，而不是另加一组按钮。
/// </summary>
public sealed record PaletteOption(
    string Id,
    string Name,
    string Accent,
    string Secondary,
    bool IsCustom,
    IBrush AccentBrush,
    IBrush SecondaryBrush) : System.ComponentModel.INotifyPropertyChanged
{
    private bool _isSelected;

    /// <summary>当前是否被选中（radio 的 IsChecked 绑定）。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ShowsRowActions)));
        }
    }

    /// <summary>上游：只有**选中的自定义方案**才显示行内「编辑方案 / 删除方案」。</summary>
    public bool ShowsRowActions => IsCustom && IsSelected;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public static PaletteOption From(UiPalette palette, bool dark, bool isCustom, bool selected)
    {
        var colors = palette.For(dark);
        return new PaletteOption(palette.Id, palette.Name, colors.Accent, colors.Secondary, isCustom,
            new SolidColorBrush(Color.Parse(colors.Accent)), new SolidColorBrush(Color.Parse(colors.Secondary)))
        {
            IsSelected = selected,
        };
    }
}
