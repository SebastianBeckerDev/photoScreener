using System.Diagnostics;
using PhotoScreener.Infrastructure;

namespace PhotoScreener.TestFixtures;

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
    public static async Task<SmallExportVolume> Create()
    {
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
                await Run("/usr/bin/hdiutil", "create", "-size", "64m", "-fs", "APFS", "-volname", volume.label, "-format", "UDRW", volume.image);
                volume.attachAttempted = true;
                volume.helperOutput = await Run("/usr/bin/hdiutil", "attach", "-nobrowse", "-mountpoint", volume.Root, volume.image);
            }
            volume.ValidateMountedVolume(); return volume;
        }
        catch { await volume.DisposeAsync(); throw; }
    }
    public void ValidateMountedVolume()
    {
        ValidateFixture();
        DriveInfo drive;
        try { drive = Drive; }
        catch (InvalidOperationException e) { throw new IOException("Test disk image did not mount: " + helperOutput, e); }
        if (!drive.IsReady || drive.VolumeLabel != label || drive.TotalSize is < 16 * 1024 * 1024 or > 80 * 1024 * 1024)
            throw new IOException("Refusing to use an unverified test disk image.");
        PathGuard.ValidateNoLinks(Root);
    }
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
