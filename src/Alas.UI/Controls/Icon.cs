using System;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace Alas.UI.Controls;

/// <summary>
/// 按上游 lucide 图标的 24×24 网格绘制几何：坐标不变，描边宽度按控件尺寸等比缩放
/// （24px 时 2px，与上游默认参数一致）。
/// </summary>
public sealed class Icon : TemplatedControl
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<Icon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(Size), 20d);

    public static readonly StyledProperty<double> StrokeWidthProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(StrokeWidth), 2d);

    static Icon()
    {
        AffectsRender<Icon>(DataProperty, SizeProperty, StrokeWidthProperty, ForegroundProperty);
        AffectsMeasure<Icon>(SizeProperty);
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    /// <summary>24 坐标系内的描边宽度。</summary>
    public double StrokeWidth
    {
        get => GetValue(StrokeWidthProperty);
        set => SetValue(StrokeWidthProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        if (Data is null) return;
        var scale = Math.Min(Bounds.Width, Bounds.Height) / 24d;
        if (scale <= 0) return;
        using var transform = context.PushTransform(Matrix.CreateScale(scale, scale));
        var pen = new Pen(Foreground ?? Brushes.Black, StrokeWidth)
        {
            LineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        context.DrawGeometry(null, pen, Data);
    }
}
