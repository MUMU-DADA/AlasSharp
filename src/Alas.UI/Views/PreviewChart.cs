using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Alas.UI.Views;

/// <summary>共享绘制能力的固定演示，不读取或推断任务统计。</summary>
public sealed class PreviewChart : Control
{
    private static readonly double[] Samples = [0.2, 0.3, 0.28, 0.5, 0.4, 0.65, 0.6, 0.85, 0.75, 0.55, 0.62, 0.4];

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var pen = new Pen(new SolidColorBrush(Color.Parse("#0071E3")), 3);
        var grid = new Pen(new SolidColorBrush(Color.Parse("#8096A5B7")), 0.5);
        double width = Math.Max(0, Bounds.Width - 12), height = Math.Max(0, Bounds.Height - 12);
        for (int row = 0; row < 4; row++)
            context.DrawLine(grid, new Point(6, 6 + height * row / 3), new Point(6 + width, 6 + height * row / 3));
        for (int i = 1; i < Samples.Length; i++)
            context.DrawLine(pen,
                new Point(6 + width * (i - 1) / (Samples.Length - 1), 6 + height * (1 - Samples[i - 1])),
                new Point(6 + width * i / (Samples.Length - 1), 6 + height * (1 - Samples[i])));
    }
}
