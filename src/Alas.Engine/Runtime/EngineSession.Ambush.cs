using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed partial class EngineSession
{
    private IMapEncounterHandler CreateMapEncounterHandler(CampaignState state, CampaignConfiguration configuration,
        StageEntranceKind entrances, MapEncounterProbe probe)
    {
        _interruptions.Configure(configuration.Retirement, configuration.EmotionMode);
        var observations = new MapUiObservations(() => Driver.Frame ?? throw new InvalidOperationException("No ambush screenshot"),
            _vision, _assets, Driver.Server, entrances);
        var info = new MapAmbushInfo(_vision, _assets,
            () => Driver.Frame ?? throw new InvalidOperationException("No ambush message screenshot"), Driver.Server);
        ValueTask ReadStatus(CancellationToken token) => ReadFleetStatusAfterCombatAsync(state, state.FleetIndex, configuration, token);
        var ambush = new MapAmbushHandler(Driver, observations, () => Driver.Frame?.Sequence ?? 0, info.ReadAsync,
            (searching, token) => CreateCampaignCombatFlow(state, configuration, entrances).RunAutoAsync(
                CombatFlowOptions.Default with { WaitForEnemySearch = searching }, token),
            configuration.AmbushEvade, () => probe.AmbushFromOverlay, ReadStatus, _interruptions, new MapMysteryItemHandler(Driver));
        return new MapCombatHandler(token => CreateCampaignCombatFlow(state, configuration, entrances).RunAutoAsync(token: token),
            ambush, ReadStatus);
    }
}
