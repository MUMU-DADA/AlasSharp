using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed record WalkInterruptionEvidence(long ObservedFrame, long? FinishedFrame = null,
    bool RetirementHandled = false, bool OffensiveCompleted = false, bool LowEmotionHandled = false);
public readonly record struct WalkInterruptionResult(bool OffMap, bool ResetWalk);

/// <summary>Fleet._goto's fleet-lock interruption order and Combat.map_offensive, entirely in C#.</summary>
public sealed class MapWalkInterruptions(IUiDriver ui, Func<long> frameSequence,
    ICampaignInterruptions interruptions, CombatAppearance combat)
{
    public static readonly SourceFile Source = CampaignState.InitializationSource;
    private readonly List<WalkInterruptionEvidence> _evidence = [];
    public IReadOnlyList<WalkInterruptionEvidence> Evidence => _evidence.AsReadOnly();

    public async ValueTask<WalkInterruptionResult> HandleAsync(long observedFrame, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (observedFrame <= 0 || frameSequence() != observedFrame)
            throw new InvalidDataException("Walk interruption does not belong to the current frame");
        if (await ui.AppearsAsync(UiAssets.Handler.IN_MAP, token: token)) return default;
        int index = _evidence.Count;
        _evidence.Add(new(observedFrame));
        if (await interruptions.RetirementAsync(token))
        {
            _evidence[index] = _evidence[index] with { RetirementHandled = true };
            await OffensiveAsync(token);
            _evidence[index] = _evidence[index] with { OffensiveCompleted = true };
        }
        // Native deliberately checks this even after retirement/map_offensive succeeds.
        bool emotion = await interruptions.LowEmotionAsync(token);
        token.ThrowIfCancellationRequested();
        long finished = frameSequence();
        if (finished < observedFrame) throw new InvalidDataException("Walk interruption moved to an older frame");
        _evidence[index] = _evidence[index] with { FinishedFrame = finished, LowEmotionHandled = emotion };
        return new(true, _evidence[index].OffensiveCompleted || emotion);
    }

    public async ValueTask OffensiveAsync(CancellationToken token)
    {
        bool first = true;
        if (frameSequence() <= 0) throw new InvalidDataException("Map offensive requires a current screenshot");
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!first)
            {
                long sequence = frameSequence();
                await ui.ScreenshotAsync(token);
                if (frameSequence() <= sequence) throw new InvalidDataException("Map offensive reused a stale screenshot");
            }
            first = false;
            if (await ui.AppearsAsync(UiAssets.Map.MAP_OFFENSIVE, interval: 1, token: token))
            { await ui.ClickAsync(UiAssets.Map.MAP_OFFENSIVE, token); continue; }
            if (await interruptions.LowEmotionAsync(token))
            { ui.ResetInterval(UiAssets.Map.MAP_OFFENSIVE); continue; }
            if (await interruptions.RetirementAsync(token)) continue;
            if (await combat.AppearsAsync(token)) return;
        }
    }
}
