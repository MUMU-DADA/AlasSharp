using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static partial class CampaignMapCombatChecks
{
    private sealed record SpecialSample(string Kind, bool Enabled = true, bool Siren = true, bool Movable = true,
        int Boss = 2, string? Second = "E1", string Start = "A1", string[]? Caught = null,
        string[]? Inactive = null, int Hit = 0, bool Calculate = false, bool Locked = false);

    private static MapDefinition SpecialMap(bool rescue) => new("E2", "SP -- -- -- SP\n-- ME ME ME MB", ["C1"], [],
        rescue ? [new(0, Siren: 1), new(1)] : [new(0)],
        mechanisms: new(bouncingRoutes: [[Cell.Parse("B2"), Cell.Parse("C2"), Cell.Parse("D2")],
            [Cell.Parse("C2"), Cell.Parse("E2")]]));

    private static (CampaignState State, CampaignConfiguration Config) SpecialState(SpecialSample sample, bool roundEnemy = false)
    {
        bool rescue = sample.Kind == "rescue";
        var config = new CampaignConfiguration { HasSiren = sample.Siren, HasMovableEnemy = sample.Movable,
            HasBouncingEnemy = !rescue && sample.Enabled, HasAmbush = false, PoorMapData = true,
            Fleet2 = sample.Second is null ? 0 : 2,
            FleetOrder = sample.Boss == 2 ? FleetOrder.Fleet1MobFleet2Boss : FleetOrder.Fleet1AllFleet2Standby,
            EmotionMode = sample.Calculate ? CampaignEmotionMode.Calculate : CampaignEmotionMode.Ignore,
            UseFleetLock = sample.Locked };
        var state = new CampaignState(SpecialMap(rescue || roundEnemy));
        state.InitializeMapData(new(PoorMapData: true, BouncingEnemy: config.HasBouncingEnemy));
        state.Fleet1Location = Cell.Parse(sample.Start);
        state.Fleet2Location = sample.Second is null ? null : Cell.Parse(sample.Second);
        state[state.Fleet1Location.Value].IsFleet = state[state.Fleet1Location.Value].IsCurrentFleet = true;
        if (state.Fleet2Location is { } second) state[second].IsFleet = true;
        foreach (string c in sample.Caught ?? []) state[Cell.Parse(c)].IsCaughtBySiren = true;
        foreach (string c in sample.Inactive ?? []) state[Cell.Parse(c)].MayBouncingEnemy = false;
        state.Rounds.Initialize(config); state.RefreshFleetPaths(config);
        return (state, config);
    }

    public static async Task SpecialEnemyChecksAsync(string python, string upstream, string artifacts)
    {
        var cases = new List<SpecialSample>();
        foreach (bool siren in new[] { false, true })
        foreach (bool movable in new[] { false, true })
        foreach (int boss in new[] { 1, 2 })
        foreach (string? second in new string?[] { null, "E1" })
        foreach (string[] caught in new string[][] { [], ["A1"], ["E1"], ["A1", "E1", "B1"] })
        foreach (bool wait in new[] { false, true })
            cases.Add(new("rescue", Siren: siren, Movable: movable, Boss: boss, Second: second,
                Caught: caught, Inactive: [], Calculate: wait, Locked: wait));
        foreach (bool enabled in new[] { false, true })
        foreach (string[] inactive in new string[][] { [], ["B2", "C2", "D2"], ["B2", "C2", "D2", "E2"] })
        foreach (int hit in new[] { 0, 1, 2, 3, 13 })
        foreach (bool wait in new[] { false, true })
            cases.Add(new("bounce", Enabled: enabled, Siren: false, Movable: false, Second: null,
                Start: "B2", Caught: [], Inactive: inactive, Hit: hit, Calculate: wait, Locked: wait));
        foreach (string second in new[] { "B2", "C2" })
        foreach (int hit in new[] { 0, 1, 3, 13 })
            cases.Add(new("bounce", Siren: false, Movable: false, Second: second, Caught: [], Inactive: [], Hit: hit));
        string input = Path.Combine(artifacts, "special-enemies-input.json");
        string output = Path.Combine(artifacts, "special-enemies-native.json");
        await File.WriteAllTextAsync(input, JsonSerializer.Serialize(cases, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var nativeProcess = await new ProcessRunner().RunAsync(python,
            [Path.Combine(AppContext.BaseDirectory, "native_special_enemy_reference.py"), upstream, input, output], TimeSpan.FromMinutes(2));
        Check(nativeProcess.ExitCode == 0, "Native special enemy oracle failed: " + nativeProcess.Error);
        var native = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
        foreach (var source in new[] { CampaignMapCombat.Source, MapRounds.Source, MapPathfinder.Source, CellState.Source })
            Check(native["sources"]![source.Path]!.GetValue<string>() == source.Sha256, "Special enemy source drifted: " + source.Path);
        for (int i = 0; i < cases.Count; i++)
        {
            var sample = cases[i]; var expected = native["results"]![i]!;
            var (state, config) = SpecialState(sample);
            var trace = new List<string>();
            Camera? camera = null;
            camera = new Camera(state) { Trace = trace.Add,
                CombatWhen = () => sample.Kind == "rescue" || camera!.Taps == sample.Hit };
            var combat = Create(state, config, camera, (fleet, token) =>
            {
                trace.Add("emotion:" + fleet); return ValueTask.CompletedTask;
            }, (fleet, token) =>
            {
                trace.Add("switch:" + fleet); state.FleetIndex = fleet; state.RefreshFleetPaths(config);
                return ValueTask.CompletedTask;
            });
            bool result = sample.Kind == "rescue" ? await combat.BreakSirenCaughtAsync() : await combat.ClearBouncingEnemyAsync();
            var actual = new JsonObject
            {
                ["result"] = result, ["trace"] = JsonSerializer.SerializeToNode(trace),
                ["fleet1"] = state.Fleet1Location?.ToString(), ["fleet2"] = state.Fleet2Location?.ToString(),
                ["index"] = state.FleetIndex, ["battle"] = state.BattleCount, ["siren"] = state.SirenCount,
                ["ammo"] = state.FleetAmmo, ["round"] = state.Rounds.Round,
                ["caught"] = JsonSerializer.SerializeToNode(state.Cells.Where(g => g.IsCaughtBySiren).Select(g => g.Location.ToString())),
                ["active"] = JsonSerializer.SerializeToNode(state.Cells.Where(g => g.MayBouncingEnemy).Select(g => g.Location.ToString())),
                ["cleared"] = JsonSerializer.SerializeToNode(state.Cells.Where(g => g.IsCleared).Select(g => g.Location.ToString()))
            };
            // Combat I/O deliberately differs: C# waits for a fresh marker after the typed winning result.
            // Empty probes use identical native timers, so compare their exact image count as well.
            if (state.BattleCount == 0) actual["frames"] = camera.FrameSequence - 1;
            else expected.AsObject().Remove("frames");
            Check(JsonNode.DeepEquals(actual, expected), $"Special enemy native trace {i}: actual={actual}, expected={expected}");
        }
        await SpecialEnemyFailuresAsync();
        await SpecialEnemyRouteCompositionAsync();
        await SpecialEnemyExecutionAsync();
        Console.WriteLine($"Special enemies: {cases.Count} actual native rescue/bouncing/goto traces, failure boundaries and C# campaign composition passed offline; no live device acceptance.");
    }

    private static async Task SpecialEnemyFailuresAsync()
    {
        foreach (string kind in new[] { "rescue", "bounce" })
        foreach (string failure in new[] { "tap", "stale", "cancel", "timeout", "rank", "missing-rank", "scan" })
        {
            bool rescue = kind == "rescue";
            var (state, config) = SpecialState(new(kind, Siren: rescue, Movable: rescue,
                Second: rescue ? "E1" : null, Caught: ["B1", "E1"]));
            using var cancellation = new CancellationTokenSource();
            var camera = new Camera(state) { Failure = failure, Cancellation = cancellation, CombatWhen = () => true,
                Rank = failure == "rank" ? CombatRank.C : failure == "missing-rank" ? null : CombatRank.S };
            var combat = Create(state, config, camera, switchFleet: (fleet, token) =>
            { state.FleetIndex = fleet; state.RefreshFleetPaths(config); return ValueTask.CompletedTask; });
            bool failed = false;
            try
            {
                if (rescue) await combat.BreakSirenCaughtAsync(cancellation.Token);
                else await combat.ClearBouncingEnemyAsync(cancellation.Token);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or CampaignScriptException) { failed = true; }
            Check(failed && combat.StageReturn is null && state.BattleCount == (failure == "scan" ? 1 : 0),
                $"{kind}/{failure}: lost failure or committed unverified battle");
            Check(state.MovementInvalidated && camera.Invalidated, $"{kind}/{failure}: failed sortie remained reusable");
            if (failure != "scan")
                Check(state[Cell.Parse("E1")].IsCaughtBySiren &&
                    (rescue || state.Cells.Count(g => g.MayBouncingEnemy) == 4), $"{kind}/{failure}: reused invalid arrival or released flags");
            else if (!rescue)
                Check(state.Cells.Where(g => g.MayBouncingEnemy).Select(g => g.Location).SequenceEqual([Cell.Parse("E2")]),
                    "Scan failure rolled back confirmed route cleanup or cleared an unrelated route");
            if (rescue) Check(state.FleetIndex == 2 && state[Cell.Parse("B1")].IsCaughtBySiren,
                "Failed rescue restored fleet or reset unrelated caught flags");
        }
        // A fleet marker on its own is not a rescue combat.
        {
            var (state, config) = SpecialState(new("rescue", Caught: ["E1"]));
            var camera = new Camera(state) { CombatWhen = () => false };
            var combat = Create(state, config, camera, switchFleet: (fleet, token) =>
            { state.FleetIndex = fleet; state.RefreshFleetPaths(config); return ValueTask.CompletedTask; });
            bool refused = false;
            try { await combat.BreakSirenCaughtAsync(); } catch (CampaignScriptException) { refused = true; }
            Check(refused && state[Cell.Parse("E1")].IsCaughtBySiren && state.BattleCount == 0 && state.MovementInvalidated,
                "Same-cell marker incorrectly rescued a caught fleet");
        }
        foreach (string failure in new[] { "edges", "switch-back" })
        {
            var (state, config) = SpecialState(new("rescue", Caught: ["E1", "B1"]));
            var camera = new Camera(state) { Failure = failure };
            var combat = Create(state, config, camera, switchFleet: (fleet, token) =>
            {
                if (fleet == 1) throw new IOException("Synthetic switch-back failure");
                state.FleetIndex = fleet; state.RefreshFleetPaths(config); return ValueTask.CompletedTask;
            });
            bool failed = false;
            try { await combat.BreakSirenCaughtAsync(); } catch (IOException) { failed = true; }
            Check(failed && state.FleetIndex == 2 && state[Cell.Parse("B1")].IsCaughtBySiren &&
                camera.Taps == (failure == "edges" ? 0 : 1), "Rescue failure order differs");
        }
        foreach (string kind in new[] { "rescue", "bounce" })
        foreach (CombatRank? rank in new CombatRank?[] { CombatRank.S, CombatRank.C, null })
        {
            bool rescue = kind == "rescue";
            var (state, config) = SpecialState(new(kind, Siren: rescue, Movable: rescue,
                Second: rescue ? "E1" : null, Caught: ["E1"]));
            var camera = new Camera(state) { ReturnStage = true, Rank = rank, CombatWhen = () => true };
            var combat = Create(state, config, camera, switchFleet: (fleet, token) =>
            { state.FleetIndex = fleet; state.RefreshFleetPaths(config); return ValueTask.CompletedTask; });
            bool ended = false, rejected = false;
            try { if (rescue) await combat.BreakSirenCaughtAsync(); else await combat.ClearBouncingEnemyAsync(); }
            catch (CampaignEndedException) { ended = true; }
            catch (CampaignScriptException) { rejected = true; }
            Check(rank == CombatRank.S ? ended && combat.StageReturn is not null : rejected && combat.StageReturn is null,
                "Special enemy stage return lost rank contract");
            Check(state.BattleCount == 0 && state.FleetAmmo == 5 && state.MovementInvalidated &&
                state[Cell.Parse("E1")].IsCaughtBySiren && (rescue ? state.FleetIndex == 2 : state.Cells.Count(g => g.MayBouncingEnemy) == 4),
                "Special enemy stage return committed map accounting or cleaned route/caught flags");
        }
        await SpecialEnemyRoundInterruptAsync();
        await SpecialEnemyPreflightAsync();
    }

    private static async Task SpecialEnemyRoundInterruptAsync()
    {
        foreach (bool rescue in new[] { false, true })
        {
            var (state, config) = SpecialState(new(rescue ? "rescue" : "bounce", Caught: ["B1", "E1"]), roundEnemy: true);
            state[Cell.Parse("D1")].IsSiren = true;
            state.Rounds.Advance(); state.RefreshFleetPaths(config);
            var camera = new Camera(state) { CombatWhen = () => true };
            var combat = Create(state, config, camera, switchFleet: (fleet, token) =>
            { state.FleetIndex = fleet; state.RefreshFleetPaths(config); return ValueTask.CompletedTask; });
            bool changed = false;
            try { if (rescue) await combat.BreakSirenCaughtAsync(); else await combat.ClearBouncingEnemyAsync(); }
            catch (MapEnemyMovedException) { changed = true; }
            Check(changed && state.BattleCount == 1 && state.SirenCount == 0 && state.Rounds.Round == 2 &&
                state.MovableScans.Count == 1 && !state.MovementInvalidated &&
                (rescue ? state.FleetIndex == 2 && state[Cell.Parse("B1")].IsCaughtBySiren : state.Cells.Count(g => g.MayBouncingEnemy) == 4),
                $"Round interruption differs: rescue={rescue}, changed={changed}, battle={state.BattleCount}, siren={state.SirenCount}, round={state.Rounds.Round}, scans={state.MovableScans.Count}, invalid={state.MovementInvalidated}, fleet={state.FleetIndex}");
        }
    }

    private static async Task SpecialEnemyPreflightAsync()
    {
        foreach (string invalid in new[] { "inactive", "unknown-fleet", "unreachable", "cancel" })
        {
            var (state, config) = SpecialState(new("bounce", Siren: false, Movable: false, Second: null));
            if (invalid == "unknown-fleet") state[Cell.Parse("B2")].IsFleet = true;
            if (invalid == "inactive") foreach (var grid in state.Cells) grid.MayBouncingEnemy = false;
            if (invalid == "unreachable")
            {
                state[Cell.Parse("B2")].IsMechanismBlock = true; state.RefreshFleetPaths(config);
            }
            var camera = new Camera(state); var combat = Create(state, config, camera);
            using var cancel = new CancellationTokenSource();
            if (invalid == "cancel") cancel.Cancel();
            bool rejected = false;
            try { rejected = !await combat.ClearBouncingEnemyAsync(cancel.Token); }
            catch (Exception e) when (e is NotSupportedException or CampaignScriptException or OperationCanceledException) { rejected = true; }
            Check(rejected && camera.Taps == 0 && state.BattleCount == 0, "Bouncing preflight executed unsafe route: " + invalid);
        }
        // Brute boss fallback must fight at fleet 2's caught potential boss, without moving fleet 1 there.
        {
            var (state, config) = SpecialState(new("rescue", Second: "E2", Caught: ["E2"]));
            var camera = new Camera(state) { ReturnStage = true };
            var combat = Create(state, config, camera, switchFleet: (fleet, token) =>
            { state.FleetIndex = fleet; state.RefreshFleetPaths(config); return ValueTask.CompletedTask; });
            bool ended = false;
            try { await combat.BruteClearBossAsync(); } catch (CampaignEndedException) { ended = true; }
            Check(ended && camera.Taps == 1 && state.FleetIndex == 2 && state.Fleet1Location == Cell.Parse("A1") &&
                combat.StageReturn is not null, "Caught boss fallback used wrong fleet or lost stage evidence");
        }
    }

    private static async Task SpecialEnemyRouteCompositionAsync()
    {
        {
            var map = new MapDefinition("G1", "SP -- -- -- -- -- MS", ["D1"], [], [new(0, Siren: 1)],
                mechanisms: new(bouncingRoutes: [[Cell.Parse("G1"), Cell.Parse("F1")]]));
            var state = new CampaignState(map);
            state.InitializeMapData(new(PoorMapData: true, BouncingEnemy: true));
            state.Fleet1Location = Cell.Parse("A1"); state[Cell.Parse("A1")].IsFleet = true;
            state[Cell.Parse("G1")].IsSiren = true;
            var config = new CampaignConfiguration { HasBouncingEnemy = true, HasFleetStep = true, Fleet1Step = 2,
                HasAmbush = false, EmotionMode = CampaignEmotionMode.Ignore };
            state.Rounds.Initialize(config); state.RefreshFleetPaths(config);
            var trace = new List<string>(); var camera = new Camera(state) { Trace = trace.Add };
            Check(await Create(state, config, camera).ClearBouncingEnemyAsync() &&
                trace.SequenceEqual(["tap:C1", "tap:E1", "tap:G1", "scan"]) && state.BattleCount == 1 &&
                state.SirenCount == 0 && state.FleetAmmo == 4 && !state.Cells.Any(g => g.MayBouncingEnemy),
                "Bouncing route lost fleet steps or combat_nothing siren attribution");
        }
        {
            var map = new MapDefinition("B1", "SP MA", ["A1"], [], [new(0)],
                mechanisms: new(bouncingRoutes: [[Cell.Parse("B1")]]));
            var state = new CampaignState(map);
            state.InitializeMapData(new(PoorMapData: true, BouncingEnemy: true));
            state.Fleet1Location = Cell.Parse("A1"); state[Cell.Parse("A1")].IsFleet = true;
            var config = new CampaignConfiguration { HasBouncingEnemy = true, HasAmbush = false, EmotionMode = CampaignEmotionMode.Ignore };
            state.Rounds.Initialize(config); state.RefreshFleetPaths(config);
            var camera = new Camera(state);
            Check(!await Create(state, config, camera).ClearBouncingEnemyAsync() && camera.Taps == 26 &&
                state.Fleet1Location == Cell.Parse("B1") && state.AmmoCount == 3 && state.FleetAmmo == 5 &&
                state.BattleCount == 0 && state[Cell.Parse("B1")].MayBouncingEnemy,
                "Empty bouncing supply probes lost 13 visits, extra taps or invented supply accounting");
        }
    }

    private static async Task SpecialEnemyExecutionAsync()
    {
        foreach (bool rescue in new[] { false, true })
        {
            // Native full_scan retains a caught fleet at a potential boss. Other
            // fleet cells are wiped during scan bookkeeping, including initial scan.
            var map = new MapDefinition("E2", rescue ? "SP -- -- -- MB\n-- ME ME ME MB" : "SP -- -- -- SP\n-- ME ME ME MB", ["C1"], ["C1"],
                [new(0, Enemy: rescue ? 0 : 1, Siren: rescue ? 1 : 0), new(1, Boss: 1)],
                mechanisms: new(bouncingRoutes: [[Cell.Parse("B2"), Cell.Parse("C2"), Cell.Parse("D2")]]));
            var host = new Host { SimulateFleetSwitch = true,
                ObservationFactory = (scan, position, mode) => new(scan == 1
                    ? rescue
                        ? [new(new(1 - position.Column, 1 - position.Row), new(IsFleet: true, IsCurrentFleet: true)),
                           new(new(5 - position.Column, 1 - position.Row), new(IsCaughtBySiren: true))]
                        : [new(new(1 - position.Column, 1 - position.Row), new(IsFleet: true, IsCurrentFleet: true)),
                           new(new(3 - position.Column, 2 - position.Row), new(IsEnemy: true, EnemyScale: 1))]
                    : [new(new(5 - position.Column, 2 - position.Row), new(IsBoss: true))], position, new(0, 0), mode) };
            var rule = new SpecialRule(map);
            var execution = new CampaignExecution(rule, new()
            {
                HasBouncingEnemy = !rescue, HasSiren = rescue, HasMovableEnemy = rescue,
                Fleet2 = rescue ? 2 : 0, PoorMapData = rescue, HasAmbush = false, ClearAllThisTime = true, EmotionMode = CampaignEmotionMode.Ignore
            }, (state, config) => new InMapCampaignOperations(host, state, config, default, rule));
            CampaignLoopExit exit;
            try { exit = await execution.RunAsync(); }
            catch (Exception error)
            {
                throw new InvalidOperationException($"Special campaign rescue={rescue}, battle={execution.Context.State.BattleCount}, taps={host.Camera?.Taps}, scans={host.Camera?.Scans}, config={execution.Context.Config}, cells={string.Join(',', execution.Context.State.Cells.Select(g => g.Location + ":" + g.Encode()))}", error);
            }
            Check(exit == CampaignLoopExit.Ended && execution.Context.State.BattleCount == 1 &&
                execution.Context.State.SirenCount == 0 && host.Camera!.Taps == 3 - (rescue ? 1 : 0) &&
                execution.Context.Operations is InMapCampaignOperations { StageReturn: not null } &&
                !execution.Context.State.Cells.Any(g => rescue ? g.IsCaughtBySiren : g.MayBouncingEnemy),
                "Special enemy dispatch did not finish its C# rescue/bounce, rescan and boss sequence");
        }
    }

    private sealed class SpecialRule(MapDefinition map) : CampaignRule
    {
        public override string Id => "test/special-enemies";
        public override MapDefinition Map => map;
        public override ImmutableArray<SourceFile> Sources => [];
        protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; } = new Dictionary<int, BattleHook>();
    }
}
