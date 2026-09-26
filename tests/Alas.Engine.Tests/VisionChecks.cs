using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Alas.Engine.Devices;
using Alas.Engine.Imaging;

namespace Alas.Engine.Tests;

internal static class VisionChecks
{
    public static async Task<int> RunAsync(string python, string artifacts)
    {
        int checks = 0;
        void Check(bool value, string message)
        {
            checks++;
            if (!value) throw new InvalidOperationException(message);
        }
        string worker = Path.Combine(AppContext.BaseDirectory, "Imaging", "Worker", "vision_worker.py");
        byte[] pixels = new byte[32 * 24 * 3];
        new Random(7835).NextBytes(pixels);
        byte[] crop = new byte[5 * 4 * 3];
        for (int row = 0; row < 4; row++)
            pixels.AsSpan(((9 + row) * 32 + 14) * 3, 5 * 3).CopyTo(crop.AsSpan(row * 5 * 3));
        var frame = new ScreenFrame(17, DateTimeOffset.UtcNow, Png(32, 24, pixels));
        var request = new TemplateRequest(Png(5, 4, crop), new PixelArea(7, 5, 20, 15), 0.99);
        await using (var vision = new PythonTemplateVision(python, worker))
        {
            foreach (var preprocessing in new[] { TemplatePreprocessing.Color, TemplatePreprocessing.Luma })
            {
                var observation = await vision.MatchAsync(frame, request with { Preprocessing = preprocessing });
                Check(observation.FrameSequence == 17 && observation.Matched && observation.Location == new PixelPoint(14, 9),
                    $"Pure CV {preprocessing}: failed to locate known pattern in offset area");
                var equal = await vision.MatchAsync(frame, request with { Similarity = observation.Similarity, Preprocessing = preprocessing });
                Check(!equal.Matched, "Template threshold equality must not match");
            }
            // Binarization computes Otsu independently for both images. Equal crops isolate that preprocessing.
            var binary = await vision.MatchAsync(frame, request with
            {
                SearchArea = new PixelArea(14, 9, 5, 4), Preprocessing = TemplatePreprocessing.Binary
            });
            Check(binary.Matched && binary.Location == new PixelPoint(14, 9), "Binary match differs on identical input");
            var padded = await vision.MatchAsync(frame, request with { SearchArea = new PixelArea(-2, -3, 34, 27) });
            Check(padded.Matched && padded.Location == new PixelPoint(14, 9), "Black-padded crop lost global coordinates");
            var fullAsset = await vision.MatchAsync(frame, request with
            {
                TemplatePng = frame.Png, TemplateArea = new PixelArea(14, 9, 5, 4)
            });
            Check(fullAsset.Matched && fullAsset.Location == new PixelPoint(14, 9), "Full asset source crop lost template coordinates");
            var mean = await vision.MeanColorAsync(frame, new PixelArea(14, 9, 5, 4));
            double ChannelMean(int channel) => Enumerable.Range(0, 20).Average(i => (double)crop[i * 3 + channel]);
            Check(mean.FrameSequence == frame.Sequence && Math.Abs(mean.R - ChannelMean(0)) < 1e-9 &&
                Math.Abs(mean.G - ChannelMean(1)) < 1e-9 && Math.Abs(mean.B - ChannelMean(2)) < 1e-9,
                "Mean color changed RGB channel order or pixel coverage");
            var requests = Enumerable.Range(1, 8).Select(sequence => vision.MatchAsync(frame with { Sequence = sequence }, request).AsTask()).ToArray();
            var results = await Task.WhenAll(requests);
            Check(results.Select(r => r.FrameSequence).SequenceEqual(Enumerable.Range(1, 8).Select(x => (long)x)), "Concurrent CV responses crossed frames");
            bool ocrRejected = false;
            try { await vision.ReadTextAsync(frame, new OcrRequest(request.SearchArea, "unported", null)); }
            catch (NotSupportedException) { ocrRejected = true; }
            Check(ocrRejected, "Unported OCR did not refuse");
        }
        await using (var invalid = new PythonTemplateVision(python, worker))
        {
            bool rejected = false;
            try { await invalid.MatchAsync(frame, request with { SearchArea = new PixelArea(0, 0, 4, 3) }); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Pure CV accepted a template larger than the search area");
            rejected = false;
            try { await invalid.MatchAsync(frame, request); }
            catch (IOException) { rejected = true; }
            Check(rejected, "Failed CV protocol session remained usable");
        }

        // A deliberately wrong response id must close the session, not become a valid observation.
        string animatedWorker = Path.Combine(artifacts, "animated_measurements_worker.py");
        await File.WriteAllTextAsync(animatedWorker, """
            import json, sys
            for line in sys.stdin:
                request = json.loads(line)
                print(json.dumps(dict(protocol='alas-cv/1', id=request['id'], frame=request['frame'], candidates=[
                    dict(similarity=0.9, location=[8, 6]), dict(similarity=0.95, location=[9, 7]),
                    dict(similarity=0.8, location=[10, 8])])), flush=True)
            """);
        await using (var animated = new PythonTemplateVision(python, animatedWorker))
        {
            var first = await animated.MatchAsync(frame, request with { Similarity = 0.85 });
            Check(first.Matched && first.Location == new PixelPoint(8, 6), "Animated match selected best score instead of first passing frame");
            var equality = await animated.MatchAsync(frame, request with { Similarity = 0.9 });
            Check(equality.Matched && equality.Location == new PixelPoint(9, 7), "Animated match accepted threshold equality");
            var failed = await animated.MatchAsync(frame, request with { Similarity = 0.99 });
            Check(!failed.Matched && failed.Location == new PixelPoint(10, 8), "Animated failure lost last evaluated offset");
        }
        string wrongWorker = Path.Combine(artifacts, "wrong_response_worker.py");
        await File.WriteAllTextAsync(wrongWorker, """
            import json, sys
            for line in sys.stdin:
                request = json.loads(line)
                print(json.dumps(dict(protocol='alas-cv/1', id=request['id'] + 1,
                    frame=request['frame'], similarity=1.0, location=[14, 9])), flush=True)
            """);
        await using (var wrong = new PythonTemplateVision(python, wrongWorker))
        {
            bool rejected = false;
            try { await wrong.MatchAsync(frame, request); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "CV request identity mismatch accepted");
        }
        string stalledWorker = Path.Combine(artifacts, "stalled_worker.py");
        await File.WriteAllTextAsync(stalledWorker, "import time\ntime.sleep(60)\n");
        await using (var stalled = new PythonTemplateVision(python, stalledWorker, TimeSpan.FromMilliseconds(300)))
        {
            bool timedOut = false;
            try { await stalled.MatchAsync(frame, request); }
            catch (TimeoutException) { timedOut = true; }
            Check(timedOut, "CV request deadline was ignored");
        }
        await using (var cancelled = new PythonTemplateVision(python, stalledWorker))
        using (var cancellation = new CancellationTokenSource(300))
        {
            bool wasCancelled = false;
            try { await cancelled.MatchAsync(frame, request, cancellation.Token); }
            catch (OperationCanceledException) { wasCancelled = true; }
            Check(wasCancelled, "CV cancellation became timeout or success");
        }
        var disposing = new PythonTemplateVision(python, stalledWorker);
        var pending = disposing.MatchAsync(frame, request).AsTask();
        await disposing.DisposeAsync();
        bool disposed = false;
        try { await pending; }
        catch (ObjectDisposedException) { disposed = true; }
        Check(disposed, "Disposal of a pending CV request became timeout or success");
        await disposing.DisposeAsync();
        string failingWorker = Path.Combine(artifacts, "failing_worker.py");
        await File.WriteAllTextAsync(failingWorker, "raise RuntimeError('synthetic CV root cause')\n");
        await using (var failing = new PythonTemplateVision(python, failingWorker))
        {
            bool preserved = false;
            try { await failing.MatchAsync(frame, request); }
            catch (EndOfStreamException error) { preserved = error.Message.Contains("synthetic CV root cause", StringComparison.Ordinal); }
            Check(preserved, "Worker startup error lost its root cause");
        }
        // The worker itself rejects business operations even when bypassing the typed C# client.
        string probe = Path.Combine(artifacts, "reject_business_worker.py");
        await File.WriteAllTextAsync(probe, """
            import importlib.util, json, sys
            spec = importlib.util.spec_from_file_location('pure_cv', sys.argv[1])
            module = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(module)
            for operation in ('s3_run_plan', 'ui_ensure', 'device_click', 'scheduler_run'):
                request = dict(protocol='alas-cv/1', id=1, operation=operation, frame=1,
                    image='', template='', area=[0, 0, 1, 1], preprocessing='color')
                try:
                    module.match(request)
                except ValueError as error:
                    assert str(error) == 'unsupported_operation'
                else:
                    raise AssertionError(operation)
            assert not any(name == 'module' or name.startswith('module.') for name in sys.modules)
            print('business calls rejected; no upstream modules loaded')
            """);
        var rejection = await new ProcessRunner().RunAsync(python, ["-I", probe, worker], TimeSpan.FromSeconds(30));
        Check(rejection.ExitCode == 0, "Pure CV imported upstream or accepted business calls: " + rejection.Error);
        return checks;
    }

    private static byte[] Png(int width, int height, byte[] rgb)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = 2;
        Chunk(output, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            for (int row = 0; row < height; row++)
            {
                zlib.WriteByte(0);
                zlib.Write(rgb.AsSpan(row * width * 3, width * 3));
            }
        Chunk(output, "IDAT", compressed.ToArray());
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void Chunk(Stream output, string name, byte[] content)
    {
        Span<byte> integer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(integer, content.Length);
        output.Write(integer);
        byte[] bytes = [.. Encoding.ASCII.GetBytes(name), .. content];
        output.Write(bytes);
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        BinaryPrimitives.WriteUInt32BigEndian(integer, ~crc);
        output.Write(integer);
    }
}
