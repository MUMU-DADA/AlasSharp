using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed partial class EngineSession
{
    private readonly List<CampaignFleetSwitcher> _fleetSwitchers = [];

    internal CampaignFleetSwitcher CreateFleetSwitcher(MapCamera camera, CampaignConfiguration configuration)
    {
        var switcher = new CampaignFleetSwitcher(camera.State, configuration, new FleetSwitchHost(this, camera, configuration));
        _fleetSwitchers.Add(switcher);
        return switcher;
    }

    private sealed class FleetSwitchHost(EngineSession session, MapCamera camera,
        CampaignConfiguration configuration) : ICampaignFleetSwitchHost
    {
        private bool _waitInfoBar;
        public void SuspendCamera() => camera.Suspend();
        public void InvalidateCamera() => camera.Invalidate();
        public ValueTask AdoptSelectionImageAsync(CancellationToken token)
            => camera.AdoptImageAsync(session.Driver.Frame ?? throw new InvalidOperationException("No fleet screenshot"), token);
        public async ValueTask<FleetSelection> SelectAsync(int fleet, CancellationToken token)
        {
            var recovery = new UiRecovery(session.Driver, session._application, session.Pages, new UiRecoveryOptions());
            var observations = new MapUiObservations(() => session.Driver.Frame ?? throw new InvalidOperationException("No fleet screenshot"),
                session._vision, session._assets, session.Driver.Server);
            var guard = new MapUiRecovery(session.Driver, observations, session._application, recovery, recovery);
            // Other map operations may have advanced the physical state since the last UI image.
            await session.Driver.ScreenshotAsync(token);
            var selected = await new CampaignFleetSelector(session.Driver, recovery, guard.HandleInStageAsync,
                () => session.Driver.Frame?.Sequence ?? 0).SelectAsync(fleet, configuration, TimeSpan.FromSeconds(45), token);
            _waitInfoBar = selected.Clicks > 0 && configuration.WaitForFleetSwitchInfoBar;
            return selected;
        }
        public async ValueTask<long> RelocalizeAsync(Cell location, long selectedFrame, CancellationToken token)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var combined = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
            try
            {
                if (_waitInfoBar)
                {
                    _waitInfoBar = false;
                    await session.EnsureNoMapInfoBarAsync(combined.Token);
                }
                while (true)
                {
                    combined.Token.ThrowIfCancellationRequested();
                    await camera.RelocalizeAtAsync(location, selectedFrame, combined.Token);
                    var marker = await camera.ReadFleetMarkerAsync(location, combined.Token);
                    if (marker.Fleet && marker.Current) return camera.FrameSequence;
                    camera.Suspend();
                }
            }
            catch (OperationCanceledException error) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
            { throw new TimeoutException("Selected fleet could not be confirmed in the newly localized map", error); }
        }
        public ValueTask ReadHealthAsync(int fleet, CancellationToken token)
            => session.ReadFleetHealthAsync(camera.State, fleet, configuration, token);
        public async ValueTask ReadLevelsAsync(int fleet, CancellationToken token)
            => _ = await session.ReadFleetLevelsAsync(camera.State, fleet, false, configuration, token);
        public async ValueTask ConfigureStrategyAsync(int displayedFleet, CancellationToken token)
        {
            var buff = new MapFormationProbe(session._vision, session._assets, session.Driver.Server,
                () => session.Driver.Frame ?? throw new InvalidOperationException("No strategy screenshot"));
            _ = await new CampaignStrategy(session.Driver, buff.ObserveAsync)
                .EnsureAsync(displayedFleet, configuration, TimeSpan.FromSeconds(45), token);
            // Strategy can open/close a panel. Grid geometry must use a subsequent map frame.
            _ = await RelocalizeAsync(camera.State.FleetIndex == 1 ? camera.State.Fleet1Location!.Value :
                camera.State.Fleet2Location!.Value, session.Driver.Frame!.Sequence, token);
        }
    }
}
