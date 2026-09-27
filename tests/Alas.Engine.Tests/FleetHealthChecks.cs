using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static class FleetHealthChecks
{
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static async Task Rejects<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }

    public static async Task RunAsync(string python, string upstream, string artifacts)
    {
        string output = Path.Combine(artifacts, "native-health.json");
        var reference = await new ProcessRunner().RunAsync(python,
            [Path.Combine(AppContext.BaseDirectory, "native_health_reference.py"), upstream, output], TimeSpan.FromSeconds(60));
        Check(reference.ExitCode == 0, "Native HP reference failed: " + reference.Error);
        var data = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
        foreach (var source in new[] { FleetHealthRules.Source, CampaignWithdrawal.Source, MapUiRecovery.StageSource,
            new SourceFile("module/base/utils.py", "54a8096f8a91d9c8b5ed1ec5993c78e956bb64076de7c08df1f0fed71367260b") })
            Check(data["sources"]![source.Path]!.GetValue<string>() == source.Sha256, "HP/withdrawal source drift: " + source.Path);

        var measured = new Dictionary<string, double[]>();
        await using var vision = new PythonTemplateVision(python, Path.Combine(AppContext.BaseDirectory, "Imaging/Worker/vision_worker.py"));
        long frame = 0;
        foreach (var sample in data["images"]!.AsArray())
        {
            string file = sample!["file"]!.GetValue<string>();
            var server = Enum.Parse<GameServer>(sample["server"]!.GetValue<string>(), true);
            var image = new ScreenFrame(++frame, DateTimeOffset.UnixEpoch, await File.ReadAllBytesAsync(Path.Combine(artifacts, file)));
            var ui = new AppearanceProbe(server, UiAssets.Handler.IN_MAP.Id);
            var health = new FleetHealthState();
            var reading = await new FleetHealthReader(ui, vision, () => image).ReadAsync(health, 1, new(), default);
            var expected = sample["raw"]!.AsArray().Select(value => value!.GetValue<double>()).ToArray();
            Check(reading.Raw.Zip(expected).All(pair => Math.Abs(pair.First - pair.Second) < 1e-12), "Native HP pixels differ: " + file);
            measured.Add(file, reading.Raw.ToArray());
        }
        int states = 0;
        foreach (var series in data["results"]!.AsArray())
        {
            var health = new FleetHealthState();
            var options = new FleetHealthOptions { UseLowHpRetreat = true, BalanceWeight = series!["weight"]!.GetValue<string>() };
            foreach (var sample in series["states"]!.AsArray())
            {
                int fleet = sample!["fleet"]!.GetValue<int>();
                var reading = health.Commit(fleet, ++frame, measured[sample["image"]!.GetValue<string>()], options);
                Check(reading.Weighted.Zip(sample["weighted"]!.AsArray().Select(value => value!.GetValue<double>()))
                    .All(pair => Math.Abs(pair.First - pair.Second) < 1e-12) &&
                    reading.HasShip.SequenceEqual(sample["hasShip"]!.AsArray().Select(value => value!.GetValue<bool>())),
                    "Native HP weighting/first-slot mask differs");
                int index = 0;
                foreach (double threshold in new[] { 0, .3, .7, 1 })
                    Check(health.RetreatTriggered(fleet, options with { LowHpRetreatThreshold = threshold }) ==
                        sample["retreats"]![index++]!.GetValue<bool>(), "Native retreat decision differs");
                Check(!health.RetreatTriggered(fleet, options with { UseLowHpRetreat = false }), "Disabled HP retreat ran");
                states++;
            }
            health.Reset();
            Check(health.Get(1) is null && health.Get(2) is null && health.Observations.Count == 0, "HP reset leaked a fleet");
        }
        foreach (var sample in data["withdrawal"]!.AsArray())
        {
            var ui = new WithdrawalUi(sample!["frames"]!.AsArray());
            await ui.PrimeAsync();
            var result = await Withdrawal(ui).RunAsync("low_hp", TimeSpan.FromMinutes(1), default);
            // Native trace is unchanged; Engine additionally rechecks the last
            // stage frame after the native info-bar wait before publishing evidence.
            var finalChecks = JsonSerializer.SerializeToNode(ui.Events.TakeLast(2))!.AsArray();
            Check(finalChecks[0]!["asset"]!.GetValue<string>() == "CAMPAIGN_CHECK" &&
                finalChecks[1]!["asset"]!.GetValue<string>() == "$entrance", "Final withdrawal frame was not rechecked");
            var actual = JsonSerializer.SerializeToNode(new { calls = ui.Events.Take(ui.Events.Count - 2).ToArray(), clicks = ui.Clicks, frame = ui.Frame, error = "CampaignEnd" });
            var expected = sample.DeepClone().AsObject(); expected.Remove("frames");
            if (!JsonNode.DeepEquals(actual, expected))
            {
                await File.WriteAllTextAsync(Path.Combine(artifacts, "withdrawal-mismatch.json"),
                    new JsonObject { ["actual"] = actual, ["expected"] = expected }.ToJsonString());
                throw new InvalidOperationException("Withdrawal native trace differs");
            }
            Check(result is { StageConfirmed: true, ExitActions: > 0 } && result.StageSequence > result.StartedSequence,
                "Withdrawal has no confirmed exit evidence");
        }
        await FailureChecksAsync();
        await IntegrationChecksAsync(python);
        Console.WriteLine($"Fleet HP: {measured.Count} actual-CV images, {states} native fleet/weight/mask states and {states * 5} retreat decisions, 6 native withdrawal traces, failure/atomicity, movement/combat/contract checks passed; no real device verification.");
    }

    private static CampaignWithdrawal Withdrawal(WithdrawalUi ui)
        => new(ui, ui, new MapUiRecovery(ui, ui, ui, ui, ui), () => ui.Stale ? 1 : ui.Frame + 1);

    private static async Task FailureChecksAsync()
    {
        var options = new FleetHealthOptions { UseLowHpRetreat = true };
        var health = new FleetHealthState();
        health.Commit(1, 1, [.9, 0, 0, .9, .8, 0], options);
        var initial = health.Get(1);
        var image = new ScreenFrame(2, DateTimeOffset.UnixEpoch, new byte[] { 1 });
        var ui = new AppearanceProbe(GameServer.Cn, UiAssets.Handler.IN_MAP.Id);
        foreach (var invalid in new[] { new double[11], Enumerable.Repeat(double.NaN, 12).ToArray(),
            Enumerable.Repeat(1.1, 12).ToArray() })
            await Rejects<InvalidDataException>(async () => await new FleetHealthReader(ui,
                new Vision(() => ValueTask.FromResult<IReadOnlyList<double>>(invalid)), () => image).ReadAsync(health, 1, options, default));
        await Rejects<IOException>(async () => await new FleetHealthReader(ui,
            new Vision(() => throw new IOException("CV failed")), () => image).ReadAsync(health, 1, options, default));
        var changed = new Vision(() => { image = image with { Sequence = image.Sequence + 1 }; return ValueTask.FromResult<IReadOnlyList<double>>(new double[12]); });
        await Rejects<InvalidDataException>(async () => await new FleetHealthReader(ui, changed, () => image).ReadAsync(health, 1, options, default));
        await Rejects<InvalidDataException>(async () => await new FleetHealthReader(new AppearanceProbe(GameServer.Cn, null),
            new Vision(() => throw new InvalidOperationException("Do not read HP on a result screen")), () => image).ReadAsync(health, 1, options, default));
        await Rejects<InvalidDataException>(() => { health.Commit(1, 1, [.1, 0, 0, .1, .1, 0], options); return Task.CompletedTask; });
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Rejects<OperationCanceledException>(async () => await new FleetHealthReader(ui, changed, () => image)
            .ReadAsync(health, 1, options, cancelled.Token));
        Check(ReferenceEquals(health.Get(1), initial) && health.Observations.Count == 1, "Failed read published partial HP state");
        health.Commit(1, 5, [0, 0, 0, 0, 0, 0], options);
        Check(health.RetreatTriggered(1, options) && health.Get(1)!.HasShip[0], "Destroyed ship disappeared from occupied slots");
        await Rejects<InvalidOperationException>(() => { health.RetreatTriggered(2, options); return Task.CompletedTask; });
        foreach (string weight in new[] { "0,0,0", "-1,2,3", "1,2", "1,2,NaN", "1.2,2,3", "" })
            await Rejects<ArgumentException>(() => { (options with { BalanceWeight = weight }).Weights(); return Task.CompletedTask; });
        foreach (double threshold in new[] { -.1, 1.1, double.NaN })
            await Rejects<ArgumentException>(() => { (options with { LowHpRetreatThreshold = threshold }).Weights(); return Task.CompletedTask; });

        foreach (var frames in new[] { new[] { "CAMPAIGN_CHECK", "$entrance" }, new[] { "WITHDRAW" } })
        {
            var driver = new WithdrawalUi(JsonSerializer.SerializeToNode(new[] { frames })!.AsArray());
            await driver.PrimeAsync();
            if (frames.Length == 2)
                await Rejects<InvalidDataException>(async () => await Withdrawal(driver).RunAsync("low_hp", TimeSpan.FromSeconds(5), default));
            else
            {
                await Rejects<TimeoutException>(async () => await Withdrawal(driver).RunAsync("low_hp", TimeSpan.FromSeconds(2), default));
                driver.Stale = true;
                await Rejects<InvalidDataException>(async () => await Withdrawal(driver).RunAsync("low_hp", TimeSpan.FromSeconds(5), default));
            }
            await Rejects<OperationCanceledException>(async () => await Withdrawal(driver).RunAsync("low_hp", TimeSpan.FromSeconds(5), cancelled.Token));
        }
    }

    private static async Task IntegrationChecksAsync(string python)
    {
        var config = new CampaignConfiguration { HasAmbush = false, Health = new() { UseLowHpRetreat = true } };
        var state = new CampaignState(new MapDefinition("B1", "SP --", ["A1"], ["A1"], []));
        state.InitializeMapData(new(PoorMapData: true));
        state.Fleet1Location = new(1, 1); state[new(1, 1)].IsFleet = true;
        state.Paths.ComputeFleetCosts([new(1, state.Fleet1Location)], new(1, 1), false);
        state.Health.Commit(1, 1, [.9, 0, 0, .9, 0, 0], config.Health);
        int reads = 0;
        var handler = new MapCombatHandler(_ => ValueTask.FromResult(new CombatFlowResult(CombatReturn.InMap, null, false, false, 1)),
            readHealth: _ => { reads++; state.Health.Commit(1, 2, [.1, 0, 0, .2, 0, 0], config.Health); return ValueTask.CompletedTask; });
        await handler.HandleAsync(MapEncounterKind.Combat, default);
        Check(reads == 1 && state.Health.RetreatTriggered(1, config.Health), "Map combat did not update fleet HP");
        await new MapCombatHandler(_ => ValueTask.FromResult(new CombatFlowResult(CombatReturn.InStage, null, false, false, 1)),
            readHealth: _ => throw new InvalidOperationException("No HP on stage page")).HandleAsync(MapEncounterKind.Combat, default);
        await Rejects<IOException>(async () => await new MapCombatHandler(
            _ => ValueTask.FromResult(new CombatFlowResult(CombatReturn.InMap, null, false, false, 1)),
            readHealth: _ => throw new IOException("HP update failed")).HandleAsync(MapEncounterKind.Combat, default));
        var camera = new NoTapCamera();
        var movement = new MapMovement(state, config, camera, () => throw new InvalidOperationException("Low HP started a move"),
            withdraw: _ => ValueTask.FromResult(new CampaignWithdrawalEvidence("low_hp", 2, 5, 1, true)));
        await Rejects<CampaignEndedException>(async () => await movement.MoveAsync(new(2, 1)));
        Check(camera.Invalidated && state.Withdrawal is not null && state.BattleCount == 0 && state.Fleet1Location == new Cell(1, 1),
            "HP retreat moved the fleet, incremented battle state, or failed to invalidate camera");

        var confirmedWithdrawal = state.Withdrawal ?? throw new InvalidOperationException("Withdrawal evidence missing");
        state.Withdrawal = null;
        var failedCamera = new NoTapCamera();
        await Rejects<IOException>(async () => await new MapMovement(state, config, failedCamera,
            () => throw new InvalidOperationException("Failed withdrawal started a move"),
            withdraw: _ => throw new IOException("Exit click failed")).MoveAsync(new(2, 1)));
        Check(failedCamera.Invalidated && state.Withdrawal is null && state.BattleCount == 0, "Failed withdrawal published success");

        var rule = RuleCatalog.Create("campaign_main/campaign_1_1");
        foreach (var exit in new[] { CampaignLoopExit.Ended, CampaignLoopExit.Exhausted })
        {
            var result = CampaignResumeTask.Describe("health", "campaign_run", rule,
                new(exit, 0, null, Health: state.Health.Observations, Withdrawal: confirmedWithdrawal), true);
            Check(result.Outcome == TaskOutcome.Failed && result.Reason == "sortie_withdrawn" &&
                result.Evidence?["sortie"]?["outcome"]?.GetValue<string>() == "withdrawn" &&
                result.Evidence["cleared"]?.GetValue<bool>() == false && result.Evidence["health"]?.AsArray().Count == 2,
                "HP withdrawal lost its evidence or became a clear");
            const string script = "import json,sys;sys.path.insert(0,sys.argv[1]);from sortie_contract import evaluate;print(json.dumps(evaluate(json.loads(sys.argv[2]))['violations']))";
            var contract = await new ProcessRunner().RunAsync(python,
                ["-c", script, Path.GetFullPath("tools"), result.Evidence!["sortie"]!.ToJsonString()], TimeSpan.FromSeconds(15));
            Check(contract.ExitCode == 0 && JsonNode.Parse(contract.Output)!.AsArray().Count == 0, "Withdrawal violated frozen sortie contract");
        }
        var stage = new MapArrivalResult(MapArrivalOutcome.StageReturned, 10, 2, MapEncounterKind.Combat) {
            HandledEncounters = [MapEncounterKind.Combat],
            Combats = [new(CombatReturn.InStage, new(CombatRank.S, CombatRankSource.BattleStatus,
                UiAssets.Combat.BATTLE_STATUS_S.Id), false, false, 2)] };
        foreach (var withdrawal in new[] { confirmedWithdrawal!, confirmedWithdrawal! with { ExitActions = 0 },
            confirmedWithdrawal! with { StageSequence = confirmedWithdrawal.StartedSequence },
            confirmedWithdrawal! with { StageConfirmed = false } })
        {
            var result = CampaignResumeTask.Describe("conflict", "campaign_run", rule,
                new(CampaignLoopExit.Ended, 0, stage, Withdrawal: withdrawal), true);
            Check(result.Outcome == TaskOutcome.Failed && result.Evidence?["cleared"]?.GetValue<bool>() == false,
                "A concurrent or incomplete withdrawal acquired a winning combat's clear evidence");
        }
        var task = new CampaignResumeTask();
        task.Validate(new JsonObject { ["campaign"] = rule.Id, ["hpControl"] = new JsonObject {
            ["lowHpRetreat"] = true, ["threshold"] = .3, ["balanceWeight"] = "2000，1000，500" } });
        foreach (var control in new JsonNode?[] { null, JsonValue.Create("yes"), new JsonObject { ["threshold"] = 2 },
            new JsonObject { ["lowHpRetreat"] = null }, new JsonObject { ["extra"] = true } })
            await Rejects<ArgumentException>(() => { task.Validate(new JsonObject { ["campaign"] = rule.Id, ["hpControl"] = control }); return Task.CompletedTask; });
    }

    private sealed class Vision(Func<ValueTask<IReadOnlyList<double>>> read) : IColorBarVision
    {
        public ValueTask<IReadOnlyList<double>> ColorBarsAsync(ScreenFrame frame, IReadOnlyList<ColorBarRequest> bars, CancellationToken token) => read();
    }
    private sealed class NoTapCamera : IMapArrivalCamera
    {
        public bool Invalidated { get; private set; }
        public long FrameSequence => 1;
        public void Invalidate() => Invalidated = true;
        public void Suspend() => throw new InvalidOperationException();
        public ValueTask PrepareTapAsync(Cell destination, CancellationToken token = default) => throw new InvalidOperationException();
        public ValueTask TapCellAsync(Cell destination, CancellationToken token = default) => throw new InvalidOperationException();
        public ValueTask RefreshImageAsync(CancellationToken token = default) => throw new InvalidOperationException();
        public ValueTask<FleetMarker> ReadFleetMarkerAsync(Cell destination, CancellationToken token = default) => throw new InvalidOperationException();
        public ValueTask<FleetMarker> ReadCenterMarkerAsync(CancellationToken token = default) => throw new InvalidOperationException();
        public ValueTask RelocalizeAsync(CancellationToken token = default) => throw new InvalidOperationException();
        public ValueTask AnchorAtAsync(Cell destination, CancellationToken token = default) => throw new InvalidOperationException();
    }
    private sealed class WithdrawalUi(JsonArray frames) : AppearanceProbe(GameServer.Cn, null), IPopupHandler, IMapUiObservations, IApplicationHealth, IStoryHandler
    {
        public int Frame { get; private set; }
        public bool Stale { get; set; }
        public List<object> Events { get; } = [];
        public List<object> Clicks { get; } = [];
        public async Task PrimeAsync() => await base.ScreenshotAsync(default);
        private bool Positive(string name) => frames[Math.Min(Frame, frames.Count - 1)]!.AsArray().Any(value => value!.GetValue<string>() == name);
        public override ValueTask ScreenshotAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); Frame++; Time.Advance(.5); if (Frame > 100) throw new InvalidOperationException("Withdrawal fixture did not terminate"); return ValueTask.CompletedTask; }
        public override ValueTask<bool> AppearsAsync(AssetRule asset, ButtonOffset offset = default, double interval = 0,
            double similarity = .85, int threshold = 10, TemplatePreprocessing preprocessing = TemplatePreprocessing.Color, CancellationToken token = default)
        { Events.Add(new { asset = asset.Name, offset = new[] { offset.Left, offset.Top, offset.Right, offset.Bottom }, interval, frame = Frame }); return ValueTask.FromResult(Positive(asset.Name)); }
        public override ValueTask ClickAsync(AssetRule asset, CancellationToken token)
        { Clicks.Add(new { asset = asset.Name, frame = Frame }); return ValueTask.CompletedTask; }
        private bool Observe(string name)
        { Events.Add(new { asset = name, offset = Array.Empty<int>(), interval = 0, frame = Frame }); return Positive(name); }
        public ValueTask<bool> ConfirmAsync(CancellationToken token) => ValueTask.FromResult(Observe("$popup"));
        public ValueTask<int> InfoBarCountAsync(CancellationToken token) => ValueTask.FromResult(Observe("$info") ? 1 : 0);
        public ValueTask<bool> HasStageEntranceAsync(CancellationToken token) => ValueTask.FromResult(Observe("$entrance"));
        public ValueTask<bool> IsRunningAsync(CancellationToken token) => throw new InvalidOperationException();
        public ValueTask RefreshOrientationAsync(CancellationToken token) => throw new InvalidOperationException();
        public ValueTask StopAsync(CancellationToken token) => throw new InvalidOperationException();
        public ValueTask<bool> StorySkipAsync(CancellationToken token) => throw new InvalidOperationException();
        public ValueTask EnsureNoStoryAsync(bool skipFirst, CancellationToken token) => throw new InvalidOperationException();
    }
}
