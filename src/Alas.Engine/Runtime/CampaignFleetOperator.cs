using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed record FleetSlot(AssetRule Choose, AssetRule Advice, AssetRule Bar,
    AssetRule Clear, AssetRule InUse, AssetRule HardSatisfied)
{
    public static FleetSlot First { get; } = new(UiAssets.Map.FLEET_1_CHOOSE, UiAssets.Map.FLEET_1_ADVICE,
        UiAssets.Map.FLEET_1_BAR, UiAssets.Map.FLEET_1_CLEAR, UiAssets.Map.FLEET_1_IN_USE,
        UiAssets.Map.FLEET_1_HARD_SATIESFIED);
    public static FleetSlot Second { get; } = new(UiAssets.Map.FLEET_2_CHOOSE, UiAssets.Map.FLEET_2_ADVICE,
        UiAssets.Map.FLEET_2_BAR, UiAssets.Map.FLEET_2_CLEAR, UiAssets.Map.FLEET_2_IN_USE,
        UiAssets.Map.FLEET_2_HARD_SATIESFIED);
    public static FleetSlot SecondFor(IUiDriver ui)
    {
        var clear = UiAssets.Map.FLEET_1_CLEAR;
        var original = clear.For(ui.Server).Area ?? throw new InvalidDataException("Fleet clear has no area");
        return ui.ButtonArea(clear).Top - original.Top < -10
            ? Second with { InUse = UiAssets.Map.FLEET_2_IN_USE_W15 } : Second;
    }
    public static FleetSlot Submarine { get; } = new(UiAssets.Map.SUBMARINE_CHOOSE, UiAssets.Map.SUBMARINE_ADVICE,
        UiAssets.Map.SUBMARINE_BAR, UiAssets.Map.SUBMARINE_CLEAR, UiAssets.Map.SUBMARINE_IN_USE,
        UiAssets.Map.SUBMARINE_HARD_SATIESFIED);
}

