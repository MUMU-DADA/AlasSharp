using System.Text.Json.Nodes;
using Alas.Engine.Imaging;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;
using Alas.Engine.Tasks;

namespace Alas.Engine.Tests;

internal static class CampaignStageReaderChecks
{
    public static async Task RunAsync()
    {
        foreach (var (raw, expected) in new[]
        {
            ("7--2", "7-2"), ("--72", "7-2"), ("I1-1", "11-1"),
            ("1I-1", "11-1"), ("I-I", "1-1"), ("isp-2", "isp-2"),
            ("SP3", "sp3"), ("EXTRA", "extra")
        })
            Check(CampaignStageReader.Normalize(raw) == expected, "Native stage OCR normalization changed: " + raw);
        foreach (var (name, chapter, index) in new[]
        {
            ("1-2", "1", "2"), ("d3", "d", "3"), ("sp", "ex_sp", "1"),
            ("sp3", "sp", "3"), ("ex", "ex_ex", "1"), ("extra", "ex_ex", "1"),
            ("49x", null, null), ("1-2-3", null, null), ("1", null, null)
        })
            Check(CampaignStageReader.Separate(name) == (chapter, index), "Native stage name split changed: " + name);

        var frame = new ScreenFrame(8, DateTimeOffset.UnixEpoch, ReadOnlyMemory<byte>.Empty);
        var entrances = new[]
        {
            new StageEntrance(new(10, 20, 40, 45), new(50, 30, 100, 50)),
            new StageEntrance(new(110, 20, 140, 45), new(150, 30, 200, 50)),
            new StageEntrance(new(210, 20, 240, 45), new(250, 30, 300, 50))
        };
        var vision = new Vision(["I-1", "2-1", "I-2"]);
        StageEntranceKind? requested = null;
        var reader = new CampaignStageReader((image, kinds, _) =>
        {
            Check(image.Sequence == frame.Sequence, "Stage detector used a different frame");
            requested = kinds;
            return ValueTask.FromResult<IReadOnlyList<StageEntrance>>(entrances);
        }, vision, GameServer.Jp);
        var observed = await reader.ObserveAsync(frame, StageEntranceKind.Normal | StageEntranceKind.Half);
        Check(requested == (StageEntranceKind.Normal | StageEntranceKind.Half) &&
            observed.FrameSequence == frame.Sequence && observed.Chapter == "1" &&
            observed.Readings.Select(r => r.Name).SequenceEqual(["1-1", "2-1", "1-2"]) &&
            observed.Readings[0].Index == "1" &&
            vision.Requests.Select(r => r.Area).SequenceEqual(entrances.Select(e => e.Name.Area)) &&
            vision.Requests.All(r => r.Language == "azur_lane_jp" && r.Alphabet == CampaignStageReader.Alphabet),
            "Stage reader changed OCR geometry, language, ordering or chapter majority");

        var stale = new CampaignStageReader((_, _, _) =>
            ValueTask.FromResult<IReadOnlyList<StageEntrance>>([entrances[0]]),
            new Vision(["1-1"], stale: true), GameServer.Cn);
        await Throws<InvalidDataException>(() => stale.ObserveAsync(frame, StageEntranceKind.Normal).AsTask(),
            "Stage OCR accepted a stale screenshot");
        var unknown = new CampaignStageReader((_, _, _) =>
            ValueTask.FromResult<IReadOnlyList<StageEntrance>>([entrances[0]]),
            new Vision(["49X"]), GameServer.Cn);
        await Throws<InvalidDataException>(() => unknown.ObserveAsync(frame, StageEntranceKind.Normal).AsTask(),
            "Unknown stage OCR acquired a chapter");

        var task = new CampaignStagesTask();
        var input = new JsonObject { ["entrances"] = new JsonArray("normal", "blue") };
        task.Validate(input);
        Check(task.Preconditions(new("stages", task.Kind, input), new(false, false)).SequenceEqual(["ocr_models"]),
            "Stage task ran without OCR models");
        var result = await task.RunAsync(new("stages", task.Kind, input),
            new TaskContext(null!, null!, null!, TimeSpan.FromSeconds(30), Stages: new Service(observed)), default);
        Check(result.Outcome == TaskOutcome.Succeeded && result.Evidence?["chapter"]?.GetValue<string>() == "1" &&
            result.Evidence["campaignIdentityVerified"]?.GetValue<bool>() == false &&
            result.Evidence["mapEntered"]?.GetValue<bool>() == false,
            "Read-only stage observation claimed an entered map");
        await Throws<ArgumentException>(() => Task.Run(() => task.Validate(
            new JsonObject { ["entrances"] = new JsonArray("unported") })),
            "Unknown stage entrance variant was accepted");
        await Throws<ArgumentException>(() => Task.Run(() => task.Validate(
            new JsonObject { ["entrances"] = null })),
            "Null stage entrance selection silently chose normal");
        Console.WriteLine("Campaign stage OCR: upstream name parsing, same-frame requests, chapter majority and read-only task gating passed offline.");
    }

    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static async Task Throws<T>(Func<Task> action, string message) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException(message); }
    private sealed class Service(CampaignStages stages) : ICampaignStageObservationService
    {
        public ValueTask<CampaignStages> ObserveStagesAsync(StageEntranceKind kinds, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(stages); }
    }
    private sealed class Vision(IEnumerable<string> names, bool stale = false) : IVision
    {
        private readonly Queue<string> _names = new(names);
        public List<OcrRequest> Requests { get; } = [];
        public ValueTask<OcrObservation> ReadTextAsync(ScreenFrame frame, OcrRequest request, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(request);
            return ValueTask.FromResult(new OcrObservation(frame.Sequence - (stale ? 1 : 0), _names.Dequeue(), null));
        }
        public ValueTask<TemplateObservation> MatchAsync(ScreenFrame frame, TemplateRequest request, CancellationToken token = default)
            => throw new NotSupportedException();
        public ValueTask<MeanColorObservation> MeanColorAsync(ScreenFrame frame, PixelArea area, CancellationToken token = default)
            => throw new NotSupportedException();
        public ValueTask<ColorBandObservation> ColorBandsAsync(ScreenFrame frame, ColorBandRequest request, CancellationToken token = default)
            => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
