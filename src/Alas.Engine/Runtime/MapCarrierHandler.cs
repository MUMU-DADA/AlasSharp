using System.Collections.Immutable;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed record MapCarrierResult(long ObservedSequence, EnemySearchWaitResult Waiting)
{
    public bool IsConsistent => ObservedSequence > 0 && Waiting.FrameSequence > ObservedSequence && Waiting.CapturedFrames > 0 &&
        !(Waiting.TimedOut && Waiting.CombatLoading);
}
public sealed record CarrierEncounterEvidence(int Fleet, Cell Destination, MapCarrierResult Result);
public sealed record CarrierScanEvidence(int CarrierCount, ImmutableArray<Cell> NewEnemies, MapScanResult Scan);

/// <summary>Native mystery carrier branch. The counter records the observed spawn before waiting.</summary>
public sealed class MapCarrierHandler(IUiDriver ui, CampaignState state, bool enabled,
    Func<long> frameSequence, MapEnemySearching searching, IMapEncounterHandler? next = null) : IMapEncounterHandler
{
    public static readonly SourceFile Source = MapMysteryItemHandler.Source;
    public async ValueTask<MapEncounterHandling> HandleAsync(MapEncounterKind encounter, CancellationToken token)
    {
        if (encounter != MapEncounterKind.CarrierSpawn)
            return next is null ? new(MapEncounterContinuation.Unhandled) : await next.HandleAsync(encounter, token);
        if (!enabled || !await MapEnemySearching.AppearsAsync(ui, token)) return new(MapEncounterContinuation.Unhandled);
        long observed = frameSequence();
        if (observed <= 0) throw new InvalidDataException("Carrier observation requires a numbered frame");
        state.CarrierCount = checked(state.CarrierCount + 1);
        var wait = await searching.WaitAsync(TimeSpan.FromMinutes(2), token);
        return new(MapEncounterContinuation.InMap, Carrier: new(observed, wait));
    }
}
