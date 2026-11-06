using System.Security.Cryptography;
using PhotoScreener.Core;
using PhotoScreener.Infrastructure;
using Xunit;

namespace PhotoScreener.Tests;
public sealed class ComparisonTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "photoscreener-compare-" + Guid.NewGuid().ToString("N"));
    private readonly FolderComparer comparer = new();
    private readonly SourceCatalog catalog = new();
    public ComparisonTests() => Directory.CreateDirectory(root);
    private Photo Source(string name, string content)
    { var path = Path.Combine(root, name); File.WriteAllText(path, content); return catalog.Enumerate(root, default).Single(p => p.Path == path); }
    private string Export(string name, string content)
    { var target = Path.Combine(root, "export"); Directory.CreateDirectory(target); var path = Path.Combine(target, name); File.WriteAllText(path, content); return path; }
    private Observation Compare(Photo photo, params Photo[] sources) => comparer.Compare(photo, comparer.Index(new(root, Path.Combine(root, "export")), default), sources.Length == 0 ? [photo] : sources, default);
    [Fact] public void RecoversEightPicksWithoutMetadataAndKeepsUnreviewedUnknown()
    {
        var sources = Enumerable.Range(0, 10).Select(i => Source($"{i}.JPG", $"bytes-{i}")).ToArray();
        foreach (var photo in sources.Take(8)) Export(LegacyExportName.FromDigest(photo.Name, MD5.HashData(File.ReadAllBytes(photo.Path))), File.ReadAllText(photo.Path));
        Assert.Equal(8, sources.Count(p => Compare(p).Picked == true));
        Assert.Equal(2, sources.Count(p => Compare(p).Picked == false));
        Assert.False(Directory.Exists(Path.Combine(root, "export", ".photoscreener")));
    }
    [Fact] public void RejectsEqualLengthFalsePositiveAndKeepsAllVerifiedCopies()
    {
        var photo = Source("a.JPG", "abc");
        Export("a.kAFQ.JPG", "abc"); Export("a.zzzz.JPG", "abc"); Export("a.wrong.JPG", "xyz");
        var result = Compare(photo); Assert.True(result.Picked); Assert.Equal(2, result.Copies.Count);
        File.WriteAllText(Path.Combine(root, "export", "a.kAFQ.JPG"), "xyz");
        result = Compare(photo); Assert.Single(result.Copies);
    }
    [Fact] public void BareCopiesAndNaturalSuffixRequireExplicitOwnership()
    {
        var a = Source("a.JPG", "abc"); var dotted = Source("a.abCD.JPG", "abc");
        var exported = Export("a.abCD.JPG", "abc");
        Assert.Equal(PickStatus.Ambiguous, Compare(a, a, dotted).Status);
        Assert.Equal(PickStatus.Ambiguous, Compare(dotted, a, dotted).Status);
        comparer.SetOwner(exported, dotted.Name);
        Assert.False(Compare(a, a, dotted).Picked); Assert.True(Compare(dotted, a, dotted).Picked);
    }
    [Fact] public void MissingPreviouslyAvailableExportIsUnavailable()
    {
        var photo = Source("a.JPG", "abc"); var target = Path.Combine(root, "export");
        Assert.False(Compare(photo).Picked); Directory.CreateDirectory(target); Assert.False(Compare(photo).Picked);
        Directory.Move(target, target + ".disconnected"); Assert.Null(Compare(photo).Picked);
        Assert.Equal(PickStatus.Unavailable, Compare(photo).Status);
    }
    [Fact] public void SourceVersionChangesInvalidateEarlierAssociation()
    {
        var photo = Source("a.JPG", "abc"); Export("a.kAFQ.JPG", "abc"); Assert.True(Compare(photo).Picked);
        File.WriteAllText(photo.Path, "xyz"); Assert.Equal(PickStatus.Outdated, Compare(photo).Status);
        var current = catalog.Enumerate(root, default).Single(); Assert.False(Compare(current).Picked);
        Assert.Equal("abc", File.ReadAllText(Path.Combine(root, "export", "a.kAFQ.JPG")));
    }
    public void Dispose() => Directory.Delete(root, true);
}
