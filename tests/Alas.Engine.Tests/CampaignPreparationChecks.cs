using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static class CampaignPreparationChecks
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static async Task Rejects<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }

    public static async Task RunAsync(string python, string upstream, string artifacts)
    {
        string output = Path.Combine(artifacts, "preparation-native.json");
        var process = await new ProcessRunner().RunAsync(python,
            [Path.Combine(AppContext.BaseDirectory, "native_preparation_reference.py"), upstream, output], TimeSpan.FromSeconds(90));
        Check(process.ExitCode == 0, "Preparation oracle failed: " + process.Error);
        var native = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
        foreach (var source in new[] { CampaignMapPreparation.Source, CampaignPreparation.Source })
            Check(native["sources"]![source.Path]!.GetValue<string>() == source.Sha256, "Preparation source drifted");
        foreach (var sample in native["results"]!.AsArray())
        {
            var scenario = sample!["case"]!;
            var ui = new Replay(scenario);
            var preparation = new CampaignMapPreparation(ui, ui, ui, () => ui.Image,
                _ => ValueTask.FromResult(ui.Data["info"]?.GetValue<int>() ?? 0));
            var configuration = new CampaignConfiguration
            {
                UseClearMode = scenario["useClear"]?.GetValue<bool>() ?? true,
                UseDoubleBook = scenario["useBook"]?.GetValue<bool>() ?? false,
                HasClearPercentage = scenario["hasPercentage"]?.GetValue<bool>() ?? true,
                ClearPercentageShort = scenario["short"]?.GetValue<bool>() ?? false,
                IsOneTimeStage = scenario["oneTime"]?.GetValue<bool>() ?? false
            };
            if (scenario["kind"]!.GetValue<string>() == "map")
            {
                var actual = await preparation.PrepareMapAsync(configuration, Timeout, default);
                var expected = sample["expected"]!.Deserialize<CampaignMapPreparationResult>(TaskQueue.Json)!;
                Check(actual == expected, "Map state differs from native: " + scenario + "\n" + JsonSerializer.Serialize(actual));
            }
            else
            {
                await ui.ScreenshotAsync(default);
                configuration = configuration with { IsClearMode = true };
                if (!sample["expected"]!["confirmed"]!.GetValue<bool>() && sample["expected"]!["clicks"]!.GetValue<int>() > 0)
                {
                    await Rejects<TimeoutException>(() => preparation.PrepareDoubleBookAsync(configuration, Timeout, default).AsTask());
                    Check(preparation.Evidence.DoubleBook is { Enabled: null, Clicks: 4 }, "Unconfirmed book became disabled");
                }
                else
                {
                    var actual = await preparation.PrepareDoubleBookAsync(configuration, Timeout, default);
                    Check(actual.Available == sample["expected"]!["confirmed"]!.GetValue<bool>() &&
                        actual.Enabled == (actual.Available && configuration.UseDoubleBook), "Book state differs from native");
                }
            }
            Check(ui.Frame == sample["frames"]!.GetValue<int>() &&
                JsonNode.DeepEquals(JsonSerializer.SerializeToNode(ui.Clicks, TaskQueue.Json), sample["clicks"]),
                "Preparation screenshots/clicks differ from native: " + scenario);
        }
        await PixelChecksAsync(python, upstream, artifacts, native["pixels"]!.AsArray());
        await SessionChecksAsync(python, upstream, artifacts);
        await FailureChecksAsync();
        await ConfigurationChecksAsync();
        await CampaignStageSelectorChecks.RunAsync(upstream);
        Console.WriteLine($"Preparation: {native["results"]!.AsArray().Count} native traces and " +
            $"{native["pixels"]!.AsArray().Count} four-server CV images; state propagation, loop declarations, failure and task checks passed. No live-device result.");
    }

    private static async Task PixelChecksAsync(string python, string upstream, string artifacts, JsonArray samples)
    {
        await using var vision = new PureVisionWorker(python,
            Path.Combine(AppContext.BaseDirectory, "Imaging/Worker/vision_worker.py"));
        var assets = new AssetFiles(Path.Combine(upstream, "assets"));
        foreach (var sample in samples)
        {
            var server = Enum.Parse<GameServer>(sample!["server"]!.GetValue<string>(), true);
            var frame = new ScreenFrame(1, DateTimeOffset.UnixEpoch,
                await File.ReadAllBytesAsync(Path.Combine(artifacts, sample["file"]!.GetValue<string>())));
            var ui = new UiDriver(server, new ImageDevice(frame), vision, assets);
            await ui.ScreenshotAsync(default);
            var preparation = new CampaignMapPreparation(ui, vision, vision, () => ui.Frame!, _ => ValueTask.FromResult(0));
            var configuration = new CampaignConfiguration { ClearPercentageShort = sample["short"]!.GetValue<bool>() };
            Check(await preparation.ReadClearModeAsync(default) == sample["clear"]?.GetValue<string>(), "Native clear-mode color/offset mismatch");
            Check(await new CampaignAutoSearch(ui, vision, () => ui.Frame!).ReadAsync(default) == sample["auto"]?.GetValue<string>(),
                "Native auto-search color/title mismatch");
            var info = await preparation.ReadInfoAsync(configuration, default);
            Check(Math.Abs(info.ClearPercentage - sample["percentage"]!.GetValue<double>()) < 1e-12 &&
                new[] { info.Star1, info.Star2, info.Star3 }.SequenceEqual(sample["stars"]!.AsArray().Select(v => v!.GetValue<bool>())),
                "Native progress/star pixel thresholds differ");
            Check(sample["bookPresent"]!.GetValue<bool>(), "Book positive fixture failed in native oracle");
            var book = await preparation.PrepareDoubleBookAsync(configuration with
                { IsClearMode = true, UseDoubleBook = sample["book"]!.GetValue<bool>() }, Timeout, default);
            Check(book is { Available: true, Clicks: 0 } && book.Enabled == sample["book"]!.GetValue<bool>(), "Native book color/offset mismatch");
        }
    }

    private static async Task FailureChecksAsync()
    {
        var scenario = JsonNode.Parse("""{"frames":[{"book":"off"},{}],"step":0.5}""")!;
        var ui = new Replay(scenario);
        var preparation = new CampaignMapPreparation(ui, ui, ui, () => ui.Image, _ => ValueTask.FromResult(0));
        await ui.ScreenshotAsync(default);
        await Rejects<TimeoutException>(() => preparation.PrepareDoubleBookAsync(new() { IsClearMode = true, UseDoubleBook = true },
            TimeSpan.FromSeconds(6), default).AsTask());
        Check(preparation.Evidence.DoubleBook is { Enabled: null, Clicks: 1 }, "Disappearing book control became confirmed");
        ui = new Replay(scenario) { FailClick = true };
        preparation = new(ui, ui, ui, () => ui.Image, _ => ValueTask.FromResult(0));
        await ui.ScreenshotAsync(default);
        await Rejects<IOException>(() => preparation.PrepareDoubleBookAsync(new() { IsClearMode = true, UseDoubleBook = true },
            Timeout, default).AsTask());
        Check(preparation.Evidence.DoubleBook is { Enabled: null, Clicks: 1 }, "Input failure retained old book value");
        ui = new Replay(scenario) { Stale = true };
        preparation = new(ui, ui, ui, () => ui.Image, _ => ValueTask.FromResult(0));
        await Rejects<InvalidDataException>(() => preparation.PrepareMapAsync(new(), Timeout, default).AsTask());
        ui = new Replay(scenario) { WrongPatchFrame = true };
        preparation = new(ui, ui, ui, () => ui.Image, _ => ValueTask.FromResult(0));
        await ui.ScreenshotAsync(default);
        await Rejects<InvalidDataException>(() => preparation.ReadInfoAsync(new(), default).AsTask());
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Rejects<OperationCanceledException>(() => preparation.PrepareMapAsync(new(), Timeout, cancelled.Token).AsTask());
        Check(ui.Clicks.Count == 0, "Rejected observation caused actions");
        var invalid = new JsonObject { ["campaign"] = "campaign_main/campaign_1_1", ["fleet1"] = 1, ["fleet2"] = 0, ["submarine"] = 0 };
        foreach (string field in new[] { "clearMode", "doubleBook" })
        {
            var input = (JsonObject)invalid.DeepClone(); input[field] = null;
            await Rejects<ArgumentException>(() => { new CampaignRunTask().Validate(input); return Task.CompletedTask; });
        }
        foreach (var mode in new[] { "calculate", "calculate_ignore" })
            await Rejects<NotSupportedException>(() =>
            {
                new CampaignResumeTask().Validate(new JsonObject
                    { ["campaign"] = "campaign_main/campaign_1_1", ["emotionMode"] = mode });
                return Task.CompletedTask;
            });
    }

    private static async Task SessionChecksAsync(string python, string upstream, string artifacts)
    {
        string fixture = Path.Combine(artifacts, "preparation-adb.json");
        string frame = Path.Combine(artifacts, "preparation-cn-2.png");
        await File.WriteAllTextAsync(fixture, JsonSerializer.Serialize(new { first = frame, second = frame, failTap = true, advance = false }));
        string? previous = Environment.GetEnvironmentVariable("ALAS_TEST_ADB_FIXTURE");
        Environment.SetEnvironmentVariable("ALAS_TEST_ADB_FIXTURE", fixture);
        try
        {
            var executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Alas.Engine.Tests.exe" : "Alas.Engine.Tests");
            var options = new EngineSessionOptions(executable, "offline-replay", GameServer.Cn, Path.Combine(upstream, "assets"),
                python, "org.example.game", AllowActions: true);
            var result = await new TaskQueue([new PreparationProbe()]).RunAsync(
                [new("confirmed", "preparation_probe", new() { ["double"] = false }, TimeoutSeconds: 45),
                 new("input-failure", "preparation_probe", new() { ["double"] = true }, TimeoutSeconds: 45)],
                options, new(artifacts));
            Check(result.Tasks[0].Outcome == TaskOutcome.Succeeded && result.Tasks[1].Outcome == TaskOutcome.Failed,
                "Actual session did not confirm preparation or expose failed book input: " + JsonSerializer.Serialize(result.Tasks));
            var files = Directory.GetFiles(result.Directory, "map-preparation.json", SearchOption.AllDirectories);
            Check(files.Length == 2 && RunReport.Build(result.Directory).ToJson()["evidence_complete"]!.GetValue<bool>(),
                "Actual queue/session preparation evidence was missing");
            var records = files.Select(file => JsonSerializer.Deserialize<CampaignPreparationEvidence>(File.ReadAllText(file), TaskQueue.Json)!).ToArray();
            Check(records.All(record => record is { Info.ClearModeAvailable: true, ClearMode: true, AutoSearch.Enabled: false }) &&
                records.Any(record => record.DoubleBook is { Enabled: false, Clicks: 0 }) &&
                records.Any(record => record.DoubleBook is { Enabled: null, Clicks: 1 }), "Session lost confirmed or partial book evidence");
            string saved = await File.ReadAllTextAsync(files[0]);
            File.Delete(files[0]);
            Check(!RunReport.Build(result.Directory).ToJson()["evidence_complete"]!.GetValue<bool>(), "Missing preparation evidence was accepted");
            await File.WriteAllTextAsync(files[0], saved);
            await using var session = new EngineSession(options);
            session.BeginTask(TimeSpan.FromSeconds(5));
            await session.Driver.ScreenshotAsync(default);
            await Rejects<InvalidOperationException>(() => session.PrepareDoubleBookAsync(new(), Timeout, default).AsTask());
            Check((await session.SaveEvidenceAsync(Path.Combine(artifacts, "new-preparation-task"), false)).MapPreparationFile is null,
                "Preparation state was reused without observing a map");
        }
        finally { Environment.SetEnvironmentVariable("ALAS_TEST_ADB_FIXTURE", previous); }
    }
    private sealed class PreparationProbe : ITaskRunner
    {
        public string Kind => "preparation_probe";
        public bool RequiresActions => true;
        public void Validate(JsonObject? input) { }
        public IReadOnlyList<string> Preconditions(TaskRequest request, TaskCapabilities capabilities) => [];
        public async ValueTask<TaskResult> RunAsync(TaskRequest request, TaskContext context, CancellationToken token)
        {
            var configuration = new CampaignConfiguration { UseDoubleBook = request.Input!["double"]!.GetValue<bool>() };
            var preparation = context.MapPreparation ?? throw new InvalidOperationException("Session omitted preparation service");
            var map = await preparation.PrepareMapAsync(configuration, context.Timeout, token);
            Check(map.ClearMode, "Synthetic real-CV preparation did not observe clear mode");
            var book = await preparation.PrepareDoubleBookAsync(configuration with { IsClearMode = map.ClearMode }, context.Timeout, token);
            return new(request.Id, Kind, TaskOutcome.Succeeded, "preparation_observed",
                JsonSerializer.SerializeToNode(book, TaskQueue.Json)!.AsObject());
        }
    }

    private static Task ConfigurationChecksAsync()
    {
        foreach (bool complete in new[] { false, true })
        foreach (bool clear in new[] { false, true })
        {
            var map = new MapDefinition("C1", complete ? "SP ME MB" : "SP -- --", ["A1"], ["A1"],
                complete ? [new(0, Enemy: 1), new(1, Boss: 1)] : [], loopTiles: "SP MB --",
                loopWaves: [new(0, Boss: 1)]);
            var rule = new OverrideRule(map);
            var execution = new CampaignExecution(rule, new() { IsClearMode = clear, IsDoubleBook = true }, (ICampaignOperations)null!);
            var config = execution.Context.Config;
            Check(config.HasAmbush == !clear && config.HasPortal == !clear && config.HasMovableEnemy == !clear &&
                config.HasMaze == !clear && config.HasFleetStep == !clear && config.HasDecoyEnemy == !clear &&
                config.PoorMapData == !(clear && complete) && config.IsDoubleBook,
                "Runtime overrides did not survive chapter configuration");
            var state = execution.Context.State;
            state.InitializeMapData(new(config.IsClearMode, config.PoorMapData));
            Check(state.PoorMapData == config.PoorMapData &&
                state.ActiveWaves.SequenceEqual(clear ? map.LoopWaves : map.Waves) &&
                state.Cells.Count(cell => cell.MayBoss) == (clear || complete ? 1 : 0), "Loop data or poor-data dispatch differs");
        }
        return Task.CompletedTask;
    }

    private sealed class OverrideRule(MapDefinition map) : CampaignRule
    {
        public override string Id => "synthetic/preparation";
        public override MapDefinition Map => map;
        public override System.Collections.Immutable.ImmutableArray<SourceFile> Sources => [];
        protected override IReadOnlyDictionary<int, BattleHook> Hooks => new Dictionary<int, BattleHook>();
        public override CampaignConfiguration Configure(CampaignConfiguration input) => input with
        {
            IsClearMode = !input.IsClearMode, IsDoubleBook = false,
            PoorMapData = true, HasAmbush = true, HasMovableEnemy = true, HasMovableNormalEnemy = true,
            HasFleetStep = true, HasDecoyEnemy = true, HasPortal = true, HasLandBased = true, HasMaze = true,
            HasFortress = true, HasBouncingEnemy = true
        };
    }

    private sealed class Replay : AppearanceProbe, IImagePatchVision, IColorBarVision
    {
        private readonly JsonNode _case;
        public int Frame { get; private set; }
        public bool FailClick { get; init; }
        public bool Stale { get; init; }
        public bool WrongPatchFrame { get; init; }
        public JsonNode Data => _case["frames"]![Math.Min(Math.Max(Frame - 1, 0), _case["frames"]!.AsArray().Count - 1)]!;
        public ScreenFrame Image => new(Stale ? 0 : Frame, DateTimeOffset.UnixEpoch, ReadOnlyMemory<byte>.Empty);
        public List<object> Clicks { get; } = [];
        public Replay(JsonNode sample) : base(GameServer.Cn, null)
        { _case = sample; base.ScreenshotAsync(default).GetAwaiter().GetResult(); }
        public override ValueTask ScreenshotAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); Frame++; Time.Advance(_case["step"]?.GetValue<double>() ?? .5); return ValueTask.CompletedTask; }
        public override ValueTask<bool> AppearsAsync(AssetRule asset, ButtonOffset offset = default, double interval = 0,
            double similarity = .85, int threshold = 10, TemplatePreprocessing preprocessing = TemplatePreprocessing.Color,
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (interval > 0 && !Timer(asset, interval).Reached()) return ValueTask.FromResult(false);
            bool value = asset.Name switch
            {
                "MAP_PREPARATION" => (Data["page"]?.GetValue<string>() ?? "normal") == "normal",
                "MAP_PREPARATION_HARD" => Data["page"]?.GetValue<string>() == "hard",
                "MAP_GREEN" => Data["safe"]?.GetValue<bool>() ?? false,
                "CLEAR_MODE_TITLE" => Data["clear"] is not null,
                "AUTO_SEARCH_TITLE" => Data["auto"] is not null,
                "BOOK_CHECK_PREP" => Data["book"] is not null,
                _ => false
            };
            if (value && interval > 0) Timer(asset, interval).Reset();
            return ValueTask.FromResult(value);
        }
        public override Rectangle ButtonArea(AssetRule asset) => asset.For(Server).ClickArea!.Value;
        public override void LoadOffset(AssetRule target, AssetRule reference) { }
        public override ValueTask ClickAsync(AssetRule asset, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (FailClick) throw new IOException("Synthetic input failure");
            Clicks.Add(new { asset = asset.Name, frame = Frame }); return ValueTask.CompletedTask;
        }
        public ValueTask<ImagePatchObservation> MeasurePatchAsync(ScreenFrame frame, ImagePatchRequest request, CancellationToken token = default)
        {
            double value;
            if (request.Color == new PixelColor(250, 232, 140))
            {
                var stars = new[] { UiAssets.Handler.MAP_STAR_1, UiAssets.Handler.MAP_STAR_2, UiAssets.Handler.MAP_STAR_3 };
                int index = Array.FindIndex(stars, star => star.For(Server).Area!.Value.Area == request.Area);
                Check(index >= 0 && request.MinimumSimilarity == 180, "Star measurement used wrong area/threshold");
                value = Data["stars"]?[index]?.GetValue<int>() ?? new[] { 36, 35, 40 }[index];
            }
            else
            {
                Check(request.MinimumSimilarity == 225, "Switch color threshold drifted");
                value = request.Color switch
                {
                    { R: 130, G: 229, B: 255 } => Data["clear"]?.GetValue<string>() == "on" ? 51 : 0,
                    { R: 255, G: 255, B: 255 } => Data["clear"]?.GetValue<string>() == "off" ? 201 : 0,
                    { R: 158, G: 234, B: 94 } => Data["auto"]?.GetValue<string>() == "on" ? 51 : 0,
                    { R: 156, G: 255, B: 82 } => Data["book"]?.GetValue<string>() == "on" ? 21 : 0,
                    _ => throw new InvalidOperationException("Unexpected pixel rule")
                };
            }
            return ValueTask.FromResult(new ImagePatchObservation(frame.Sequence - (WrongPatchFrame ? 1 : 0), value));
        }
        public ValueTask<IReadOnlyList<double>> ColorBarsAsync(ScreenFrame frame, IReadOnlyList<ColorBarRequest> bars, CancellationToken token)
        {
            Check(bars.Count == 1 && bars[0].Color == new PixelColor(231, 170, 82), "Progress color rule drifted");
            return ValueTask.FromResult<IReadOnlyList<double>>([Data["percent"]?.GetValue<double>() ?? .99]);
        }
    }
}
