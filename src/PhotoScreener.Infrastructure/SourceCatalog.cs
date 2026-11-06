using PhotoScreener.Core;

namespace PhotoScreener.Infrastructure;
public interface IReadOnlySource
{
    IEnumerable<Photo> Enumerate(string folder, CancellationToken token);
    FileStream Open(Photo photo);
}
public sealed class SourceCatalog : IReadOnlySource
{
    public static bool IsImage(string path) => Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png";
    public IEnumerable<Photo> Enumerate(string folder, CancellationToken token)
    {
        PathGuard.ValidateNoLinks(folder);
        var directoryIdentities = new List<string>();
        for (var ancestor = PathGuard.Canonical(folder); ancestor != null; ancestor = Path.GetDirectoryName(ancestor))
            directoryIdentities.Add(PlatformFileSystem.DirectoryIdentity(ancestor));
        foreach (var path in Directory.EnumerateFiles(folder))
        {
            token.ThrowIfCancellationRequested();
            if (!IsImage(path)) continue;
            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            yield return new Photo(path, folder, info.Name, PathGuard.Revision(path)) { SourceDirectoryIdentities = directoryIdentities };
        }
    }
    public FileStream Open(Photo photo)
    {
        PathGuard.ValidateNoLinks(photo.Path);
        var stream = PlatformFileSystem.OpenOwned(photo.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024);
        if (PlatformFileSystem.Identity(stream.SafeFileHandle, out _) != photo.Revision.Identity || PathGuard.Revision(photo.Path) != photo.Revision) { stream.Dispose(); throw new IOException("Source changed; refresh before picking."); }
        return stream;
    }
}
public sealed class ExportBindingResolver
{
    private readonly object gate = new();
    private readonly Dictionary<string, string> overrides = new(PathGuard.Comparer);
    public ExportBinding Resolve(string folder) { lock (gate) return new(PathGuard.Canonical(folder), overrides.GetValueOrDefault(PathGuard.Canonical(folder)) ?? Path.Combine(PathGuard.Canonical(folder), "export")); }
    public void Bind(string folder, string target, IEnumerable<string> protectedFolders)
    {
        var binding = new ExportBinding(PathGuard.Canonical(folder), PathGuard.Canonical(target));
        PathGuard.ValidateBinding(binding, protectedFolders);
        lock (gate) overrides[binding.SourceFolder] = binding.ExportRoot;
    }
    // Call on a worker: native directory identities also exclude case/short-path aliases.
    public bool Excluded(string path)
    {
        string[] targets; lock (gate) targets = overrides.Values.Distinct(PathGuard.Comparer).ToArray();
        if (Path.GetFileName(path).Equals("export", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(path).Equals(".photoscreener", StringComparison.OrdinalIgnoreCase) || targets.Any(root => PathGuard.Within(path, root))) return true;
        if (targets.Length == 0) return false;
        try
        {
            var identities = targets.Where(Directory.Exists).Select(PlatformFileSystem.DirectoryIdentity).ToHashSet();
            if (identities.Count == 0) return false;
            var ancestor = PathGuard.Canonical(path);
            while (ancestor != null)
            {
                if (Directory.Exists(ancestor) && identities.Contains(PlatformFileSystem.DirectoryIdentity(ancestor))) return true;
                ancestor = Path.GetDirectoryName(ancestor);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
        return false;
    }
}
