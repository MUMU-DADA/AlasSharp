using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

/// <summary>AmbushHandler.handle_walk_out_of_step: native info-bar gate and letter transform.</summary>
public sealed class MapWalkStep(IUiDriver ui, IMapUiObservations observations, IVision vision,
    AssetFiles assets, Func<ScreenFrame> current)
{
    public static readonly SourceFile Source = MapEncounterProbe.AmbushSource;

    public async ValueTask<bool> ObserveAsync(long sequence, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var frame = current();
        if (sequence <= 0 || frame.Sequence != sequence) throw new InvalidDataException("Walk message belongs to another frame");
        bool found = false;
        if (await observations.InfoBarCountAsync(token) > 0)
        {
            var template = await assets.ReadAsync(UiAssets.Template.TEMPLATE_MAP_WALK_OUT_OF_STEP.For(ui.Server), token);
            var result = await vision.MatchAsync(frame, new(template,
                UiAssets.Handler.INFO_BAR_DETECT.For(ui.Server).Area!.Value.Area, .85, Transform: new(64, .75)), token);
            if (result.FrameSequence != sequence) throw new InvalidDataException("Walk message returned stale CV evidence");
            found = result.Matched;
        }
        token.ThrowIfCancellationRequested();
        if (current().Sequence != sequence) throw new InvalidDataException("Walk message changed frames during recognition");
        return found;
    }

    public async ValueTask ClearAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var limit = new IntervalTimer(ui.Clock, 30); limit.Reset();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), ui.Clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            while (await observations.InfoBarCountAsync(linked.Token) > 0)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (limit.Reached()) throw new TimeoutException("Walk information bar did not disappear");
                long before = current().Sequence;
                await ui.ScreenshotAsync(linked.Token);
                if (current().Sequence <= before) throw new InvalidDataException("Walk information bar reused a stale screenshot");
            }
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException("Walk information bar did not disappear", error); }
    }
}
