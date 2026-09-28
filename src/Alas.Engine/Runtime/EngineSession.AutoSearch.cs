using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed partial class EngineSession
{
    private AutoSearchFlow? _autoSearch;
    private AutoSearchHandlers? _autoSearchHandlers;

    private AutoSearchFlow GetAutoSearch(CampaignState state, CampaignConfiguration configuration)
    {
        if (_autoSearch is not null)
        {
            if (!ReferenceEquals(_autoSearchHandlers!.State, state)) throw new InvalidOperationException("Auto-search state belongs to another sortie");
            return _autoSearch;
        }
        _autoSearchHandlers = new(this, state, configuration);
        return _autoSearch = new(Driver, _autoSearchHandlers, () => Driver.Frame?.Sequence ?? 0);
    }

    ValueTask ICampaignInMapHost.ResetAutoSearchLevelsAsync(CampaignState state, CampaignConfiguration configuration, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        state.Levels.Reset();
        _autoSearch = null; _autoSearchHandlers = null;
        _ = GetAutoSearch(state, configuration);
        return ValueTask.CompletedTask;
    }
    async ValueTask ICampaignInMapHost.ReadAutoSearchLevelsAsync(CampaignState state, CampaignConfiguration configuration, CancellationToken token)
    {
        _ = GetAutoSearch(state, configuration);
        await _autoSearchHandlers!.ReadInitialLevelsAsync(token);
    }
    ValueTask ICampaignInMapHost.AutoSearchMoveAsync(CampaignState state, CampaignConfiguration configuration, CancellationToken token)
        => GetAutoSearch(state, configuration).MoveAsync(TimeSpan.FromMinutes(6), token);

    async ValueTask ICampaignInMapHost.AutoSearchCombatAsync(CampaignState state, CampaignConfiguration configuration, int fleetIndex, CancellationToken token)
    {
        var flow = GetAutoSearch(state, configuration);
        if (fleetIndex != state.FleetIndex || fleetIndex is not (1 or 2))
            throw new InvalidDataException("Auto-search battle fleet differs from the current observation");
        // Upstream passes fleet_show_index (physical fleet), not logical mob/boss identity.
        int displayed = _autoSearchHandlers!.DisplayedFleet;
        var emotion = configuration.EmotionMode.Calculates() ? RequireEmotion(configuration).ForBattle(displayed,
            () => Driver.Frame?.Sequence ?? 0, configuration.IsDoubleBook) : null;
        var submarine = new CombatSubmarineCall(Driver, () => Driver.Frame?.Sequence ?? 0,
            configuration.Submarine == 0 ? SubmarineMode.DoNotUse : configuration.SubmarineMode, _submarineClick);
        _submarineCalls.Add(submarine);
        await flow.CombatAsync(displayed, submarine, emotion, token: token);
    }

    private sealed class AutoSearchHandlers : IAutoSearchHandlers
    {
        private readonly EngineSession _session;
        private readonly CampaignConfiguration _configuration;
        private readonly UiRecovery _recovery;
        private readonly MapUiRecovery _map;
        private readonly MapWalkPopups _popups;
        private readonly CombatLoadingProbe _loading;
        private long _lastLevelFrame;
        public CampaignState State { get; }
        public int DisplayedFleet { get; private set; } = 1;
        public AutoSearchResources Resources { get; }
        public AutoSearchHandlers(EngineSession session, CampaignState state, CampaignConfiguration configuration)
        {
            (_session, State, _configuration) = (session, state, configuration);
            var ui = session.Driver;
            _recovery = new(ui, session._application, session.Pages, new(MapIsThreatSafe: configuration.PreparationInfo?.ThreatSafe ?? false));
            var observations = new MapUiObservations(() => ui.Frame!, session._vision, session._assets, ui.Server);
            _map = new(ui, observations, session._application, _recovery, _recovery);
            _popups = new(ui, new UiVisuals(session._vision, () => ui.Frame!), () => ui.Frame?.Sequence ?? 0,
                configuration.IsClearMode, session._mapCatAttack);
            _loading = new(session._vision, session._assets, ui.Server, () => ui.Frame!);
            Resources = new(ui, () => ui.Frame?.Sequence ?? 0, configuration.OilLimit);
        }
        public async ValueTask ReadInitialLevelsAsync(CancellationToken token)
        {
            await ReadFleetAsync(token);
            _ = await _session.ReadFleetLevelsAsync(State, State.FleetIndex, false, _configuration, token);
            _lastLevelFrame = _session.Driver.Frame!.Sequence;
        }
        private async ValueTask ReadFleetAsync(CancellationToken token)
        {
            // Preserve upstream's documented unknown=1 fallback; it is never used to assert a fleet switch.
            DisplayedFleet = await _session.Driver.AppearsAsync(UiAssets.Map.FLEET_NUM_1, ButtonOffset.Expand(20, 20), token: token) ? 1
                : await _session.Driver.AppearsAsync(UiAssets.Map.FLEET_NUM_2, ButtonOffset.Expand(20, 20), token: token) ? 2 : 1;
            State.FleetIndex = FleetRoles.LogicalIndex(DisplayedFleet, _configuration);
        }
        public async ValueTask WatchAsync(bool firstObservation, CancellationToken token)
        {
            if (firstObservation) Resources.BeginMove();
            int previous = State.FleetIndex;
            await ReadFleetAsync(token);
            if ((firstObservation || State.FleetIndex != previous) && _session.Driver.Frame!.Sequence > _lastLevelFrame)
            {
                _ = await _session.ReadFleetLevelsAsync(State, State.FleetIndex, State.FleetIndex == previous, _configuration, token);
                _lastLevelFrame = _session.Driver.Frame.Sequence;
            }
            await Resources.ObserveAsync(token);
        }
        public ValueTask<bool> LoadingAsync(CancellationToken token) => _loading.ObserveAsync(token);
        public ValueTask<bool> InStageAsync(CancellationToken token) => _map.IsInStageAsync(token);
        public ValueTask<bool> RetirementAsync(CancellationToken token) => _session._interruptions.RetirementAsync(token);
        public ValueTask<bool> LowEmotionAsync(CancellationToken token) => _session._interruptions.LowEmotionAsync(token);
        public ValueTask<bool> StoryAsync(CancellationToken token) => _recovery.StorySkipAsync(token);
        public ValueTask<bool> CatAttackAsync(CancellationToken token) => _popups.HandleCatAsync(token);
        public ValueTask<bool> ConfirmAsync(CancellationToken token) => _recovery.ConfirmAsync(token);
        public ValueTask<bool> UrgentCommissionAsync(CancellationToken token) => _recovery.UrgentCommissionAsync(token);
        public ValueTask<bool> GuildPopupAsync(CancellationToken token) => _recovery.GuildPopupCancelAsync(token);
        public ValueTask<bool> MissionPopupAsync(CancellationToken token) => _recovery.MissionPopupAckAsync(token);
    }
}
