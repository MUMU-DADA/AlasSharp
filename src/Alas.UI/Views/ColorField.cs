using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Alas.UI.Theming;

namespace Alas.UI.Views;

/// <summary>
/// 配色弹窗里「一个颜色」的取值控件：**真实取色控件 + 十六进制文本框**，两者双向同步。
///
/// 上游对应 <c>ThemePreferences.tsx</c> 的一行：
/// <c>&lt;label&gt;</c> + <c>&lt;input type="color"&gt;</c> + 十六进制文本框
/// （<c>maxLength=7</c>、<c>pattern="#[0-9a-fA-F]{6}"</c>、非法时 <c>aria-invalid</c>）。
///
/// 关于"取色控件"：Avalonia 的 <c>ColorPicker</c>/<c>ColorView</c> 属于独立包
/// `Avalonia.Controls.ColorPicker`，本仓离线包源里**没有**该包（构建必须离线），
/// 因此这里用**基础控件**搭一个等价的取色器：HSV 色相/饱和度/明度三个滑杆组成的色域 +
/// 十六进制文本框。它表达的是与 HTML 取色器相同的能力（任意颜色可选、改动即时回显），
/// 不是占位标签；不引入新依赖，也不伪造能力。
/// </summary>
public sealed class ColorField : UserControl
{
    /// <summary>当前颜色（<c>#RRGGBB</c> 大写）；非法输入时保持上一次的合法值。</summary>
    public static readonly StyledProperty<string> ColorHexProperty =
        AvaloniaProperty.Register<ColorField, string>(nameof(ColorHex), "#000000", defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>是否已启用（禁用时整块不可交互）。</summary>
    public static readonly StyledProperty<bool> IsEditableProperty =
        AvaloniaProperty.Register<ColorField, bool>(nameof(IsEditable), true);

    private readonly Slider _hue;
    private readonly Slider _saturation;
    private readonly Slider _value;
    private readonly TextBox _hex;
    private readonly Border _preview;
    private bool _updating;

    public ColorField()
    {
        _preview = new Border
        {
            Name = "ColorFieldPreview",
            Width = 32, Height = 32, CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
        };
        _preview[!Border.BorderBrushProperty] =
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AlasBorderBrush");

        _hue = Slider("ColorFieldHue", "色相", 0, 360, "°");
        _saturation = Slider("ColorFieldSaturation", "饱和度", 0, 100, "%");
        _value = Slider("ColorFieldValue", "明度", 0, 100, "%");

        _hex = new TextBox
        {
            Name = "ColorFieldHex",
            FontSize = 12, Width = 120, MaxLength = 7,
            // 上游用 pattern + maxLength 约束；这里保持同样的输入形态（#RRGGBB）。
            Watermark = "#RRGGBB",
        };
        Avalonia.Automation.AutomationProperties.SetName(_hex, "颜色十六进制值");

        _hex.PropertyChanged += (_, args) =>
        {
            if (args.Property != TextBox.TextProperty || _updating) return;
            var text = _hex.Text ?? string.Empty;
            if (UiPalettes.IsValidColor(text))
            {
                SetInvalid(false);
                SetCurrentValue(ColorHexProperty, text.ToUpperInvariant());
            }
            else
            {
                // 非法：如实标记，不回写颜色（上游 aria-invalid + 错误文案）。
                SetInvalid(true);
            }
        };
        _hue.PropertyChanged += OnChannelChanged;
        _saturation.PropertyChanged += OnChannelChanged;
        _value.PropertyChanged += OnChannelChanged;

        var swatch = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { _preview, _hex },
        };
        var channels = new StackPanel { Spacing = 4, Children = { _hue, _saturation, _value } };
        Content = new StackPanel { Name = "ColorFieldRoot", Spacing = 6, Children = { swatch, channels } };
        SyncFromHex(ColorHex);
    }

    /// <summary>当前颜色（<c>#RRGGBB</c> 大写）；非法输入时保持上一次的合法值。</summary>
    public string ColorHex
    {
        get => GetValue(ColorHexProperty);
        set => SetValue(ColorHexProperty, value);
    }

    /// <summary>是否已启用（禁用时整块不可交互）。</summary>
    public bool IsEditable
    {
        get => GetValue(IsEditableProperty);
        set => SetValue(IsEditableProperty, value);
    }

    /// <summary>十六进制值当前是否非法（供弹窗决定"保存"是否可用）。</summary>
    public bool IsInvalid => _invalid;

    private bool _invalid;

    /// <summary>非法状态变化时触发。</summary>
    public event Action? InvalidChanged;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ColorHexProperty) SyncFromHex(ColorHex);
        else if (change.Property == IsEditableProperty) IsEnabled = IsEditable;
    }

    /// <summary>把当前十六进制值拆成 HSV 填到滑杆上（不触发回写）。</summary>
    private void SyncFromHex(string hex)
    {
        _updating = true;
        try
        {
            var color = UiPalettes.IsValidColor(hex) ? Color.Parse(hex) : Colors.Black;
            var hsv = color.ToHsv();
            _hue.Value = Math.Round(hsv.H);
            _saturation.Value = Math.Round(hsv.S * 100);
            _value.Value = Math.Round(hsv.V * 100);
            _preview.Background = new SolidColorBrush(color);
            if (_hex.Text != hex) _hex.Text = hex;
            _invalid = false;
        }
        finally { _updating = false; }
        PublishInvalid();
    }

    private void OnChannelChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property != RangeBase.ValueProperty || _updating) return;
        var hsv = new HsvColor(1, _hue.Value, _saturation.Value / 100, _value.Value / 100);
        var color = hsv.ToRgb();
        var hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        _updating = true;
        try
        {
            _preview.Background = new SolidColorBrush(color);
            _hex.Text = hex;
            _invalid = false;
        }
        finally { _updating = false; }
        SetCurrentValue(ColorHexProperty, hex);
        PublishInvalid();
    }

    private void PublishInvalid() => InvalidChanged?.Invoke();

    /// <summary>
    /// 设置非法状态并**按变化**通知订阅者。
    /// 不能只在赋值后调用通知：从十六进制框清掉非法内容时，颜色属性路径不会产生变化事件
    /// （值没变），订阅者于是收不到"已经合法"这件事，保存键会一直灰着。
    /// </summary>
    private void SetInvalid(bool value)
    {
        if (_invalid == value)
        {
            PublishInvalid();
            return;
        }
        _invalid = value;
        PublishInvalid();
    }

    /// <summary>离屏检查用：直接按颜色分量设值，等价于用户拖动取色滑杆。</summary>
    public void SetChannels(double hue, double saturationPercent, double valuePercent)
    {
        _hue.Value = hue;
        _saturation.Value = saturationPercent;
        _value.Value = valuePercent;
    }

    private static Slider Slider(string name, string label, double min, double max, string unit)
    {
        var slider = new Slider
        {
            Name = name, Minimum = min, Maximum = max, Width = 200,
            HorizontalAlignment = HorizontalAlignment.Left, FontSize = 12,
        };
        Avalonia.Automation.AutomationProperties.SetName(slider, label);
        ToolTip.SetTip(slider, label);
        return slider;
    }

    /// <summary>供检查读取滑杆（离屏下用真实指针操作滑杆）。</summary>
    public Slider HueSlider => _hue;
    public Slider SaturationSlider => _saturation;
    public Slider ValueSlider => _value;
    public TextBox HexBox => _hex;
    public Border Preview => _preview;
}
