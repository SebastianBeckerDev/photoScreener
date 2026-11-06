using PhotoScreener.Core;
using PhotoScreener.Infrastructure;
using Xunit;

namespace PhotoScreener.Tests;
public sealed class BoundaryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "photoscreener-tests-" + Guid.NewGuid().ToString("N"));
    public BoundaryTests() => Directory.CreateDirectory(root);
    [Fact] public void ExportCannotContainSourcesAndDefaultIsLazy()
    {
        var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        var resolver = new ExportBindingResolver(); var binding = resolver.Resolve(source);
        Assert.False(Directory.Exists(binding.ExportRoot));
        Assert.Throws<IOException>(() => resolver.Bind(source, root, [source]));
        Assert.Throws<IOException>(() => resolver.Bind(source, source, [source]));
        var target = Path.Combine(root, "custom"); resolver.Bind(source, target, [source]);
        Assert.Equal(target, resolver.Resolve(source).ExportRoot);
        Assert.True(resolver.Excluded(Path.Combine(source, "export")));
        Assert.True(resolver.Excluded(target));
    }
    [Fact] public void SourceReadRefusesAChangedRevision()
    {
        var path = Path.Combine(root, "a.JPG"); File.WriteAllText(path, "abc");
        var catalog = new SourceCatalog(); var photo = Assert.Single(catalog.Enumerate(root, default));
        File.WriteAllText(path, "abcd"); Assert.Throws<IOException>(() => catalog.Open(photo));
        Assert.Equal("abcd", File.ReadAllText(path));
    }
    [Fact] public void OutsideExportFilesCannotBeDeleted()
    {
        var path = Path.Combine(root, "source.JPG"); File.WriteAllText(path, "abc");
        Assert.Throws<IOException>(() => PathGuard.ValidateExportFile(path, new(root, Path.Combine(root, "export"))));
    }
    [Fact] public void FileIdentityChangesAfterReplacement()
    {
        var path = Path.Combine(root, "a.JPG"); File.WriteAllText(path, "abc"); var before = PathGuard.Revision(path);
        File.Move(path, path + ".old"); File.WriteAllText(path, "abc");
        Assert.NotEqual(before.Identity, PathGuard.Revision(path).Identity);
    }
    public void Dispose() => Directory.Delete(root, true);
}
