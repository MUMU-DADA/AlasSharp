using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Alas.Engine.Rules;

namespace Alas.Engine.Imaging;

public sealed record OcrModel(string Name, string ModelSha256, string LabelsSha256);

/// <summary>Pinned upstream ONNX image models; no Python modules/configuration are required.</summary>
public sealed class OcrModels(string directory)
{
    public static readonly SourceFile InferenceSource = new("module/ocr/al_ocr.py", "1413416468a87c24cf2b3be9bf995d6af3325084c988a03385103b882569a070");
    public static readonly SourceFile OcrSource = new("module/ocr/ocr.py", "df4a9aa2f8f883bad7cc4f53b3eb92fca6c6b5e901350bc22296fe65ae2d03f1");
    public static ImmutableArray<OcrModel> All { get; } =
    [
        new("azur_lane", "9f62d9da6106f165c49d942340a33a110e69fd2963edcbd409817b6e975af94f", "e7263c53529ccd453da7cd6c504d197a9f0458e2174a2a1c093fb980006f2a64"),
        new("azur_lane_jp", "d881b58feddb6485e965bd5591612879a317e914bb5b4e784fa521c61b648986", "e7263c53529ccd453da7cd6c504d197a9f0458e2174a2a1c093fb980006f2a64"),
        new("cnocr", "d4330aaf8cd37ca2b68c442e4993b6cd95ed28a74b95eb4b748d109a2995ea4e", "d054d5a6603b16a38822696ad553b6141d6c6dc7c4f19d66b25543413508f62d"),
        new("jp", "536c50de6280b18d4f29fd1c508089612ade380cf39a3e2f0ff1e71c6f7a68fa", "626c2e133bc0d96c2abc32610354d4a66eb2545dbc1bc98f4ff9bed6b13887f3"),
        new("tw", "55808d05ef9da2e9f4ff4c8aaeda48ad4f145f9f509950f17657f6f8f3d5c9ac", "609d66823bc971503f5d472c036918e23bc2cfbe92a86c8383abe433c57bf09d")
    ];
    public string Directory { get; } = Path.GetFullPath(directory);
    private readonly ConcurrentDictionary<string, ImmutableArray<string>> _labels = new(StringComparer.Ordinal);
    public static OcrModel Find(string name) => All.FirstOrDefault(m => m.Name == name)
        ?? throw new NotSupportedException("Unknown OCR model: " + name);
    public static string LanguageFor(string language, GameServer server) => language == "azur_lane" && server == GameServer.Jp ? "azur_lane_jp" : language;
    public async Task<ImmutableArray<string>> LabelsAsync(string name, CancellationToken token)
    {
        if (_labels.TryGetValue(name, out var cached)) return cached;
        var model = Find(name);
        byte[] data = await File.ReadAllBytesAsync(Path.Combine(Directory, name + ".labels.txt"), token);
        if (Convert.ToHexStringLower(SHA256.HashData(data)) != model.LabelsSha256)
            throw new InvalidDataException("OCR label snapshot hash mismatch: " + name);
        var labels = ImmutableArray.CreateBuilder<string>();
        labels.Add(""); // CTC blank.
        using var reader = new StringReader(new UTF8Encoding(false, true).GetString(data));
        while (reader.ReadLine() is { } line) labels.Add(line == "<space>" ? " " : line);
        var result = labels.ToImmutable();
        _labels.TryAdd(name, result);
        return result;
    }
    public static int[]? CandidateIds(ImmutableArray<string> labels, string? alphabet)
    {
        if (alphabet is null) return null;
        // Python iterates Unicode characters, not UTF-16 code units. Duplicate labels use the final id.
        var indices = labels.Select((label, index) => (label, index)).GroupBy(v => v.label)
            .ToDictionary(g => g.Key, g => g.Last().index, StringComparer.Ordinal);
        var result = new List<int> { 0 };
        foreach (var rune in alphabet.EnumerateRunes())
            result.Add(indices.TryGetValue(rune.ToString(), out int index) ? index : throw new ArgumentException("OCR alphabet contains an unknown label"));
        return result.Distinct().Order().ToArray();
    }
    public static string Decode(IReadOnlyList<int> classes, IReadOnlyList<double> probabilities, int resizedWidth, ImmutableArray<string> labels)
    {
        if (resizedWidth is < 1 or > 280 || classes.Count != 70 || probabilities.Count != 70)
            throw new InvalidDataException("Invalid OCR inference dimensions");
        int previous = 0, end = resizedWidth / 4;
        var text = new StringBuilder();
        for (int i = 0; i < classes.Count; i++)
        {
            int value = classes[i];
            double score = probabilities[i];
            if (value < 0 || value >= labels.Length || !double.IsFinite(score) || score is < 0 or > 1)
                throw new InvalidDataException("Invalid OCR inference class or probability");
            if (score <= 0.5 || end > 0 && end < classes.Count && i >= end) value = 0;
            if (value != 0 && value != previous) text.Append(labels[value]);
            previous = value;
        }
        return text.ToString();
    }
}
