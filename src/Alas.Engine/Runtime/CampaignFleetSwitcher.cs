using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed record FleetSwitchEvidence(int From, int To, Cell Location, FleetSelection? Selection = null,
    long? CameraFrame = null, bool Ready = false);

public interface ICampaignFleetSwitchHost
{
    void SuspendCamera();
    void InvalidateCamera();
    ValueTask<FleetSelection> SelectAsync(int fleet, CancellationToken token);
    ValueTask<long> RelocalizeAsync(Cell location, long selectedFrame, CancellationToken token);
    ValueTask ReadHealthAsync(int fleet, CancellationToken token);
    ValueTask ReadLevelsAsync(int fleet, CancellationToken token);
    ValueTask ConfigureStrategyAsync(int displayedFleet, CancellationToken token);
}

/// <summary>Fleet.fleet_ensure: observed number, camera, costs, HP, levels, then displayed-fleet strategy.</summary>
public sealed class CampaignFleetSwitcher(CampaignState state, CampaignConfiguration configuration,
    ICampaignFleetSwitchHost host)
{
    public static readonly SourceFile Source = CampaignState.InitializationSource;
    private readonly List<FleetSwitchEvidence> _evidence = [];
    private bool _faulted;
    public IReadOnlyList<FleetSwitchEvidence> Evidence => _evidence.ToArray();

    public async ValueTask SwitchAsync(int fleet, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_faulted) throw new InvalidOperationException("Failed fleet switch requires a new initialized map session");
        if (fleet is not (1 or 2) || fleet == 2 && configuration.Fleet2 == 0) throw new ArgumentOutOfRangeException(nameof(fleet));
        if (fleet == state.FleetIndex) return;
        if (!state.IsMapInitialized) throw new InvalidOperationException("Initialize the map before changing fleet");
        var location = (fleet == 1 ? state.Fleet1Location : state.Fleet2Location)
            ?? throw new InvalidOperationException("Cannot switch to a fleet with an unknown map location");
        int entry = _evidence.Count;
        _evidence.Add(new(state.FleetIndex, fleet, location));
        try
        {
            host.SuspendCamera();
            var selected = await host.SelectAsync(fleet, token);
            if (selected.LogicalIndex != fleet || selected.DisplayedIndex is not (1 or 2) || selected.FrameSequence <= 0 || selected.Clicks < 0 ||
                selected.LogicalIndex != FleetRoles.LogicalIndex(selected.DisplayedIndex, configuration))
                throw new InvalidDataException("Fleet switch returned an inconsistent observed identity");
            _evidence[entry] = _evidence[entry] with { Selection = selected };
            // Selection is already physically observed. Later failure cannot relabel it as the previous fleet.
            state.FleetIndex = fleet;
            state.ResetCurrentFleet();
            long frame = await host.RelocalizeAsync(location, selected.FrameSequence, token);
            if (frame <= selected.FrameSequence) throw new InvalidDataException("Fleet switch reused the selection image");
            _evidence[entry] = _evidence[entry] with { CameraFrame = frame };
            state[location].IsFleet = state[location].IsCurrentFleet = true;
            state.Paths.ComputeFleetCosts([new(1, state.Fleet1Location), new(2, state.Fleet2Location)], location, configuration.HasAmbush);
            await host.ReadHealthAsync(fleet, token);
            await host.ReadLevelsAsync(fleet, token);
            await host.ConfigureStrategyAsync(selected.DisplayedIndex, token);
            _evidence[entry] = _evidence[entry] with { Ready = true };
        }
        catch
        {
            _faulted = true;
            host.InvalidateCamera();
            throw;
        }
    }
}
