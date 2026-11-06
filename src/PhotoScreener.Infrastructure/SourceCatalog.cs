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
        foreach (var path in Directory.EnumerateFiles(folder))
        {
            token.ThrowIfCancellationRequested();
            if (!IsImage(path)) continue;
            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            yield return new(path, folder, info.Name, PathGuard.Revision(path));
        }
    }
    public FileStream Open(Photo photo)
    {
        PathGuard.ValidateNoLinks(photo.Path);
        var stream = new FileStream(photo.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        if (PathGuard.Revision(photo.Path) != photo.Revision) { stream.Dispose(); throw new IOException("Source changed; refresh before picking."); }
        return stream;
    }
}
public sealed class ExportBindingResolver
{
    private readonly Dictionary<string, string> overrides = new(PathGuard.Comparer);
    public ExportBinding Resolve(string folder) => new(PathGuard.Canonical(folder), overrides.GetValueOrDefault(PathGuard.Canonical(folder)) ?? Path.Combine(PathGuard.Canonical(folder), "export"));
    public void Bind(string folder, string target, IEnumerable<string> protectedFolders)
    {
        var binding = new ExportBinding(PathGuard.Canonical(folder), PathGuard.Canonical(target));
        PathGuard.ValidateBinding(binding, protectedFolders);
        overrides[binding.SourceFolder] = binding.ExportRoot;
    }
    public bool Excluded(string path) => Path.GetFileName(path).Equals("export", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(path).Equals(".photoscreener", StringComparison.OrdinalIgnoreCase) || overrides.Values.Any(root => PathGuard.Within(path, root));
}
