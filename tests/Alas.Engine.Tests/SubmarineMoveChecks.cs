using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static partial class SubmarineMoveChecks
{
    private sealed record Case(string Shape, string Tiles, string Origin, string Boss, string Mode, int Fleet, string Distance);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Rejects<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    internal static CampaignState State(string tiles = "SP -- -- -- --\n__ -- -- -- MB\n-- -- -- -- --")
    {
        var state = new CampaignState(new MapDefinition("E3", tiles, [], [], []));
        state.InitializeMapData(new()); state.Fleet1Location = new(1, 1); state.SubmarineLocation = new(1, 2);
        state.RefreshFleetPaths(new()); return state;
    }
    public static async Task RunAsync(string python, string upstream, string artifacts)
    {
        Directory.CreateDirectory(artifacts);
        var cases = new List<Case>();
        foreach (var mode in Enum.GetValues<SubmarineMode>())
        foreach (int fleet in new[] { 0, 1 })
        foreach (string distance in new[] { "to_boss_position", "1_grid_to_boss", "2_grid_to_boss", "use_open_ocean_support" })
        foreach (string origin in new[] { "A2", "D2", "E2" })
        foreach (string tiles in new[] { "SP -- -- -- --\n__ -- -- -- MB\n-- -- -- -- --",
            "SP -- ++ -- --\n__ -- ++ -- MB\n-- -- -- -- --", "SP -- -- ++ ++\n__ -- ++ ++ ++\n-- -- -- ++ ++",
            "SP -- -- -- --\n-- -- -- -- MB\n-- -- -- -- --" })
            cases.Add(new("E3", tiles, origin, "E2", mode.Name(), fleet, distance));
        string input = Path.Combine(artifacts, "move-input.json"), output = Path.Combine(artifacts, "move-reference.json");
        await File.WriteAllTextAsync(input, JsonSerializer.Serialize(cases, TaskQueue.Json));
        var run = await new ProcessRunner().RunAsync(python,
            [Path.Combine(AppContext.BaseDirectory, "native_submarine_move_reference.py"), upstream, input, output], TimeSpan.FromMinutes(2));
        Check(run.ExitCode == 0, "Native submarine move oracle failed: " + run.Error);
        var native = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
        foreach (var source in new[] { SubmarineMovement.FleetSource, CampaignAutoSearchSettings.Source, CampaignStrategy.Source })
            Check(native["sources"]![source.Path]!.GetValue<string>() == source.Sha256 &&
                Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(upstream, source.Path)))) == source.Sha256,
                "Submarine source drift: " + source.Path);
        for (int i = 0; i < cases.Count; i++)
        {
            var sample = cases[i]; var reference = native["results"]![i]!;
            var state = State(sample.Tiles); state.SubmarineLocation = Cell.Parse(sample.Origin);
            var costs = state.Cells.Select(c => (c.Cost, c.Cost1, c.Cost2, c.Connection)).ToArray();
            var configuration = new CampaignConfiguration { Submarine = sample.Fleet, SubmarineMode = SubmarineRules.Parse(sample.Mode),
                SubmarineDistanceToBoss = sample.Distance };
            var selected = SubmarineMovement.SelectTarget(state, Cell.Parse(sample.Boss), configuration);
            Check(selected?.ToString() == reference["target"]?.GetValue<string>(), "Submarine target differs: " + i);
            Check(costs.SequenceEqual(state.Cells.Select(c => (c.Cost, c.Cost1, c.Cost2, c.Connection))), "Submarine targeting left fleet path costs changed");
            foreach (bool boss in new[] { false, true })
            {
                string expected = reference[boss ? "boss" : "ordinary"]?.GetValue<string>() ?? (sample.Fleet == 0 ? "do_not_use" : sample.Mode);
                Check(configuration.CombatMode(boss).Name() == expected, "Submarine encounter mode differs from native");
            }
        }
        await SettingsAsync(native["settings"]!.AsArray());
        await MovementAsync();
        await ReportsAsync(python, upstream, artifacts);
        await CampaignFleetSetupChecks.RunAsync();
        await CampaignStageSelectorChecks.RunAsync(upstream);
        await MapViewChecks.SubmarineMoveCameraAsync(upstream);
        foreach (JsonNode? value in new JsonNode?[] { null, JsonValue.Create("invalid") })
            await Rejects<ArgumentException>(() => { new CampaignRunTask().Validate(new() { ["campaign"] = "campaign_main/campaign_13_1",
                ["fleet1"] = 1, ["fleet2"] = 0, ["submarine"] = 1, ["submarineDistanceToBoss"] = value }); return Task.CompletedTask; });
        Console.WriteLine($"Submarine BOSS: {cases.Count} native target/mode comparisons, 256 server setting masks, movement and failure traces passed offline; no real sortie.");
    }

    private static async Task MovementAsync()
    {
        foreach (string scenario in new[] { "arrow", "already", "fleet", "retry", "stale", "never", "cancel", "popup_failure" })
        {
            var probe = new MoveProbe(scenario); var state = State();
            var movement = new SubmarineMovement(probe, probe, probe, () => probe.FrameSequence);
            var configuration = new CampaignConfiguration { Submarine = 1, SubmarineMode = SubmarineMode.BossOnly };
            var operation = movement.MoveNearBossAsync(state, new(5, 2), configuration).AsTask();
            bool failure = scenario is "stale" or "never" or "cancel" or "popup_failure";
            if (scenario == "stale") await Rejects<InvalidDataException>(() => operation);
            else if (scenario == "never") await Rejects<TimeoutException>(() => operation);
            else if (scenario == "cancel") await Rejects<OperationCanceledException>(() => operation);
            else if (scenario == "popup_failure") await Rejects<IOException>(() => operation);
            else
            {
                var result = await operation ?? throw new InvalidOperationException("Missing relocation result");
                Check(result.Moved == (scenario is "arrow" or "retry") && result.Target == new Cell(3, 2), "Wrong move selection result");
                Check(probe.Page == "map" && !probe.View && probe.Trace[^1] == "STRATEGY_OPENED", "Relocation did not hide the zone and close strategy");
                Check(probe.Trace.Contains(result.Moved ? "SUBMARINE_MOVE_CONFIRM" : "SUBMARINE_MOVE_CANCEL"), "Relocation used wrong commit button");
                Check(probe.Taps == (scenario == "retry" ? 2 : 1) && probe.Recoveries == (scenario == "retry" ? 1 : 0), "Relocation retry differed");
                Check(movement.Evidence.Single() is { Phase: "completed", ReturnedFrame: not null } evidence && evidence.ReturnedFrame > evidence.SelectionFrame,
                    "Move completion did not preserve fresh return evidence");
            }
            foreach (var evidence in movement.Evidence) RunReport.ValidateSubmarineMove(evidence);
            Check(state.MovementInvalidated == failure && probe.Invalidated == failure, "Failed move left usable physical state");
            Check(state.SubmarineLocation == new Cell(1, 2), "Movement changed native localization semantics");
        }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var untouched = new MoveProbe("arrow");
        await Rejects<OperationCanceledException>(() => new SubmarineMovement(untouched, untouched, untouched, () => untouched.FrameSequence)
            .MoveNearBossAsync(State(), new(5, 2), new() { Submarine = 1, SubmarineMode = SubmarineMode.BossOnly }, cancelled.Token).AsTask());
        Check(untouched.Trace.Count == 0 && untouched.Taps == 0, "Cancelled relocation touched input");
        var valid = new SubmarineMoveEvidence(new(5, 2), new(1, 2), new(3, 2), "completed", 1, 5, 8, true);
        foreach (var corrupt in new[] { valid with { Attempts = 0 }, valid with { SelectionFrame = null }, valid with { ReturnedFrame = 5 },
            valid with { Phase = "unknown" }, valid with { Moved = null }, valid with { Phase = "opening" } })
            await Rejects<InvalidDataException>(() => { RunReport.ValidateSubmarineMove(corrupt); return Task.CompletedTask; });
    }

    private sealed class MoveProbe(string scenario) : AppearanceProbe(GameServer.Cn, null), ISubmarineMoveCamera, IPopupHandler
    {
        public long FrameSequence { get; private set; } = 1;
        public string Page { get; private set; } = "map";
        public bool View { get; private set; } = true;
        public bool Invalidated { get; private set; }
        public int Taps { get; private set; }
        public int Recoveries { get; private set; }
        public List<string> Trace { get; } = [];
        public override ValueTask ScreenshotAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); FrameSequence++; Time.Advance(.25); return ValueTask.CompletedTask; }
        public override ValueTask<bool> AppearsAsync(AssetRule asset, ButtonOffset offset = default, double interval = 0,
            double similarity = .85, int threshold = 10, TemplatePreprocessing preprocessing = TemplatePreprocessing.Color, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (interval > 0 && !Timer(asset, interval, true).Reached()) return ValueTask.FromResult(false);
            bool found = asset.Name switch {
                "IN_MAP" => Page == "map", "STRATEGY_OPENED" or "SUBMARINE_MOVE_ENTER" => Page == "strategy",
                "SUBMARINE_MOVE_CONFIRM" or "SUBMARINE_MOVE_CANCEL" => Page == "moving",
                "SUBMARINE_VIEW_ON" => Page == "strategy" && View, "SUBMARINE_VIEW_OFF" => Page == "strategy" && !View, _ => false };
            if (found && interval > 0) Timer(asset).Reset();
            return ValueTask.FromResult(found);
        }
        public override ValueTask ClickAsync(AssetRule asset, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Trace.Add(asset.Name);
            switch (asset.Name)
            {
                case "STRATEGY_OPEN": Check(Page == "map", "Open strategy outside map"); Page = "strategy"; break;
                case "SUBMARINE_MOVE_ENTER": Page = "moving"; break;
                case "SUBMARINE_MOVE_CONFIRM": case "SUBMARINE_MOVE_CANCEL": Page = "popup"; break;
                case "SUBMARINE_VIEW_ON": View = false; break;
                case "STRATEGY_OPENED": Page = "map"; break;
                default: throw new InvalidOperationException("Unexpected strategy click");
            }
            return ValueTask.CompletedTask;
        }
        public ValueTask<bool> ConfirmAsync(CancellationToken token)
        {
            if (Page != "popup") return ValueTask.FromResult(false);
            if (scenario == "popup_failure") throw new IOException("Synthetic popup failure");
            Page = "strategy"; Trace.Add("popup"); return ValueTask.FromResult(true);
        }
        public ValueTask PrepareSubmarineTapAsync(Cell target, CancellationToken token = default)
        { Check(Page == "moving", "Target prepared outside move mode"); return ValueTask.CompletedTask; }
        public ValueTask TapCellAsync(Cell target, CancellationToken token = default)
        { Taps++; return ValueTask.CompletedTask; }
        public ValueTask RefreshImageAsync(CancellationToken token = default)
        {
            if (scenario == "cancel") throw new OperationCanceledException();
            if (scenario == "stale") return ValueTask.CompletedTask;
            return ScreenshotAsync(token);
        }
        public ValueTask<bool> PredictSubmarineAsync(Cell target, CancellationToken token = default) => ValueTask.FromResult(scenario == "already");
        public ValueTask<bool> PredictSubmarineMoveAsync(Cell target, CancellationToken token = default)
            => ValueTask.FromResult(scenario is "arrow" or "popup_failure" || scenario == "retry" && Taps > 1);
        public ValueTask<FleetMarker> ReadFleetMarkerAsync(Cell target, CancellationToken token = default) => ValueTask.FromResult(new FleetMarker(scenario == "fleet", false));
        public ValueTask EnsureEdgesAsync(bool skipFirstUpdate, CancellationToken token)
        { Check(!skipFirstUpdate && Page == "moving", "Bad recovery mode"); Recoveries++; return ValueTask.CompletedTask; }
        public void Invalidate() => Invalidated = true;
    }
}
