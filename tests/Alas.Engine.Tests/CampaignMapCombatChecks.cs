using System.Security.Cryptography;
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

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

        map = new MapDefinition("C1", "SP MM MM", ["B1"], [],
            [new SpawnWave(0, Mystery: 2)]);
        state = Prepare(map);
        state[new(2, 1)].IsMystery = state[new(3, 1)].IsMystery = true;
        state.Paths.ComputeFleetCosts([new(1, state.Fleet1Location)], state.Fleet1Location!.Value, true);
        camera = new Camera(state);
        combat = Create(state, new(), camera);
        Check(!await combat.ClearMysteriesAsync() && camera.Taps == 2 && state.MysteryCount == 2 &&
            state.Fleet1Location == new Cell(3, 1) && !state.Cells.Any(grid => grid.IsMystery) &&
            state.BattleCount == 0 && state.AmmoCount == 3 && !await combat.ClearMysteriesAsync(),
            "Accessible mysteries were not picked up in cost order without changing battle state");

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

        await ExecutionChecksAsync();
        Console.WriteLine("Campaign map combat: route, priority, scan and stage-return evidence passed offline; no entry or settlement verification.");
    }

    private static async Task ExecutionChecksAsync()
    {
        var map = new MapDefinition("C1", "SP ME MB", ["B1"], ["B1"],
            [new SpawnWave(0, Enemy: 1), new SpawnWave(1, Boss: 1)]);
        var host = new Host();
        var execution = new CampaignExecution(new TwoBattleRule(map), new(),
            (state, config) => new InMapCampaignOperations(host, state, config, default));
        Check(await execution.RunAsync() == CampaignLoopExit.Ended && host.Camera is { Taps: 2, Scans: 2 } &&
            execution.Context.State.BattleCount == 1 &&
            execution.Context.Operations is InMapCampaignOperations
            { StageReturn: { Combats: [ { Return: CombatReturn.InStage, Rank.IsWinningRank: true } ] } },
            "Compiled campaign loop did not execute the in-map C# scan, combat and stage-return sequence");

        var mysteryMap = new MapDefinition("D1", "SP MM ME MB", ["B1"], ["B1"],
            [new SpawnWave(0, Enemy: 1, Mystery: 1), new SpawnWave(1, Boss: 1)]);
        var mysteryHost = new Host { HasMystery = true };
        var mysteryExecution = new CampaignExecution(new MysteryTwoBattleRule(mysteryMap), new(),
            (state, config) => new InMapCampaignOperations(mysteryHost, state, config, default));
        Check(await mysteryExecution.RunAsync() == CampaignLoopExit.Ended &&
            mysteryHost.Camera is { Taps: 3, Scans: 2 } &&
            mysteryExecution.Context.State is { MysteryCount: 1, BattleCount: 1, AmmoCount: 2 } &&
            !mysteryExecution.Context.State.Cells.Any(grid => grid.IsMystery),
            "Compiled rule did not pick up mystery before combat and boss return in one C# sortie");

        var task = new CampaignResumeTask();
        var request = new TaskRequest("resume", task.Kind,
            new JsonObject { ["campaign"] = "campaign_main/campaign_1_1" });
        var stage = ((InMapCampaignOperations)execution.Context.Operations).StageReturn;
        var result = await task.RunAsync(request,
            new TaskContext(null!, null!, null!, TimeSpan.FromMinutes(2),
                Campaign: new ResumeService(new(CampaignLoopExit.Ended, 1, stage))), default);
        Check(result is { Outcome: TaskOutcome.Failed, Reason: "sortie_settlement_unverified" } &&
            result.Evidence?["cleared"]?.GetValue<bool>() == false &&
            result.Evidence["settlementVerified"]?.GetValue<bool>() == false &&
            result.Evidence["stageReturn"] is not null,
            "Campaign task promoted a loop end or stage return into a cleared outcome");

        host = new Host { InMap = false };
        execution = new CampaignExecution(new TwoBattleRule(map), new(),
            (state, config) => new InMapCampaignOperations(host, state, config, default));
        bool rejected = false;
        try { await execution.RunAsync(); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected && host.Camera is null && !execution.Context.State.IsMapInitialized,
            "Campaign resume entered a map workflow without an observed in-map page");
    }

    private sealed class ResumeService(CampaignResumeResult result) : ICampaignExecutionService
    {
        public ValueTask<CampaignResumeResult> ResumeInMapAsync(CampaignRule rule, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(result); }
    }

    private sealed class TwoBattleRule(MapDefinition map) : CampaignRule
    {
        public override string Id => "test/two_battles";
        public override MapDefinition Map => map;
        public override ImmutableArray<SourceFile> Sources => [];
        protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; } =
            new Dictionary<int, BattleHook>
            {
                [0] = static context => context.Operations.ClearEnemyAsync(),
                [1] = static context => context.Operations.ClearBossAsync()
            };
    }

    private sealed class MysteryTwoBattleRule(MapDefinition map) : CampaignRule
    {
        public override string Id => "test/mystery_two_battles";
        public override MapDefinition Map => map;
        public override ImmutableArray<SourceFile> Sources => [];
        protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; } =
            new Dictionary<int, BattleHook>
            {
                [0] = static async context =>
                {
                    await context.Operations.ClearMysteriesAsync();
                    return await context.Operations.ClearEnemyAsync();
                },
                [1] = static context => context.Operations.ClearBossAsync()
            };
    }

    private sealed class Host : ICampaignInMapHost
    {
        public bool InMap { get; init; } = true;
        public bool HasMystery { get; init; }
        public Camera? Camera { get; private set; }
        public ValueTask<bool> VerifyInMapAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(InMap); }
        public ValueTask<IMapScanCamera> CreateCameraAsync(CampaignState state,
            CampaignConfiguration configuration, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Camera = new Camera(state)
            {
                StageForBoss = true,
                ObservationFactory = (scan, position, mode) => new MapObservation(scan == 1
                    ? HasMystery
                        ? [new(new(0, 0), new(IsFleet: true, IsCurrentFleet: true)),
                           new(new(1, 0), new(IsMystery: true)),
                           new(new(2, 0), new(IsEnemy: true, EnemyScale: 1))]
                        : [new(new(0, 0), new(IsFleet: true, IsCurrentFleet: true)),
                           new(new(1, 0), new(IsEnemy: true, EnemyScale: 1))]
                    : HasMystery
                        ? [new(new(2, 0), new(IsFleet: true, IsCurrentFleet: true)),
                           new(new(3, 0), new(IsBoss: true))]
                        : [new(new(1, 0), new(IsFleet: true, IsCurrentFleet: true)),
                           new(new(2, 0), new(IsBoss: true))], position, new(1, 0), mode)
            };
            return ValueTask.FromResult<IMapScanCamera>(Camera);
        }
        public CampaignMapCombat CreateCombat(IMapScanCamera camera, CampaignConfiguration configuration)
            => Camera == camera ? Create(Camera.State, configuration, Camera) :
                throw new InvalidOperationException("Wrong camera instance");
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
        public CampaignState State => state;
        public Clock Clock { get; } = new();
        public Cell Position { get; private set; } = new(1, 1);
        public Cell? Destination { get; private set; }
        public bool ReturnStage { get; init; }
        public bool StageForBoss { get; init; }
        public Func<int, Cell, MapScanMode, MapObservation>? ObservationFactory { get; init; }
        public bool ReturningToStage => ReturnStage || StageForBoss && Destination is { } cell && state[cell].IsBoss;
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
        {
            token.ThrowIfCancellationRequested(); Scans++;
            return ValueTask.FromResult(ObservationFactory?.Invoke(Scans, Position, mode) ??
                new MapObservation([], Position, new(0, 0), mode));
        }
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
            if (_fired || camera.Destination is not { } destination)
                return ValueTask.FromResult(MapEncounterKind.None);
            var encounter = camera.IsCombatDestination ? MapEncounterKind.Combat :
                camera.State[destination].IsMystery ? MapEncounterKind.ItemPopup : MapEncounterKind.None;
            if (encounter == MapEncounterKind.None) return ValueTask.FromResult(encounter);
            _fired = true;
            return ValueTask.FromResult(encounter);
        }
    }

    private sealed class Handler(Camera camera) : IMapEncounterHandler
    {
        public ValueTask<MapEncounterHandling> HandleAsync(MapEncounterKind encounter, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (encounter == MapEncounterKind.ItemPopup && camera.Suspended)
                return ValueTask.FromResult(new MapEncounterHandling(MapEncounterContinuation.InMap));
            if (encounter != MapEncounterKind.Combat || !camera.Suspended) throw new InvalidDataException();
            var returned = camera.ReturningToStage ? CombatReturn.InStage : CombatReturn.InMap;
            var rank = camera.Rank is { } value
                ? new CombatRankEvidence(value, CombatRankSource.BattleStatus, UiAssets.Combat.BATTLE_STATUS_S.Id) : null;
            return ValueTask.FromResult(new MapEncounterHandling(camera.ReturningToStage ?
                MapEncounterContinuation.InStage : MapEncounterContinuation.InMap,
                new CombatFlowResult(returned, rank, false, false, 1)));
        }
    }
}
