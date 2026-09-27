using System.Collections.Immutable;
using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed record FleetHealthSnapshot(int FleetIndex, long FrameSequence, ImmutableArray<double> Raw,
    ImmutableArray<double> Weighted, ImmutableArray<bool> HasShip);

/// <summary>Per-sortie state keyed by logical fleet; occupied slots are frozen on the fleet's first reading.</summary>
public sealed class FleetHealthState
{
    private readonly Dictionary<int, FleetHealthSnapshot> _fleets = [];
    private readonly List<FleetHealthSnapshot> _observations = [];
    public IReadOnlyList<FleetHealthSnapshot> Observations => _observations.AsReadOnly();
    public void Reset() { _fleets.Clear(); _observations.Clear(); }
    public FleetHealthSnapshot? Get(int fleet) => _fleets.GetValueOrDefault(fleet);
    public FleetHealthSnapshot Commit(int fleet, long frame, IReadOnlyList<double> raw, FleetHealthOptions options)
    {
        if (fleet is not (1 or 2) || frame <= 0) throw new ArgumentException("Invalid fleet health identity");
        var weights = options.Weights();
        var values = raw.ToImmutableArray();
        if (values.Length != 6 || values.Any(value => !double.IsFinite(value) || value is < 0 or > 1))
            throw new InvalidDataException("Fleet health requires six valid measurements");
        var previous = Get(fleet);
        if (previous is not null && frame <= previous.FrameSequence)
            throw new InvalidDataException("Fleet health reused a stale frame");
        var weighted = values.Select((value, i) => i < 3 ? value : value * weights[i - 3] / weights.Max()).ToImmutableArray();
        var snapshot = new FleetHealthSnapshot(fleet, frame, values, weighted,
            previous?.HasShip ?? weighted.Select(value => value > .3).ToImmutableArray());
        _fleets[fleet] = snapshot;
        _observations.Add(snapshot);
        return snapshot;
    }
    public bool RetreatTriggered(int fleet, FleetHealthOptions options)
    {
        _ = options.Weights();
        if (!options.UseLowHpRetreat) return false;
        var snapshot = Get(fleet) ?? throw new InvalidOperationException("Read the current fleet's HP before testing retreat");
        return snapshot.Weighted.Where((_, i) => snapshot.HasShip[i]).Any(value => value < options.LowHpRetreatThreshold);
    }
}

/// <summary>Publishes a complete reading from the same confirmed map image, never a combat-result page.</summary>
public sealed class FleetHealthReader(IUiDriver ui, IColorBarVision vision, Func<ScreenFrame> current)
{
    public async ValueTask<FleetHealthSnapshot> ReadAsync(FleetHealthState state, int fleet,
        FleetHealthOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _ = options.Weights();
        var frame = current();
        if (!await ui.AppearsAsync(UiAssets.Handler.IN_MAP, token: token))
            throw new InvalidDataException("Fleet health requires a confirmed in-map frame");
        var values = await vision.ColorBarsAsync(frame, FleetHealthRules.Bars(ui.Server), token);
        token.ThrowIfCancellationRequested();
        if (current().Sequence != frame.Sequence || values.Count != 12 ||
            values.Any(value => !double.IsFinite(value) || value is < 0 or >= 1))
            throw new InvalidDataException("Fleet health measurements are incomplete or belong to a changed frame");
        return state.Commit(fleet, frame.Sequence,
            Enumerable.Range(0, 6).Select(i => Math.Max(values[i * 2], values[i * 2 + 1])).ToArray(), options);
    }
}
