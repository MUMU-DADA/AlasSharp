using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Rules;
using Alas.Engine.Rules.Main;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static partial class CampaignMapCombatChecks
{
    public static async Task ChapterFourteenAsync(string python, string upstream, string artifacts)
    {
        int overlays = 0;
        foreach (var server in Enum.GetValues<GameServer>())
        {
            string output = Path.Combine(artifacts, server.ToString().ToLowerInvariant() + "-chapter14.json");
            var run = await new ProcessRunner().RunAsync(python,
                [Path.Combine(AppContext.BaseDirectory, "native_main_chapter_reference.py"), upstream, server.ToString().ToLowerInvariant(), output, "14"], TimeSpan.FromMinutes(1));
            Check(run.ExitCode == 0, "Native chapter fourteen declarations failed: " + run.Error);
            var native = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
            foreach (var entry in native["chapters"]!.AsArray())
                CompareChapterTwoConfig(RuleCatalog.Create(entry!["id"]!.GetValue<string>()), entry["config"]!);
            overlays += await MapEncounterProbeChecks.OverlayRulesAsync(python, upstream, artifacts, server, native);
        }
        string file = Path.Combine(artifacts, "chapter14-pickups.json");
        var reference = await new ProcessRunner().RunAsync(python,
            [Path.Combine(AppContext.BaseDirectory, "native_chapter_fourteen_reference.py"), upstream, file], TimeSpan.FromMinutes(1));
        Check(reference.ExitCode == 0, "Native pickup/map initialization failed: " + reference.Error);
        var data = JsonNode.Parse(await File.ReadAllTextAsync(file))!;
        Check(data["sources"]![CampaignMapCombat.PickupSource.Path]!.GetValue<string>() == CampaignMapCombat.PickupSource.Sha256,
            "Pickup source drifted");
        foreach (var entry in data["maps"]!.AsArray())
        {
            var rule = RuleCatalog.Create(entry!["id"]!.GetValue<string>());
            var state = new CampaignState(rule.Map, rule);
            state.RecordFlare(Cell.Parse("A1")); state.RecordLightHouse(Cell.Parse("A1"));
            state.InitializeMapData(new(ClearMode: entry["clear"]!.GetValue<bool>()));
            Check(state.PickedFlares.Count == entry["flares"]!.GetValue<int>() && state.PickedLightHouses.Count == entry["lights"]!.GetValue<int>(),
                "Map initialization retained pickups from another sortie");
            foreach (var cell in entry["cells"]!.AsArray())
            {
                var grid = state[Cell.Parse(cell!["cell"]!.GetValue<string>())];
                Check(grid.MayEnemy == cell["enemy"]!.GetValue<bool>() && grid.MayAmbush == cell["ambush"]!.GetValue<bool>(),
                    "Chapter map override differs: " + rule.Id + ":" + grid.Location);
            }
        }
        foreach (var entry in data["pickups"]!.AsArray()) await PickupReferenceAsync(entry!);
        await PickupMovementAsync();
        await ChapterTwoCampaignsAsync(14, "Light");
        await ChapterFourteenDualFleetAsync();
        Console.WriteLine($"Chapter fourteen: four native Config declarations, {overlays} four-server CV overlays, eight map initializations, {data["pickups"]!.AsArray().Count} pickup traces, raw movement/failed evidence and four compiled campaigns passed offline; no live acceptance.");
    }

    private sealed class PickupRule(MapDefinition map, bool countItems = false) : ChapterFourteenRule
    {
        public override string Id => "test/pickups";
        public override MapDefinition Map => map;
        public override bool CountMysteryItems => countItems;
        public override ImmutableArray<SourceFile> Sources => [];
        protected override IReadOnlyDictionary<int, BattleHook> Hooks { get; } = new Dictionary<int, BattleHook>();
    }

    private static (CampaignState State, CampaignConfiguration Config) PickupState(bool countItems = false)
    {
        var map = new MapDefinition("B1", "SP MM", ["A1"], [], []);
        var rule = new PickupRule(map, countItems);
        var state = new CampaignState(map, rule);
        state.InitializeMapData(new());
        state.Fleet1Location = new(1, 1);
        var config = new CampaignConfiguration { HasAmbush = false, EmotionMode = CampaignEmotionMode.Ignore };
        state.RefreshFleetPaths(config);
        return (state, config);
    }

    private static async Task PickupReferenceAsync(JsonNode entry)
    {
        var (state, config) = PickupState();
        Cell target = new(2, 1);
        bool flare = entry["kind"]!.GetValue<string>() == "flare";
        string failure = entry["failure"]!.GetValue<string>();
        bool accessible = entry["accessible"]!.GetValue<bool>();
        bool picked = entry["picked"]!.GetValue<bool>();
        if (picked) { if (flare) state.RecordFlare(target); else state.RecordLightHouse(target); }
        if (!accessible) state[target].Cost = 9999;
        var trace = new List<string>();
        var camera = new Camera(state) { Trace = action =>
        {
            if (!action.StartsWith("tap:", StringComparison.Ordinal)) return;
            trace.Add("goto:" + target);
            if (failure == "goto") throw new IOException("Synthetic goto failure");
            if (failure == "moved") throw new MapEnemyMovedException();
        } };
        var scanner = new MapScanner(state, camera, camera.Clock);
        var movement = new MapMovement(state, config, camera, () => new(camera, state, camera.InMapAsync,
            camera.Clock, new Probe(camera), new Handler(camera)));
        var combat = new CampaignMapCombat(state, config, movement, scanner, waitForInfoBar: _ =>
        {
            trace.Add("wait");
            if (failure == "wait") throw new IOException("Synthetic info-bar failure");
            return ValueTask.CompletedTask;
        });
        bool? value = null; string? error = null;
        try { value = flare ? await combat.PickUpFlareAsync(target) : await combat.PickUpLightHouseAsync(target); }
        catch (IOException) { error = "IOError"; }
        catch (MapEnemyMovedException) { error = "MapEnemyMoved"; }
        Check(value == entry["value"]?.GetValue<bool>() && error == entry["error"]?.GetValue<string>() &&
            state[target].IsFlare == entry["flare"]!.GetValue<bool>() &&
            trace.SequenceEqual(entry["trace"]!.AsArray().Select(n => n!.GetValue<string>())) &&
            state.PickedFlares.Select(c => c.ToString()).SequenceEqual(entry["flares"]!.AsArray().Select(n => n!.GetValue<string>())) &&
            state.PickedLightHouses.Select(c => c.ToString()).SequenceEqual(entry["lights"]!.AsArray().Select(n => n!.GetValue<string>())) &&
            state.MysteryCount == 0, "Native pickup state/order differs: " + entry);
        if (error is not null) Check(state.MovementInvalidated, "Failed pickup left reusable movement state");
    }

    private sealed class PickupProbe(params MapEncounterKind[] events) : IMapEncounterProbe
    {
        private int _index;
        public ValueTask InitializeAsync(long frameSequence, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<MapEncounterKind> InspectAsync(long frameSequence, CancellationToken token)
            => ValueTask.FromResult(_index < events.Length ? events[_index++] : MapEncounterKind.None);
    }

    private static async Task PickupMovementAsync()
    {
        foreach (bool count in new[] { false, true })
        foreach (bool fight in new[] { false, true })
        foreach (bool stage in new[] { false, true })
        foreach (bool winning in new[] { false, true })
        {
            var (state, config) = PickupState(count);
            Cell target = new(2, 1);
            state[target].IsEnemy = fight;
            state.RefreshFleetPaths(config);
            var camera = new Camera(state) { ReturnStage = stage, Rank = winning ? CombatRank.S : CombatRank.C };
            var movement = new MapMovement(state, config, camera, () => new(camera, state, camera.InMapAsync, camera.Clock,
                new PickupProbe(MapEncounterKind.ItemPopup, MapEncounterKind.Combat), new Handler(camera)));
            var result = fight ? await movement.FightAsync(target) : await movement.VisitAsync(target);
            bool accepted = winning && (!fight || !count);
            Check(result.Outcome == (!accepted ? MapMoveOutcome.UnsupportedEncounter : stage ? MapMoveOutcome.StageReturned : MapMoveOutcome.Committed) &&
                result.Arrival.HandledEncounters.SequenceEqual([MapEncounterKind.ItemPopup, MapEncounterKind.Combat]) &&
                state.BattleCount == (accepted && !stage ? 1 : 0) &&
                state.MysteryCount == (accepted && !stage && count ? 1 : 0), "Pickup plus combat lost evidence, result gate or counting");
        }
        foreach (string action in new[] { "move", "visit", "mystery", "boss", "flare", "lighthouse" })
        {
            var (state, config) = PickupState();
            Cell target = new(2, 1);
            state[target].IsMystery = action == "mystery";
            state[target].MayBoss = action == "boss";
            var camera = new Camera(state);
            var movement = new MapMovement(state, config, camera, () => new(camera, state, camera.InMapAsync, camera.Clock,
                new PickupProbe(MapEncounterKind.ItemPopup, MapEncounterKind.AmmoNotification), new Handler(camera)));
            var combat = new CampaignMapCombat(state, config, movement, new(state, camera, camera.Clock), waitForInfoBar: _ => ValueTask.CompletedTask);
            if (action == "flare") await combat.PickUpFlareAsync(target);
            else if (action == "lighthouse") await combat.PickUpLightHouseAsync(target);
            else
            {
                var result = action switch { "move" => await movement.MoveAsync(target), "visit" => await movement.VisitAsync(target),
                    "mystery" => await movement.CollectMysteryAsync(target), _ => await movement.ProbeBossAsync(target) };
                Check(result.Outcome == MapMoveOutcome.Committed, "Uncounted item rejected by action: " + action);
            }
            Check(state.Fleet1Location == target && state.MysteryCount == 1 && state.BattleCount == 0,
                "Uncounted item suppressed ammo evidence or invented combat: " + action);
        }
        foreach (string failure in new[] { "tap", "timeout", "cancel", "stale" })
        {
            var (state, config) = PickupState();
            using var cancellation = new CancellationTokenSource();
            var camera = new Camera(state) { Failure = failure, Cancellation = cancellation };
            var combat = Create(state, config, camera);
            Exception? error = null;
            try { await combat.PickUpFlareAsync(new(2, 1), cancellation.Token); }
            catch (Exception caught) { error = caught; }
            Check(error is not null && state.PickedFlares.Count == 0 && state.MysteryCount == 0 &&
                state.Fleet1Location == new Cell(1, 1) && state.MovementInvalidated &&
                (failure != "cancel" || error is OperationCanceledException), "Unconfirmed pickup was recorded: " + failure);
        }
        {
            var (state, config) = PickupState();
            var camera = new Camera(state);
            var combat = Create(state, config, camera);
            await combat.PickUpFlareAsync(new(1, 1));
            await combat.PickUpFlareAsync(new(1, 1));
            Check(camera.Taps == 1 && state.PickedFlares.SequenceEqual([new Cell(1, 1)]), "Current-grid or repeated pickup differs");
        }
    }

    private static async Task ChapterFourteenDualFleetAsync()
    {
        var rule = new Campaign142();
        var state = new CampaignState(rule.Map, rule);
        state.InitializeMapData(new());
        var config = rule.Configure(new() { Fleet2 = 2, BossFleet = 2, EmotionMode = CampaignEmotionMode.Ignore });
        state.Fleet1Location = Cell.Parse("H2"); state.Fleet2Location = Cell.Parse("I2"); state.RefreshFleetPaths(config);
        var camera = new Camera(state);
        var switches = new List<int>();
        var combat = Create(state, config, camera, switchFleet: (fleet, _) =>
        { switches.Add(fleet); state.FleetIndex = fleet; state.RefreshFleetPaths(config); return ValueTask.CompletedTask; });
        foreach (var target in new[] { Cell.Parse("H7"), Cell.Parse("A5") })
        { await combat.SwitchFleetAsync(2); await combat.PickUpFlareAsync(target); }
        await combat.SwitchFleetAsync(2); await combat.MoveFleetAsync(Cell.Parse("D6")); await combat.SwitchFleetAsync(1);
        Check(switches.SequenceEqual([2, 1]) && state.PickedFlares.SequenceEqual([Cell.Parse("H7"), Cell.Parse("A5")]) &&
            state.Fleet2Location == Cell.Parse("D6") && state.Fleet1Location == Cell.Parse("H2") && state.MysteryCount == 0,
            "Native repeated fleet_boss property access caused extra switches or lost pickup order");
    }
}
