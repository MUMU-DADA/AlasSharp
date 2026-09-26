using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Alas.Engine.Imaging;

/// <summary>Native Ocr/Digit/Counter/Duration post-processing belongs to C#, separate from image inference.</summary>
public static class OcrValues
{
    public const string DigitAlphabet = "0123456789IDSB";
    public const string CounterAlphabet = "0123456789/IDSB";
    public const string DurationAlphabet = "0123456789:IDSB";
    public static string CorrectDigits(string text) => text.Replace('I', '1').Replace('D', '0').Replace('S', '5').Replace('B', '8');
    public static BigInteger Digit(string text)
    {
        text = CorrectDigits(text);
        return text.Length == 0 ? BigInteger.Zero : BigInteger.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }
    public static (BigInteger Current, BigInteger Remaining, BigInteger Total) Counter(string text)
    {
        var match = Regex.Match(CorrectDigits(text), @"([0-9]+)/([0-9]+)", RegexOptions.CultureInvariant);
        if (!match.Success) return (0, 0, 0);
        var total = BigInteger.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var current = BigInteger.Min(BigInteger.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), total);
        return (current, total - current, total);
    }
    public static TimeSpan Duration(string text)
    {
        var match = Regex.Match(CorrectDigits(text), @"([0-9]{1,2}):?([0-9]{2}):?([0-9]{2})", RegexOptions.CultureInvariant);
        return !match.Success ? TimeSpan.Zero : TimeSpan.FromSeconds(
            int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 3600 +
            int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * 60 + int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture));
    }
}
