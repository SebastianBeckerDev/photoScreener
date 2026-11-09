using System.Security.Cryptography;
using System.Runtime.InteropServices;
using PhotoScreener.Core;
using PhotoScreener.Infrastructure;
using Xunit;

namespace PhotoScreener.Tests;

public sealed class RawCompanionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lumapick-nef-" + Guid.NewGuid().ToString("N"));
    private readonly SourceCatalog source = new();
    private readonly FolderComparer comparer = new();
    private readonly ExportStore store;
    private ExportBinding Binding => new(root, Path.Combine(root, "export"));
    public RawCompanionTests() { Directory.CreateDirectory(root); store = new(comparer); }
    private Photo Jpeg(string name = "DSC_001.JPG", string bytes = "jpeg")
    { File.WriteAllText(Path.Combine(root, name), bytes); return source.Enumerate(root, default).Single(p => p.Name == name); }
    private string Nef(string name = "DSC_001.NEF", string bytes = "raw")
    { var path = Path.Combine(root, name); File.WriteAllText(path, bytes); return path; }
    private string ExportName(string path) => Path.Combine(Binding.ExportRoot, LegacyExportName.FromDigest(Path.GetFileName(path), MD5.HashData(File.ReadAllBytes(path))));
    private Observation Observe(Photo jpeg) => comparer.Compare(jpeg, comparer.Index(Binding, default), [jpeg], default);

    [Fact]
    public void SelectionUsesOnlyTheJpegAndBothFilesCopyAndUnpickWithoutMetadata()
    {
        var jpeg = Jpeg(); var raw = Nef("DSC_001.nef");
        var bytes = new byte[2 * 1024 * 1024 + 17]; new Random(917).NextBytes(bytes); File.WriteAllBytes(raw, bytes);
        Assert.Equal(jpeg.Path, Assert.Single(source.Enumerate(root, default)).Path);
        using (var writer = new ExportWriter(Binding, [root]))
        {
            var jpegCopy = store.Pick(jpeg, Binding, () => true, default);
            Assert.Equal(ExportName(jpeg.Path), jpegCopy.Path); Assert.Equal(bytes, File.ReadAllBytes(ExportName(raw)));
            Assert.Equal("jpeg", File.ReadAllText(jpegCopy.Path));
        }
        Directory.Delete(Path.Combine(Binding.ExportRoot, ".photoscreener"), true);
        var recovered = Observe(jpeg); Assert.True(recovered.Picked); Assert.Single(recovered.Copies);
        using (var writer = new ExportWriter(Binding, [root])) store.Remove(jpeg, Binding, recovered.Copies, default);
        Assert.False(Observe(jpeg).Picked); Assert.Empty(Directory.EnumerateFiles(Binding.ExportRoot));
        Assert.Equal("jpeg", File.ReadAllText(jpeg.Path)); Assert.Equal(bytes, File.ReadAllBytes(raw));
    }

    [Fact]
    public void StandaloneNefOrPngWithTheSameStemDoesNotEstablishAJpegPick()
    {
        var jpeg = Jpeg(); var raw = Nef(); using var writer = new ExportWriter(Binding, [root]);
        File.Copy(raw, ExportName(raw)); Assert.False(Observe(jpeg).Picked); Assert.Single(source.Enumerate(root, default));
        File.WriteAllText(Path.Combine(root, "other.png"), "png"); Nef("other.NEF", "other raw");
        var png = source.Enumerate(root, default).Single(p => p.Name == "other.png");
        store.Pick(png, Binding, () => true, default);
        Assert.False(File.Exists(ExportName(Path.Combine(root, "other.NEF"))));
        Assert.Equal(2, source.Enumerate(root, default).Count());
    }

    [Fact]
    public void DifferentContentAtTheNefFilenamePreventsJpegPublicationWithoutOverwrite()
    {
        var jpeg = Jpeg(); var raw = Nef(); using var writer = new ExportWriter(Binding, [root]);
        var target = ExportName(raw); File.WriteAllText(target, "unrelated");
        Assert.Throws<IOException>(() => store.Pick(jpeg, Binding, () => true, default));
        Assert.False(Observe(jpeg).Picked); Assert.Equal("unrelated", File.ReadAllText(target));
        Assert.Equal("raw", File.ReadAllText(raw)); Assert.Equal("jpeg", File.ReadAllText(jpeg.Path));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(Binding.ExportRoot, ".photoscreener", "staging")));
    }

    [Fact]
    public void ChangedNefKeepsTheOlderRawVersionWhenCurrentSelectionIsRemoved()
    {
        var jpeg = Jpeg(); var raw = Nef(); using var writer = new ExportWriter(Binding, [root]);
        store.Pick(jpeg, Binding, () => true, default); var older = ExportName(raw);
        File.WriteAllText(raw, "new raw"); store.Pick(jpeg, Binding, () => true, default); var current = ExportName(raw);
        File.Copy(current, Path.Combine(Binding.ExportRoot, "DSC_001.abCD.NEF"));
        store.Remove(jpeg, Binding, Observe(jpeg).Copies, default);
        Assert.Equal("raw", File.ReadAllText(older)); Assert.False(File.Exists(current));
        Assert.False(File.Exists(Path.Combine(Binding.ExportRoot, "DSC_001.abCD.NEF")));
        Assert.False(Observe(jpeg).Picked); Assert.Equal("new raw", File.ReadAllText(raw));
    }

    [Fact]
    public void NefEditDuringJpegVerificationCannotPublishACompletedJpegPick()
    {
        var jpeg = Jpeg(); var raw = Nef(); using var writer = new ExportWriter(Binding, [root]);
        var phases = 0; store.Phase = phase => { if (phase == "verify" && ++phases == 2) File.WriteAllText(raw, "edited raw"); };
        Assert.Throws<IOException>(() => store.Pick(jpeg, Binding, () => true, default));
        Assert.Equal(2, phases); Assert.False(Observe(jpeg).Picked); Assert.Equal("edited raw", File.ReadAllText(raw));
        Assert.Equal("raw", File.ReadAllText(Assert.Single(Directory.EnumerateFiles(Binding.ExportRoot, "*.NEF"))));
        Assert.Equal("jpeg", File.ReadAllText(jpeg.Path));
    }

    [Fact]
    public void CancellationDuringRawStagingPublishesNeitherFileAndCleansOwnedParts()
    {
        var jpeg = Jpeg(); var raw = Nef(); using var writer = new ExportWriter(Binding, [root]);
        using var cancellation = new CancellationTokenSource(); store.Phase = phase => { if (phase == "staging") cancellation.Cancel(); };
        Assert.Throws<OperationCanceledException>(() => store.Pick(jpeg, Binding, () => true, cancellation.Token));
        Assert.Empty(Directory.EnumerateFiles(Binding.ExportRoot)); Assert.False(Observe(jpeg).Picked);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(Binding.ExportRoot, ".photoscreener", "staging")));
        Assert.Equal("raw", File.ReadAllText(raw));
    }

    [Fact]
    public void RawExportReplacedDuringJpegVerificationPreventsJpegPublication()
    {
        var jpeg = Jpeg(); var raw = Nef(); using var writer = new ExportWriter(Binding, [root]);
        var phases = 0; store.Phase = phase => { if (phase == "verify" && ++phases == 2) File.WriteAllText(ExportName(raw), "external replacement"); };
        Assert.Throws<IOException>(() => store.Pick(jpeg, Binding, () => true, default));
        Assert.False(Observe(jpeg).Picked); Assert.Equal("external replacement", File.ReadAllText(ExportName(raw)));
        Assert.Equal("raw", File.ReadAllText(raw)); Assert.Equal("jpeg", File.ReadAllText(jpeg.Path));
    }

    [Fact]
    public void RawDuplicateAddedDuringRemovalKeepsTheJpegSelectedWithAnError()
    {
        var jpeg = Jpeg(); Nef(); using var writer = new ExportWriter(Binding, [root]); store.Pick(jpeg, Binding, () => true, default);
        var duplicate = Path.Combine(Binding.ExportRoot, "DSC_001.abCD.NEF"); var inserted = false;
        store.Phase = phase => { if (phase == "deleting" && !inserted) { File.WriteAllText(duplicate, "raw"); inserted = true; } };
        Assert.Throws<IOException>(() => store.Remove(jpeg, Binding, Observe(jpeg).Copies, default));
        Assert.True(inserted); Assert.True(Observe(jpeg).Picked); Assert.Equal("raw", File.ReadAllText(duplicate));
        store.Phase = null; store.Remove(jpeg, Binding, Observe(jpeg).Copies, default);
        Assert.False(Observe(jpeg).Picked); Assert.False(File.Exists(duplicate));
    }

    [Fact]
    public void CompletedRawAfterCancellationNeverCountsAsPickedAndIsReusedOnRetry()
    {
        var jpeg = Jpeg(); var raw = Nef(); using var writer = new ExportWriter(Binding, [root]);
        using var cancellation = new CancellationTokenSource(); store.Phase = phase => { if (phase == "published") cancellation.Cancel(); };
        Assert.Throws<OperationCanceledException>(() => store.Pick(jpeg, Binding, () => true, cancellation.Token));
        var rawCopy = ExportName(raw); var identity = PlatformFileSystem.Identity(rawCopy);
        Assert.False(Observe(jpeg).Picked); store.Phase = null; store.Pick(jpeg, Binding, () => true, default);
        Assert.True(Observe(jpeg).Picked); Assert.Equal(identity, PlatformFileSystem.Identity(rawCopy));
        Assert.Equal("raw", File.ReadAllText(rawCopy));
    }

    [Fact]
    public void AmbiguousDottedRawOwnershipPreservesBothJpegAndRawCopies()
    {
        var jpeg = Jpeg("a.JPG"); Nef("a.NEF"); Nef("a.abCD.NEF");
        using var writer = new ExportWriter(Binding, [root]); store.Pick(jpeg, Binding, () => true, default);
        var ambiguous = Path.Combine(Binding.ExportRoot, "a.abCD.NEF"); File.WriteAllText(ambiguous, "raw");
        Assert.Throws<IOException>(() => store.Remove(jpeg, Binding, Observe(jpeg).Copies, default));
        Assert.True(Observe(jpeg).Picked); Assert.Equal("raw", File.ReadAllText(ambiguous));
    }

    [Fact]
    public void MissingRawOriginalLeavesItsUnverifiableExportPreservedOnJpegUnpick()
    {
        var jpeg = Jpeg(); var raw = Nef(); using var writer = new ExportWriter(Binding, [root]);
        store.Pick(jpeg, Binding, () => true, default); var exported = ExportName(raw); File.Delete(raw);
        store.Remove(jpeg, Binding, Observe(jpeg).Copies, default);
        Assert.False(Observe(jpeg).Picked); Assert.Equal("raw", File.ReadAllText(exported));
    }

    [Fact]
    public void MultipleJpegExtensionsSharingOneRawAreRejectedBeforeCopying()
    {
        var jpeg = Jpeg(); Jpeg("DSC_001.jpeg"); Nef(); using var writer = new ExportWriter(Binding, [root]);
        Assert.Throws<IOException>(() => store.Pick(jpeg, Binding, () => true, default));
        Assert.Empty(Directory.EnumerateFiles(Binding.ExportRoot)); Assert.False(Observe(jpeg).Picked);
    }

    [Fact]
    public async Task ExplicitPickRepairsAMissingCompanionOfAnAlreadyPickedJpeg()
    {
        var jpeg = Jpeg();
        using (var writer = new ExportWriter(Binding, [root])) store.Pick(jpeg, Binding, () => true, default);
        var raw = Nef(); await using var coordinator = new MutationCoordinator(comparer); coordinator.RegisterPhoto(jpeg, Binding);
        MutationResult? result = null; coordinator.Completed += value => result = value;
        coordinator.Submit(new(jpeg, Binding, true, false, 1)); await coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(result!.Error); Assert.True(result.Observation.Picked); Assert.Equal("raw", File.ReadAllText(ExportName(raw)));
    }

    [Fact]
    public async Task SharedJpegSelectionCopiesEachFoldersDifferentRawAndUnpickRemovesBoth()
    {
        var one = Path.Combine(root, "one"); var two = Path.Combine(root, "two"); Directory.CreateDirectory(one); Directory.CreateDirectory(two);
        File.WriteAllText(Path.Combine(one, "a.JPG"), "same jpeg"); File.WriteAllText(Path.Combine(two, "a.JPG"), "same jpeg");
        File.WriteAllText(Path.Combine(one, "a.NEF"), "raw one"); File.WriteAllText(Path.Combine(two, "a.NEF"), "raw two");
        var a = source.Enumerate(one, default).Single(); var b = source.Enumerate(two, default).Single();
        var bindingA = new ExportBinding(one, Binding.ExportRoot); var bindingB = new ExportBinding(two, Binding.ExportRoot);
        await using var coordinator = new MutationCoordinator(comparer); coordinator.Register([(a, bindingA), (b, bindingB)]);
        MutationResult? result = null; coordinator.Completed += value => result = value;
        coordinator.Submit(new(a, bindingA, true, false, 1)); await coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(result!.Error); Assert.Equal(2, result.Photos.Count); Assert.Equal(2, Directory.EnumerateFiles(Binding.ExportRoot, "*.NEF").Count());
        coordinator.Submit(new(b, bindingB, false, false, 2)); await coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(result!.Error); Assert.False(result.Observation.Picked); Assert.Empty(Directory.EnumerateFiles(Binding.ExportRoot));
        Assert.Equal("raw one", File.ReadAllText(Path.Combine(one, "a.NEF"))); Assert.Equal("raw two", File.ReadAllText(Path.Combine(two, "a.NEF")));
    }

    [Fact]
    public async Task LockedNefKeepsTheJpegPickedUntilRemovalCanBeRetried()
    {
        if (!OperatingSystem.IsWindows()) return;
        var jpeg = Jpeg(); var raw = Nef(); using (var writer = new ExportWriter(Binding, [root])) store.Pick(jpeg, Binding, () => true, default);
        await using var coordinator = new MutationCoordinator(comparer); coordinator.RegisterPhoto(jpeg, Binding);
        MutationResult? result = null; coordinator.Completed += value => result = value;
        using (var locked = new FileStream(ExportName(raw), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            coordinator.Submit(new(jpeg, Binding, false, false, 1)); await coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(result!.Error); Assert.True(result.Observation.Picked); Assert.True(Observe(jpeg).Picked);
        }
        coordinator.Submit(new(jpeg, Binding, false, false, 2)); await coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(result!.Error); Assert.False(result.Observation.Picked); Assert.False(File.Exists(ExportName(raw))); Assert.Equal("raw", File.ReadAllText(raw));
    }

    [Fact]
    public void NefExportAliasingItsSourceCannotBeReusedOrDeleted()
    {
        var jpeg = Jpeg(); var raw = Nef(); using var writer = new ExportWriter(Binding, [root]);
        var alias = ExportName(raw);
        if (OperatingSystem.IsWindows()) Assert.True(CreateHardLink(alias, raw, IntPtr.Zero)); else Assert.Equal(0, Link(raw, alias));
        Assert.Throws<IOException>(() => store.Pick(jpeg, Binding, () => true, default));
        Assert.False(Observe(jpeg).Picked); Assert.Equal("raw", File.ReadAllText(raw)); Assert.Equal("raw", File.ReadAllText(alias));
    }

    [Fact]
    public async Task LatestUnpickDuringNefCopyCancelsBothAndLaterPickCopiesThePair()
    {
        var jpeg = Jpeg(); var raw = Nef(); await using var coordinator = new MutationCoordinator(comparer); coordinator.RegisterPhoto(jpeg, Binding);
        using var staged = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        coordinator.Phase = phase => { if (phase == "staging") { staged.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); } };
        coordinator.Submit(new(jpeg, Binding, true, false, 1)); Assert.True(staged.Wait(TimeSpan.FromSeconds(10)));
        coordinator.Submit(new(jpeg, Binding, false, false, 2)); release.Set();
        await coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(Observe(jpeg).Picked); Assert.False(File.Exists(ExportName(raw)));
        coordinator.Phase = null; coordinator.Submit(new(jpeg, Binding, true, false, 3));
        await coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(Observe(jpeg).Picked); Assert.Equal("raw", File.ReadAllText(ExportName(raw)));
        Assert.Equal("jpeg", File.ReadAllText(jpeg.Path)); Assert.Equal("raw", File.ReadAllText(raw));
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLink(string alias, string original, IntPtr security);
    [DllImport("libc", EntryPoint = "link", SetLastError = true)] private static extern int Link(string original, string alias);

    public void Dispose() => Directory.Delete(root, true);
}
