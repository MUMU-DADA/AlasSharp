using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static partial class MapMovableChecks
{
    private static MapMovement Movement(CampaignState state, CampaignConfiguration config, Camera camera)
        => new(state, config, camera, () => new(camera, state, _ => ValueTask.FromResult(true), camera.Clock,
            new Probe(camera), new Handler()), movableScan: new(state, config, new(state, camera, camera.Clock)));

    private static async Task MovementChecksAsync()
    {
        var config = new CampaignConfiguration { HasMovableEnemy = true, HasSiren = true, HasAmbush = false };
        foreach (bool combat in new[] { false, true })
        {
            var state = State(); state[C("B3")].IsSiren = true;
            state.Rounds.Initialize(config); state.RefreshFleetPaths(config);
            var camera = new Camera(state) { ObservedSirens = [C("A3")], Combat = combat };
            var move = Movement(state, config, camera);
            Check((await move.MoveAsync(C("D4"))).Outcome == MapMoveOutcome.Committed && state.Rounds.Round == 1 &&
                camera.Frames == 4 && camera.Scans == 0, "First step did not commit before movable turn");
            int frames = camera.Frames;
            if (combat) state[C("D3")].IsEnemy = true;
            try
            {
                if (combat) await move.FightAsync(C("D3")); else await move.MoveAsync(C("D3"));
                throw new InvalidOperationException("Movable round did not redispatch");
            }
            catch (MapEnemyMovedException) { }
            Check(state.Fleet1Location == C("D3") && state.Rounds.Round == 2 && state.BattleCount == (combat ? 1 : 0) &&
                state.FleetAmmo == (combat ? 4 : 5) && !state.MovementInvalidated && !camera.Invalidated &&
                state.MovableScans.Count == 1 && state[C("A3")].IsMovable && !state[C("B3")].IsSiren &&
                state[C("D3")].Cost == 0 && camera.Frames - frames == (combat ? 6 : 16),
                "Round scan, post-combat wait, fleet paths or redispatch differs");
            Check(state.MovableScans[0].Before.Sirens.SequenceEqual([C("B3")]) &&
                state.MovableScans[0].Scan.Visited.Contains(C("B3")), "Siren's previous location was not mandatory scan evidence");
            try { Check((await move.MoveAsync(C("D2"))).Outcome == MapMoveOutcome.Committed, "Next round did not commit"); }
            catch (MapEnemyMovedException) when (combat) { }
            Check(state.Fleet1Location == C("D2") && state.Rounds.Round == 3 && !state.MovementInvalidated && !camera.Invalidated &&
                state.MovableScans.Count == (combat ? 2 : 1), "Next spawn epoch or successful redispatch incorrectly invalidated camera or state");
        }
        foreach (string failure in new[] { "scan", "cancel", "stale", "click" })
        {
            var state = State(); state[C("B3")].IsSiren = true;
            state.Rounds.Initialize(config); state.Rounds.Advance(); state.RefreshFleetPaths(config);
            using var cancel = new CancellationTokenSource();
            var camera = new Camera(state) { Failure = failure, Cancellation = cancel };
            try { await Movement(state, config, camera).MoveAsync(C("D4"), token: cancel.Token); throw new InvalidOperationException("Dynamic failure was swallowed"); }
            catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException) { }
            bool arrived = failure is "scan" or "cancel";
            Check(state.Fleet1Location == C(arrived ? "D4" : "C4") && state.Rounds.Round == (arrived ? 2 : 1) &&
                state.MovementInvalidated && camera.Invalidated && state.MovableScans.Count == 0,
                "Dynamic failure erased physical identity, advanced before confirmation, or recorded completed scan: " + failure);
            int taps = camera.Taps;
            try { await Movement(state, config, new Camera(state)).MoveAsync(C("E4")); throw new Exception("Faulted state reused"); }
            catch (InvalidOperationException) { }
            Check(camera.Taps == taps, "Rejected state produced a tap");
        }
        foreach (bool combat in new[] { false, true })
        {
            var combined = config with { HasLandBased = true };
            var state = State(); state[C("B3")].IsSiren = true;
            var trigger = state[C("D4")]; var block = state[C("E4")];
            trigger.IsMechanismTrigger = true; trigger.MechanismTrigger = [trigger]; trigger.MechanismBlock = [block];
            block.IsMechanismBlock = true; trigger.IsEnemy = combat;
            state.Rounds.Initialize(combined); state.Rounds.Advance(); state.RefreshFleetPaths(combined);
            var camera = new Camera(state) { Combat = combat, ObservedSirens = [C("A3")] };
            try
            {
                var move = Movement(state, combined, camera);
                if (combat) await move.FightAsync(C("D4")); else await move.MoveAsync(C("D4"));
                throw new InvalidOperationException("Mechanism plus enemy round did not redispatch");
            }
            catch (MapEnemyMovedException) { }
            Check(state.MechanismReleases.Single().ConfirmSeconds == (combat ? 2.5 : 5.5) &&
                camera.Frames == (combat ? 14 : 24) && !block.IsMechanismBlock && state.MovableScans.Count == 1,
                "Mechanism evidence or post-combat timer lost the combined round delay");
        }
        foreach (var invalid in new[] {
            config with { MovableEnemyTurns = [0] }, config with { MovableNormalEnemyTurns = [-1] },
            config with { SirenMoveWait = double.NaN }, config with { MovableEnemyStep = -1 },
            config with { Fleet1Step = 0 } })
        {
            var state = State();
            try { state.Rounds.Initialize(invalid); throw new InvalidOperationException("Invalid round configuration accepted"); }
            catch (ArgumentException) { }
            Check(!state.Rounds.Initialized, "Invalid configuration partially initialized rounds");
        }
        foreach (var order in Enum.GetValues<FleetOrder>())
        foreach (int fleet in new[] { 1, 2 })
        {
            var c = config with { Fleet2 = 2, HasFleetStep = true, FleetOrder = order, Fleet1Step = 3, Fleet2Step = 2 };
            Check(FleetRoles.Step(fleet, c) == (FleetRoles.LogicalIndex(fleet, c) == 1 ? 3 : 2), "Fleet role reversed step selection");
        }
        var normalOnly = config with { HasMovableEnemy = false, HasMovableNormalEnemy = true };
        var normalState = State(); normalState.Rounds.Initialize(normalOnly); normalState.RefreshFleetPaths(normalOnly);
        var normalCamera = new Camera(normalState);
        Check((await Movement(normalState, normalOnly, normalCamera).MoveAsync(C("D4"))).Outcome == MapMoveOutcome.Committed &&
            normalState.Rounds.Round == 0 && normalCamera.Scans == 0, "Normal-only mode invented native round advancement");
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance() => _ticks += TimeSpan.FromSeconds(.25).Ticks;
    }
    private sealed class Camera(CampaignState state) : IMapArrivalCamera, IMapScanCamera
    {
        public Clock Clock { get; } = new();
        public Cell Position { get; private set; } = C("C4");
        public long FrameSequence { get; private set; } = 1;
        public int Frames { get; private set; }
        public int Taps { get; private set; }
        public int Scans { get; private set; }
        public bool Combat { get; init; }
        public bool CombatDestination { get; private set; }
        public bool Invalidated { get; private set; }
        public string? Failure { get; init; }
        public CancellationTokenSource? Cancellation { get; init; }
        public Cell[] ObservedSirens { get; init; } = [];
        public Cell[] ObservedEnemies { get; init; } = [];
        public MovableEnemySnapshot? BeforeScan { get; private set; }
        public void Invalidate() => Invalidated = true;
        public void Suspend() { }
        public ValueTask PrepareTapAsync(Cell destination, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); if (Invalidated) throw new InvalidOperationException(); return ValueTask.CompletedTask; }
        public ValueTask TapCellAsync(Cell destination, CancellationToken token = default)
        { Taps++; if (Failure == "click") throw new IOException("Synthetic tap failure"); CombatDestination = Combat && state[destination].IsEnemy; return ValueTask.CompletedTask; }
        public ValueTask RefreshImageAsync(CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Clock.Advance(); Frames++; if (Failure != "stale") FrameSequence++; return ValueTask.CompletedTask; }
        public ValueTask<FleetMarker> ReadFleetMarkerAsync(Cell destination, CancellationToken token = default) => ValueTask.FromResult(new FleetMarker(true, true));
        public ValueTask<FleetMarker> ReadCenterMarkerAsync(CancellationToken token = default) => ReadFleetMarkerAsync(Position, token);
        public ValueTask RelocalizeAsync(CancellationToken token = default) => RefreshImageAsync(token);
        public ValueTask AnchorAtAsync(Cell location, CancellationToken token = default) { Position = location; return ValueTask.CompletedTask; }
        public ValueTask FocusAsync(Cell destination, CancellationToken token) => AnchorAtAsync(destination, token);
        public ValueTask CenterAsync(double tolerance, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<MapObservation> ObserveAsync(MapScanMode mode, CancellationToken token)
        {
            Scans++;
            BeforeScan ??= MovableEnemySnapshot.Capture(state);
            if (Failure == "scan") throw new IOException("Synthetic scan failure after arrival");
            if (Failure == "cancel") Cancellation!.Cancel();
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new MapObservation(ObservedSirens.Select(cell => new MapCellObservation(
                new(cell.Column - Position.Column, cell.Row - Position.Row), new(IsSiren: true, EnemyGenre: "Siren_DD")))
                .Concat(ObservedEnemies.Select(cell => new MapCellObservation(new(cell.Column - Position.Column, cell.Row - Position.Row),
                    new(IsEnemy: true, EnemyScale: 1, EnemyGenre: "Light")))).ToArray(), Position, new(0, 0), mode));
        }
        public ValueTask EnsureEdgesAsync(bool skipFirstUpdate, CancellationToken token) => ValueTask.CompletedTask;
    }
    private sealed class Probe(Camera camera) : IMapEncounterProbe
    {
        private bool _fired;
        public ValueTask InitializeAsync(long frameSequence, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<MapEncounterKind> InspectAsync(long frameSequence, CancellationToken token)
        { bool combat = camera.CombatDestination && !_fired; _fired = true; return ValueTask.FromResult(combat ? MapEncounterKind.Combat : MapEncounterKind.None); }
    }
    private sealed class Handler : IMapEncounterHandler
    {
        public ValueTask<MapEncounterHandling> HandleAsync(MapEncounterKind encounter, CancellationToken token)
            => ValueTask.FromResult(new MapEncounterHandling(MapEncounterContinuation.InMap,
                new CombatFlowResult(CombatReturn.InMap, new(CombatRank.S, CombatRankSource.BattleStatus, UiAssets.Combat.BATTLE_STATUS_S.Id), false, false, 1)));
    }
}
