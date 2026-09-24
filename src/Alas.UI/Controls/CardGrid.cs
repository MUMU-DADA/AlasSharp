using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;

namespace Alas.UI.Controls;

/// <summary>
/// 对应上游 <c>.resource-grid</c> 的弹性换行行为：
/// 每张卡片基准宽度 <see cref="MinItemWidth"/>（CSS <c>flex: 1 1 150px</c>），
/// 一行放得下几张就放几张，再等宽拉伸填满整行；卡片间距与行距是 <see cref="ItemGap"/>。
/// </summary>
public sealed class CardGrid : Panel
{
    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<CardGrid, double>(nameof(MinItemWidth), 150d);

    public static readonly StyledProperty<double> ItemGapProperty =
        AvaloniaProperty.Register<CardGrid, double>(nameof(ItemGap), 8d);

    static CardGrid()
    {
        AffectsMeasure<CardGrid>(MinItemWidthProperty, ItemGapProperty);
    }

    public double MinItemWidth
    {
        get => GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public double ItemGap
    {
        get => GetValue(ItemGapProperty);
        set => SetValue(ItemGapProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = Children.Count;
        if (count == 0) return default;
        var gap = Math.Max(0, ItemGap);
        var width = double.IsInfinity(availableSize.Width)
            ? count * MinItemWidth + (count - 1) * gap
            : availableSize.Width;
        var columns = Columns(width, count, gap);
        var itemWidth = Math.Max(0, (width - (columns - 1) * gap) / columns);

        var rows = new List<double>();
        double rowHeight = 0;
        for (var index = 0; index < count; index++)
        {
            var child = Children[index];
            child.Measure(new Size(itemWidth, availableSize.Height));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            if ((index + 1) % columns != 0 && index != count - 1) continue;
            rows.Add(rowHeight);
            rowHeight = 0;
        }

        var height = 0d;
        foreach (var row in rows) height += row;
        height += gap * Math.Max(0, rows.Count - 1);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var count = Children.Count;
        if (count == 0) return finalSize;
        var gap = Math.Max(0, ItemGap);
        var columns = Columns(finalSize.Width, count, gap);
        var itemWidth = Math.Max(0, (finalSize.Width - (columns - 1) * gap) / columns);

        var offset = 0d;
        for (var start = 0; start < count; start += columns)
        {
            var end = Math.Min(start + columns, count);
            var rowHeight = 0d;
            for (var index = start; index < end; index++) rowHeight = Math.Max(rowHeight, Children[index].DesiredSize.Height);
            for (var index = start; index < end; index++)
            {
                var column = index - start;
                Children[index].Arrange(new Rect(column * (itemWidth + gap), offset, itemWidth, rowHeight));
            }
            offset += rowHeight + gap;
        }
        return finalSize;
    }

    private int Columns(double width, int count, double gap)
    {
        var perRow = (int)Math.Floor((width + gap) / (Math.Max(1, MinItemWidth) + gap));
        return Math.Clamp(perRow, 1, count);
    }
}
