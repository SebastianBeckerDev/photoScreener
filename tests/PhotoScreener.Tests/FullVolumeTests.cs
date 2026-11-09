using System.Security.Cryptography;
using PhotoScreener.Core;
using PhotoScreener.Infrastructure;
using PhotoScreener.TestFixtures;
using Xunit;
using Xunit.Abstractions;

namespace PhotoScreener.Tests;

public sealed class FullVolumeTheoryAttribute : TheoryAttribute
{
    public FullVolumeTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("LUMAPICK_TEST_DISK_IMAGES") != "1" || !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
            Skip = "Opt-in native disk image test; CI sets LUMAPICK_TEST_DISK_IMAGES=1. Windows requires elevation.";
    }
}

public sealed class FullVolumeTests(ITestOutputHelper output)
{
    [FullVolumeTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task FullExportDiskCannotPublishAPartialPickAndRetryCopiesOriginalBytes(bool withNef)
    {
        await using var volume = await SmallExportVolume.Create();
        var source = Path.Combine(volume.Directory, "source"); Directory.CreateDirectory(source);
        var jpegBytes = new byte[3 * 1024 * 1024]; new Random(702).NextBytes(jpegBytes);
        var rawBytes = new byte[2 * 1024 * 1024]; new Random(704).NextBytes(rawBytes);
        var sourcePath = Path.Combine(source, "a.JPG"); File.WriteAllBytes(sourcePath, jpegBytes);
        if (withNef) File.WriteAllBytes(Path.Combine(source, "a.NEF"), rawBytes);
        var photo = new SourceCatalog().Enumerate(source, default).Single();
        var binding = new ExportBinding(source, Path.Combine(volume.Root, "export"));
        using (var owner = new ExportWriter(binding, [source]))
        {
            Directory.CreateDirectory(Path.Combine(binding.ExportRoot, ".photoscreener", "staging"));
            File.WriteAllText(Path.Combine(binding.ExportRoot, "unrelated.txt"), "preserve");
        }
        var filler = Path.Combine(volume.Root, "filler.bin");
        await volume.ValidateMountedVolume();
        using (var fill = new FileStream(filler, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var block = new byte[65536]; new Random(708).NextBytes(block);
            while (volume.Drive.AvailableFreeSpace > 512 * 1024 + block.Length)
            {
                if (fill.Length > 80 * 1024 * 1024) throw new IOException("Test disk filling exceeded its fixed bound.");
                fill.Write(block);
            }
            fill.Flush(true);
        }
        var freeBefore = volume.Drive.AvailableFreeSpace;
        Assert.InRange(freeBefore, 1, 1024 * 1024 - 1);
        var comparer = new FolderComparer();
        await using var coordinator = new MutationCoordinator(comparer); coordinator.RegisterPhoto(photo, binding);
        MutationResult? result = null; coordinator.Completed += value => result = value;
        var staged = 0; var verified = 0;
        coordinator.Phase = phase => { if (phase == "staging") staged++; if (phase == "verify") verified++; };
        coordinator.Submit(new(photo, binding, true, false, 1));
        await coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(result!.Error); Assert.False(result.Observation.Picked); Assert.True(result.Desired);
        Assert.Equal(1, staged); Assert.Equal(0, verified);
        Assert.DoesNotContain(Directory.EnumerateFiles(binding.ExportRoot), p => SourceCatalog.IsImage(p) || SourceCatalog.IsNef(p));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(binding.ExportRoot, ".photoscreener", "staging")));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(binding.ExportRoot, "unrelated.txt")));
        output.WriteLine($"{volume.Drive.DriveFormat} disk image: {volume.Drive.TotalSize} bytes, {freeBefore} free before failure; NEF={withNef}; error={result.Error}");
        await volume.ValidateMountedVolume(); File.Delete(filler);
        coordinator.Submit(new(photo, binding, true, false, 2));
        await coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Null(result!.Error); Assert.True(result.Observation.Picked);
        var jpegCopy = Assert.Single(result.Observation.Copies);
        Assert.Equal(jpegBytes, File.ReadAllBytes(jpegCopy.Path));
        if (withNef) Assert.Equal(rawBytes, File.ReadAllBytes(Assert.Single(Directory.EnumerateFiles(binding.ExportRoot, "*.NEF"))));
        Assert.Equal(SHA256.HashData(jpegBytes), SHA256.HashData(File.ReadAllBytes(sourcePath)));
        if (withNef) Assert.Equal(rawBytes, File.ReadAllBytes(Path.Combine(source, "a.NEF")));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(binding.ExportRoot, "unrelated.txt")));
    }
}
