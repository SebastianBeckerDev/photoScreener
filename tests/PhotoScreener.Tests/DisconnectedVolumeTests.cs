using PhotoScreener.Core;
using PhotoScreener.Infrastructure;
using PhotoScreener.TestFixtures;
using Xunit;
using Xunit.Abstractions;

namespace PhotoScreener.Tests;

public sealed class DisconnectedVolumeTests(ITestOutputHelper output)
{
    [FullVolumeTheory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task DetachedSourceOrExportReportsUnavailableAndRecoversCompletedPicksAfterReattach(bool disconnectSource, bool withNef)
    {
        await using var sourceVolume = await SmallExportVolume.Create();
        await using var exportVolume = await SmallExportVolume.Create();
        var source = Path.Combine(sourceVolume.Root, "photos"); Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.JPG"), "abc");
        if (withNef) File.WriteAllText(Path.Combine(source, "a.NEF"), "message digest");
        var catalog = new SourceCatalog(); var photo = Assert.Single(catalog.Enumerate(source, default));
        var binding = new ExportBinding(source, Path.Combine(exportVolume.Root, "export"));
        var comparer = new FolderComparer();
        await using var mutations = new MutationCoordinator(comparer); mutations.RegisterPhoto(photo, binding);
        MutationResult? result = null; mutations.Completed += value => result = value;
        Assert.True(mutations.Submit(new(photo, binding, true, false, 1)));
        await mutations.DrainAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Null(result!.Error); Assert.True(result.Observation.Picked);
        var exported = Directory.EnumerateFiles(binding.ExportRoot).ToDictionary(p => Path.GetFileName(p)!, File.ReadAllBytes);
        Assert.Equal(withNef ? 2 : 1, exported.Count);
        var unrelated = Path.Combine(binding.ExportRoot, "unrelated.txt"); File.WriteAllText(unrelated, "preserve");
        Assert.False(File.Exists(Path.Combine(binding.ExportRoot, ".photoscreener", "review.json")));
        Assert.True(comparer.Compare(photo, comparer.Index(binding, default), [photo], default).Picked);

        var detached = disconnectSource ? sourceVolume : exportVolume;
        var format = detached.Drive.DriveFormat;
        await detached.Disconnect();
        var unavailable = comparer.Compare(photo, comparer.Index(binding, default), [photo], default);
        Assert.Equal(PickStatus.Unavailable, unavailable.Status); Assert.Null(unavailable.Picked);
        var publicationPhases = 0; mutations.Phase = _ => publicationPhases++;
        Assert.True(mutations.Submit(new(photo, binding, false, false, 2)));
        await mutations.DrainAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.NotNull(result.Error); Assert.Equal(PickStatus.Unavailable, result.Observation.Status); Assert.Null(result.Observation.Picked);
        Assert.False(result.Desired); Assert.Equal(0, publicationPhases);
        if (disconnectSource) AssertExportsPreserved();
        else Assert.False(Directory.Exists(binding.ExportRoot)); // Do not create a replacement below an empty mountpoint.

        await detached.Reconnect();
        var refreshed = Assert.Single(catalog.Enumerate(source, default)); mutations.RegisterPhoto(refreshed, binding);
        AssertExportsPreserved();
        Assert.True(comparer.Compare(refreshed, comparer.Index(binding, default), [refreshed], default).Picked);
        Assert.False(File.Exists(Path.Combine(binding.ExportRoot, ".photoscreener", "review.json")));
        Assert.True(mutations.Submit(new(refreshed, binding, false, false, 3)));
        await mutations.DrainAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Null(result.Error); Assert.False(result.Observation.Picked);
        Assert.DoesNotContain(Directory.EnumerateFiles(binding.ExportRoot), p => SourceCatalog.IsImage(p) || SourceCatalog.IsNef(p));
        Assert.Equal("preserve", File.ReadAllText(unrelated));
        Assert.Equal("abc", File.ReadAllText(refreshed.Path));
        if (withNef) Assert.Equal("message digest", File.ReadAllText(Path.Combine(source, "a.NEF")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(binding.ExportRoot, ".photoscreener", "staging")));
        Assert.False(comparer.Compare(refreshed, comparer.Index(binding, default), [refreshed], default).Picked);
        output.WriteLine($"Native {format} private image detach/reattach; source={disconnectSource}; NEF={withNef}; unavailable without publication, completed bytes recovered and explicit unpick succeeded.");

        void AssertExportsPreserved()
        {
            foreach (var copy in exported) Assert.Equal(copy.Value, File.ReadAllBytes(Path.Combine(binding.ExportRoot, copy.Key!)));
            Assert.Equal("preserve", File.ReadAllText(unrelated));
        }
    }
}
