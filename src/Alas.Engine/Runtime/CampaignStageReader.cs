using System.Text.RegularExpressions;
using Alas.Engine.Imaging;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed record CampaignStageReading(StageEntrance Entrance, string Raw, string Name,
    string? Chapter, string? Index);
public sealed record CampaignStages(long FrameSequence, string Chapter,
    IReadOnlyList<CampaignStageReading> Readings);

/// <summary>Upstream CampaignOcr name rules over pure CV entrance and OCR observations.</summary>
public sealed class CampaignStageReader(
    Func<ScreenFrame, StageEntranceKind, CancellationToken, ValueTask<IReadOnlyList<StageEntrance>>> find,
    IVision vision, GameServer server)
{
    public static readonly SourceFile Source = StageEntranceRules.Source;
    public const string Alphabet = "0123456789ABCDEFGHIJKLMNPQRSTUVWXYZ-";
    private static readonly Regex NumericStage = new("[0-9I]+-[0-9I]+", RegexOptions.CultureInvariant);

    public async ValueTask<CampaignStages> ObserveAsync(ScreenFrame frame, StageEntranceKind kinds,
        CancellationToken token = default)
    {
        var entrances = await find(frame, kinds, token);
        if (entrances.Count == 0) throw new InvalidDataException("No stage entrance was found");
        var readings = new List<CampaignStageReading>(entrances.Count);
        string language = OcrModels.LanguageFor("azur_lane", server);
        foreach (var entrance in entrances)
        {
            var ocr = await vision.ReadTextAsync(frame,
                new(entrance.Name.Area, language, Alphabet), token);
            if (ocr.FrameSequence != frame.Sequence)
                throw new InvalidDataException("Stage OCR used a different screenshot");
            string name = Normalize(ocr.Text);
            var (chapter, index) = Separate(name);
            readings.Add(new(entrance, ocr.Text, name, chapter, index));
        }
        var chapters = readings.Where(r => r.Chapter is not null)
            .GroupBy(r => r.Chapter!, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count()).ToArray();
        if (chapters.Length == 0 || chapters[0].Key == "0")
            throw new InvalidDataException("Stage OCR did not identify a chapter");
        return new(frame.Sequence, chapters[0].Key, readings);
    }

    public static string Normalize(string text)
    {
        string result = text.Replace("--", "-", StringComparison.Ordinal)
            .Replace("--", "-", StringComparison.Ordinal).TrimStart('-');
        result = NumericStage.Replace(result, match => match.Value.Replace('I', '1'), 1);
        if (result.Length == 2 && char.IsDigit(result[0])) result = result.Insert(1, "-");
        return result.ToLowerInvariant();
    }

    public static (string? Chapter, string? Index) Separate(string name)
    {
        name = name.Trim('-');
        if (name.Length == 0) return (null, null);
        if (name == "sp") return ("ex_sp", "1");
        if (name.StartsWith("extra", StringComparison.Ordinal) || name == "ex") return ("ex_ex", "1");
        int hyphen = name.IndexOf('-');
        if (hyphen >= 0)
        {
            if (hyphen == 0 || hyphen == name.Length - 1 || name.IndexOf('-', hyphen + 1) >= 0)
                return (null, null);
            return (name[..hyphen], name[(hyphen + 1)..]);
        }
        if (name.StartsWith("sp", StringComparison.Ordinal)) return ("sp", name[^1..]);
        if (name.Length > 1 && char.IsDigit(name[^1])) return (name[..^1], name[^1..]);
        return (null, null);
    }
}

public interface ICampaignStageObservationService
{
    ValueTask<CampaignStages> ObserveStagesAsync(StageEntranceKind kinds, CancellationToken token);
}