/// <summary>Upstream FleetOperator control flow over C# button rules and numeric CV observations.</summary>
public sealed class CampaignFleetOperator(IUiDriver ui, IImagePatchVision vision,
    Func<ScreenFrame> currentFrame, FleetSlot slot, IPopupHandler? popups = null)
{
    public static readonly SourceFile Source = new("module/map/map_fleet_preparation.py",
        "247f3df730c70a6f5bcb21412f96be0390b82d38b173412a5e49468264619589");
    private static ButtonOffset SearchOffset => ButtonOffset.Bounds(-20, -80, 20, 5);
    private const int RowHeight = 33;
    private const int RowStep = 42;

    public async ValueTask InitializeAsync(CancellationToken token = default)
    {
        if (!await AllowedAsync(token)) return;
        foreach (var asset in new[] { slot.Choose, slot.Bar, slot.InUse, slot.HardSatisfied })
            ui.LoadOffset(asset, slot.Clear);
    }

    public ValueTask<bool> AllowedAsync(CancellationToken token = default)
        => ui.AppearsAsync(slot.Clear, SearchOffset, token: token);

    public ValueTask<bool> IsHardAsync(CancellationToken token = default)
        => ui.AppearsAsync(slot.Advice, SearchOffset, token: token);

    public async ValueTask<bool?> HardSatisfiedAsync(CancellationToken token = default)
    {
        if (!await IsHardAsync(token)) return null;
        var frame = currentFrame();
        var area = ui.ButtonArea(slot.HardSatisfied);
        var peaks = await vision.MeasurePatchAsync(frame, new(area.Area, area.Width, area.Height,
            PatchMeasure.RowPeakCount, PatchProcessing.ColorSimilarity, new(249, 199, 0),
            PeakHeight: 180, PeakDistance: 5), token);
        if (peaks.FrameSequence != frame.Sequence) throw new InvalidDataException("Hard fleet restriction belongs to another frame");
        return peaks.Value > 0;
    }

    public async ValueTask<bool> InUseAsync(CancellationToken token = default)
    {
        var frame = currentFrame();
        var area = ui.ButtonArea(slot.InUse);
        var mean = await ui.ColorAsync(area, token);
        if (mean.FrameSequence != frame.Sequence) throw new InvalidDataException("Fleet color belongs to another frame");
        if (Similar(mean, new(224, 154, 114), 30) || Similar(mean, new(124, 141, 171), 30)) return true;
        var deviation = await vision.MeasurePatchAsync(frame, new(area.Area, area.Width, area.Height,
            PatchMeasure.StandardDeviation, PatchProcessing.Gray), token);
        if (deviation.FrameSequence != frame.Sequence) throw new InvalidDataException("Fleet contrast belongs to another frame");
        return deviation.Value > 27;
    }

    public async ValueTask<bool> BarOpenedAsync(CancellationToken token = default)
    {
        var frame = currentFrame();
        var bar = ui.ButtonArea(slot.Bar);
        var observation = await vision.MeasurePatchAsync(frame, new(
            new(bar.Right - 1, bar.Top, 1, bar.Height), 1, bar.Height,
            PatchMeasure.SimilarityCount, PatchProcessing.Gray, MinimumSimilarity: 169), token);
        if (observation.FrameSequence != frame.Sequence) throw new InvalidDataException("Fleet dropdown belongs to another frame");
        return observation.Value / bar.Height > .5;
    }

    public async ValueTask<IReadOnlyList<int>> SelectedAsync(CancellationToken token = default)
    {
        var frame = currentFrame();
        var bar = ui.ButtonArea(slot.Bar);
        var selected = new List<int>();
        for (int index = 1, y = 0; y < bar.Height; index++, y += RowStep)
        {
            var mean = await ui.ColorAsync(new(bar.Left, bar.Top + y, bar.Right, bar.Top + y + RowHeight), token);
            if (mean.FrameSequence != frame.Sequence) throw new InvalidDataException("Fleet row belongs to another frame");
            double average = (mean.R + mean.G + mean.B) / 3;
            double deviation = Math.Sqrt((Math.Pow(mean.R - average, 2) + Math.Pow(mean.G - average, 2) +
                Math.Pow(mean.B - average, 2)) / 2);
            if (deviation > 45) selected.Add(index);
        }
        return selected;
    }

    public async ValueTask EnsureAsync(int index, CancellationToken token = default)
    {
        var bar = ui.ButtonArea(slot.Bar);
        if (index < 1 || (long)(index - 1) * RowStep + RowHeight > bar.Height)
            throw new ArgumentOutOfRangeException(nameof(index));
        if (!await AllowedAsync(token)) throw new InvalidOperationException("Fleet slot cannot be selected");
        await OpenAsync(token);
        if ((await SelectedAsync(token)).Contains(index)) await CloseAsync(token);
        else await SelectAsync(index, token);
    }

    public async ValueTask ClearAsync(CancellationToken token = default)
    {
        var click = new IntervalTimer(ui.Clock, 3, 6);
        for (int attempt = 0; attempt < 80; attempt++)
        {
            if (attempt > 0) await ui.ScreenshotAsync(token);
            if (popups is not null && await popups.ConfirmAsync(token)) continue;
            if (!await AllowedAsync(token))
            {
                await ui.DelayAsync(TimeSpan.FromMilliseconds(250), token);
                continue;
            }
            if (!await InUseAsync(token)) return;
            if (click.Reached()) { await ui.ClickAsync(slot.Clear, token); click.Reset(); }
            await ui.DelayAsync(TimeSpan.FromMilliseconds(250), token);
        }
        throw new TimeoutException("Fleet could not be cleared");
    }

    public ValueTask ClickClearAsync(CancellationToken token = default)
        => ui.ClickAsync(slot.Clear, token);

    private async ValueTask OpenAsync(CancellationToken token)
        => await WaitForDropdownAsync(true, token);
    private async ValueTask CloseAsync(CancellationToken token)
        => await WaitForDropdownAsync(false, token);

    private async ValueTask WaitForDropdownAsync(bool expected, CancellationToken token)
    {
        var click = new IntervalTimer(ui.Clock, 3, 6);
        for (int attempt = 0; attempt < 80; attempt++)
        {
            if (attempt > 0) await ui.ScreenshotAsync(token);
            if (await BarOpenedAsync(token) == expected) return;
            if (click.Reached()) { await ui.ClickAsync(slot.Choose, token); click.Reset(); }
            await ui.DelayAsync(TimeSpan.FromMilliseconds(250), token);
        }
        throw new TimeoutException(expected ? "Fleet dropdown did not open" : "Fleet dropdown did not close");
    }

    private async ValueTask SelectAsync(int index, CancellationToken token)
    {
        var click = new IntervalTimer(ui.Clock, 3, 6);
        for (int attempt = 0; attempt < 80; attempt++)
        {
            if (attempt > 0) await ui.ScreenshotAsync(token);
            if (!await BarOpenedAsync(token))
            {
                if (await InUseAsync(token)) return;
                await OpenAsync(token);
            }
            if (click.Reached())
            {
                var bar = ui.ButtonArea(slot.Bar);
                int top = checked(bar.Top + (index - 1) * RowStep);
                await ui.ClickAreaAsync(new(bar.Left, top, bar.Right, top + RowHeight), token);
                click.Reset();
            }
            await ui.DelayAsync(TimeSpan.FromMilliseconds(250), token);
        }
        throw new TimeoutException("Fleet selection did not settle");
    }

    private static bool Similar(MeanColorObservation observed, Rgb expected, int threshold)
        => AssetMatcher.ColorSimilar(observed, expected, threshold);
}
