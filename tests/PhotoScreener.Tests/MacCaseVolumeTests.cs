using PhotoScreener.Core;
using PhotoScreener.Infrastructure;
using PhotoScreener.TestFixtures;
using Xunit;

namespace PhotoScreener.Tests;

public sealed class MacCaseVolumeTheoryAttribute : TheoryAttribute
{
    public MacCaseVolumeTheoryAttribute()
    {
        if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("LUMAPICK_TEST_DISK_IMAGES") != "1")
            Skip = "Opt-in native APFS/APFSX image scenario; macOS CI enables disk images.";
    }
}

[Collection("Native disk images")]
public sealed class MacCaseVolumeTests
{
    [MacCaseVolumeTheory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task DifferentlyCasedOriginalNamesRemainIndependentOrFailExplicitlyOnAnUnrepresentableTarget(bool caseSensitiveTarget, bool withNef)
    {
        await using var sourceVolume = await SmallExportVolume.Create(caseSensitive: true);
        await using var targetVolume = await SmallExportVolume.Create(caseSensitive: caseSensitiveTarget);
        var source = Path.Combine(sourceVolume.Root, "photos"); Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.JPG"), "abc"); File.WriteAllText(Path.Combine(source, "A.JPG"), "abc");
        Assert.Equal(2, Directory.EnumerateFiles(source).Count()); // Prove the actual source is case-sensitive.
        Assert.True(PlatformFileSystem.CaseSensitiveNames(source));
        if (withNef) { File.WriteAllText(Path.Combine(source, "a.NEF"), "abc"); File.WriteAllText(Path.Combine(source, "A.NEF"), "abc"); }
        var probe = Path.Combine(targetVolume.Root, "probe"); File.WriteAllText(probe, "case probe");
        Assert.Equal(!caseSensitiveTarget, File.Exists(Path.Combine(targetVolume.Root, "PROBE"))); File.Delete(probe);
        Assert.Equal(caseSensitiveTarget, PlatformFileSystem.CaseSensitiveNames(targetVolume.Root));
        var photos = new SourceCatalog().Enumerate(source, default).ToArray();
        var lower = photos.Single(p => p.Name == "a.JPG"); var upper = photos.Single(p => p.Name == "A.JPG");
        Assert.NotEqual(lower.Revision.Identity, upper.Revision.Identity);
        var binding = new ExportBinding(source, Path.Combine(targetVolume.Root, "export"));
        var comparer = new FolderComparer();
        await using var mutations = new MutationCoordinator(comparer); mutations.Register(photos.Select(photo => (photo, binding)).ToArray());
        MutationResult? result = null; mutations.Completed += value => result = value;
        mutations.Submit(new(lower, binding, true, false, 1)); await mutations.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(result!.Error); Assert.True(result.Observation.Picked);
        var first = Assert.Single(Directory.EnumerateFiles(binding.ExportRoot, "*.JPG")); Assert.Equal("a.kAFQ.JPG", Path.GetFileName(first));

        mutations.Submit(new(upper, binding, true, false, 2)); await mutations.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        if (caseSensitiveTarget)
        {
            Assert.Null(result!.Error); Assert.True(result.Observation.Picked);
            Assert.Equal(2, Directory.EnumerateFiles(binding.ExportRoot, "*.JPG").Count());
            Assert.True(comparer.Compare(upper, comparer.Index(binding, default), photos, default).Picked);
            mutations.Submit(new(upper, binding, false, false, 3)); await mutations.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(result.Error); Assert.False(result.Observation.Picked);
        }
        else
        {
            Assert.NotNull(result!.Error);
            Assert.False(result.Observation.Picked);
            Assert.False(comparer.Compare(upper, comparer.Index(binding, default), photos, default).Picked);
        }
        Assert.Equal(first, Assert.Single(Directory.EnumerateFiles(binding.ExportRoot, "*.JPG")));
        Assert.Equal("abc", File.ReadAllText(first));
        Assert.True(comparer.Compare(lower, comparer.Index(binding, default), photos, default).Picked);
        Assert.Equal("abc", File.ReadAllText(lower.Path)); Assert.Equal("abc", File.ReadAllText(upper.Path));
        if (withNef)
        {
            Assert.Equal("a.kAFQ.NEF", Path.GetFileName(Assert.Single(Directory.EnumerateFiles(binding.ExportRoot, "*.NEF"))));
            Assert.Equal("abc", File.ReadAllText(Path.Combine(binding.ExportRoot, "a.kAFQ.NEF")));
            Assert.Equal("abc", File.ReadAllText(Path.Combine(source, "a.NEF"))); Assert.Equal("abc", File.ReadAllText(Path.Combine(source, "A.NEF")));
        }
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(binding.ExportRoot, ".photoscreener", "staging")));
    }

    [MacCaseVolumeTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task CanonicallyEquivalentUnicodeNamesRecoverAndUnpickExistingExportsWithoutRenamingOriginals(bool caseSensitive)
    {
        await using var volume = await SmallExportVolume.Create(caseSensitive);
        var source = Path.Combine(volume.Root, "photos"); Directory.CreateDirectory(source);
        var original = Path.Combine(source, "Cafe\u0301.JPG"); File.WriteAllText(original, "abc");
        Assert.True(File.Exists(Path.Combine(source, "Caf\u00e9.JPG"))); // Native APFS Unicode equivalence.
        var photo = Assert.Single(new SourceCatalog().Enumerate(source, default));
        var binding = new ExportBinding(source, Path.Combine(volume.Root, "export")); Directory.CreateDirectory(binding.ExportRoot);
        var exported = Path.Combine(binding.ExportRoot, "Caf\u00e9.kAFQ.JPG"); File.WriteAllText(exported, "abc");
        var originalName = Path.GetFileName(Assert.Single(Directory.EnumerateFiles(source)));
        var comparer = new FolderComparer();
        Assert.True(comparer.Compare(photo, comparer.Index(binding, default), [photo], default).Picked);
        Assert.True(comparer.Compare(photo, comparer.IndexAssociation(photo, binding, default), [photo], default).Picked);
        using (var writer = new ExportWriter(binding, [source]))
        {
            var reused = new ExportStore(comparer).Pick(photo, binding, () => true, default);
            Assert.Equal("abc", File.ReadAllText(reused.Path)); Assert.Single(Directory.EnumerateFiles(binding.ExportRoot, "*.JPG"));
        }
        await using var mutations = new MutationCoordinator(comparer); mutations.RegisterPhoto(photo, binding);
        MutationResult? result = null; mutations.Completed += value => result = value;
        mutations.Submit(new(photo, binding, true, false, 1)); await mutations.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(result!.Error); Assert.True(result.Observation.Picked); Assert.Single(Directory.EnumerateFiles(binding.ExportRoot, "*.JPG"));
        mutations.Submit(new(photo, binding, false, false, 2)); await mutations.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(result.Error); Assert.False(result.Observation.Picked); Assert.False(File.Exists(exported));
        Assert.Equal(originalName, Path.GetFileName(Assert.Single(Directory.EnumerateFiles(source)))); Assert.Equal("abc", File.ReadAllText(original));
        Assert.False(comparer.Compare(photo, comparer.Index(binding, default), [photo], default).Picked);
    }

    [MacCaseVolumeTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task CanonicallyEquivalentOriginalNamesShareOnlyIdenticalBytesInAnExplicitSharedTarget(bool caseSensitive)
    {
        await using var volume = await SmallExportVolume.Create(caseSensitive);
        var firstFolder = Path.Combine(volume.Root, "one"); var secondFolder = Path.Combine(volume.Root, "two");
        Directory.CreateDirectory(firstFolder); Directory.CreateDirectory(secondFolder);
        File.WriteAllText(Path.Combine(firstFolder, "Cafe\u0301.JPG"), "abc"); File.WriteAllText(Path.Combine(secondFolder, "Caf\u00e9.JPG"), "abc");
        var first = Assert.Single(new SourceCatalog().Enumerate(firstFolder, default)); var second = Assert.Single(new SourceCatalog().Enumerate(secondFolder, default));
        var firstBinding = new ExportBinding(firstFolder, Path.Combine(volume.Root, "export")); var secondBinding = new ExportBinding(secondFolder, firstBinding.ExportRoot);
        var comparer = new FolderComparer(); await using var mutations = new MutationCoordinator(comparer);
        mutations.Register([(first, firstBinding), (second, secondBinding)]);
        MutationResult? result = null; mutations.Completed += value => result = value;
        mutations.Submit(new(first, firstBinding, true, false, 1)); await mutations.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(result!.Error); Assert.True(result.Observation.Picked); Assert.Equal(2, result.Photos.Count);
        Assert.Single(Directory.EnumerateFiles(firstBinding.ExportRoot, "*.JPG"));
        Assert.True(comparer.Compare(second, comparer.Index(secondBinding, default), [first, second], default).Picked);
        mutations.Submit(new(second, secondBinding, false, false, 2)); await mutations.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(result.Error); Assert.False(result.Observation.Picked); Assert.Equal(2, result.Photos.Count);
        Assert.Empty(Directory.EnumerateFiles(firstBinding.ExportRoot, "*.JPG"));
        Assert.Equal("abc", File.ReadAllText(first.Path)); Assert.Equal("abc", File.ReadAllText(second.Path));
        File.WriteAllText(second.Path, "xyz"); second = Assert.Single(new SourceCatalog().Enumerate(secondFolder, default));
        mutations.RegisterPhoto(second, secondBinding);
        mutations.Submit(new(first, firstBinding, true, false, 3)); await mutations.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(result.Error); Assert.True(result.Observation.Picked); Assert.Single(result.Photos);
        Assert.NotEqual(true, comparer.Compare(second, comparer.Index(secondBinding, default), [first, second], default).Picked);
        Assert.Equal("abc", File.ReadAllText(first.Path)); Assert.Equal("xyz", File.ReadAllText(second.Path));
    }

    [MacCaseVolumeTheory]
    [InlineData(false)]
    public async Task CaseInsensitiveSourceStillRecoversLegacyCaseVariants(bool caseSensitive)
    {
        await using var volume = await SmallExportVolume.Create(caseSensitive);
        var source = Path.Combine(volume.Root, "photos"); Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.JPG"), "abc");
        Assert.False(PlatformFileSystem.CaseSensitiveNames(source));
        var photo = Assert.Single(new SourceCatalog().Enumerate(source, default));
        var binding = new ExportBinding(source, Path.Combine(volume.Root, "export")); Directory.CreateDirectory(binding.ExportRoot);
        var exported = Path.Combine(binding.ExportRoot, "A.kAFQ.jpg"); File.WriteAllText(exported, "abc");
        var comparer = new FolderComparer(); Assert.True(comparer.Compare(photo, comparer.Index(binding, default), [photo], default).Picked);
        await using var mutations = new MutationCoordinator(comparer); mutations.RegisterPhoto(photo, binding);
        MutationResult? result = null; mutations.Completed += value => result = value;
        mutations.Submit(new(photo, binding, false, false, 1)); await mutations.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(result!.Error); Assert.False(result.Observation.Picked); Assert.False(File.Exists(exported)); Assert.Equal("abc", File.ReadAllText(photo.Path));
    }
}
