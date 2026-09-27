using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static partial class CampaignMapCombatChecks
{
    private sealed record DeclaredRoadSample(int Enemy, int Cleared, int[] Scales, int[] Weights,
        string[][][] Roads, bool Potential, string Priority, bool ClearAll, int Active, int BossFleet);
    public static async Task DeclaredRoadChecksAsync(string python, string upstream, string artifacts)
    {
        var random = new Random(77123);
        var samples = new List<DeclaredRoadSample>();
        for (int i = 0; i < 240; i++)
        {
            string[][][] roads = [ [ ["B1", "B2"], ["B2", "C1"] ], [ ["A2", "A3", "B3"], ["C2"] ] ];
            if (i % 4 == 0) roads = [ [ ["A1", "B1"], ["B1", "C3"], [], ["B2", "B2"] ] ];
            samples.Add(new(random.Next(512) & 254, random.Next(512), Enumerable.Range(0, 9).Select(_ => random.Next(4)).ToArray(),
                Enumerable.Range(1, 9).OrderBy(_ => random.Next()).Select(x => x * 10).ToArray(), roads, i % 2 == 0,
                new[] { "default", "S3_enemy_first", "S1_enemy_first" }[i % 3], i % 5 == 0, i / 2 % 2 + 1, i % 2 + 1));
        }
        string input = Path.Combine(artifacts, "declared-road-input.json"), output = Path.Combine(artifacts, "declared-road-native.json");
        await File.WriteAllTextAsync(input, JsonSerializer.Serialize(samples, TaskQueue.Json));
        var run = await new ProcessRunner().RunAsync(python,
            [Path.Combine(AppContext.BaseDirectory, "native_declared_road_reference.py"), upstream, input, output], TimeSpan.FromMinutes(1));
        Check(run.ExitCode == 0, "Native declared-road oracle failed: " + run.Error);
        var native = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
        foreach (var source in new[] { CampaignMapCombat.Source, RoadDefinition.Source, CampaignState.InitializationSource, FleetRoles.BossSource })
            Check(native["sources"]![source.Path]!.GetValue<string>() == source.Sha256, "Declared road source drifted");
        int fights = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];
            var expected = native["results"]![i]!;
            var config = new CampaignConfiguration { HasAmbush = false, Fleet2 = 2, ClearAllThisTime = sample.ClearAll,
                UseFleetLock = false, BossFleet = sample.BossFleet, EnemyPriority = sample.Priority switch {
                    "S3_enemy_first" => EnemyScalePriority.StrongestFirst, "S1_enemy_first" => EnemyScalePriority.WeakestFirst,
                    _ => EnemyScalePriority.Default } };
            var state = Prepare(new("C3", "-- -- --\n-- -- --\n-- -- --", ["B2"], [], []));
            state.Fleet2Location = new(3, 3); state.FleetIndex = sample.Active;
            for (int index = 0; index < state.Cells.Count; index++)
            {
                var cell = state.Cells[index];
                cell.IsEnemy = (sample.Enemy & (1 << index)) != 0;
                cell.IsCleared = (sample.Cleared & (1 << index)) != 0;
                cell.EnemyScale = sample.Scales[index]; cell.Weight = sample.Weights[index];
            }
            state.RefreshFleetPaths(config);
            var roads = sample.Roads.Select(road => new RoadDefinition(road.Select(group => group.Select(Cell.Parse).ToImmutableArray()).ToImmutableArray())).ToArray();
            Check(roads.SelectMany(road => road.Select(state, sample.Potential)).Select(cell => cell.Location.ToString()).Distinct().Order(StringComparer.Ordinal)
                .SequenceEqual(expected["selected"]!.AsArray().Select(x => x!.GetValue<string>())), "Native RoadGrids groups differ: " + i);
            var camera = new Camera(state);
            var combat = Create(state, config, camera);
            var snapshot = state.Cells.Select(cell => (cell.Cost, cell.Cost1, cell.Cost2, cell.Connection, cell.IsFleet)).ToArray();
            foreach (int fleet in new[] { 1, 2 })
                Check(state.Cells.Select(cell => combat.CheckAccessibility(cell.Location, fleet)).SequenceEqual(
                    expected["access"]![fleet - 1]!.AsArray().Select(x => x!.GetValue<bool>())), "Native fleet accessibility differs: " + i);
            Check(state.FleetIndex == sample.Active && state.Cells.Select(cell => (cell.Cost, cell.Cost1, cell.Cost2, cell.Connection, cell.IsFleet)).SequenceEqual(snapshot),
                "Accessibility query mutated fleet/cost/predecessor state");
            bool result = await combat.ClearRoadblocksAsync(roads, sample.Potential);
            Check(result == expected["success"]!.GetValue<bool>() && camera.Destination?.ToString() == expected["target"]?.GetValue<string>() &&
                state.BattleCount == (result ? 1 : 0) && state.FleetAmmo == (result ? 4 : 5), "Declared road action/priority differs: " + i);
            if (result) fights++;
        }
        for (int i = 0; i < 4; i++) CompareChapterTwoConfig(RuleCatalog.Create($"campaign_main/campaign_2_{i + 1}"), native["configs"]![i]!);
        foreach (var role in native["bossRoles"]!.AsArray())
            Check(FleetRoles.BossIndex(new() { FleetOrder = FleetRoles.Parse(role!["order"]!.GetValue<string>()),
                Fleet2 = role["second"]!.GetValue<int>(), BossFleet = role["override"]?.GetValue<int>() }) == role["expected"]!.GetValue<int>(),
                "Explicit native boss override lost priority over fleet-order derivation");
        await DeclaredRoadFailuresAsync();
        await ChapterTwoCampaignsAsync();
        await CombatFlowChecks.ChapterTwoExperienceAsync();
        Console.WriteLine($"Declared roads: {samples.Count} native selection/dispatch cases ({fights} synthetic battles), 4320 fleet accessibility checks, 24 boss roles, four inherited configurations, failure boundaries and compiled second-chapter campaigns passed offline; no live acceptance.");
    }
    private static async Task DeclaredRoadFailuresAsync()
    {
        var config = new CampaignConfiguration { HasAmbush = false, EmotionMode = CampaignEmotionMode.Ignore };
        RoadDefinition[] roads = [new([[new Cell(2, 1)]])];
        foreach (bool first in new[] { false, true })
        foreach (string failure in new[] { "tap", "scan", "loss", "missing-rank" })
        {
            var state = Prepare(new("B1", "SP ME", ["A1"], [], []));
            state[new(2, 1)].IsEnemy = true; state.RefreshFleetPaths(config);
            var camera = new Camera(state) { Failure = failure, Rank = failure == "loss" ? CombatRank.C :
                failure == "missing-rank" ? null : CombatRank.S };
            var combat = Create(state, config, camera);
            Exception? error = null;
            try { if (first) await combat.ClearFirstRoadblocksAsync(roads); else await combat.ClearRoadblocksAsync(roads); }
            catch (Exception caught) { error = caught; }
            bool committed = failure == "scan";
            Check((committed || failure == "tap" ? error is IOException : error is CampaignScriptException) &&
                state.BattleCount == (committed ? 1 : 0) && state.FleetAmmo == (committed ? 4 : 5) &&
                state[new(2, 1)].IsEnemy == !committed && combat.StageReturn is null,
                $"Road failure misreported success, committed early or rolled back a confirmed battle: {failure}, error={error?.GetType().Name}, battles={state.BattleCount}, ammo={state.FleetAmmo}");
        }
        foreach (string failure in new[] { "unobserved", "partial" })
        {
            var state = Prepare(new("C1", "SP SP MB", [], [], []));
            var dual = config with { Fleet2 = 2 };
            state.Fleet2Location = new(2, 1); state[new(3, 1)].IsBoss = true; state.RefreshFleetPaths(dual);
            var camera = new Camera(state) { StageForBoss = true };
            var combat = Create(state, dual, camera, switchFleet: (fleet, token) =>
            {
                if (failure == "partial")
                {
                    state.FleetIndex = fleet;
                    throw new IOException("Synthetic post-switch failure");
                }
                return ValueTask.CompletedTask;
            });
            Exception? error = null;
            try { await combat.ClearBossForFleetAsync(2); }
            catch (Exception caught) { error = caught; }
            Check((failure == "partial" ? error is IOException : error is InvalidDataException) && camera.Taps == 0 &&
                state.BattleCount == 0 && combat.StageReturn is null && state.FleetIndex == (failure == "partial" ? 2 : 1),
                "Explicit boss-fleet operation fought after an incomplete switch or rewound observed identity");
        }
    }
    private static void CompareChapterTwoConfig(CampaignRule rule, JsonNode expected)
    {
        foreach (var order in Enum.GetValues<FleetOrder>())
        foreach (int second in new[] { 0, 2 })
        {
            var config = rule.Configure(new() { Fleet2 = second, FleetOrder = order, Submarine = 1, AmbushEvade = false });
            int? bossOverride = expected["FLEET_BOSS"]?.GetValue<int>();
            Check(config.Fleet2 == second && config.Submarine == (expected["SUBMARINE"]?.GetValue<int>() ?? 1) && !config.AmbushEvade &&
                config.BossFleet == bossOverride && FleetRoles.BossIndex(config) == FleetRoles.BossIndex(new() { Fleet2 = second, FleetOrder = order, BossFleet = bossOverride }),
                "Chapter Config lost unrelated options or failed to override boss fleet");
            var detector = new MapDetectionRules().WithChapter(config.Vision); detector.Validate();
            static object Peaks(LinePeakParameters peaks)
            {
                var result = new Dictionary<string, object> { ["height"] = new[] { peaks.Height.Low, peaks.Height.High },
                    ["prominence"] = peaks.Prominence, ["distance"] = peaks.Distance };
                if (peaks.Width is { } width) result["width"] = new[] { width.Low, width.High };
                if (peaks.Window is { } window) result["wlen"] = window;
                return result;
            }
            var actual = JsonSerializer.SerializeToNode(new Dictionary<string, object?> {
                ["FLEET_BOSS"] = config.BossFleet!, ["INTERNAL_LINES_HOUGHLINES_THRESHOLD"] = detector.InternalLinesThreshold,
                ["EDGE_LINES_HOUGHLINES_THRESHOLD"] = detector.EdgeLinesThreshold, ["HOMO_EDGE_HOUGHLINES_THRESHOLD"] = detector.EdgeHoughThreshold,
                ["COINCIDENT_POINT_ENCOURAGE_DISTANCE"] = detector.CoincidentEncourage,
                ["INTERNAL_LINES_FIND_PEAKS_PARAMETERS"] = Peaks(detector.InternalPeaks), ["EDGE_LINES_FIND_PEAKS_PARAMETERS"] = Peaks(detector.EdgePeaks),
                ["HOMO_CANNY_THRESHOLD"] = new[] { detector.Canny.Low, detector.Canny.High },
                ["HOMO_EDGE_COLOR_RANGE"] = new[] { detector.EdgeColor.Low, detector.EdgeColor.High },
                ["MID_DIFF_RANGE_H"] = new[] { detector.MidHorizontal.Low, detector.MidHorizontal.High },
                ["MID_DIFF_RANGE_V"] = new[] { detector.MidVertical.Low, detector.MidVertical.High } });
            if (expected["MAP_MYSTERY_HAS_CARRIER"] is not null) actual!["MAP_MYSTERY_HAS_CARRIER"] = config.MysteryHasCarrier;
            if (expected.AsObject().ContainsKey("SUBMARINE")) actual!["SUBMARINE"] = expected["SUBMARINE"] is null ? null : JsonValue.Create(config.Submarine);
            if (expected.AsObject().ContainsKey("MAP_SWIPE_MULTIPLY"))
            {
                var camera = new MapCameraRules().WithChapter(config.SwipeMultipliers); camera.Validate();
                actual!["MAP_SWIPE_MULTIPLY"] = JsonSerializer.SerializeToNode(new[] { camera.Multiply.X, camera.Multiply.Y });
                actual["MAP_SWIPE_MULTIPLY_MINITOUCH"] = JsonSerializer.SerializeToNode(new[] { camera.MultiplyMinitouch.X, camera.MultiplyMinitouch.Y });
                actual["MAP_SWIPE_MULTIPLY_MAATOUCH"] = JsonSerializer.SerializeToNode(new[] { camera.MultiplyMaaTouch.X, camera.MultiplyMaaTouch.Y });
            }
            if (expected.AsObject().ContainsKey("HOMO_STORAGE"))
            {
                var storage = detector.Storage;
                actual!["HOMO_STORAGE"] = storage is null ? null : JsonSerializer.SerializeToNode(new object[] {
                    new[] { storage.GridSize.X, storage.GridSize.Y },
                    new[] { storage.Corners.TopLeft, storage.Corners.TopRight, storage.Corners.BottomLeft, storage.Corners.BottomRight }
                        .Select(point => new[] { point.X, point.Y }).ToArray() });
                actual["MAP_ENSURE_EDGE_INSIGHT_CORNER"] = new MapCameraRules().WithChapter(config.SwipeMultipliers, config.MapEdgeCorner).EdgeCorner;
                actual["MAP_HAS_MYSTERY"] = config.HasMystery;
            }
            if (expected.AsObject().ContainsKey("DETECTION_BACKEND"))
                actual!["DETECTION_BACKEND"] = detector.Backend.ToString().ToLowerInvariant();
            Check(JsonNode.DeepEquals(actual, expected), "Inherited chapter config differs: " + rule.Id + ": " + actual);
        }
    }
    private static async Task ChapterTwoCampaignsAsync(int chapter = 2)
    {
        foreach (var id in Enumerable.Range(1, 4).Select(i => $"campaign_main/campaign_{chapter}_{i}"))
        {
            var rule = RuleCatalog.Create(id);
            bool carrier = rule.Configure(new()).MysteryHasCarrier;
            var carrierTargets = new Dictionary<int, Cell>();
            var completedCombats = new List<CombatFlowResult>();
            Host? host = null;
            host = new Host { ObservationFactory = (_, position, mode) =>
            {
                var state = host!.Camera!.State;
                Cell start = state.Fleet1Location ?? state.Cells.First(cell => cell.IsSpawnPoint).Location;
                var shadow = new CampaignState(rule.Map);
                shadow.InitializeMapData(new(PoorMapData: true));
                shadow.Fleet1Location = start; shadow.RefreshFleetPaths(new() { HasAmbush = false });
                bool boss = state.BattleCount >= rule.Map.ExpectedBattles - 1;
                var target = mode == MapScanMode.Carrier && carrierTargets.TryGetValue(state.CarrierCount, out var priorCarrier)
                    ? shadow[priorCarrier] : shadow.Cells.Where(cell => cell.Location != start && cell.IsAccessible &&
                    (mode == MapScanMode.Carrier ? cell.IsSea && !cell.MayEnemy && !cell.MayMystery && !cell.MayAmmo && state[cell.Location].IsSea : boss ? cell.MayBoss : cell.MayEnemy))
                    .OrderBy(cell => cell.Cost).First();
                if (mode == MapScanMode.Carrier) carrierTargets[state.CarrierCount] = target.Location;
                var observations = new List<MapCellObservation> { new(new(start.Column - position.Column, start.Row - position.Row), new(IsFleet: true, IsCurrentFleet: true)),
                    new(new(target.Location.Column - position.Column, target.Location.Row - position.Row),
                        boss && mode != MapScanMode.Carrier ? new(IsBoss: true) : new(IsEnemy: true, EnemyScale: 1)) };
                int pendingMysteries = rule.Map.Waves.Where(wave => wave.Battle <= state.BattleCount).Sum(wave => wave.Mystery) - state.MysteryCount;
                if ((carrier || rule is Alas.Engine.Rules.Main.ChapterSevenRule or Alas.Engine.Rules.Main.ChapterEightRule or Alas.Engine.Rules.Main.ChapterNineRule or Alas.Engine.Rules.Main.ChapterTenRule) && pendingMysteries > 0)
                {
                    foreach (var mystery in shadow.Cells.Where(cell => cell.MayMystery && cell.Location != start).Take(pendingMysteries))
                        observations.Add(new(new(mystery.Location.Column - position.Column, mystery.Location.Row - position.Row), new(IsMystery: true)));
                }
                return new(observations, position, new(0, 0), mode);
            }, CombatFactory = (camera, config, refocus) =>
            {
                var scanner = new MapScanner(camera.State, camera, camera.Clock);
                var movement = new MapMovement(camera.State, config, camera, () => new(camera, camera.State, camera.InMapAsync, camera.Clock,
                    carrier ? new CarrierCampaignProbe(camera) : new Probe(camera),
                    carrier ? new CarrierSequenceHandler(camera) { OnCombat = completedCombats.Add } : new Handler(camera, completedCombats.Add),
                    new MapCombatRecovery(camera.State, camera, refocus).RecoverAsync),
                    waitForInfoBar: _ => ValueTask.CompletedTask, carrierScanner: scanner);
                return new(camera.State, config, movement, scanner);
            } };
            var execution = new CampaignExecution(rule, new() { EmotionMode = CampaignEmotionMode.Ignore, UseFleetLock = false },
                (state, config) => new InMapCampaignOperations(host, state, config, default, rule));
            var exit = await execution.RunAsync();
            var operations = (InMapCampaignOperations)execution.Context.Operations;
            // A late carrier can block the boss route. Count completed synthetic encounters
            // independently instead of assuming the boss spawn wave is the final battle count.
            Check(exit == CampaignLoopExit.Ended && ReferenceEquals(execution.Context.State.Rule, rule) &&
                execution.Context.State.BattleCount == completedCombats.Count(combat => combat.Return == CombatReturn.InMap) &&
                execution.Context.State.BattleCount >= rule.Map.ExpectedBattles - 1 &&
                execution.Context.State.BattleCount <= rule.Map.ExpectedBattles - 1 + carrierTargets.Count &&
                completedCombats.Count(combat => combat.Return == CombatReturn.InStage) == 1 && operations.StageReturn is not null,
                $"Compiled chapter failed its C# campaign/movement/boss-return composition: {id}; exit={exit}, battles={execution.Context.State.BattleCount}, expected={rule.Map.ExpectedBattles - 1}, stage={operations.StageReturn is not null}, mysteries={execution.Context.State.MysteryCount}, carriers={execution.Context.State.CarrierCount}");
            if (carrier)
            {
                int expected = rule.Map.Waves.Sum(wave => wave.Mystery);
                Check(execution.Context.State.MysteryCount == expected && execution.Context.State.CarrierCount == expected &&
                    execution.Context.State.CarrierScans.Count == expected && execution.Context.State.CarrierScans.All(scan =>
                        scan.NewEnemies.Except(scan.Scan.Predictions).SequenceEqual([carrierTargets[scan.CarrierCount]])),
                    $"Compiled chapter omitted declared mystery waves/carrier scanning: {id}; expected={expected}, mysteries={execution.Context.State.MysteryCount}, carriers={execution.Context.State.CarrierCount}, scans={JsonSerializer.Serialize(execution.Context.State.CarrierScans)}");
            }
            if (rule is Alas.Engine.Rules.Main.Campaign64)
                Check(operations.AmmoPickups.Count == 1 && operations.AmmoPickups[0].ExpectedRecovered == 3,
                    "Compiled 6-4 omitted the native pre-boss supply pickup");
            if (rule is Alas.Engine.Rules.Main.ChapterSevenRule or Alas.Engine.Rules.Main.ChapterEightRule)
                Check(execution.Context.State.MysteryCount == rule.Map.Waves.Sum(wave => wave.Mystery), "Compiled chapter omitted declared mysteries: " + id);
            if (rule is Alas.Engine.Rules.Main.Campaign71)
                Check(host.RefocusPresets.SequenceEqual([(-3, -2)]), "Compiled 7-1 omitted boss camera preset");
            if (rule is Alas.Engine.Rules.Main.Campaign74)
                Check(operations.AmmoPickups.Count > 0 && operations.AmmoPickups[0].ExpectedRecovered == 3,
                    "Compiled 7-4 omitted third-battle supply pickup");
            if (rule is Alas.Engine.Rules.Main.Campaign94)
                Check(operations.AmmoPickups.Count == 1 && operations.AmmoPickups[0].ExpectedRecovered == 3,
                    "Compiled 9-4 omitted boss-fleet supply pickup");
            if (rule is Alas.Engine.Rules.Main.Campaign112)
                Check(host.RefocusPresets.SequenceEqual([(-3, -2)]), "Compiled 11-2 omitted boss camera preset");
            if (rule is Alas.Engine.Rules.Main.Campaign124)
                Check(operations.AmmoPickups.Count > 0 && operations.AmmoPickups[0].ExpectedRecovered == 3,
                    "Compiled 12-4 omitted post-third-battle supply pickup");
            if (rule is Alas.Engine.Rules.Main.Campaign92)
                Check(execution.Context.State.MysteryCount == 0 && execution.Context.State.Cells.Any(cell => cell.IsMystery),
                    "Compiled 9-2 added mystery collection absent from its native hooks");
        }
    }
}
