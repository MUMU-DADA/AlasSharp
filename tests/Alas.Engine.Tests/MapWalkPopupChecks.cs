using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Imaging;
using Alas.Engine.Navigation;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static partial class MapWalkPopupChecks
{
    private sealed record Signal(int Cat = 0, int Mirror = 0, bool Confirm = false, bool Cancel = false, bool Marker = true);
    private sealed record Sample(bool Clear, bool Walk, Signal[] Signals);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    public static async Task RunAsync(string python, string upstream, string artifacts)
    {
        Directory.CreateDirectory(artifacts);
        var samples = new List<Sample>();
        foreach (bool clear in new[] { false, true })
        {
            foreach (int cat in new[] { 0, 100, 101 })
            foreach (int mirror in new[] { 0, 200, 201 })
            foreach (bool confirm in new[] { false, true })
            foreach (bool cancel in new[] { false, true })
                samples.Add(new(clear, false, Enumerable.Repeat(new Signal(cat, mirror, confirm, cancel), 12).ToArray()));
            foreach (var popup in new[] { new Signal(Cat: 101), new Signal(Mirror: 201), new Signal(Confirm: true, Cancel: true),
                new Signal(Cat: 101, Mirror: 201, Confirm: true, Cancel: true) })
            foreach (int start in new[] { 0, 1, 2 })
            foreach (int length in new[] { 1, 3, 10 })
                samples.Add(new(clear, true, Enumerable.Repeat(new Signal(), start)
                    .Concat(Enumerable.Repeat(popup, length)).Concat([new Signal()]).ToArray()));
        }
        string input = Path.Combine(artifacts, "input.json");
        await File.WriteAllTextAsync(input, JsonSerializer.Serialize(samples, TaskQueue.Json));
        await using var vision = new PureVisionWorker(python, Path.Combine(AppContext.BaseDirectory, "Imaging/Worker/vision_worker.py"));
        int fixtureCount = 0;
        foreach (var server in Enum.GetValues<GameServer>())
        {
            string output = Path.Combine(artifacts, server + ".json");
            var process = await new ProcessRunner().RunAsync(python,
                [Path.Combine(AppContext.BaseDirectory, "native_walk_popups_reference.py"), upstream,
                    server.ToString().ToLowerInvariant(), input, output], TimeSpan.FromMinutes(2));
            Check(process.ExitCode == 0, "Native popup oracle failed: " + process.Error);
            var native = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
            foreach (var source in new[] { MapWalkPopups.Source, UiRecovery.InfoSource, CampaignState.InitializationSource })
                Check(native["sources"]![source.Path]!.GetValue<string>() == source.Sha256, "Popup source drift: " + source.Path);
            for (int i = 0; i < samples.Count; i++) await CompareAsync(server, samples[i], native["results"]![i]!);
            foreach (var fixture in native["fixtures"]!.AsArray())
            {
                var frame = new ScreenFrame(++fixtureCount, DateTimeOffset.UnixEpoch,
                    await File.ReadAllBytesAsync(Path.Combine(artifacts, fixture!["image"]!.GetValue<string>())));
                var ui = new AppearanceProbe(server, null);
                var popup = new MapWalkPopups(ui, new(vision, () => frame), () => frame.Sequence, fixture["clear"]!.GetValue<bool>());
                Check((await popup.ObserveAsync(frame.Sequence, default) == MapEncounterKind.CatAttack) == fixture["matched"]!.GetValue<bool>(),
                    "Cat attack color-count differs from native pixels");
            }
        }
        await FailureChecksAsync();
        await SessionChecksAsync(python, upstream, artifacts);
        Console.WriteLine($"Walk popups: {samples.Count} actual native handler/arrival traces per server, {fixtureCount} CV fixtures, failure and session checks passed; synthetic device only.");
    }

    private static async Task CompareAsync(GameServer server, Sample sample, JsonNode reference)
    {
        var ui = new Replay(server, sample);
        var popup = new MapWalkPopups(ui, new(ui, () => ui.Frame), () => ui.FrameSequence, sample.Clear);
        var state = State();
        if (sample.Walk)
        {
            var probe = new MapEncounterProbe(ui, false, walkPopups: popup);
            var move = new MapMovement(state, new() { HasAmbush = false }, ui,
                () => new(ui, state, _ => ValueTask.FromResult(true), ui.Clock, probe, walkPopups: popup));
            var result = await move.MoveAsync(new(2, 1));
            Check(result.Outcome == MapMoveOutcome.Committed && result.Arrival.Combats.IsEmpty &&
                !state.MovementInvalidated && ui.Relocalizations == 0,
                "Popup was treated as a battle, interruption, or camera relocation");
            Check(result.Arrival.HandledEncounters.Length == ui.Clicks.Count - 1, "Popup evidence lost or duplicated");
        }
        else
            foreach (var _ in sample.Signals)
            {
                await ui.ScreenshotAsync(default);
                var kind = await popup.ObserveAsync(ui.FrameSequence, default);
                if (kind != MapEncounterKind.None) await popup.HandleAsync(kind, ui.FrameSequence, default);
            }
        string label = JsonSerializer.Serialize(sample, TaskQueue.Json);
        Check(ui.Frames == reference["frames"]!.GetValue<int>(), "Popup arrival frame differs: " + label);
        Check(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(ui.Clicks), reference["clicks"]), "Popup clicks differ: " + label + " actual=" + JsonSerializer.Serialize(ui.Clicks, TaskQueue.Json) + " native=" + reference["clicks"]);
        Check(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(ui.Queries), reference["queries"]), "Popup short-circuit sequence differs: " + label);
        Check(state.Fleet1Location.ToString() == reference["fleet"]!.GetValue<string>() && state.BattleCount == 0 && state.MysteryCount == 0,
            "Popup changed gameplay counters or fleet position without arrival");
    }

    private static CampaignState State()
    {
        var state = new CampaignState(new MapDefinition("B1", "SP --", [], [], []));
        state.InitializeMapData(new()); state.Fleet1Location = new(1, 1); state.RefreshFleetPaths(new());
        return state;
    }

    private sealed class Replay(GameServer server, Sample sample) : AppearanceProbe(server, null), IMapArrivalCamera, IImagePatchVision
    {
        public int Frames { get; private set; }
        public long FrameSequence => Frames + 1;
        public ScreenFrame Frame => new(FrameSequence, DateTimeOffset.UnixEpoch, ReadOnlyMemory<byte>.Empty);
        public Signal Current => sample.Signals[Math.Min(Math.Max(Frames - 1, 0), sample.Signals.Length - 1)];
        public List<object[]> Clicks { get; } = [];
        public List<object[]> Queries { get; } = [];
        public bool Invalidated { get; private set; }
        public int Relocalizations { get; private set; }
        public string Failure { get; set; } = "";
        public override ValueTask ScreenshotAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Failure == "cancel") throw new OperationCanceledException();
            if (Failure != "stale") Frames++;
            Time.Advance(.25);
            if (Frames > 500) throw new InvalidOperationException("Popup replay did not finish");
            return ValueTask.CompletedTask;
        }
        public override ValueTask<bool> AppearsAsync(AssetRule asset, ButtonOffset offset = default, double interval = 0,
            double similarity = .85, int threshold = 10, TemplatePreprocessing preprocessing = TemplatePreprocessing.Color, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (asset != UiAssets.Handler.GUILD_POPUP_CONFIRM && asset != UiAssets.Handler.GUILD_POPUP_CANCEL) return ValueTask.FromResult(false);
            Check(offset == ButtonOffset.Expand(3, 30) && similarity == .85 && threshold == 10, "Guild popup parameters drifted");
            if (interval > 0 && !Timer(asset, interval, true).Reached()) return ValueTask.FromResult(false);
            string name = asset == UiAssets.Handler.GUILD_POPUP_CONFIRM ? "confirm" : "cancel";
            Queries.Add([Frames, name]);
            bool found = name == "confirm" ? Current.Confirm : Current.Cancel;
            if (found && interval > 0) Timer(asset).Reset();
            return ValueTask.FromResult(found);
        }
        public ValueTask<ImagePatchObservation> MeasurePatchAsync(ScreenFrame frame, ImagePatchRequest request, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            string name = request.Area == UiAssets.Map.MAP_CAT_ATTACK.For(Server).Area!.Value.Area ? "cat" : "mirror";
            Check(request.Measure == PatchMeasure.SimilarityCount && request.Processing == PatchProcessing.ColorSimilarity &&
                request.MinimumSimilarity == 225 && request.Color == new PixelColor(255, 231, 123), "Cat color parameters drifted");
            Queries.Add([Frames, name]);
            return ValueTask.FromResult(new ImagePatchObservation(Failure == "wrong_cv" ? frame.Sequence + 1 : frame.Sequence,
                name == "cat" ? Current.Cat : Current.Mirror));
        }
        public override ValueTask ClickAsync(AssetRule asset, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Failure == "click") throw new IOException("Synthetic popup click failure");
            Clicks.Add([Frames, asset.Name]); return ValueTask.CompletedTask;
        }
        public ValueTask PrepareTapAsync(Cell destination, CancellationToken token = default) => ValueTask.CompletedTask;
        public ValueTask TapCellAsync(Cell destination, CancellationToken token = default)
        { Clicks.Add([Frames, destination.ToString()]); return ValueTask.CompletedTask; }
        public ValueTask RefreshImageAsync(CancellationToken token = default) => ScreenshotAsync(token);
        public ValueTask<FleetMarker> ReadFleetMarkerAsync(Cell destination, CancellationToken token = default)
            => ValueTask.FromResult(new FleetMarker(Current.Marker, Current.Marker));
        public ValueTask<FleetMarker> ReadCenterMarkerAsync(CancellationToken token = default) => throw new InvalidOperationException();
        public ValueTask RelocalizeAsync(CancellationToken token = default) { Relocalizations++; return ScreenshotAsync(token); }
        public ValueTask AnchorAtAsync(Cell location, CancellationToken token = default) => throw new InvalidOperationException();
        public void Suspend() => throw new InvalidOperationException("Popup suspended the map camera");
        public void Invalidate() => Invalidated = true;
    }
}
