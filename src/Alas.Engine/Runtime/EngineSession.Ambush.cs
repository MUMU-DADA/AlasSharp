using Alas.Engine.Rules;
using Alas.Engine.Navigation;

namespace Alas.Engine.Runtime;

public sealed partial class EngineSession
{
    private IMapEncounterHandler CreateMapEncounterHandler(CampaignState state, CampaignConfiguration configuration,
        StageEntranceKind entrances, MapEncounterProbe probe, UiRecovery recovery)
    {
        _interruptions.Configure(configuration.Retirement, configuration.EmotionMode);
        var observations = new MapUiObservations(() => Driver.Frame ?? throw new InvalidOperationException("No ambush screenshot"),
            _vision, _assets, Driver.Server, entrances);
        var info = new MapAmbushInfo(_vision, _assets,
            () => Driver.Frame ?? throw new InvalidOperationException("No ambush message screenshot"), Driver.Server);
        var mapUi = new MapUiRecovery(Driver, observations, _application, recovery, recovery);
        var loading = new CombatLoadingProbe(_vision, _assets, Driver.Server,
            () => Driver.Frame ?? throw new InvalidOperationException("No combat-loading screenshot"));
        var searching = new MapEnemySearching(Driver, recovery, mapUi.HandleInStageAsync,
            recovery.GuildPopupCancelAsync, recovery.UrgentCommissionAsync, loading.ObserveAsync, () => Driver.Frame?.Sequence ?? 0);
        var carrier = new MapCarrierHandler(Driver, state, configuration.MysteryHasCarrier,
            () => Driver.Frame?.Sequence ?? 0, searching, new MapMysteryItemHandler(Driver));
        ValueTask ReadStatus(CancellationToken token) => ReadFleetStatusAfterCombatAsync(state, state.FleetIndex, configuration, token);
        var ambush = new MapAmbushHandler(Driver, observations, () => Driver.Frame?.Sequence ?? 0, info.ReadAsync,
            (searching, token) => CreateCampaignCombatFlow(state, configuration, entrances).RunAutoAsync(
                CombatFlowOptions.Default with { WaitForEnemySearch = searching }, token),
            configuration.AmbushEvade, () => probe.AmbushFromOverlay, ReadStatus, _interruptions, carrier);
        return new MapCombatHandler(token => CreateCampaignCombatFlow(state, configuration, entrances).RunAutoAsync(token: token),
            ambush, ReadStatus);
    }
}
