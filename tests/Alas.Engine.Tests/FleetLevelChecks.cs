using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static class FleetLevelChecks
{
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static async Task Rejects<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static int[] Numbers(JsonNode node) => node.AsArray().Select(value => value!.GetValue<int>()).ToArray();
    public static async Task RunAsync(string python, string upstream, string artifacts)
    {
        string inputs = Path.Combine(artifacts, "level-parameters.json"), output = Path.Combine(artifacts, "native-level.json");
        string worker = Path.Combine(AppContext.BaseDirectory, "Imaging/Worker/vision_worker.py");
        var parameters = Enum.GetValues<GameServer>().Select(server => {
            var requests = FleetLevelRules.Requests(server);
            return new { server = server.ToString().ToLowerInvariant(),
                areas = requests.Select(request => new[] { request.Area.X, request.Area.Y, request.Area.Width, request.Area.Height }),
                prefix = requests[0].PrefixCrop!.Protocol() };
        });
        await File.WriteAllTextAsync(inputs, JsonSerializer.Serialize(parameters));
        var reference = await new ProcessRunner().RunAsync(python,
            [Path.Combine(AppContext.BaseDirectory, "native_level_reference.py"), upstream, inputs, output, worker], TimeSpan.FromSeconds(60));
        Check(reference.ExitCode == 0, "Native level oracle failed: " + reference.Error);
        var data = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
        Check(data["sources"]![FleetLevelRules.Source.Path]!.GetValue<string>() == FleetLevelRules.Source.Sha256,
            "Native level source drifted");
        await using var vision = new PureVisionWorker(python, worker, modelDirectory: Path.Combine(upstream, "bin/ocr_models"));
        int images = 0, states = 0;
        long frame = 0;
        foreach (var sample in data["cases"]!.AsArray())
        {
            var server = Enum.Parse<GameServer>(sample!["server"]!.GetValue<string>(), true);
            var image = new ScreenFrame(++frame, DateTimeOffset.UnixEpoch,
                await File.ReadAllBytesAsync(Path.Combine(artifacts, sample["file"]!.GetValue<string>())));
            var ui = new ImageUi(server, vision, image);
            var state = new FleetLevelState();
            var reading = await new FleetLevelReader(ui, () => image.Sequence).ReadAsync(state, 1, false, new(120), default);
            Check(reading is not null && reading.Levels.SequenceEqual(Numbers(sample["levels"]!)),
                "Native level OCR differs: " + sample["file"]);
            Check(ui.Reads == 6 && !reading!.ReachLevelTriggered && !reading.Level32Triggered,
                "Initial level reading triggered a stop or omitted a slot");
            images++;
        }
        foreach (var scenario in data["states"]!.AsArray())
        {
            var options = new FleetLevelOptions(scenario!["limit"]!.GetValue<int>(), scenario["flag32"]!.GetValue<bool>());
            var state = new FleetLevelState();
            foreach (var step in scenario["trace"]!.AsArray())
            {
                var ui = new LevelUi(Numbers(step!["input"]!).Select(value => value.ToString()).ToArray(), ++frame);
                var reading = await new FleetLevelReader(ui, () => frame).ReadAsync(state, 1,
                    step["afterBattle"]!.GetValue<bool>(), options, default);
                Check(state.Levels.SequenceEqual(Numbers(step["levels"]!)) && state.BeforeBattle.SequenceEqual(Numbers(step["before"]!)) &&
                    state.ReachLevelTriggered == step["triggered"]!.GetValue<bool>() && state.Level32Triggered == step["triggered32"]!.GetValue<bool>(),
                    "Native level baseline or sticky trigger differs");
                Check(options.Enabled ? reading is not null && ui.Reads == 6 : reading is null && ui.Reads == 0 && ui.Calls.Count == 0,
                    "Disabled level tracking performed a visual read");
                states++;
            }
            state.Reset();
            Check(state.Levels.SequenceEqual(Numbers(scenario["resetLevels"]!)) && state.BeforeBattle.SequenceEqual(Numbers(scenario["resetBefore"]!)) &&
                state.ReachLevelTriggered == scenario["resetTriggered"]!.GetValue<bool>() &&
                state.Level32Triggered == scenario["reset32"]!.GetValue<bool>(), "Level reset changed native trigger flags");
        }
        await FailureChecksAsync();
        await InputAndEvidenceChecksAsync();
        var request = FleetLevelRules.Requests(GameServer.Cn)[0];
        var sourceImage = new ScreenFrame(1, DateTimeOffset.UnixEpoch,
            await File.ReadAllBytesAsync(Path.Combine(artifacts, "level-cn-0.png")));
        foreach (var invalid in new[] { request with { PrefixCrop = null }, request with { Preprocessing = OcrPreprocessing.Letters },
            request with { PrefixCrop = request.PrefixCrop! with { MaskRows = 20 } },
            request with { PrefixCrop = request.PrefixCrop! with { MaskScale = double.NaN } },
            request with { PrefixCrop = request.PrefixCrop! with { Background = new(255, 255, 255) } },
            request with { PrefixCrop = request.PrefixCrop! with { SearchBottom = 20 } },
            request with { PrefixCrop = request.PrefixCrop! with { Border = -1 } } })
            await Rejects<ArgumentException>(async () => await vision.ReadTextAsync(sourceImage, invalid));
        _ = await vision.ReadTextAsync(sourceImage, request);
        Console.WriteLine($"Fleet levels: {images * 6} actual native/worker model inferences, {data["pixels"]} exact preprocessing images and {states} native read/trigger states passed; no real level observation or continuous-run scheduler verification.");
    }

    private static async Task FailureChecksAsync()
    {
        var options = new FleetLevelOptions(120);
        var state = new FleetLevelState();
        var baseline = state.Commit(1, 1, [119, 0, 0, 23, 0, 0], false, options);
        var ui = new LevelUi(["120", "", "I", "ID", "S", "B"], 2);
        var result = await new FleetLevelReader(ui, () => 2).ReadAsync(state, 1, true, options, default);
        Check(result!.Levels.SequenceEqual(new[] { 120, 0, 1, 10, 5, 8 }) && state.ReachLevelTriggered,
            "Level-specific empty/correction handling or crossing was lost");
        foreach (string invalid in new[] { "bad", "99999999999999999999999999999", "-1" })
        {
            ui = new LevelUi(["121", "0", invalid, "25", "0", "0"], 3);
            bool failed = false;
            try { await new FleetLevelReader(ui, () => 3).ReadAsync(state, 1, true, options, default); }
            catch (Exception e) when (e is FormatException or OverflowException or InvalidDataException) { failed = true; }
            Check(failed && state.Levels == result.Levels && state.Evidence(options).Readings.Count == 2,
                "Malformed OCR published a partial reading");
        }
        ui = new LevelUi(["121", "0", "0", "25", "0", "0"], 3) { FailAt = 4 };
        await Rejects<IOException>(async () => await new FleetLevelReader(ui, () => 3).ReadAsync(state, 1, true, options, default));
        ui = new LevelUi(["121", "0", "0", "25", "0", "0"], 3) { WrongFrameAt = 2 };
        await Rejects<InvalidDataException>(async () => await new FleetLevelReader(ui, () => 3).ReadAsync(state, 1, true, options, default));
        ui = new LevelUi(["121", "0", "0", "25", "0", "0"], 3);
        await Rejects<InvalidDataException>(async () => await new FleetLevelReader(ui, () => ui.Reads < 2 ? 3 : 4).ReadAsync(state, 1, true, options, default));
        ui = new LevelUi(["121", "0", "0", "25", "0", "0"], 2);
        await Rejects<InvalidDataException>(async () => await new FleetLevelReader(ui, () => 2).ReadAsync(state, 1, true, options, default));
        using var cancelled = new CancellationTokenSource();
        ui = new LevelUi(["121", "0", "0", "25", "0", "0"], 3) { CancelAt = 2, Cancellation = cancelled };
        await Rejects<OperationCanceledException>(async () => await new FleetLevelReader(ui, () => 3).ReadAsync(state, 1, true, options, cancelled.Token));
        Check(state.Levels == result.Levels && state.Evidence(options).Readings.Count == 2, "Failed level reading changed state");
        await Rejects<InvalidDataException>(() => { state.Commit(2, 4, [120, 0, 0, 23, 0, 0], true, options); return Task.CompletedTask; });
        var nextFleet = state.Commit(2, 4, [30, 0, 0, 23, 0, 0], false, options);
        Check(nextFleet.BeforeBattle.All(level => level == -1), "Fleet switch kept the previous fleet's baseline");
        var noFrame = new AppearanceProbe(GameServer.Cn, null);
        await Rejects<InvalidDataException>(async () => await new FleetLevelReader(noFrame, () => 5).ReadAsync(state, 2, true, options, default));
    }

    private static async Task InputAndEvidenceChecksAsync()
    {
        var rule = RuleCatalog.Create("campaign_main/campaign_1_1");
        var task = new CampaignResumeTask();
        var input = new JsonObject { ["campaign"] = rule.Id, ["reachLevel"] = 120 };
        var request = new TaskRequest("levels", "campaign_resume", input);
        task.Validate(input);
        Check(task.Preconditions(request, new(true, false)).SequenceEqual(new[] { "ocr_models" }) &&
            task.Preconditions(request, new(true, true)).Count == 0, "Enabled level OCR ignored its model prerequisite");
        var state = new FleetLevelState();
        state.Commit(1, 1, [119, 0, 0, 20, 0, 0], false, new(120));
        state.Commit(1, 2, [120, 0, 0, 21, 0, 0], true, new(120));
        var service = new Service(new(CampaignLoopExit.Ended, 1, null, Levels: state.Evidence(new(120))));
        var result = await task.RunAsync(request, new(null!, null!, null!, TimeSpan.FromSeconds(1), Campaign: service), default);
        Check(service.Configuration?.Levels.ReachLevel == 120 && result.Evidence?["levels"]?["reachLevelTriggered"]?.GetValue<bool>() == true &&
            result.Evidence["sortie"]?["outcome"]?.GetValue<string>() == "ended_unknown" && result.Outcome == TaskOutcome.Failed,
            "Level trigger was dropped or promoted to a sortie clear");
        foreach (JsonNode? value in new JsonNode?[] { null, JsonValue.Create(-1) })
            await Rejects<ArgumentException>(() => { task.Validate(new JsonObject { ["campaign"] = rule.Id, ["reachLevel"] = value }); return Task.CompletedTask; });
        var disabled = request with { Input = new JsonObject { ["campaign"] = rule.Id } };
        Check(task.Preconditions(disabled, new(true, false)).Count == 0, "Disabled level OCR requires a model");
    }

    private sealed class Service(CampaignResumeResult result) : ICampaignExecutionService
    {
        public CampaignConfiguration? Configuration { get; private set; }
        public ValueTask<CampaignResumeResult> ResumeInMapAsync(CampaignRule rule, CampaignConfiguration configuration, CancellationToken token)
        { Configuration = configuration; return ValueTask.FromResult(result); }
    }
    private sealed class ImageUi(GameServer server, IVision vision, ScreenFrame image) : AppearanceProbe(server, UiAssets.Handler.IN_MAP.Id)
    {
        public int Reads { get; private set; }
        public override ValueTask<OcrObservation> ReadTextAsync(OcrRequest request, CancellationToken token)
        { Reads++; return vision.ReadTextAsync(image, request, token); }
    }
    private sealed class LevelUi(string[] texts, long sequence) : AppearanceProbe(GameServer.Cn, UiAssets.Handler.IN_MAP.Id)
    {
        public int Reads { get; private set; }
        public int FailAt { get; init; } = -1;
        public int WrongFrameAt { get; init; } = -1;
        public int CancelAt { get; init; } = -1;
        public CancellationTokenSource? Cancellation { get; init; }
        public override ValueTask<OcrObservation> ReadTextAsync(OcrRequest request, CancellationToken token)
        {
            int index = Reads++;
            if (index == FailAt) throw new IOException("Inference failed");
            if (index == CancelAt) Cancellation!.Cancel();
            return ValueTask.FromResult(new OcrObservation(index == WrongFrameAt ? sequence + 1 : sequence, texts[index], null));
        }
    }
}
