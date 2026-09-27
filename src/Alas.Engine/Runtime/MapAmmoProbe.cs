using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

/// <summary>MysteryHandler.handle_mystery_ammo: a passive, rate-limited observation, not a click or reward verdict.</summary>
public sealed class MapAmmoProbe(IUiDriver ui, IMapUiObservations observations, Func<long> currentFrame)
{
    public static readonly SourceFile Source = MapMysteryItemHandler.Source;
    private readonly IntervalTimer _interval = new(ui.Clock, 3);

    public async ValueTask<bool> ObserveAsync(long frameSequence, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (frameSequence <= 0 || currentFrame() != frameSequence)
            throw new InvalidDataException("Ammo observation belongs to another frame");
        bool present = await observations.InfoBarCountAsync(token) > 0 && _interval.Reached() &&
            await ui.AppearsAsync(UiAssets.Handler.GET_AMMO, token: token);
        token.ThrowIfCancellationRequested();
        if (currentFrame() != frameSequence)
            throw new InvalidDataException("Ammo observation changed frames during recognition");
        if (present) _interval.Reset();
        return present;
    }
}
