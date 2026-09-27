using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static partial class CampaignMapCombatChecks
{
    public static async Task DecoyChecksAsync(string python, string upstream, string artifacts)
    {
        string output = Path.Combine(artifacts, "decoy-native.json");
        var run = await new ProcessRunner().RunAsync(python,
            [Path.Combine(AppContext.BaseDirectory, "native_decoy_reference.py"), upstream, output], TimeSpan.FromMinutes(2));
        Check(run.ExitCode == 0, "Native decoy oracle failed: " + run.Error);
        var native = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
        foreach (var source in new[] { CampaignState.InitializationSource, MapPathfinder.Source, CellState.Source })
            Check(native["sources"]![source.Path]!.GetValue<string>() == source.Sha256, "Decoy source drift: " + source.Path);
        int comparisons = 0, strictRefusals = 0;
        foreach (var expected in native["results"]!.AsArray())
        {
            var sample = expected!["sample"]!;
            bool enabled = sample["enabled"]!.GetValue<bool>(), combat = sample["combat"]!.GetValue<bool>();
            string kind = sample["kind"]!.GetValue<string>();
            var expectation = Enum.Parse<MapCombatExpectation>(sample["expectation"]!.GetValue<string>(), true);
            int? phase = sample["round"]?.GetValue<int>();
            var config = new CampaignConfiguration { HasDecoyEnemy = enabled, HasMaze = phase is not null,
                HasAmbush = false, PoorMapData = true, EmotionMode = CampaignEmotionMode.Ignore };
            var state = DecoyState(config);
            for (int i = 0; i < phase.GetValueOrDefault(); i++) state.Rounds.Advance();
            var target = state[new(2, 1)];
            target.IsEnemy = kind == "enemy"; target.IsSiren = kind == "siren";
            target.IsBoss = kind == "boss"; target.IsFortress = kind == "fortress";
            state.RefreshFleetPaths(config);
            var camera = new Camera(state) { CombatWhen = () => combat };
            var movement = DecoyMovement(state, config, camera);
            bool moved = false;
            MapMoveResult? result = null;
            try { result = await movement.FightAsync(target.Location, expectation: expectation); }
            catch (MapEnemyMovedException) { moved = true; }
            bool decoy = enabled && !combat && expectation == MapCombatExpectation.Enemy;
            if (!combat && !decoy)
            {
                // Native accepts these empty battles. The existing Engine battle
                // evidence contract deliberately refuses them; decoys cannot weaken it.
                Check(result?.Outcome == MapMoveOutcome.UnsupportedEncounter && !moved && state.MovementInvalidated &&
                    camera.Invalidated && state.BattleCount == 0 && state.FleetAmmo == 5 && state.Fleet1Location == new Cell(1, 1) &&
                    state.DecoyArrivals.Count == 0, "Non-decoy empty battle was accepted: " + sample);
                strictRefusals++;
                continue;
            }
            Check(moved == expected["moved"]!.GetValue<bool>() &&
                state.Fleet1Location?.ToString() == expected["fleet"]!.GetValue<string>() &&
                state.BattleCount == expected["battle"]!.GetValue<int>() && state.SirenCount == expected["siren"]!.GetValue<int>() &&
                state.FleetAmmo == expected["ammo"]!.GetValue<int>() && state.Rounds.Round == expected["round"]!.GetValue<int>() &&
                target.IsEnemy == expected["enemy"]!.GetValue<bool>() && target.IsCleared == expected["cleared"]!.GetValue<bool>() &&
                state.DecoyArrivals.Count == (decoy ? 1 : 0) && !state.MovementInvalidated && !camera.Invalidated && camera.Taps == 1,
                "Decoy state/redispatch differs from native: " + sample);
            // Combat I/O is a typed synthetic result; only empty arrivals have identical captures.
            if (decoy) Check(camera.FrameSequence - 1 == expected["frames"]!.GetValue<int>(),
                $"Decoy unexpected-arrival timer differs: {sample}, frames={camera.FrameSequence - 1}, native={expected["frames"]}");
            comparisons++;
        }
        foreach (var scan in native["scans"]!.AsArray())
        {
            var config = new CampaignConfiguration { HasDecoyEnemy = scan!["enabled"]!.GetValue<bool>(), PoorMapData = true };
            var state = DecoyState(config);
            MapScanMode? used = null;
            var camera = new Camera(state) { ObservationFactory = (_, position, mode) =>
            {
                used = mode;
                return new([new(new(3 - position.Column, 0), new(IsEnemy: true))], position, new(0, 0), mode);
            } };
            await new MapScanner(state, camera).ScanAsync(state.Progress, TimeSpan.FromSeconds(5),
                mode: Enum.Parse<MapScanMode>(scan["mode"]!.GetValue<string>(), true), fleet: new(config.HasDecoyEnemy));
            Check(used?.ToString().ToLowerInvariant() == scan["effective"]!.GetValue<string>() &&
                state[new(3, 1)].IsEnemy == scan["enemy"]!.GetValue<bool>(), "Decoy scan gate differs: " + scan);
        }
        await DecoyFailuresAsync();
        await DecoyCampaignAsync();
        await DecoyRedispatchBoundAsync();
        Console.WriteLine($"Decoys: {native["results"]!.AsArray().Count} actual native arrival traces ({comparisons} state/timer comparisons, {strictRefusals} evidence-contract refusals), 10 scan gates, late battle/failure boundaries and compiled campaign redispatch passed offline; no live device acceptance.");
    }

    private static CampaignState DecoyState(CampaignConfiguration config)
    {
        var state = new CampaignState(new MapDefinition("C1", "SP ME --", ["B1"], ["A1"], [new(0, Enemy: 1)]));
        state.InitializeMapData(new(PoorMapData: true));
        state.Fleet1Location = new(1, 1);
        state[new(1, 1)].IsFleet = state[new(1, 1)].IsCurrentFleet = true;
        state[new(2, 1)].IsEnemy = true;
        state.Rounds.Initialize(config); state.RefreshFleetPaths(config);
        return state;
    }
    private static MapMovement DecoyMovement(CampaignState state, CampaignConfiguration config, Camera camera)
        => new(state, config, camera, () => new(camera, state, camera.InMapAsync, camera.Clock, new Probe(camera), new Handler(camera)));

    private static async Task DecoyFailuresAsync()
    {
        var config = new CampaignConfiguration { HasDecoyEnemy = true, HasAmbush = false, PoorMapData = true,
            EmotionMode = CampaignEmotionMode.Ignore };
        foreach (string failure in new[] { "tap", "stale", "timeout", "cancel", "rank", "missing-rank" })
        {
            var state = DecoyState(config);
            using var cancel = new CancellationTokenSource();
            bool battle = failure is "rank" or "missing-rank";
            var camera = new Camera(state) { Failure = failure, Cancellation = cancel, CombatWhen = () => battle,
                Rank = failure == "rank" ? CombatRank.C : null };
            MapMoveResult? result = null;
            bool failed = false;
            try { result = await DecoyMovement(state, config, camera).FightAsync(new(2, 1), token: cancel.Token); }
            catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException) { failed = true; }
            Check((failed || result?.Outcome is MapMoveOutcome.UnsupportedEncounter or MapMoveOutcome.Unconfirmed) &&
                state.Fleet1Location == new Cell(1, 1) && state[new(2, 1)].IsEnemy && state.BattleCount == 0 &&
                state.FleetAmmo == 5 && state.DecoyArrivals.Count == 0 && state.MovementInvalidated && camera.Invalidated,
                "Decoy failure was committed or masked: " + failure);
        }
        // A delayed battle must win over the early marker on the enemy cell.
        {
            var state = DecoyState(config); Camera? camera = null;
            camera = new(state) { CombatWhen = () => camera!.FrameSequence >= 7 };
            var result = await DecoyMovement(state, config, camera).FightAsync(new(2, 1));
            Check(result.Outcome == MapMoveOutcome.Committed && state.BattleCount == 1 && state.FleetAmmo == 4 &&
                state.DecoyArrivals.Count == 0, "Early arrival marker swallowed a delayed real battle");
        }
        {
            var state = DecoyState(config);
            var camera = new Camera(state) { CombatWhen = () => false,
                MarkerAtFrame = frame => frame == 5 ? new(false, false) : new(true, true) };
            bool moved = false;
            try { await DecoyMovement(state, config, camera).FightAsync(new(2, 1)); }
            catch (MapEnemyMovedException) { moved = true; }
            Check(moved && camera.FrameSequence >= 15 && state.DecoyArrivals.Count == 1,
                "Flickering marker did not restart empty-target confirmation");
        }
        foreach (bool stage in new[] { false, true })
        {
            var state = DecoyState(config);
            state[new(2, 1)].MayBoss = true;
            var camera = new Camera(state) { CombatWhen = () => stage, ReturnStage = stage };
            // A candidate boss drawn as an ordinary enemy still expects a boss battle.
            var result = await DecoyMovement(state, config, camera).FightAsync(new(2, 1), expectation: MapCombatExpectation.Boss);
            Check(result.Outcome == (stage ? MapMoveOutcome.StageReturned : MapMoveOutcome.UnsupportedEncounter) &&
                state.DecoyArrivals.Count == 0 && state.BattleCount == 0, "Boss evidence was mistaken for a decoy");
        }
        foreach (bool boss in new[] { false, true })
        {
            var state = DecoyState(config);
            var target = state[new(2, 1)];
            if (boss) target.MayBoss = true;
            else { target.IsMechanismTrigger = true; target.MechanismTrigger = [target]; target.MechanismBlock = []; }
            var camera = new Camera(state) { CombatWhen = () => false };
            var combat = Create(state, config with { HasLandBased = !boss }, camera);
            bool rejected = false;
            try { if (boss) await combat.ClearBossAsync(); else await combat.ClearMechanismAsync(); }
            catch (CampaignScriptException) { rejected = true; }
            Check(rejected && state.DecoyArrivals.Count == 0 && state.BattleCount == 0 && target.IsEnemy,
                "Campaign boss/raw mechanism dispatch lost its original expected-result gate");
        }
        foreach (var kind in new[] { MapEncounterKind.ItemPopup, MapEncounterKind.AmmoNotification })
        {
            var state = DecoyState(config);
            var camera = new Camera(state) { CombatWhen = () => false };
            var movement = new MapMovement(state, config, camera, () => new(camera, state, camera.InMapAsync, camera.Clock,
                new DecoyOtherProbe(kind), new Handler(camera)));
            var result = await movement.FightAsync(new(2, 1));
            Check(result.Outcome == MapMoveOutcome.UnsupportedEncounter && state.DecoyArrivals.Count == 0 &&
                state.BattleCount == 0 && state.MysteryCount == 0 && state[new(2, 1)].IsEnemy,
                "Reward interaction was relabeled as an empty decoy arrival");
        }
        var disabled = CampaignPreparationRules.Apply(config with { IsClearMode = true }, new CampaignState(
            new MapDefinition("C1", "SP ME --", [], [], [new(0)])));
        Check(!disabled.HasDecoyEnemy, "Clear-mode preparation retained decoy enemies");
        // Movable-enemy scanning runs before decoy redispatch. A failed scan keeps
        // the confirmed empty move, but makes the partially scanned sortie unusable.
        foreach (bool failScan in new[] { false, true })
        {
            var dynamic = config with { HasMovableEnemy = true, MovableEnemyTurns = [1] };
            var state = new CampaignState(new MapDefinition("C1", "SP ME MS", ["B1"], [], [new(0, Enemy: 1, Siren: 1)]));
            state.InitializeMapData(new(PoorMapData: true));
            state.Fleet1Location = new(1, 1); state[new(1, 1)].IsFleet = true;
            state[new(2, 1)].IsEnemy = true; state[new(3, 1)].IsSiren = true;
            state.Rounds.Initialize(dynamic); state.RefreshFleetPaths(dynamic);
            var camera = new Camera(state) { CombatWhen = () => false, Failure = failScan ? "scan" : null,
                ObservationFactory = (_, position, mode) => new([new(new(3 - position.Column, 0), new(IsSiren: true))],
                    position, new(0, 0), mode) };
            var movement = new MapMovement(state, dynamic, camera, () =>
                new(camera, state, camera.InMapAsync, camera.Clock, new Probe(camera), new Handler(camera)),
                movableScan: new(state, dynamic, new(state, camera, camera.Clock)));
            bool moved = false, failed = false;
            try { await movement.FightAsync(new(2, 1)); }
            catch (MapEnemyMovedException) { moved = true; }
            catch (IOException) { failed = true; }
            Check(moved == !failScan && failed == failScan && state.Fleet1Location == new Cell(2, 1) &&
                state.BattleCount == 0 && state.FleetAmmo == 5 && state.DecoyArrivals.Count == 1 &&
                state.MovableScans.Count == (failScan ? 0 : 1) && state.MovementInvalidated == failScan && camera.Invalidated == failScan,
                "Decoy redispatch bypassed movable scanning or rolled back a confirmed arrival");
        }
    }

    private static async Task DecoyCampaignAsync()
    {
        var map = new MapDefinition("E1", "SP ME -- -- MB", ["B1"], ["B1"],
            [new(0, Enemy: 1), new(1, Enemy: 1), new(2, Boss: 1)]);
        var rule = new DecoyRule(map);
        var host = new Host
        {
            CombatWhen = camera => camera.Destination != new Cell(3, 1),
            ObservationFactory = (scan, position, mode) => new(scan == 1
                ? [new(new(1 - position.Column, 0), new(IsFleet: true, IsCurrentFleet: true)),
                   new(new(2 - position.Column, 0), new(IsEnemy: true, EnemyScale: 1))]
                : scan == 2
                    ? [new(new(3 - position.Column, 0), new(IsEnemy: true)), new(new(4 - position.Column, 0), new(IsEnemy: true))]
                    : [new(new(5 - position.Column, 0), new(IsBoss: true))], position, new(0, 0), mode)
        };
        var execution = new CampaignExecution(rule, new() { EmotionMode = CampaignEmotionMode.Ignore },
            (state, config) => new InMapCampaignOperations(host, state, config, default, rule));
        var exit = await execution.RunAsync();
        var state = execution.Context.State;
        var operations = (InMapCampaignOperations)execution.Context.Operations;
        Check(exit == CampaignLoopExit.Ended && state.BattleCount == 2 && state.FleetAmmo == 3 && host.Camera?.Taps == 4 &&
            host.Camera.Scans == 3 && state.DecoyArrivals is [ { From: { Column: 2 }, To: { Column: 3 }, BattleCount: 1 } ] &&
            !state[new(3, 1)].IsEnemy && !state[new(3, 1)].IsCleared && operations.StageReturn is not null,
            $"C# decoy campaign differs: {exit}, battles={state.BattleCount}, ammo={state.FleetAmmo}, taps={host.Camera?.Taps}, scans={host.Camera?.Scans}, decoys={state.DecoyArrivals.Count}");
        var result = CampaignResumeTask.Describe("test", "campaign_run", rule,
            new(exit, state.BattleCount, operations.StageReturn, DecoyArrivals: state.DecoyArrivals), true);
        Check(result.Evidence?["decoyArrivals"]?.AsArray().Count == 1, "Campaign artifact lost confirmed empty-target evidence");
    }

    private sealed class DecoyRule(MapDefinition map) : CampaignRule
    {
        public override string Id => "test/decoy";
        public override MapDefinition Map => map;
        public override ImmutableArray<SourceFile> Sources => [];
        public override CampaignConfiguration Configure(CampaignConfiguration input)
            => input with { HasDecoyEnemy = true, HasAmbush = false };
        protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; } = new Dictionary<int, BattleHook>
        {
            [0] = static context => context.Operations.ClearEnemyAsync(),
            [2] = static context => context.Operations.ClearBossAsync()
        };
    }

    private sealed class DecoyOtherProbe(MapEncounterKind kind) : IMapEncounterProbe
    {
        private bool _fired;
        public ValueTask InitializeAsync(long sequence, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<MapEncounterKind> InspectAsync(long sequence, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var result = _fired ? MapEncounterKind.None : kind;
            _fired = true;
            return ValueTask.FromResult(result);
        }
    }

    private static async Task DecoyRedispatchBoundAsync()
    {
        var rule = new DecoyRule(new("M1", "SP " + string.Join(' ', Enumerable.Repeat("ME", 12)),
            ["A1"], ["A1"], [new(0, Enemy: 12)]));
        var host = new Host { CombatWhen = _ => false, ObservationFactory = (_, position, mode) =>
            new(Enumerable.Range(1, 13).Select(x => new MapCellObservation(new(x - position.Column, 0), x == 1
                ? new(IsFleet: true, IsCurrentFleet: true) : new(IsEnemy: true))).ToArray(), position, new(0, 0), mode) };
        var execution = new CampaignExecution(rule, new() { EmotionMode = CampaignEmotionMode.Ignore },
            (state, config) => new InMapCampaignOperations(host, state, config, default, rule));
        bool exhausted = false;
        try { await execution.RunAsync(); } catch (CampaignScriptException) { exhausted = true; }
        Check(exhausted && execution.Context.State.DecoyArrivals.Count == 10 && execution.Context.State.BattleCount == 0 &&
            host.Camera is { Taps: 10, Scans: 1 }, "Decoys bypassed native ten-attempt battle redispatch limit");
    }
}
