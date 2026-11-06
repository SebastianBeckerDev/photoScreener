using System.Text.RegularExpressions;

namespace PhotoScreener.Infrastructure;

public static partial class LegacyExportName
{
    public static string FromDigest(string originalName, ReadOnlySpan<byte> md5) =>
        Path.GetFileNameWithoutExtension(originalName) + "." +
        Convert.ToBase64String(md5)[..4].Replace('+', '-').Replace('/', '_') + Path.GetExtension(originalName);
    public static string StripSuffix(string name)
    {
        var match = Suffix().Match(Path.GetFileNameWithoutExtension(name));
        return match.Success ? match.Groups[1].Value + Path.GetExtension(name) : name;
    }
    public static bool Candidate(string original, string exported) =>
        string.Equals(original, exported, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(original, StripSuffix(exported), StringComparison.OrdinalIgnoreCase);
    [GeneratedRegex(@"^(.+)\.[A-Za-z0-9_-]{4}$")]
    private static partial Regex Suffix();
}
