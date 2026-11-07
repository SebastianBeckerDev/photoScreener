using PhotoScreener.Core;

namespace PhotoScreener.Infrastructure;

public static class BindingIdentityGuard
{
    // Run on a worker, including after a previously missing export root is created.
    // String keys cannot safely model aliases or case-sensitive Windows directories.
    public static void Validate(ExportBinding binding, IEnumerable<ExportBinding> knownBindings)
    {
        PathGuard.ValidateExportSpelling(binding.ExportRoot);
        var source = ExistingIdentity(binding.SourceFolder);
        var target = ExistingIdentity(binding.ExportRoot);
        foreach (var known in knownBindings.Distinct())
        {
            Check(binding.SourceFolder, source, known.SourceFolder, "source folder");
            Check(binding.ExportRoot, target, known.ExportRoot, "export folder");
        }
    }

    private static void Check(string path, string? identity, string otherPath, string kind)
    {
        path = PathGuard.Canonical(path); otherPath = PathGuard.Canonical(otherPath);
        if (StringComparer.Ordinal.Equals(path, otherPath)) return;
        var otherIdentity = ExistingIdentity(otherPath);
        if (identity == null || otherIdentity == null) return;
        var sameKey = PathGuard.Comparer.Equals(path, otherPath);
        if (sameKey && identity != otherIdentity)
            throw new IOException($"These {kind} paths differ only by case but identify different directories. This session cannot safely distinguish them: {path} and {otherPath}");
        if (!sameKey && identity == otherIdentity)
            throw new IOException($"These paths alias the same {kind}. Use one consistent path spelling for all bindings: {path} and {otherPath}");
    }

    private static string? ExistingIdentity(string path)
    {
        PathGuard.ValidateNoLinks(path);
        return Directory.Exists(path) ? PlatformFileSystem.DirectoryIdentity(path) : null;
    }
}
