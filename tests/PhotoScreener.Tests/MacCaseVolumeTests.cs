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

public sealed class MacCaseVolumeTests
{
    [MacCaseVolumeTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task DifferentlyCasedOriginalNamesRemainIndependentOrFailExplicitlyOnAnUnrepresentableTarget(bool caseSensitiveTarget)
    {
        await using var sourceVolume = await SmallExportVolume.Create(caseSensitive: true);
        await using var targetVolume = await SmallExportVolume.Create(caseSensitive: caseSensitiveTarget);
        var source = Path.Combine(sourceVolume.Root, "photos"); Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.JPG"), "abc"); File.WriteAllText(Path.Combine(source, "A.JPG"), "abc");
        Assert.Equal(2, Directory.EnumerateFiles(source).Count()); // Prove the actual source is case-sensitive.
        var probe = Path.Combine(targetVolume.Root, "probe"); File.WriteAllText(probe, "case probe");
        Assert.Equal(!caseSensitiveTarget, File.Exists(Path.Combine(targetVolume.Root, "PROBE"))); File.Delete(probe);
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
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(binding.ExportRoot, ".photoscreener", "staging")));
    }
}
