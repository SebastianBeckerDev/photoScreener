using PhotoScreener.Core;

namespace PhotoScreener.Infrastructure;
public static class PathGuard
{
    public static StringComparer Comparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    public static string Canonical(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    public static bool Within(string path, string root)
    {
        path = Canonical(path); root = Canonical(root);
        return Comparer.Equals(path, root) || path.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
    public static void ValidateNoLinks(string path)
    {
        var current = Canonical(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Links/junctions are excluded from review and export operations: " + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            var parent = Path.GetDirectoryName(current);
            if (parent == current) break;
            current = parent;
        }
    }
    public static void ValidateBinding(ExportBinding binding, IEnumerable<string> protectedFolders)
    {
        ValidateNoLinks(binding.SourceFolder); ValidateNoLinks(binding.ExportRoot);
        foreach (var folder in protectedFolders.Append(binding.SourceFolder))
            if (Within(folder, binding.ExportRoot)) throw new IOException("An export cannot equal or contain a protected source folder.");
    }
    public static void ValidateExportFile(string path, ExportBinding binding)
    {
        if (!Comparer.Equals(Canonical(Path.GetDirectoryName(path)!), Canonical(binding.ExportRoot))) throw new IOException("Export file is outside its captured flat target.");
        ValidateNoLinks(path);
        if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) throw new IOException("Export copy is not a regular file.");
    }
    public static PhotoRevision Revision(string path)
    {
        var info = new FileInfo(path); info.Refresh();
        if (!info.Exists) throw new FileNotFoundException("Photo unavailable", path);
        return new(info.Length, info.LastWriteTimeUtc.Ticks, PlatformFileSystem.Identity(path));
    }
}
