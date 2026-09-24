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
        EditPaletteCommand = new PreviewCommand(parameter => OpenPaletteDialog(parameter as PaletteOption));
        DeletePaletteCommand = new PreviewCommand(parameter => DeletePalette(parameter as PaletteOption));
        SavePaletteCommand = new PreviewCommand(_ => SavePalette());
        CancelPaletteCommand = new PreviewCommand(_ => ClosePaletteDialog());
        ApplyBackgroundCommand = new PreviewCommand(_ => ApplyBackground());
        ReloadPalettes();
    }

    public string Title => "界面设置";

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
    public string DraftName { get => _draftName; set => SetField(ref _draftName, value); }
    public string DraftPrimary { get => _draftPrimary; set { if (SetField(ref _draftPrimary, value)) Notify(nameof(DraftPreview)); } }
    public string DraftSecondary { get => _draftSecondary; set { if (SetField(ref _draftSecondary, value)) Notify(nameof(DraftPreview)); } }

    public IBrush DraftPreview => new SolidColorBrush(
        UiPalettes.IsValidColor(DraftPrimary) ? Color.Parse(DraftPrimary) : Colors.Transparent);

    public string DraftError
    {
        get => _draftError;
        private set => SetField(ref _draftError, value);
    }

    public string PaletteLimitNotice =>
        $"最多 {UiPalettes.MaxCustom} 个自定义配色，当前 {CustomPalettes.Count} 个。";

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
        Palettes.Clear();
        foreach (var preset in UiPalettes.Presets)
        {
            Palettes.Add(PaletteOption.From(preset, _theme.IsDark, isCustom: false));
        }
        foreach (var custom in _theme.Preference.CustomPalettes)
        {
            Palettes.Add(PaletteOption.From(custom, _theme.IsDark, isCustom: true));
        }
        Notify(nameof(PaletteLimitNotice));
    }

    private void OpenPaletteDialog(PaletteOption? option)
    {
        _editing = option?.IsCustom == true ? option.Id : null;
        DraftName = option?.Id.Replace("custom:", string.Empty) ?? string.Empty;
        DraftPrimary = option?.Accent ?? "#245DBE";
        DraftSecondary = option?.Secondary ?? "#13777C";
        DraftError = string.Empty;
        IsPaletteDialogOpen = true;
        Notify(nameof(DraftTitle));
        Notify(nameof(DraftPreview));
    }

    private void ClosePaletteDialog()
    {
        // 上游：取消必须丢弃草稿，不写回偏好。
        IsPaletteDialogOpen = false;
        _editing = null;
        DraftName = string.Empty;
        DraftPrimary = string.Empty;
        DraftSecondary = string.Empty;
        DraftError = string.Empty;
        Notify(nameof(DraftTitle));
    }

    private void SavePalette()
    {
        if (!UiPalettes.IsValidColor(DraftPrimary) || !UiPalettes.IsValidColor(DraftSecondary))
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

    private void DeletePalette(PaletteOption? option)
    {
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

/// <summary>配色格子：显示主/副两个色块，自定义配色额外显示编辑/删除。</summary>
public sealed record PaletteOption(string Id, string Accent, string Secondary, bool IsCustom, IBrush AccentBrush, IBrush SecondaryBrush)
{
    public static PaletteOption From(UiPalette palette, bool dark, bool isCustom)
    {
        var colors = palette.For(dark);
        return new PaletteOption(palette.Id, colors.Accent, colors.Secondary, isCustom,
            new SolidColorBrush(Color.Parse(colors.Accent)), new SolidColorBrush(Color.Parse(colors.Secondary)));
    }
}
