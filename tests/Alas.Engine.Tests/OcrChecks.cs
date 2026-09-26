using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Alas.Engine.Devices;
using Alas.Engine.Imaging;
using Alas.Engine.Rules;

namespace Alas.Engine.Tests;

internal static class OcrChecks
{
    public static async Task RunAsync(string python, string upstream, string artifacts)
    {
        foreach (var source in new[] { OcrModels.InferenceSource, OcrModels.OcrSource })
            if (Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(upstream, source.Path)))) != source.Sha256)
                throw new InvalidOperationException("OCR upstream source drifted: " + source.Path);
        string worker = Path.Combine(AppContext.BaseDirectory, "Imaging/Worker/vision_worker.py");
        string output = Path.Combine(artifacts, "native-ocr.json");
        var reference = await new ProcessRunner().RunAsync(python,
            [Path.Combine(AppContext.BaseDirectory, "native_ocr_reference.py"), upstream, output, worker], TimeSpan.FromMinutes(2));
        if (reference.ExitCode != 0) throw new InvalidOperationException("Native OCR reference failed: " + reference.Error);
        var native = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
        string models = Path.Combine(upstream, "bin/ocr_models");
        await using var vision = new PythonTemplateVision(python, worker, modelDirectory: models);
        int inferred = 0, rejectedAlphabets = 0, sequence = 0, decoded = 0, parsed = 0;
        foreach (var sample in native["cases"]!.AsArray())
        {
            var area = sample!["area"]!.AsArray().Select(v => v!.GetValue<int>()).ToArray();
            var letter = sample["letter"]!.AsArray().Select(v => v!.GetValue<int>()).ToArray();
            var frame = new ScreenFrame(++sequence, DateTimeOffset.UtcNow, await File.ReadAllBytesAsync(Path.Combine(artifacts, sample["image"]!.GetValue<string>())));
            var request = new OcrRequest(new PixelArea(area[0], area[1], area[2], area[3]), sample["language"]!.GetValue<string>(),
                sample["alphabet"]?.GetValue<string>(), letter[0], letter[1], letter[2], sample["threshold"]!.GetValue<int>(),
                Enum.Parse<OcrPreprocessing>(sample["mode"]!.GetValue<string>(), true));
            if (sample["error"] is not null)
            {
                bool alphabetRejected = false;
                try { await vision.ReadTextAsync(frame, request); } catch (ArgumentException) { alphabetRejected = true; }
                if (!alphabetRejected) throw new InvalidOperationException("Native unsupported alphabet was accepted");
                rejectedAlphabets++;
                continue;
            }
            var observed = await vision.ReadTextAsync(frame, request);
            inferred++;
            if (observed.FrameSequence != frame.Sequence || observed.Text != sample["text"]!.GetValue<string>())
                throw new InvalidOperationException($"OCR differs from native sample {inferred}: {observed.Text} vs {sample["text"]}");
        }
        var labels = await new OcrModels(models).LabelsAsync("azur_lane", default);
        foreach (var sample in native["decode"]!.AsArray())
        {
            var text = OcrModels.Decode(sample!["ids"]!.AsArray().Select(n => n!.GetValue<int>()).ToArray(),
                sample["scores"]!.AsArray().Select(n => n!.GetValue<double>()).ToArray(), sample["width"]!.GetValue<int>(), labels);
            if (text != sample["text"]!.GetValue<string>()) throw new InvalidOperationException("Native CTC decoding differs");
            decoded++;
        }
        foreach (var sample in native["numeric"]!.AsArray())
        {
            string text = sample!["text"]!.GetValue<string>();
            string? value = null, error = null;
            try { value = OcrValues.Digit(text).ToString(System.Globalization.CultureInfo.InvariantCulture); }
            catch (FormatException) { error = nameof(FormatException); }
            var counter = OcrValues.Counter(text);
            if (value != sample["digit"]?.GetValue<string>() || error != sample["error"]?.GetValue<string>() ||
                !new[] { counter.Current.ToString(), counter.Remaining.ToString(), counter.Total.ToString() }.SequenceEqual(sample["counter"]!.AsArray().Select(n => n!.GetValue<string>())) ||
                OcrValues.Duration(text).TotalSeconds != sample["duration"]!.GetValue<double>())
                throw new InvalidOperationException("Native numeric OCR parser differs");
            parsed++;
        }
        if (OcrModels.LanguageFor("azur_lane", GameServer.Jp) != "azur_lane_jp" || OcrModels.LanguageFor("cnocr", GameServer.Jp) != "cnocr")
            throw new InvalidOperationException("Native OCR server language rule differs");
        bool rejected = false;
        try { OcrModels.CandidateIds(labels, "😀"); } catch (ArgumentException) { rejected = true; }
        if (!rejected) throw new InvalidOperationException("Unknown OCR alphabet was accepted");
        string corrupt = Path.Combine(artifacts, "corrupt-models");
        Directory.CreateDirectory(corrupt);
        await File.WriteAllTextAsync(Path.Combine(corrupt, "azur_lane.labels.txt"), "corrupt");
        rejected = false;
        try { await new OcrModels(corrupt).LabelsAsync("azur_lane", default); } catch (InvalidDataException) { rejected = true; }
        if (!rejected) throw new InvalidOperationException("Corrupt OCR labels were accepted");
        await File.WriteAllBytesAsync(Path.Combine(corrupt, "azur_lane.labels.txt"), await File.ReadAllBytesAsync(Path.Combine(models, "azur_lane.labels.txt")));
        await File.WriteAllTextAsync(Path.Combine(corrupt, "azur_lane.onnx"), "corrupt");
        await using var broken = new PythonTemplateVision(python, worker, modelDirectory: corrupt);
        rejected = false;
        try { await broken.ReadTextAsync(new ScreenFrame(1, DateTimeOffset.UtcNow, VisionChecks.Png(1, 1, [255, 255, 255])), new OcrRequest(new PixelArea(0, 0, 1, 1), "azur_lane", null)); }
        catch (InvalidDataException failure) { rejected = failure.Message.Contains("ocr_model_hash_mismatch", StringComparison.Ordinal); }
        if (!rejected) throw new InvalidOperationException("Corrupt OCR model was accepted");
        Console.WriteLine($"Native OCR: {inferred} model inferences / {rejectedAlphabets} alphabet rejections / {decoded} CTC sequences / {parsed} numeric parser cases / {native["preprocess"]} pixel preprocessing comparisons; model/labels rejection passed.");
    }
}
