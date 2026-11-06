using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace PhotoScreener.Services;

/// <summary>
/// Pure static file-system helpers — no WPF dependency, fully testable.
/// </summary>
public static class PhotoFileService
{
    private static readonly Regex HashSuffixRe =
        new(@"^(.+)\.[A-Za-z0-9\-_]{4}$", RegexOptions.Compiled);

    /// <summary>Returns image files in the folder ordered by LastWriteTimeUtc.</summary>
    public static IReadOnlyList<FileInfo> ScanFolder(string path, IReadOnlyList<string> extensions)
    {
        if (!Directory.Exists(path)) return [];
        try
        {
            return Directory.EnumerateFiles(path)
                .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .Select(f => new FileInfo(f))
                .OrderBy(fi => fi.LastWriteTimeUtc)
                .ToList();
        }
        catch { return []; }
    }

    /// <summary>
    /// Scans the target folder and builds a lookup keyed by "originalName|size".
    /// Hash suffixes ([NAME].[XXXX].[EXT]) are stripped so they map back to the source file name.
    /// </summary>
    public static Dictionary<string, string> BuildTargetIndex(string targetFolder)
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(targetFolder)) return index;
        try
        {
            foreach (var f in Directory.EnumerateFiles(targetFolder))
            {
                var fi = new FileInfo(f);
                var originalName = StripHashSuffix(fi.Name);
                index.TryAdd(MakePickKey(originalName, fi.Length), f);
            }
        }
        catch { }
        return index;
    }

    /// <summary>"filename|size" key used for pick matching.</summary>
    public static string MakePickKey(string fileName, long size) =>
        $"{fileName}|{size}".ToLowerInvariant();

    /// <summary>
    /// If <paramref name="fileName"/> ends with a 4-char hash suffix ([NAME].[XXXX].[EXT]),
    /// returns [NAME].[EXT]. Otherwise returns the original name unchanged.
    /// </summary>
    public static string StripHashSuffix(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext  = Path.GetExtension(fileName);
        var m = HashSuffixRe.Match(stem);
        return m.Success ? m.Groups[1].Value + ext : fileName;
    }

    /// <summary>Returns the target path [NAME].[HASH4][EXT] for the given source file.</summary>
    public static string ComputeTargetPath(string sourcePath, string targetFolder)
    {
        var hash4 = ComputeHash4(sourcePath);
        var name  = Path.GetFileNameWithoutExtension(sourcePath);
        var ext   = Path.GetExtension(sourcePath);
        return Path.Combine(targetFolder, $"{name}.{hash4}{ext}");
    }

    /// <summary>MD5 of file content → first 4 chars of Base64 (filename-safe).</summary>
    public static string ComputeHash4(string path)
    {
        using var md5 = MD5.Create();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        var hash = md5.ComputeHash(stream);
        return Convert.ToBase64String(hash)[..4]
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>Recursive depth-first enumeration of all directories under <paramref name="root"/>.</summary>
    public static void CollectFoldersDfs(string root, List<string> result)
    {
        result.Add(root);
        try
        {
            foreach (var sub in Directory.GetDirectories(root)
                         .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
                CollectFoldersDfs(sub, result);
        }
        catch { /* skip inaccessible dirs */ }
    }

    /// <summary>Returns true if the folder contains at least one image file present in the target index.</summary>
    public static bool FolderHasPickedFiles(string folderPath,
        Dictionary<string, string> targetIndex,
        IReadOnlyList<string> extensions)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(folderPath))
            {
                if (!extensions.Contains(Path.GetExtension(f).ToLowerInvariant())) continue;
                var fi = new FileInfo(f);
                if (targetIndex.ContainsKey(MakePickKey(fi.Name, fi.Length))) return true;
            }
        }
        catch { /* skip inaccessible dirs */ }
        return false;
    }
}
