using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static partial class CampaignMapCombatChecks
{
    private sealed record OverlapSample(int Active, int Step, bool Turning, string Kind);
    public static async Task FleetOverlapChecksAsync(string python, string upstream, string artifacts)
    {
        Directory.CreateDirectory(artifacts);
        var samples = (from active in new[] { 1, 2 } from step in new[] { 0, 1, 2, 3 }
            from turning in new[] { false, true } from kind in new[] { "sea", "enemy", "siren", "mystery", "ammo" }
            select new OverlapSample(active, step, turning, kind)).ToArray();
        string input = Path.Combine(artifacts, "overlap-input.json"), output = Path.Combine(artifacts, "overlap-native.json");
        await File.WriteAllTextAsync(input, JsonSerializer.Serialize(samples, TaskQueue.Json));
        var process = await new ProcessRunner().RunAsync(python,
            [Path.Combine(AppContext.BaseDirectory, "native_fleet_overlap_reference.py"), upstream, input, output], TimeSpan.FromMinutes(1));
        Check(process.ExitCode == 0, "Native shared-fleet replay failed: " + process.Error);
        var native = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
        foreach (var source in new[] { CampaignState.InitializationSource, MapPathfinder.Source, CellState.Source })
            Check(native["sources"]![source.Path]!.GetValue<string>() == source.Sha256, "Shared-fleet source drift: " + source.Path);
        for (int i = 0; i < samples.Length; i++)
        {
            var sample = samples[i];
            var config = new CampaignConfiguration { Fleet2 = 2, HasAmbush = sample.Turning, HasFleetStep = sample.Step > 0,
                Fleet1Step = sample.Step, Fleet2Step = sample.Step, EmotionMode = CampaignEmotionMode.Ignore };
            var state = Prepare(new("G1", "SP -- -- SP -- -- --", ["D1"], [], []));
            state.FleetIndex = sample.Active;
            state.Fleet1Location = Cell.Parse(sample.Active == 1 ? "A1" : "D1");
            state.Fleet2Location = Cell.Parse(sample.Active == 1 ? "D1" : "A1");
            var target = state[Cell.Parse("D1")];
            if (sample.Kind == "enemy") target.IsEnemy = target.MayEnemy = true;
            if (sample.Kind == "siren") target.IsSiren = target.MaySiren = true;
            if (sample.Kind == "mystery") target.IsMystery = target.MayMystery = true;
            if (sample.Kind == "ammo") target.IsAmmo = target.MayAmmo = true;
            state.RefreshFleetPaths(config);
            var taps = new List<string>();
            var camera = new Camera(state) { Trace = trace => { if (trace.StartsWith("tap:", StringComparison.Ordinal)) taps.Add(state.FleetIndex + ":" + trace[4..]); } };
            var movement = new MapMovement(state, config, camera, () => new(camera, state, camera.InMapAsync, camera.Clock, new Probe(camera), new Handler(camera)));
            async Task Move(string destination, string kind = "sea")
            {
                var route = state.Paths.FindRoute(Cell.Parse(destination), sample.Step, sample.Turning);
                Check(route.IsReachable, "Shared-fleet route became unreachable");
                foreach (var node in route.Waypoints)
                {
                    bool last = node == route.Waypoints[^1];
                    var result = last && kind is "enemy" or "siren" ? await movement.FightAsync(node) :
                        last && kind == "mystery" ? await movement.CollectMysteryAsync(node) : await movement.RepositionAsync(node);
                    Check(result.Outcome == MapMoveOutcome.Committed, "Shared-fleet route did not commit its observed arrival");
                }
            }
            var snapshots = new JsonArray();
            await Move("D1", sample.Kind); snapshots.Add(Snapshot());
            Check(state.Fleet1Location == Cell.Parse("D1") && state.Fleet2Location == Cell.Parse("D1") &&
                state.Cells.Count(g => g.IsFleet) == 1, "Arrival overwrote the stationary fleet or invented a second grid");
            await Move("G1"); snapshots.Add(Snapshot());
            Check(state[Cell.Parse("D1")].IsFleet && state.Cells.Count(g => g.IsFleet) == 2,
                "Departure erased the stationary fleet from the shared origin");
            state.FleetIndex = 3 - sample.Active; state.RefreshFleetPaths(config);
            await Move("F1"); snapshots.Add(Snapshot());
            Check(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(taps), native["results"]![i]!["taps"]) &&
                JsonNode.DeepEquals(snapshots, native["results"]![i]!["snapshots"]),
                $"Native shared-fleet state differs at {i}/{sample}: {snapshots}; native={native["results"]![i]}");
            JsonNode Snapshot() => JsonSerializer.SerializeToNode(new
            {
                active = state.FleetIndex, fleet1 = state.Fleet1Location!.ToString(), fleet2 = state.Fleet2Location!.ToString(),
                battles = state.BattleCount, sirens = state.SirenCount, mysteries = state.MysteryCount, ammo = state.FleetAmmo, stock = state.AmmoCount,
                flags = state.Cells.Select(g => new[] { g.IsFleet, g.IsEnemy, g.IsSiren, g.IsMystery, g.IsAmmo, g.IsCleared }),
                costs = state.Cells.Select(g => new[] { g.Cost, g.Cost1, g.Cost2 })
            })!;
        }
        await FleetOverlapFailuresAsync();
        await FleetOverlapSessionChecks.RunAsync(python, upstream, artifacts);
        Console.WriteLine($"Fleet overlap: {samples.Length} native goto/_goto paths and 240 join/depart/switch state snapshots, unknown-fleet, failure and real-session boundaries passed; synthetic I/O only.");
    }

    private static async Task FleetOverlapFailuresAsync()
    {
        foreach (int fleet in new[] { 1, 2 })
        foreach (string failure in new[] { "tap", "stale", "timeout", "cancel", "losing", "missing", "unplanned", "unknown" })
        {
            var config = new CampaignConfiguration { Fleet2 = 2, HasAmbush = false };
            var state = Prepare(new("C1", "SP -- SP", ["B1"], [], []));
            state.FleetIndex = fleet;
            state.Fleet1Location = new(fleet == 1 ? 1 : 3, 1); state.Fleet2Location = new(fleet == 1 ? 3 : 1, 1);
            var target = Cell.Parse("C1"); state.RefreshFleetPaths(config);
            if (failure == "unknown") { target = Cell.Parse("B1"); state[target].IsFleet = true; }
            bool fight = failure is "losing" or "missing" or "unplanned";
            if (fight) state[target].IsEnemy = true;
            using var cancel = new CancellationTokenSource();
            var camera = new Camera(state) { Failure = failure, Cancellation = cancel,
                Rank = failure == "missing" ? null : failure == "losing" ? CombatRank.C : CombatRank.S };
            var movement = new MapMovement(state, config, camera, () => new(camera, state, camera.InMapAsync, camera.Clock, new Probe(camera), new Handler(camera)));
            Exception? error = null; MapMoveResult? result = null;
            try { result = fight && failure != "unplanned" ? await movement.FightAsync(target, token: cancel.Token) : await movement.MoveAsync(target, token: cancel.Token); }
            catch (Exception caught) { error = caught; }
            Check((error is not null || result!.Outcome != MapMoveOutcome.Committed) && state.BattleCount == 0 &&
                state.Fleet1Location == new Cell(fleet == 1 ? 1 : 3, 1) && state.Fleet2Location == new Cell(fleet == 1 ? 3 : 1, 1) &&
                state[Cell.Parse("A1")].IsFleet && state[Cell.Parse("C1")].IsFleet, "Failed overlap mutated either fleet: " + failure);
            if (failure == "unknown") Check(camera.Taps == 0, "An unidentified fleet marker was treated as known fleet occupancy");
        }
    }
}
