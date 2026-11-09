using PhotoScreener.Core;

namespace PhotoScreener.Infrastructure;

public static class AssociationSources
{
    // The UI publishes rows incrementally. Ownership must also account for names
    // not published yet, without loading metadata for every image on every toggle.
    public static IReadOnlyList<Photo> Read(Photo requested, ExportIndex index, IEnumerable<ExportBinding> bindings, CancellationToken token)
    {
        var entryNames = index.Candidates.GetValueOrDefault(FolderComparer.Key(requested.Name, requested.Revision.Length))?.Select(e => e.Name).Distinct().ToArray() ?? [];
        var result = new List<Photo> { requested };
        foreach (var folder in bindings.Where(b => PathGuard.Comparer.Equals(b.ExportRoot, index.Binding.ExportRoot)).Select(b => b.SourceFolder).Append(requested.Folder).Distinct(PathGuard.Comparer))
        {
            PathGuard.ValidateNoLinks(folder);
            var names = new HashSet<string>(PathGuard.Comparer);
            foreach (var path in Directory.EnumerateFiles(folder))
            {
                token.ThrowIfCancellationRequested();
                var name = Path.GetFileName(path);
                if (SourceCatalog.IsNef(requested.Name) ? !SourceCatalog.IsNef(name) : !SourceCatalog.IsImage(name)) continue;
                if (!names.Add(name)) throw new IOException("Source image names differ only by case and cannot be safely distinguished in this session: " + name);
                if (PathGuard.Comparer.Equals(path, requested.Path) ||
                    PlatformFileSystem.AssociationName(name) != PlatformFileSystem.AssociationName(requested.Name) && !entryNames.Any(entry => LegacyExportName.Candidate(name, entry))) continue;
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                result.Add(new(path, folder, name, PathGuard.Revision(path)));
            }
        }
        return result;
    }
}
