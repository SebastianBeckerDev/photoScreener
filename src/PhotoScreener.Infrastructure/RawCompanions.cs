using PhotoScreener.Core;

namespace PhotoScreener.Infrastructure;

// RAW files never become review rows or establish JPEG selection. Their current
// bytes are companion exports of a same-stem JPEG in its captured source folder.
public static class RawCompanions
{
    public static IReadOnlyList<Photo> Find(IEnumerable<Photo> photos, CancellationToken token)
    {
        var result = new Dictionary<string, Photo>(PathGuard.Comparer);
        foreach (var jpeg in photos.Where(p => SourceCatalog.IsJpeg(p.Name)))
        {
            token.ThrowIfCancellationRequested(); PathGuard.ValidateNoLinks(jpeg.Folder);
            var stem = PlatformFileSystem.AssociationName(Path.GetFileNameWithoutExtension(jpeg.Name));
            var comparison = PlatformFileSystem.CaseSensitiveNames(jpeg.Folder) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            string? rawPath = null; var jpegNames = 0;
            foreach (var path in Directory.EnumerateFiles(jpeg.Folder))
            {
                token.ThrowIfCancellationRequested();
                if (!PlatformFileSystem.AssociationName(Path.GetFileNameWithoutExtension(path)).Equals(stem, comparison)) continue;
                if (SourceCatalog.IsJpeg(path)) jpegNames++;
                if (!SourceCatalog.IsNef(path)) continue;
                if (rawPath != null) throw new IOException("Multiple NEF files match " + jpeg.Name + "; companion ownership is ambiguous.");
                rawPath = path;
            }
            if (rawPath == null) continue;
            if (jpegNames != 1) throw new IOException("Multiple JPEG files share the NEF stem " + stem + "; companion ownership is ambiguous.");
            PathGuard.ValidateNoLinks(rawPath);
            result[rawPath] = new(rawPath, jpeg.Folder, Path.GetFileName(rawPath), PathGuard.Revision(rawPath))
                { SourceDirectoryIdentities = jpeg.SourceDirectoryIdentities };
        }
        return result.Values.ToArray();
    }
}
