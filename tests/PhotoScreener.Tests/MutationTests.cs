using System.Security.Cryptography;
using PhotoScreener.Core;
using PhotoScreener.Infrastructure;
using PhotoScreener.Services;
using Xunit;

namespace PhotoScreener.Tests;
public sealed class MutationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "photoscreener-mutate-" + Guid.NewGuid().ToString("N"));
    private readonly FolderComparer comparer = new();
    private readonly SourceCatalog catalog = new();
    private readonly ExportStore store;
    private ExportBinding Binding => new(root, Path.Combine(root, "export"));
    public MutationTests() { Directory.CreateDirectory(root); store = new(comparer); }
    private Photo Source(string name = "a.JPG", string bytes = "abc")
    { var path = Path.Combine(root, name); File.WriteAllText(path, bytes); return catalog.Enumerate(root, default).Single(p => p.Path == path); }
    private Observation Observe(Photo photo) => comparer.Compare(photo, comparer.Index(Binding, default), [photo], default);
    [Fact] public async Task AnUnrelatedLockedExportDoesNotBlockChangingTheCurrentAssociation()
    {
        var photo = Source(); Directory.CreateDirectory(Binding.ExportRoot);
        var unrelated = Path.Combine(Binding.ExportRoot, "a.another-original.JPG"); File.WriteAllText(unrelated, "preserve");
        await using var coordinator = new MutationCoordinator(comparer); coordinator.RegisterPhoto(photo, Binding);
        MutationResult? result = null; coordinator.Completed += value => result = value;
        using (var locked = new FileStream(unrelated, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            coordinator.Submit(new(photo, Binding, true, false, 1));
            await coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(result!.Error); Assert.True(result.Observation.Picked);
            coordinator.Submit(new(photo, Binding, false, false, 2));
            await coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(result!.Error); Assert.False(result.Observation.Picked);
        }
        Assert.Equal("preserve", File.ReadAllText(unrelated)); Assert.Equal("abc", File.ReadAllText(photo.Path));
        Assert.Equal(0, comparer.FullIndexScans); Assert.True(comparer.AssociationIndexScans >= 3);
    }
    [Fact] public void SourceEditedAfterStagingCannotPublishTheOldRevisionAsACompletedRequest()
    {
        var photo = Source(); using var writer = new ExportWriter(Binding, [root]);
        store.Phase = phase => { if (phase == "verify") File.WriteAllText(photo.Path, "xyz"); };
        Assert.Throws<IOException>(() => store.Pick(photo, Binding, () => true, default));
        Assert.Empty(Directory.EnumerateFiles(Binding.ExportRoot, "*.JPG"));
        Assert.Equal("xyz", File.ReadAllText(photo.Path));
    }
    [Fact] public async Task SameLengthEditWithRestoredTimestampCannotReuseOldHashOrDeleteOlderExport()
    {
        var photo = Source();
        using (var writer = new ExportWriter(Binding, [root])) store.Pick(photo, Binding, () => true, default);
        Assert.True(Observe(photo).Picked); // Prime the source and export digest caches.
        var written = File.GetLastWriteTimeUtc(photo.Path);
        File.WriteAllText(photo.Path, "xyz"); File.SetLastWriteTimeUtc(photo.Path, written);
        var changed = catalog.Enumerate(root, default).Single();
        Assert.Equal(photo.Revision.Length, changed.Revision.Length);
        Assert.Equal(photo.Revision.ModifiedTicks, changed.Revision.ModifiedTicks);
        Assert.Equal(photo.Revision.Identity, changed.Revision.Identity);
        Assert.NotNull(changed.Revision.ChangeToken);
        Assert.NotEqual(photo.Revision.ChangeToken, changed.Revision.ChangeToken);
        Assert.Equal(PickStatus.Outdated, Observe(photo).Status);
        Assert.False(Observe(changed).Picked);
        await using var coordinator = new MutationCoordinator(comparer); coordinator.RegisterPhoto(changed, Binding);
        coordinator.Submit(new(changed, Binding, false, false, 1));
        await coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("abc", File.ReadAllText(Path.Combine(Binding.ExportRoot, "a.kAFQ.JPG")));
        Assert.Equal("xyz", File.ReadAllText(photo.Path));
    }
    [Fact] public async Task EarlyToggleCannotDeleteAnExportWithAnUndiscoveredDottedNameOwner()
    {
        var photo = Source(); Source("a.kAFQ.JPG");
        Directory.CreateDirectory(Binding.ExportRoot);
        var copy = Path.Combine(Binding.ExportRoot, "a.kAFQ.JPG"); File.WriteAllText(copy, "abc");
        await using var coordinator = new MutationCoordinator(comparer);
        coordinator.RegisterPhoto(photo, Binding); // Only the first UI batch has arrived.
        MutationResult? result = null; coordinator.Completed += value => result = value;
        coordinator.Submit(new(photo, Binding, null, true, 1));
        await coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(result?.Error); Assert.Equal(PickStatus.Ambiguous, result!.Observation.Status);
        Assert.Equal("abc", File.ReadAllText(copy));
        Assert.Equal("abc", File.ReadAllText(photo.Path));
    }
    [Fact] public async Task UnpickFindsDuplicatesAddedAfterThePreviousMutationWithoutAWatcher()
    {
        var photo = Source(); await using var coordinator = new MutationCoordinator(comparer);
        coordinator.RegisterPhoto(photo, Binding); MutationResult? result = null;
        coordinator.Completed += value => result = value;
        coordinator.Submit(new(photo, Binding, true, false, 1));
        await coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var original = Assert.Single(Observe(photo).Copies);
        File.Copy(original.Path, Path.Combine(Binding.ExportRoot, "a.abCD.JPG"));
        coordinator.Submit(new(photo, Binding, false, false, 2));
        await coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(result!.Error); Assert.False(result.Observation.Picked);
        Assert.Empty(Directory.EnumerateFiles(Binding.ExportRoot, "*.JPG"));
    }
    [Fact] public async Task UnpickReportsAnExternalCopyAppearingDuringDeletionAsStillPicked()
    {
        var photo = Source();
        using (var writer = new ExportWriter(Binding, [root])) store.Pick(photo, Binding, () => true, default);
        await using var coordinator = new MutationCoordinator(comparer); coordinator.RegisterPhoto(photo, Binding);
        MutationResult? result = null; coordinator.Completed += value => result = value;
        var inserted = false;
        coordinator.Phase = phase =>
        {
            if (phase == "deleting" && !inserted)
            { File.WriteAllText(Path.Combine(Binding.ExportRoot, "a.abCD.JPG"), "abc"); inserted = true; }
        };
        coordinator.Submit(new(photo, Binding, false, false, 1));
        await coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(inserted); Assert.NotNull(result!.Error); Assert.True(result.Observation.Picked);
        Assert.Equal("abc", File.ReadAllText(Path.Combine(Binding.ExportRoot, "a.abCD.JPG")));
    }
    [Fact] public void CopyDeleteRepickIsLegacyReadableAndSourceReadOnly()
    {
        var photo = Source();
        using (var writer = new ExportWriter(Binding, [root]))
        {
            var copy = store.Pick(photo, Binding, () => true, default);
            Assert.Equal("a.kAFQ.JPG", Path.GetFileName(copy.Path)); Assert.Equal("abc", File.ReadAllText(copy.Path));
            Assert.Equal(copy.Path, PhotoFileService.BuildTargetIndex(Binding.ExportRoot)[PhotoFileService.MakePickKey(photo.Name, 3)]);
            store.Remove(photo, Binding, Observe(photo).Copies, default); Assert.False(Observe(photo).Picked);
            var again = store.Pick(photo, Binding, () => true, default); Assert.Equal(copy.Path, again.Path);
        }
        Assert.Equal("abc", File.ReadAllText(photo.Path));
    }
    [Fact] public void CollisionNeverOverwritesAndIdenticalDestinationReuses()
    {
        var photo = Source();
        using var writer = new ExportWriter(Binding, [root]);
        var expected = Path.Combine(Binding.ExportRoot, "a.kAFQ.JPG"); File.WriteAllText(expected, "xyz");
        Assert.Throws<IOException>(() => store.Pick(photo, Binding, () => true, default)); Assert.Equal("xyz", File.ReadAllText(expected));
        File.WriteAllText(expected, "abc"); var identity = PlatformFileSystem.Identity(expected);
        Assert.Equal(identity, store.Pick(photo, Binding, () => true, default).Revision.Identity);
        Assert.Equal("abc", File.ReadAllText(photo.Path));
    }
    [Fact] public void ActualShortSuffixCollisionIsRejected()
    {
        var hashes = new Dictionary<string, byte[]>(); byte[]? first = null, second = null;
        for (var i = 0; i < 100000; i++)
        {
            var bytes = BitConverter.GetBytes(i); var key = LegacyExportName.FromDigest("a.JPG", MD5.HashData(bytes));
            if (hashes.TryGetValue(key, out first)) { second = bytes; break; } hashes[key] = bytes;
        }
        Assert.NotNull(second); Assert.NotNull(first);
        var path = Path.Combine(root, "a.JPG"); File.WriteAllBytes(path, first!); var photo = catalog.Enumerate(root, default).Single();
        using var writer = new ExportWriter(Binding, [root]); var old = store.Pick(photo, Binding, () => true, default);
        File.WriteAllBytes(path, second!); var changed = catalog.Enumerate(root, default).Single();
        Assert.Throws<IOException>(() => store.Pick(changed, Binding, () => true, default));
        Assert.Equal(first!, File.ReadAllBytes(old.Path)); Assert.Equal(second!, File.ReadAllBytes(path));
    }
    [Fact] public void RemovesEveryVerifiedCopyAndPreservesOlderVersionsAndUnrelatedFiles()
    {
        var photo = Source(); using var writer = new ExportWriter(Binding, [root]); var old = store.Pick(photo, Binding, () => true, default);
        File.WriteAllText(photo.Path, "xyz"); var changed = catalog.Enumerate(root, default).Single(); var current = store.Pick(changed, Binding, () => true, default);
        File.Copy(current.Path, Path.Combine(Binding.ExportRoot, "a.abCD.JPG"));
        var unrelated = Path.Combine(Binding.ExportRoot, "keep.txt"); File.WriteAllText(unrelated, "keep");
        var result = Observe(changed); Assert.Equal(2, result.Copies.Count); store.Remove(changed, Binding, result.Copies, default);
        Assert.False(Observe(changed).Picked); Assert.Equal("abc", File.ReadAllText(old.Path)); Assert.Equal("keep", File.ReadAllText(unrelated));
        Assert.Equal("xyz", File.ReadAllText(photo.Path));
    }
    [Fact] public void ReplacedDeletionTargetIsPreserved()
    {
        var photo = Source(); using var writer = new ExportWriter(Binding, [root]); var copy = store.Pick(photo, Binding, () => true, default);
        File.Move(copy.Path, copy.Path + ".old"); File.WriteAllText(copy.Path, "xyz");
        Assert.Throws<IOException>(() => store.Remove(photo, Binding, [copy], default));
        Assert.Equal("xyz", File.ReadAllText(copy.Path)); Assert.Equal("abc", File.ReadAllText(photo.Path));
    }
    [Fact] public void CanceledStagingNeverBecomesAPick()
    {
        var photo = Source(); using var writer = new ExportWriter(Binding, [root]);
        Assert.Throws<OperationCanceledException>(() => store.Pick(photo, Binding, () => false, default));
        Assert.False(Observe(photo).Picked); Assert.Empty(Directory.EnumerateFiles(Path.Combine(Binding.ExportRoot, ".photoscreener", "staging")));
    }
    [Fact] public void SecondWriterCannotOwnPendingTarget()
    {
        Source(); using var writer = new ExportWriter(Binding, [root]);
        Assert.Throws<IOException>(() => new ExportWriter(Binding, [root]));
    }
    [Fact] public async Task WindowsLockedDuplicateRetainsObservedPickAndCanBeRetried()
    {
        if (!OperatingSystem.IsWindows()) return;
        var photo = Source();
        string duplicate;
        using (var writer = new ExportWriter(Binding, [root]))
        { var copy = store.Pick(photo, Binding, () => true, default); duplicate = Path.Combine(Binding.ExportRoot, "a.abCD.JPG"); File.Copy(copy.Path, duplicate); }
        await using var coordinator = new MutationCoordinator(comparer); coordinator.Register([(photo, Binding)]);
        MutationResult? result = null; coordinator.Completed += completed => result = completed;
        using (var locked = new FileStream(duplicate, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            coordinator.Submit(new(photo, Binding, false, false, 1)); await coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(result?.Error); Assert.True(result!.Observation.Picked); Assert.True(Observe(photo).Picked);
        }
        coordinator.Submit(new(photo, Binding, false, false, 2)); await coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(result!.Error); Assert.False(result.Observation.Picked); Assert.False(Observe(photo).Picked);
        Assert.Equal("abc", File.ReadAllText(photo.Path));
    }
    [Fact] public void VerificationFailurePreservesSourceAndPublishesNoFinalCopy()
    {
        var photo = Source(); using var writer = new ExportWriter(Binding, [root]);
        store.Phase = phase => { if (phase == "verify") throw new IOException("Injected verification failure"); };
        Assert.Throws<IOException>(() => store.Pick(photo, Binding, () => true, default));
        Assert.False(Observe(photo).Picked); Assert.Equal("abc", File.ReadAllText(photo.Path));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(Binding.ExportRoot, ".photoscreener", "staging")));
    }
    [Fact] public async Task LatestIntentCancelsCopyAndConvergesAfterNavigationContextChanges()
    {
        var photo = Source(); await using var coordinator = new MutationCoordinator(comparer);
        coordinator.Register([(photo, Binding)]);
        using var staged = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        coordinator.Phase = phase => { if (phase == "staging") { staged.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); } };
        coordinator.Submit(new(photo, Binding, true, false, 1)); Assert.True(staged.Wait(TimeSpan.FromSeconds(10)));
        coordinator.Submit(new(photo, Binding, false, false, 2)); release.Set();
        await coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.False(Observe(photo).Picked);
        coordinator.Submit(new(photo, Binding, true, false, 3)); await coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.True(Observe(photo).Picked);
        Assert.Equal("abc", File.ReadAllText(photo.Path));
    }
    [Fact] public async Task SharedTargetLatestSequenceWinsAcrossEqualNamesAndBytes()
    {
        var folderA = Path.Combine(root, "one"); var folderB = Path.Combine(root, "two"); Directory.CreateDirectory(folderA); Directory.CreateDirectory(folderB);
        File.WriteAllText(Path.Combine(folderA, "a.JPG"), "abc"); File.WriteAllText(Path.Combine(folderB, "a.JPG"), "abc");
        var a = catalog.Enumerate(folderA, default).Single(); var b = catalog.Enumerate(folderB, default).Single();
        var target = Path.Combine(root, "shared"); var bindingA = new ExportBinding(folderA, target); var bindingB = new ExportBinding(folderB, target);
        await using var coordinator = new MutationCoordinator(comparer); coordinator.Register([(a, bindingA), (b, bindingB)]);
        coordinator.Submit(new(a, bindingA, true, false, 1)); coordinator.Submit(new(b, bindingB, false, false, 2));
        await coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(comparer.Compare(a, comparer.Index(bindingA, default), [a, b], default).Picked);
        coordinator.Submit(new(b, bindingB, true, false, 3)); await coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(comparer.Compare(a, comparer.Index(bindingA, default), [a, b], default).Picked);
        Assert.True(comparer.Compare(b, comparer.Index(bindingB, default), [a, b], default).Picked);
    }
    public void Dispose() => Directory.Delete(root, true);
}
