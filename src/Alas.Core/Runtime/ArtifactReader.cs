namespace Alas.Runtime;

/// <summary>
/// Observers must not deny writes to live artifacts on Windows. A read may see
/// an incomplete document; callers still report parse errors and re-read later.
/// This changes file sharing only, never the evidence validation rules.
/// </summary>
internal static class ArtifactReader
{
    private static StreamReader Open(string path) => new(new FileStream(path, FileMode.Open,
        FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));

    public static string ReadAllText(string path)
    {
        using var reader = Open(path);
        return reader.ReadToEnd();
    }

    public static IEnumerable<string> ReadLines(string path)
    {
        using var reader = Open(path);
        while (reader.ReadLine() is { } line) yield return line;
    }
}
