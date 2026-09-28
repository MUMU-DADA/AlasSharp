using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed record CampaignMapInfo(long FrameSequence, double ClearPercentage, bool Star1, bool Star2,
    bool Star3, bool ThreatSafe, bool ClearModeAvailable)
{
    public bool FullyCleared => ClearPercentage > .95;
    public bool ThreeStars => Star1 && Star2 && Star3;
}
public sealed record CampaignMapPreparationResult(CampaignMapInfo Info, bool ClearMode,
    bool ClearModeChanged, AutoSearchObservation AutoSearch);
public sealed record DoubleBookObservation(bool Available, bool? Enabled, int Clicks, long FrameSequence);
public sealed record CampaignPreparationEvidence(CampaignMapInfo? Info = null, bool? ClearMode = null,
    AutoSearchObservation? AutoSearch = null, DoubleBookObservation? DoubleBook = null);

public interface ICampaignMapPreparationService
{
    ValueTask<CampaignMapPreparationResult> PrepareMapAsync(CampaignConfiguration configuration,
        TimeSpan timeout, CancellationToken token);
    ValueTask<DoubleBookObservation> PrepareDoubleBookAsync(CampaignConfiguration configuration,
        TimeSpan timeout, CancellationToken token);
}

/// <summary>MapOperation.handle_map_preparation and FastForwardHandler, with pure CV measurements only.</summary>
public sealed class CampaignMapPreparation(IUiDriver ui, IImagePatchVision patches, IColorBarVision bars,
    Func<ScreenFrame> currentFrame, Func<CancellationToken, ValueTask<int>> infoBars)
    : ICampaignMapPreparationService
{
    public static readonly SourceFile Source = CampaignFleetLock.Source;
    private readonly UiVisuals _visuals = new(patches, currentFrame);
    private readonly CampaignAutoSearch _auto = new(ui, patches, currentFrame);
    private static ButtonOffset Offset => ButtonOffset.Expand(20, 20);
    public CampaignPreparationEvidence Evidence { get; private set; } = new();

    public async ValueTask<CampaignMapPreparationResult> PrepareMapAsync(CampaignConfiguration configuration,
        TimeSpan timeout, CancellationToken token)
    {
        using var deadline = Deadline(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        var limit = new IntervalTimer(ui.Clock, timeout.TotalSeconds); limit.Reset();
        try
        {
            await WaitReadyAsync(configuration, limit, linked.Token);
            var info = await ReadInfoAsync(configuration, linked.Token);
            Evidence = Evidence with { Info = info };
            bool clear = info.ClearModeAvailable && configuration.UseClearMode;
            bool changed = false;
            if (info.ClearModeAvailable)
            {
                changed = await new UiSwitch(ui,
                    [new("on", UiAssets.Handler.CLEAR_MODE_TITLE, UiAssets.Handler.CLEAR_MODE_CHECK),
                     new("off", UiAssets.Handler.CLEAR_MODE_TITLE, UiAssets.Handler.CLEAR_MODE_CHECK)],
                    Offset, read: ReadClearModeAsync, frameSequence: () => currentFrame().Sequence)
                    .SetAsync(clear ? "on" : "off", timeout, token: linked.Token);
                Evidence = Evidence with { ClearMode = clear };
                if (changed) await WaitAutoSearchAsync(limit, linked.Token);
            }
            else Evidence = Evidence with { ClearMode = false };
            var objectives = CampaignObjectives.Apply(configuration with { PreparationInfo = info });
            var auto = await _auto.EnsureModeOnFrameAsync(clear && configuration.UseAutoSearch &&
                !objectives.ClearAllThisTime, timeout, linked.Token);
            Evidence = Evidence with { AutoSearch = auto };
            return new(info, clear, changed, auto);
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException("Map preparation state was not confirmed", error); }
    }

    private async ValueTask WaitReadyAsync(CampaignConfiguration configuration, IntervalTimer limit, CancellationToken token)
    {
        double previous = -1;
        var stable = new IntervalTimer(ui.Clock, .3, 1); stable.Reset();
        long sequence = ui.HasFrame ? currentFrame().Sequence : 0;
        while (true)
        {
            CheckLimit(limit, token);
            sequence = await FreshFrameAsync(sequence, token);
            if (!await ui.AppearsAsync(UiAssets.Map.MAP_PREPARATION, Offset, token: token) &&
                !await ui.AppearsAsync(UiAssets.Map.MAP_PREPARATION_HARD, Offset, token: token))
            { previous = -1; stable.Reset(); continue; }
            if (!configuration.HasClearPercentage || configuration.IsOneTimeStage) return;
            if (await infoBars(token) > 0) continue;
            double percentage = await PercentageAsync(configuration, token);
            if (percentage > .95 && previous >= 0 && previous < .95) return;
            if (Math.Abs(percentage - previous) < .02)
            {
                previous = percentage;
                if (stable.Reached()) return;
            }
            else { previous = percentage; stable.Reset(); }
        }
    }

    public async ValueTask<CampaignMapInfo> ReadInfoAsync(CampaignConfiguration configuration, CancellationToken token)
    {
        long sequence = currentFrame().Sequence;
        double percentage = await PercentageAsync(configuration, token);
        async ValueTask<bool> Star(AssetRule asset) => await _visuals.ColorCountAsync(
            asset.For(ui.Server).Area ?? throw new InvalidDataException("Star has no area"), new(250, 232, 140), 75, 35, token);
        bool first = await Star(UiAssets.Handler.MAP_STAR_1), second = await Star(UiAssets.Handler.MAP_STAR_2),
            third = await Star(UiAssets.Handler.MAP_STAR_3);
        bool safe = await ui.AppearsAsync(UiAssets.Handler.MAP_GREEN, Offset, token: token);
        bool available = percentage > .95 && await ReadClearModeAsync(token) is not null;
        EnsureFrame(sequence);
        return new(sequence, percentage, first, second, third, safe, available);
    }

    public async ValueTask<double> PercentageAsync(CampaignConfiguration configuration, CancellationToken token)
    {
        var frame = currentFrame();
        var values = await bars.ColorBarsAsync(frame,
            [new(UiAssets.Handler.MAP_CLEAR_PERCENTAGE.For(ui.Server).Area!.Value.Area, new(231, 170, 82))], token);
        token.ThrowIfCancellationRequested(); EnsureFrame(frame.Sequence);
        if (values.Count != 1 || !double.IsFinite(values[0]) || values[0] is < 0 or >= 1)
            throw new InvalidDataException("Invalid map completion measurement");
        return values[0] * (configuration.ClearPercentageShort ? 1.4 : 1);
    }

    public async ValueTask<string?> ReadClearModeAsync(CancellationToken token)
    {
        if (!await ui.AppearsAsync(UiAssets.Handler.CLEAR_MODE_TITLE, Offset, token: token)) return null;
        ui.LoadOffset(UiAssets.Handler.CLEAR_MODE_CHECK, UiAssets.Handler.CLEAR_MODE_TITLE);
        var area = ui.ButtonArea(UiAssets.Handler.CLEAR_MODE_CHECK);
        if (await _visuals.ColorCountAsync(area, new(130, 229, 255), 30, 50, token)) return "on";
        if (await _visuals.ColorCountAsync(area, new(255, 255, 255), 30, 200, token)) return "off";
        return null;
    }

    private async ValueTask WaitAutoSearchAsync(IntervalTimer limit, CancellationToken token)
    {
        var animation = new IntervalTimer(ui.Clock, 1, 3); animation.Reset();
        long sequence = currentFrame().Sequence;
        while (true)
        {
            CheckLimit(limit, token);
            sequence = await FreshFrameAsync(sequence, token);
            if (await _auto.ReadAsync(token) is not null || animation.Reached()) return;
        }
    }

    public async ValueTask<DoubleBookObservation> PrepareDoubleBookAsync(CampaignConfiguration configuration,
        TimeSpan timeout, CancellationToken token)
    {
        if (!configuration.IsClearMode)
        {
            token.ThrowIfCancellationRequested();
            var unavailable = new DoubleBookObservation(false, false, 0, currentFrame().Sequence);
            Evidence = Evidence with { DoubleBook = unavailable };
            return unavailable;
        }
        using var deadline = Deadline(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        var limit = new IntervalTimer(ui.Clock, timeout.TotalSeconds); limit.Reset();
        var absent = new IntervalTimer(ui.Clock, .3, 1); absent.Reset();
        int clicks = 0;
        long sequence = currentFrame().Sequence;
        try
        {
            // Inspect the caller's fleet-preparation frame first, as in the native loop.
            while (true)
            {
                CheckLimit(limit, linked.Token);
                if (clicks > 3) throw new TimeoutException("Double-book setting was not confirmed after four clicks");
                if (await ui.AppearsAsync(UiAssets.Handler.BOOK_CHECK_PREP, ButtonOffset.Expand(250, 30),
                        interval: 3, token: linked.Token))
                {
                    ui.LoadOffset(UiAssets.Handler.BOOK_BOX_PREP, UiAssets.Handler.BOOK_CHECK_PREP);
                    bool enabled = await _visuals.ColorCountAsync(ui.ButtonArea(UiAssets.Handler.BOOK_BOX_PREP),
                        new(156, 255, 82), 30, 20, linked.Token);
                    var observed = new DoubleBookObservation(true, enabled, clicks, sequence);
                    Evidence = Evidence with { DoubleBook = observed };
                    if (enabled == configuration.UseDoubleBook) return observed;
                    // A mutation invalidates the previous value even if the input call fails.
                    Evidence = Evidence with { DoubleBook = observed with { Enabled = null, Clicks = clicks + 1 } };
                    await ui.ClickAsync(UiAssets.Handler.BOOK_BOX_PREP, linked.Token);
                    clicks++;
                }
                if (clicks == 0 && absent.Reached())
                {
                    var unavailable = new DoubleBookObservation(false, false, 0, sequence);
                    Evidence = Evidence with { DoubleBook = unavailable };
                    return unavailable;
                }
                sequence = await FreshFrameAsync(sequence, linked.Token);
            }
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException("Double-book setting was not confirmed", error); }
    }

    private async ValueTask<long> FreshFrameAsync(long previous, CancellationToken token)
    {
        await ui.ScreenshotAsync(token);
        long sequence = currentFrame().Sequence;
        if (sequence <= previous) throw new InvalidDataException("Map preparation reused a stale screenshot");
        return sequence;
    }
    private void EnsureFrame(long sequence)
    {
        if (sequence <= 0 || currentFrame().Sequence != sequence)
            throw new InvalidDataException("Map preparation measurements belong to a changed frame");
    }
    private static CancellationTokenSource Deadline(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(timeout));
        return new(timeout);
    }
    private static void CheckLimit(IntervalTimer limit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (limit.Reached()) throw new TimeoutException("Map preparation exceeded its time limit");
    }
}
