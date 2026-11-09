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

[Collection("Native disk images")]
public sealed class FullVolumeTests(ITestOutputHelper output)
{
    [Fact]
    public void NativePlistIdentityHandlesPreservedWhitespaceAndRejectsWrongValueTypes()
    {
        var identity = SmallExportVolume.ReadNativeIdentity("""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict xml:space="preserve">
                <key>VolumeName</key>
                <string>PSFULL-private</string>
                <key>MountPoint</key>
                <string>/private/tmp/owned/mount</string>
            </dict></plist>
            """);
        Assert.Equal("PSFULL-private", identity.Label); Assert.Equal("/private/tmp/owned/mount", identity.MountPoint);
        var invalid = SmallExportVolume.ReadNativeIdentity("<plist><dict><key>VolumeName</key><false/><key>MountPoint</key><string>/other</string></dict></plist>");
        Assert.Equal("", invalid.Label); Assert.Equal("/other", invalid.MountPoint);
    }
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
        var fillReachedNativeLimit = false;
        using (var fill = new FileStream(filler, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var block = new byte[65536]; new Random(708).NextBytes(block);
            while (volume.Drive.AvailableFreeSpace > 512 * 1024 + block.Length)
            {
                if (fill.Length > 80 * 1024 * 1024) throw new IOException("Test disk filling exceeded its fixed bound.");
                try { fill.Write(block); }
                // APFS can exhaust allocatable blocks before statfs reports the
                // requested reserve. This is expected only while filling our
                // validated private image, not an export assertion to retry.
                catch (IOException error) when ((error.HResult & 0xffff) is 28 or 39 or 112)
                {
                    fillReachedNativeLimit = true;
                    // Leave room for the staging manifest and its directory
                    // metadata, but less than either original image's bytes.
                    fill.SetLength(Math.Max(0, fill.Length - 512 * 1024));
                    break;
                }
            }
            fill.Flush(true);
        }
        var freeBefore = volume.Drive.AvailableFreeSpace;
        Assert.True(fillReachedNativeLimit || freeBefore is >= 0 and < 1024 * 1024,
            $"Test image was not filled: {freeBefore} available bytes and no native allocation failure.");
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
        output.WriteLine($"{volume.Drive.DriveFormat} disk image: {volume.Drive.TotalSize} bytes, {freeBefore} reported free before failure; native filler limit={fillReachedNativeLimit}; NEF={withNef}; error={result.Error}");
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
