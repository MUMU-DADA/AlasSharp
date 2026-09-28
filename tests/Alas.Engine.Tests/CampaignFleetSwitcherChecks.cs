using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static class CampaignFleetSwitcherChecks
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static CampaignState State()
    {
        var state = new CampaignState(new MapDefinition("D1", "SP -- -- SP", [], [], []));
        state.InitializeMapData(new(PoorMapData: true));
        state.Fleet1Location = new(1, 1); state.Fleet2Location = new(4, 1);
        state[new(1, 1)].IsFleet = state[new(1, 1)].IsCurrentFleet = state[new(4, 1)].IsFleet = true;
        state.Paths.ComputeFleetCosts([new(1, state.Fleet1Location), new(2, state.Fleet2Location)], new(1, 1), false);
        state.Health.Commit(1, 1, [.9, 0, 0, .8, 0, 0], new());
        return state;
    }
    public static async Task RunAsync()
    {
        foreach (var order in Enum.GetValues<FleetOrder>())
        {
            var configuration = new CampaignConfiguration { Fleet2 = 2, FleetOrder = order };
            Check(FleetRoles.BossIndex(configuration) == (order is FleetOrder.Fleet1MobFleet2Boss or FleetOrder.Fleet1BossFleet2Mob ? 2 : 1),
                "Boss role did not reflect the native fleet order");
            Check(FleetRoles.BossIndex(new() { Fleet2 = 0, FleetOrder = order }) == 1, "Disabled second fleet retained boss role");
            var state = State(); var host = new Host(state, configuration);
            var switcher = new CampaignFleetSwitcher(state, configuration, host);
            var priorHp = state.Health.Get(1);
            await switcher.SwitchAsync(2, default);
            Check(host.Calls.SequenceEqual(["suspend", "select", "camera", "hp", "levels", "strategy"]) &&
                state.FleetIndex == 2 && state[new(4, 1)].IsCurrentFleet && !state[new(1, 1)].IsCurrentFleet &&
                state[new(1, 1)].Cost == state[new(1, 1)].Cost2 && state[new(1, 1)].Cost1 == 0 &&
                ReferenceEquals(state.Health.Get(1), priorHp) && state.Health.Get(2) is not null &&
                switcher.Evidence is [{ Ready: true, CameraFrame: 3, Selection.DisplayedIndex: var displayed }] &&
                displayed == (FleetRoles.Reversed(configuration) ? 1 : 2), "Fleet switch lost state, ordering or role evidence");
            await switcher.SwitchAsync(2, default);
            Check(host.Calls.Count == 6 && switcher.Evidence.Count == 1, "Already selected fleet was switched again");
        }
        foreach (string failure in new[] { "suspend", "select", "camera", "hp", "levels", "strategy", "identity", "stale", "cancel" })
        {
            var config = new CampaignConfiguration { Fleet2 = 2 };
            var state = State(); var host = new Host(state, config) { Failure = failure };
            var switcher = new CampaignFleetSwitcher(state, config, host);
            bool rejected = false;
            try { await switcher.SwitchAsync(2, default); }
            catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException) { rejected = true; }
            bool selected = failure is not ("suspend" or "select" or "identity");
            Check(rejected && host.Invalidated && switcher.Evidence is [{ Ready: false }] &&
                switcher.Evidence[0].Selection is not null == selected && state.FleetIndex == (selected ? 2 : 1),
                "Failed switch hid its partial state: " + failure);
            if (failure is "camera" or "stale" or "cancel")
                Check(!state.Cells.Any(c => c.IsCurrentFleet), "Failed localization retained the old current-fleet marker");
            int calls = host.Calls.Count;
            rejected = false;
            try { await switcher.SwitchAsync(2, default); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && calls == host.Calls.Count, "Partial switch was reused as an already-ready fleet");
        }
        foreach (int kind in new[] { 0, 1, 2 })
        {
            var state = State(); var config = new CampaignConfiguration { Fleet2 = kind == 0 ? 0 : 2 };
            if (kind == 1) state.Fleet2Location = null;
            var host = new Host(state, config); var switcher = new CampaignFleetSwitcher(state, config, host);
            using var cancelled = new CancellationTokenSource(); if (kind == 2) cancelled.Cancel();
            bool rejected = false;
            try { await switcher.SwitchAsync(2, cancelled.Token); }
            catch (Exception e) when (e is ArgumentOutOfRangeException or InvalidOperationException or OperationCanceledException) { rejected = true; }
            Check(rejected && host.Calls.Count == 0 && switcher.Evidence.Count == 0, "Invalid switch caused physical work");
        }
        foreach (int fleet in new[] { 1, 2 })
        foreach (string failure in new[] { "", "select", "identity", "suspend", "camera", "hp", "levels", "strategy", "stale", "cancel", "adopt" })
        {
            var config = new CampaignConfiguration { Fleet2 = 2 };
            var state = State(); state.FleetIndex = fleet; state.RefreshFleetPaths(config);
            var host = new Host(state, config) { Failure = failure, Clicks = failure is "" or "adopt" ? 0 : 1 };
            var switcher = new CampaignFleetSwitcher(state, config, host);
            var hp = state.Health.Get(1);
            Exception? error = null;
            try { await switcher.EnsureCurrentAsync(default); } catch (Exception caught) { error = caught; }
            Check((error is not null) == (failure != "") && state.FleetIndex == fleet &&
                switcher.Evidence is [{ CurrentCheck: true }] && switcher.Evidence[0].Ready == (failure == ""),
                "Current verification failure lost identity or partial evidence: " + failure);
            if (failure == "")
                Check(host.Calls.SequenceEqual(["select", "adopt"]) && ReferenceEquals(hp, state.Health.Get(1)) &&
                    switcher.Evidence[0].CameraFrame is null, "Already correct fleet unnecessarily refreshed HP, paths or geometry");
            else
            {
                Check(host.Invalidated && state.MovementInvalidated, "Failed current verification left a reusable map");
                int calls = host.Calls.Count;
                try { await switcher.EnsureCurrentAsync(default); } catch (InvalidOperationException) { }
                Check(calls == host.Calls.Count, "Failed current verification performed another physical operation");
            }
        }
        Console.WriteLine("Fleet switch: four role orders, state/cost/HP preservation, nine partial failures and three preflight rejections passed.");
    }
    private sealed class Host(CampaignState state, CampaignConfiguration configuration) : ICampaignFleetSwitchHost
    {
        public string? Failure { get; init; }
        public int Clicks { get; init; } = 1;
        public List<string> Calls { get; } = [];
        public bool Invalidated { get; private set; }
        private void Step(string name)
        { Calls.Add(name); if (Failure == name) throw new IOException(name); }
        public void SuspendCamera() => Step("suspend");
        public void InvalidateCamera() => Invalidated = true;
        public ValueTask AdoptSelectionImageAsync(CancellationToken token)
        { Step("adopt"); return ValueTask.CompletedTask; }
        public ValueTask<FleetSelection> SelectAsync(int fleet, CancellationToken token)
        {
            Step("select");
            return ValueTask.FromResult(new FleetSelection(Failure == "identity" ? 3 - fleet : fleet,
                FleetRoles.Reversed(configuration) ? 3 - fleet : fleet, Clicks, 2));
        }
        public ValueTask<long> RelocalizeAsync(Cell location, long selectedFrame, CancellationToken token)
        {
            Step("camera");
            Check(location == (state.FleetIndex == 1 ? state.Fleet1Location : state.Fleet2Location) && selectedFrame == 2,
                "Camera did not follow the observed fleet identity");
            if (Failure == "cancel") throw new OperationCanceledException();
            return ValueTask.FromResult(Failure == "stale" ? 2L : 3L);
        }
        public ValueTask ReadHealthAsync(int fleet, CancellationToken token)
        {
            Step("hp"); Check(state[fleet == 1 ? new(1, 1) : new(4, 1)].Cost == 0, "HP preceded path update");
            state.Health.Commit(fleet, 3, [.7, 0, 0, .6, 0, 0], new()); return ValueTask.CompletedTask;
        }
        public ValueTask ReadLevelsAsync(int fleet, CancellationToken token)
        { Step("levels"); Check(fleet == state.FleetIndex, "Levels used a displayed fleet identity"); return ValueTask.CompletedTask; }
        public ValueTask ConfigureStrategyAsync(int displayedFleet, CancellationToken token)
        {
            Step("strategy"); Check(displayedFleet == FleetRoles.LogicalIndex(state.FleetIndex, configuration), "Strategy used logical fleet identity");
            return ValueTask.CompletedTask;
        }
    }
}
