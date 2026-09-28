using System.Text.Json.Nodes;
using Alas.Engine.Devices;
using Alas.Engine.Imaging;
using Alas.Engine.Rules;
using Alas.Engine.Runtime;

namespace Alas.Engine.Tests;

internal static partial class MapViewChecks
{
    internal static async Task SubmarineCameraAsync(string upstream, JsonArray sights)
    {
        var frame = new ScreenFrame(1, DateTimeOffset.UnixEpoch, ReadOnlyMemory<byte>.Empty);
        var files = new AssetFiles(Path.Combine(upstream, "assets"));
        foreach (var sample in sights)
        {
            var target = Alas.Engine.Rules.Cell.Parse(sample!["target"]!.GetValue<string>());
            var expected = Alas.Engine.Rules.Cell.Parse(sample["camera"]!.GetValue<string>());
            var clock = new FakeClock(); var input = new Input(); var patches = new SubmarinePatches(.9);
            var recognition = new GridRecognition(patches, files, GameServer.Cn, new());
            var geometry = Regular(new(362, 227.5), columns: 5);
            var source = new Source(i => new(frame with { Sequence = i + 1 }, geometry), clock);
            var camera = new MapCamera(Map(), new(5, 4), new(frame, geometry), source, input, recognition,
                new(new FixedEvidence(null)), new() { Predict = false, Optimize = false }, clock: clock);
            var result = await ((IMapScanCamera)camera).InspectSubmarineAsync(target, default);
            Check(camera.Position == expected && result.Camera == expected && result.Location == target &&
                result.FrameSequence == camera.FrameSequence && result.Present && patches.SubmarineCalls > 0 &&
                patches.LastSubmarineFrame == result.FrameSequence,
                "Actual scan-camera submarine sight differs from native in_sight");
            Check(source.Captures == 0 || result.FrameSequence > frame.Sequence, "Submarine inspection reused the pre-swipe frame");
        }
        // Native global-to-local falls back to focusing the cell if the sight still leaves it offscreen.
        var fallbackClock = new FakeClock(); var fallbackInput = new Input(); var fallbackPatches = new SubmarinePatches(.85);
        var fallbackGeometry = Regular(new(262, 227.5));
        var fallbackSource = new Source(i => new(frame with { Sequence = i + 1 }, fallbackGeometry), fallbackClock);
        var fallback = new MapCamera(Map(), new(5, 4), new(frame, fallbackGeometry), fallbackSource, fallbackInput,
            new(fallbackPatches, files, GameServer.Cn, new()), new(new FixedEvidence(null)),
            new() { Predict = false, Optimize = false }, clock: fallbackClock);
        var absent = await fallback.InspectSubmarineAsync(new(1, 3));
        Check(fallback.Position == new Cell(1, 3) && !absent.Present && fallbackInput.Gestures.Count == 2,
            "Offscreen submarine did not refocus on the cell or threshold equality became a match");

        foreach (bool stale in new[] { false, true })
        {
            var clock = new FakeClock(); var input = new Input(); var patches = new SubmarinePatches(.9) { Stale = stale };
            var source = new Source(i => new(frame with { Sequence = i + 1 }, fallbackGeometry), clock);
            var camera = new MapCamera(Map(), new(5, 4), new(frame, fallbackGeometry), source, input,
                new(patches, files, GameServer.Cn, new()), new(new FixedEvidence(null)),
                new() { Predict = false, Optimize = false }, clock: clock);
            if (!stale) input.Fail = true;
            bool rejected = false;
            try { await camera.InspectSubmarineAsync(new(5, 4)); }
            catch (Exception error) when (stale ? error is InvalidDataException : error is IOException) { rejected = true; }
            Check(rejected, "Submarine camera concealed input or stale CV failure");
            rejected = false;
            try { await camera.InspectSubmarineAsync(new(5, 4)); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Failed submarine camera remained reusable");
        }
        Console.WriteLine($"Submarine scan-camera: {sights.Count} native sight positions, offscreen refocus, exact threshold and transport/stale-frame rejection passed with synthetic geometry.");
    }

    private sealed class SubmarinePatches(double score) : IImagePatchVision
    {
        public bool Stale { get; init; }
        public int SubmarineCalls { get; private set; }
        public long LastSubmarineFrame { get; private set; }
        public ValueTask<ImagePatchObservation> MeasurePatchAsync(ScreenFrame frame, ImagePatchRequest request, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            // Camera swipes refresh the whole view before the final spawn inspection.
            if (request.Color != new PixelColor(255, 243, 156))
                return ValueTask.FromResult(new ImagePatchObservation(frame.Sequence, 0));
            Check(request.Measure == PatchMeasure.Template && request.Processing == PatchProcessing.ColorSimilarity &&
                !request.Template.IsEmpty, "Submarine camera changed template measurement parameters");
            SubmarineCalls++;
            LastSubmarineFrame = frame.Sequence;
            return ValueTask.FromResult(new ImagePatchObservation(Stale ? frame.Sequence - 1 : frame.Sequence, score));
        }
    }
}
