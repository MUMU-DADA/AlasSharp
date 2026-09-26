using System.Security.Cryptography;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static class CampaignMapCombatChecks
{
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    public static async Task RunAsync(string upstream)
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(upstream, CampaignMapCombat.Source.Path));
        Check(Convert.ToHexStringLower(SHA256.HashData(bytes)) == CampaignMapCombat.Source.Sha256,
            "Native map action source drifted");

        var map = new MapDefinition("C2", "SP -- --\n-- -- ME", ["B1"], [],
            [new SpawnWave(0, Enemy: 1)], walls:
            [new MapEdge(new Cell(1, 1), new Cell(1, 2)), new MapEdge(new Cell(2, 1), new Cell(2, 2))]);
        var state = Prepare(map, walls: true);
        state[new(3, 2)].IsEnemy = true;
        state.Paths.ComputeFleetCosts([new(1, state.Fleet1Location)], state.Fleet1Location!.Value, true);
        var camera = new Camera(state);
        var combat = Create(state, new(), camera);
        Check(await combat.ClearEnemyAsync() && camera.Taps == 2 && camera.Scans == 1 &&
            state.BattleCount == 1 && state.AmmoCount == 2 && state.Fleet1Location == new Cell(3, 2) &&
            !state[new(3, 2)].IsEnemy && combat.StageReturn is null,
            "Enemy clear did not follow route nodes, commit one battle, then scan");
        Check(!await combat.ClearEnemyAsync() && camera.Taps == 2,
            "No observed enemy caused another grid action");

        map = new MapDefinition("B2", "SP ME\nME --", ["A1"], [], [new SpawnWave(0, Enemy: 2)]);
        foreach (var (priority, target) in new[]
                 {
                     (EnemyScalePriority.StrongestFirst, new Cell(2, 1)),
                     (EnemyScalePriority.WeakestFirst, new Cell(1, 2))
                 })
        {
            state = Prepare(map);
            state[new(2, 1)].IsEnemy = state[new(1, 2)].IsEnemy = true;
            state[new(2, 1)].EnemyScale = 3;
            state[new(1, 2)].EnemyScale = 1;
            state.Paths.ComputeFleetCosts([new(1, state.Fleet1Location)], state.Fleet1Location!.Value, true);
            camera = new Camera(state);
            combat = Create(state, new() { EnemyPriority = priority }, camera);
            Check(await combat.ClearEnemyAsync() && state.Fleet1Location == target && camera.Taps == 1,
                "Enemy-scale priority did not select the upstream scale group");
        }

        map = new MapDefinition("B1", "SP MB", ["A1"], [], [new SpawnWave(0, Boss: 1)]);
        foreach (var rank in new CombatRank?[] { CombatRank.S, CombatRank.C, null })
        {
            state = Prepare(map);
            state[new(2, 1)].IsBoss = true;
            state.Paths.ComputeFleetCosts([new(1, state.Fleet1Location)], state.Fleet1Location!.Value, true);
            camera = new Camera(state) { ReturnStage = true, Rank = rank };
            combat = Create(state, new(), camera);
            bool ended = false, rejected = false;
            try { await combat.ClearBossAsync(); }
            catch (CampaignEndedException) { ended = true; }
            catch (CampaignScriptException) { rejected = true; }
            Check((rank == CombatRank.S ? ended && combat.StageReturn?.Combats.Length == 1 :
                rejected && combat.StageReturn is null) && state.BattleCount == 0 &&
                state.Fleet1Location == new Cell(1, 1) && state[new(2, 1)].IsBoss,
                "Stage return was promoted without a winning rank or committed map state");
        }
        state = Prepare(map);
        state[new(2, 1)].IsBoss = true;
        state.Paths.ComputeFleetCosts([new(1, state.Fleet1Location)], state.Fleet1Location!.Value, true);
        camera = new Camera(state);
        combat = Create(state, new(), camera);
        bool fallbackRequired = false;
        try { await combat.ClearBossAsync(); }
        catch (NotSupportedException) { fallbackRequired = true; }
        Check(fallbackRequired && combat.StageReturn is null && state.BattleCount == 1,
            "Boss return to map skipped the unported potential-boss search");

        state = Prepare(map);
        state.Paths.ComputeFleetCosts([new(1, state.Fleet1Location)], state.Fleet1Location!.Value, true);
        camera = new Camera(state);
        combat = Create(state, new(), camera);
        fallbackRequired = false;
        try { await combat.ClearBossAsync(); }
        catch (NotSupportedException) { fallbackRequired = true; }
        Check(fallbackRequired && camera.Taps == 0 && state.BattleCount == 0,
            "Unobserved potential boss was clicked as an observed combat target");
        Console.WriteLine("Campaign map combat: route, priority, scan and stage-return evidence passed offline; no entry or settlement verification.");
    }

    private static CampaignState Prepare(MapDefinition map, bool walls = false)
    {
        var state = new CampaignState(map);
        state.InitializeMapData(new(PoorMapData: true, Walls: walls));
        state.Fleet1Location = new(1, 1);
        state[new(1, 1)].IsFleet = state[new(1, 1)].IsCurrentFleet = true;
        return state;
    }

    private static CampaignMapCombat Create(CampaignState state, CampaignConfiguration config, Camera camera)
    {
        var movement = new MapMovement(state, config, camera, () =>
            new MapArrivalCheck(camera, state, camera.InMapAsync, camera.Clock,
                new Probe(camera), new Handler(camera)));
        return new(state, config, movement, new MapScanner(state, camera, camera.Clock));
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance() => _ticks += TimeSpan.FromSeconds(.25).Ticks;
    }

    private sealed class Camera(CampaignState state) : IMapScanCamera, IMapArrivalCamera
    {
        public Clock Clock { get; } = new();
        public Cell Position { get; private set; } = new(1, 1);
        public Cell? Destination { get; private set; }
        public bool ReturnStage { get; init; }
        public CombatRank? Rank { get; init; } = CombatRank.S;
        public int Taps { get; private set; }
        public int Scans { get; private set; }
        public long FrameSequence { get; private set; } = 1;
        public bool Suspended { get; private set; }
        public bool Invalidated { get; private set; }
        public void Suspend() => Suspended = true;
        public void Invalidate() => Invalidated = true;
        public ValueTask FocusAsync(Cell destination, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Position = destination; return ValueTask.CompletedTask; }
        public ValueTask CenterAsync(double tolerance, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
        public ValueTask<MapObservation> ObserveAsync(MapScanMode mode, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Scans++; return ValueTask.FromResult(new MapObservation([], Position, new(0, 0), mode)); }
        public ValueTask EnsureEdgesAsync(bool skipFirstUpdate, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
        public ValueTask PrepareTapAsync(Cell destination, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); if (Invalidated || Suspended) throw new InvalidOperationException(); return ValueTask.CompletedTask; }
        public ValueTask TapCellAsync(Cell destination, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Destination = destination; Taps++; return ValueTask.CompletedTask; }
        public ValueTask RefreshImageAsync(CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); FrameSequence++; Clock.Advance(); return ValueTask.CompletedTask; }
        public ValueTask<FleetMarker> ReadFleetMarkerAsync(Cell destination, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new FleetMarker(true, true)); }
        public ValueTask<FleetMarker> ReadCenterMarkerAsync(CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new FleetMarker(true, true)); }
        public ValueTask RelocalizeAsync(CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Suspended = false; FrameSequence++; Clock.Advance(); return ValueTask.CompletedTask; }
        public ValueTask AnchorAtAsync(Cell location, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Position = location; return ValueTask.CompletedTask; }
        public ValueTask<bool> InMapAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(true); }
        public bool IsCombatDestination => Destination is { } cell &&
            (state[cell].IsEnemy || state[cell].IsBoss || state[cell].IsSiren);
    }

    private sealed class Probe(Camera camera) : IMapEncounterProbe
    {
        private bool _fired;
        public ValueTask InitializeAsync(long frameSequence, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
        public ValueTask<MapEncounterKind> InspectAsync(long frameSequence, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (_fired || !camera.IsCombatDestination) return ValueTask.FromResult(MapEncounterKind.None);
            _fired = true;
            return ValueTask.FromResult(MapEncounterKind.Combat);
        }
    }

    private sealed class Handler(Camera camera) : IMapEncounterHandler
    {
        public ValueTask<MapEncounterHandling> HandleAsync(MapEncounterKind encounter, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (encounter != MapEncounterKind.Combat || !camera.Suspended) throw new InvalidDataException();
            var returned = camera.ReturnStage ? CombatReturn.InStage : CombatReturn.InMap;
            var rank = camera.Rank is { } value
                ? new CombatRankEvidence(value, CombatRankSource.BattleStatus, UiAssets.Combat.BATTLE_STATUS_S.Id) : null;
            return ValueTask.FromResult(new MapEncounterHandling(camera.ReturnStage ?
                MapEncounterContinuation.InStage : MapEncounterContinuation.InMap,
                new CombatFlowResult(returned, rank, false, false, 1)));
        }
    }
}
