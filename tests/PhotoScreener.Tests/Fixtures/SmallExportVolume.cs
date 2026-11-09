using System.Diagnostics;
using System.Xml;
using System.Xml.Linq;
using PhotoScreener.Infrastructure;
using Xunit;

namespace PhotoScreener.TestFixtures;

[CollectionDefinition("Native disk images", DisableParallelization = true)]
public sealed class NativeDiskImageCollection { }

// Opt-in CI fixture: a new 64 MiB disk image, never a physical disk. Every
// DiskPart selection names this fixture's image file; there is no select-disk,
// clean command, or formatting of an existing volume.
internal sealed class SmallExportVolume : IAsyncDisposable
{
    private readonly string image, label;
    private string helperOutput = "";
    private bool attachAttempted;
    public string Directory { get; }
    public string Root { get; private set; } = "";
    public DriveInfo Drive => DriveInfo.GetDrives().Single(d => PathGuard.Comparer.Equals(PathGuard.Canonical(d.Name), PathGuard.Canonical(Root)));
    private SmallExportVolume()
    {
        var id = Guid.NewGuid().ToString("N");
        Directory = Path.Combine(Path.GetTempPath(), "lumapick-full-volume-" + id);
        System.IO.Directory.CreateDirectory(Directory);
        ValidateFixture(); label = "PSFULL-" + id[..8];
        image = Path.Combine(Directory, OperatingSystem.IsWindows() ? "export-test.vhd" : "export-test.dmg");
        if (File.Exists(image)) throw new IOException("Refusing to replace an existing disk image.");
    }
    public static async Task<SmallExportVolume> Create(bool caseSensitive = false)
    {
        if (caseSensitive && !OperatingSystem.IsMacOS()) throw new NotSupportedException("Case-sensitive APFS fixture requires macOS.");
        var volume = new SmallExportVolume();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var existing = Environment.GetLogicalDrives().Select(d => char.ToUpperInvariant(d[0])).ToHashSet();
                var letter = Enumerable.Range('P', 11).Select(i => (char)i).Reverse().First(c => !existing.Contains(c));
                volume.Root = letter + ":\\";
                volume.attachAttempted = true;
                await volume.DiskPart("create vdisk file=" + Quote(volume.image) + " maximum=64 type=expandable",
                    "select vdisk file=" + Quote(volume.image), "attach vdisk", "create partition primary",
                    "format fs=ntfs quick label=" + volume.label, "assign letter=" + letter);
            }
            else
            {
                volume.Root = Path.Combine(volume.Directory, "mount"); System.IO.Directory.CreateDirectory(volume.Root);
                // A blank size-based image is writable by default. Explicit
                // conversion formats require a source folder/device on this host.
                await Run("/usr/bin/hdiutil", "create", "-size", "64m", "-fs", caseSensitive ? "Case-sensitive APFS" : "APFS", "-volname", volume.label, volume.image);
                volume.attachAttempted = true;
                volume.helperOutput = await Run("/usr/bin/hdiutil", "attach", "-nobrowse", "-mountpoint", volume.Root, volume.image);
            }
            await volume.ValidateMountedVolume(); return volume;
        }
        catch { await volume.DisposeAsync(); throw; }
    }
    public async Task ValidateMountedVolume()
    {
        ValidateFixture();
        DriveInfo drive;
        try { drive = Drive; }
        catch (InvalidOperationException e) { throw new IOException("Test disk image did not mount: " + helperOutput, e); }
        var mountedLabel = drive.VolumeLabel;
        if (OperatingSystem.IsMacOS())
        {
            // Unix DriveInfo.VolumeLabel returns the mount path, not the native
            // volume name. Verify both native identity fields without weakening
            // the private label, mount-path or fixed-capacity guards.
            var info = await Run("/usr/sbin/diskutil", "info", "-plist", Root);
            var identity = ReadNativeIdentity(info);
            mountedLabel = identity.Label;
            var mountPoint = identity.MountPoint;
            if (string.IsNullOrEmpty(mountPoint) || !PathGuard.Comparer.Equals(PathGuard.Canonical(mountPoint), PathGuard.Canonical(Root)))
                throw new IOException($"Test disk image mounted at an unexpected path: {mountPoint}.");
        }
        if (!drive.IsReady || mountedLabel != label || drive.TotalSize is < 16 * 1024 * 1024 or > 80 * 1024 * 1024)
            throw new IOException($"Refusing to use an unverified test disk image: mount={drive.Name}, label={mountedLabel}, expected={label}, size={drive.TotalSize}.");
        PathGuard.ValidateNoLinks(Root);
    }
    internal static (string Label, string? MountPoint) ReadNativeIdentity(string info)
    {
        using var reader = XmlReader.Create(new StringReader(info), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024
        });
        var dictionary = XDocument.Load(reader).Root?.Element("dict")
            ?? throw new IOException("Native disk information did not contain a volume dictionary.");
        // Select the immediate next element: a plist can preserve whitespace
        // nodes, and looking ahead for any string could read another key's value.
        string? Value(string key) => dictionary.Elements("key").SingleOrDefault(e => e.Value == key)
            ?.ElementsAfterSelf().FirstOrDefault() is XElement element && element.Name == "string" ? element.Value : null;
        return (Value("VolumeName") ?? "", Value("MountPoint"));
    }
    public async Task Disconnect()
    {
        // This transition is allowed only after validating our own mounted image.
        // Never resolve or detach an arbitrary user-supplied drive/device.
        await ValidateMountedVolume();
        if (OperatingSystem.IsWindows()) await DiskPart("select vdisk file=" + Quote(image), "detach vdisk");
        else await Run("/usr/bin/hdiutil", "detach", Root);
        attachAttempted = false;
        if (MountedAtRoot()) throw new IOException("Private test disk image remained mounted after detach.");
    }
    public async Task Reconnect()
    {
        ValidateFixture(); PathGuard.ValidateNoLinks(image);
        if (attachAttempted || MountedAtRoot() || !File.Exists(image)) throw new IOException("Refusing to attach over an existing or unknown mount.");
        if (OperatingSystem.IsWindows() && Environment.GetLogicalDrives().Any(d => PathGuard.Comparer.Equals(d, Root)))
            throw new IOException("Private image's drive letter was claimed by another volume.");
        attachAttempted = true;
        if (OperatingSystem.IsWindows())
        {
            await DiskPart("select vdisk file=" + Quote(image), "attach vdisk");
            // Windows may restore this image's previous drive letter itself.
            if (!MountedAtRoot()) await DiskPart("select vdisk file=" + Quote(image), "select partition=1", "assign letter=" + Root[0]);
        }
        else helperOutput = await Run("/usr/bin/hdiutil", "attach", "-nobrowse", "-mountpoint", Root, image);
        await ValidateMountedVolume();
    }
    private bool MountedAtRoot() => DriveInfo.GetDrives().Any(d =>
        PathGuard.Comparer.Equals(PathGuard.Canonical(d.Name), PathGuard.Canonical(Root)) && d.IsReady);
    private void ValidateFixture()
    {
        var absolute = PathGuard.Canonical(Directory);
        if (!PathGuard.Comparer.Equals(Path.GetDirectoryName(absolute), PathGuard.Canonical(Path.GetTempPath())) ||
            !Path.GetFileName(absolute).StartsWith("lumapick-full-volume-", StringComparison.Ordinal))
            throw new IOException("Disk image must remain in its named temporary fixture directory.");
        PathGuard.ValidateNoLinks(absolute);
    }
    private static string Quote(string path)
    {
        if (path.IndexOfAny(['"', '\r', '\n']) >= 0) throw new IOException("Invalid disk image fixture path.");
        return "\"" + path + "\"";
    }
    private async Task DiskPart(params string[] commands)
    {
        ValidateFixture();
        var script = Path.Combine(Directory, "diskpart.txt");
        await File.WriteAllLinesAsync(script, commands.Append("exit"));
        helperOutput = await Run(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "diskpart.exe"), "/s", script);
    }
    private static async Task<string> Run(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Disk image helper could not start.");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
        catch { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } throw; }
        var messages = await output + await error;
        if (process.ExitCode != 0) throw new IOException("Disk image helper failed: " + messages);
        return messages;
    }
    public async ValueTask DisposeAsync()
    {
        ValidateFixture();
        if (attachAttempted)
        {
            if (OperatingSystem.IsWindows()) await DiskPart("select vdisk file=" + Quote(image), "detach vdisk");
            else await Run("/usr/bin/hdiutil", "detach", Root);
            attachAttempted = false;
        }
        ValidateFixture(); System.IO.Directory.Delete(Directory, true);
    }
}
