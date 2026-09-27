using Alas.Engine.Imaging;
using Alas.Engine.Rules;

namespace Alas.Engine.Navigation;

public sealed record ImageStabilityEvidence(long FirstSequence, long LastSequence, bool Stable, int CapturedFrames);

/// <summary>ModuleBase.wait_until_stable: color correlation, reference refresh and nonfatal timeout.</summary>
public sealed class ImageStability(IUiDriver ui, IVision vision, Func<ScreenFrame> current)
{
    public static readonly SourceFile Source = new("module/base/base.py",
        "e5e4a798b3c938ac731e38bd1cebfa5c41c82405277eeda92e7f5d29b42d16b1");
    // The native default Timer survives calls and resets only when the image changes.
    // Scope it to this session so independent devices do not share timing state.
    private readonly IntervalTimer _stable = new(ui.Clock, .3, count: 1);

    public async ValueTask<ImageStabilityEvidence> WaitAsync(PixelArea area, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var reference = current();
        if (reference.Sequence <= 0) throw new InvalidDataException("Stability needs a valid initial frame");
        long first = reference.Sequence, previous = first;
        int frames = 0;
        var timeout = new IntervalTimer(ui.Clock, 5, count: 10);
        timeout.Reset();
        while (true)
        {
            if (timeout.Reached()) return new(first, previous, false, frames);
            await ui.ScreenshotAsync(token);
            frames++;
            var frame = current();
            if (frame.Sequence <= previous) throw new InvalidDataException("Stability reused a stale frame");
            previous = frame.Sequence;
            var match = await vision.MatchAsync(frame, new(reference.Png, area, .85,
                TemplatePreprocessing.Color, TemplateArea: area), token);
            token.ThrowIfCancellationRequested();
            if (match.FrameSequence != frame.Sequence || current().Sequence != frame.Sequence || !double.IsFinite(match.Similarity))
                throw new InvalidDataException("Stability match does not belong to the current frame");
            if (match.Matched)
            { if (_stable.Reached()) return new(first, previous, true, frames); }
            else { reference = frame; _stable.Reset(); }
        }
    }
}
