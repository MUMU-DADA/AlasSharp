using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed record MapVisualObservation(MapViewFrame View, IReadOnlyList<MapCellObservation> Cells);
public interface IMapObservationService
{
    ValueTask<MapVisualObservation> ObserveMapAsync(CampaignRule rule, CancellationToken token);
}
