using PhotoScreener.Core;

namespace PhotoScreener.Infrastructure;
public static class PathGuard
{
    public static StringComparer Comparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    public static string Canonical(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    public static bool Within(string path, string root)
    {
        path = Canonical(path); root = Canonical(root);
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return Comparer.Equals(path, root) || path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
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
        var targetIdentity = Directory.Exists(binding.ExportRoot) ? PlatformFileSystem.DirectoryIdentity(binding.ExportRoot) : null;
        foreach (var folder in protectedFolders.Append(binding.SourceFolder).Distinct(Comparer))
        {
            if (Within(folder, binding.ExportRoot)) throw new IOException("An export cannot equal or contain a protected source folder.");
            if (targetIdentity == null) continue;
            var ancestor = Canonical(folder);
            while (ancestor != null)
            {
                if (Directory.Exists(ancestor) && PlatformFileSystem.DirectoryIdentity(ancestor) == targetIdentity)
                    throw new IOException("An export aliases a protected source folder or its ancestor.");
                ancestor = Path.GetDirectoryName(ancestor);
            }
        }
        if (OperatingSystem.IsMacOS() && Canonical(binding.ExportRoot).StartsWith("/Volumes/", StringComparison.Ordinal))
        {
            var component = Canonical(binding.ExportRoot)[9..].Split('/')[0];
            if (!Directory.Exists("/Volumes/" + component)) throw new IOException("Export volume is unavailable.");
        }
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
        using var file = PlatformFileSystem.OpenOwned(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new(info.Length, info.LastWriteTimeUtc.Ticks, PlatformFileSystem.Identity(file.SafeFileHandle, out _))
            { ChangeToken = PlatformFileSystem.ChangeToken(file.SafeFileHandle) };
    }
}
