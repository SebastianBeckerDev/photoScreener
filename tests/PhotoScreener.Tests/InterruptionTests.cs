using System.Diagnostics;
using System.Runtime.InteropServices;
using PhotoScreener.Core;
using PhotoScreener.Infrastructure;
using Xunit;

namespace PhotoScreener.Tests;
public sealed class InterruptionTests
{
    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)] private static extern int GetDescriptorFlags(int descriptor, int command);
    [Fact]
    public async Task UnrelatedChildCannotRetainAReleasedExportOwnershipLock()
    {
        var root = Path.Combine(Path.GetTempPath(), "photoscreener-kill-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "parent-source"); var childSource = Path.Combine(root, "child-source");
        Directory.CreateDirectory(source); Directory.CreateDirectory(childSource);
        File.WriteAllText(Path.Combine(source, "a.JPG"), "abc"); File.WriteAllText(Path.Combine(childSource, "a.JPG"), "abc");
        var binding = new ExportBinding(source, Path.Combine(root, "parent-export")); var childExport = Path.Combine(root, "child-export");
        ExportWriter? owner = null; Process? child = null;
        try
        {
            owner = new ExportWriter(binding, [source, childSource]);
            if (OperatingSystem.IsMacOS())
            {
                using var input = PlatformFileSystem.OpenOwned(Path.Combine(source, "a.JPG"), FileMode.Open, FileAccess.Read, FileShare.Read);
                var flags = GetDescriptorFlags(input.SafeFileHandle.DangerousGetHandle().ToInt32(), 1); // F_GETFD has no variadic third argument.
                Assert.True(flags >= 0); Assert.Equal(1, flags & 1); // FD_CLOEXEC
            }
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (repository != null && !File.Exists(Path.Combine(repository.FullName, "PhotoScreener.Rewrite.sln"))) repository = repository.Parent;
            Assert.NotNull(repository);
#if DEBUG
            const string configuration = "Debug";
#else
            const string configuration = "Release";
#endif
            var harness = Path.Combine(repository!.FullName, "tools", "PhotoScreener.FaultHarness", "bin", configuration, "net10.0", "PhotoScreener.FaultHarness.dll");
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            start.ArgumentList.Add(harness); start.ArgumentList.Add(childSource); start.ArgumentList.Add(childExport); start.ArgumentList.Add("staging");
            child = Process.Start(start)!;
            var reached = Path.Combine(childExport, ".photoscreener", "fault-reached.txt"); var wait = Stopwatch.StartNew();
            while (!File.Exists(reached) && !child.HasExited && wait.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(20);
            Assert.True(File.Exists(reached), child.HasExited ? await child.StandardError.ReadToEndAsync() : "Unrelated child did not reach staging.");
            owner.Dispose(); owner.Dispose(); owner = null;
            using (var replacement = new ExportWriter(binding, [source, childSource])) Assert.False(child.HasExited);
            var metadata = new ReviewMetadataStore(); metadata.Write(binding, new("a.JPG", null, ReviewFilter.All, []), [source, childSource]);
            Assert.Equal("a.JPG", metadata.Read(binding)!.CursorName);
            Assert.Equal("abc", File.ReadAllText(Path.Combine(source, "a.JPG"))); Assert.Equal("abc", File.ReadAllText(Path.Combine(childSource, "a.JPG")));
        }
        finally
        {
            owner?.Dispose();
            if (child != null) { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); } child.Dispose(); }
            await RemoveFixture(root);
        }
    }

    [Theory]
    [InlineData("staging", false)] [InlineData("published", true)] [InlineData("deleting", true)] [InlineData("deleted", false)]
    public async Task KillRecoversOnlyCompletedFilesAndCleansOnlyIdentifiedStaging(string phase, bool expectedPicked)
    {
        var root = Path.Combine(Path.GetTempPath(), "photoscreener-kill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var path = Path.Combine(root, "a.JPG"); File.WriteAllText(path, "abc");
        var binding = new ExportBinding(root, Path.Combine(root, "export")); var photo = new SourceCatalog().Enumerate(root, default).Single();
        var comparer = new FolderComparer(); var store = new ExportStore(comparer);
        if (phase is "deleting" or "deleted") using (var owner = new ExportWriter(binding, [root])) store.Pick(photo, binding, () => true, default);
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository != null && !File.Exists(Path.Combine(repository.FullName, "PhotoScreener.Rewrite.sln"))) repository = repository.Parent;
        Assert.NotNull(repository);
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var harness = Path.Combine(repository!.FullName, "tools", "PhotoScreener.FaultHarness", "bin", configuration, "net10.0", "PhotoScreener.FaultHarness.dll");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        start.ArgumentList.Add(harness); start.ArgumentList.Add(root); start.ArgumentList.Add(binding.ExportRoot); start.ArgumentList.Add(phase);
        using var child = Process.Start(start)!;
        try
        {
            var reached = Path.Combine(binding.ExportRoot, ".photoscreener", "fault-reached.txt");
            var wait = Stopwatch.StartNew();
            while (!File.Exists(reached) && !child.HasExited && wait.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(20);
            Assert.True(File.Exists(reached), child.HasExited ? await child.StandardError.ReadToEndAsync() : "Fault point not reached");
            child.Kill(true); await child.WaitForExitAsync();
            var staging = Path.Combine(binding.ExportRoot, ".photoscreener", "staging"); Directory.CreateDirectory(staging);
            var unrelated = Path.Combine(staging, "unrelated.part"); File.WriteAllText(unrelated, "keep");
            using (var owner = await AcquireAfterChildExit(child, binding, root)) store.CleanupOwnedStaging(binding);
            Assert.Equal("keep", File.ReadAllText(unrelated));
            Assert.Empty(Directory.EnumerateFiles(staging, "ps-v1-*.part"));
            await RemoveFixture(root, metadataOnly: true);
            var observed = comparer.Compare(photo, comparer.Index(binding, default), [photo], default);
            Assert.Equal(expectedPicked, observed.Picked); Assert.Equal("abc", File.ReadAllText(path));
        }
        finally { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); } await RemoveFixture(root); }
    }
    private static async Task<ExportWriter> AcquireAfterChildExit(Process child, ExportBinding binding, string root)
    {
        if (!child.HasExited) throw new InvalidOperationException("Recovery must wait for the killed child to exit.");
        var wait = Stopwatch.StartNew();
        while (true)
        {
            try { return new ExportWriter(binding, [root]); }
            // Windows CI may retain a transient lock after the process has exited.
            // Wait for ownership only; cleanup and recovery assertions run once.
            catch (IOException error) when (OperatingSystem.IsWindows() && (error.HResult & 0xffff) is 32 or 33 && wait.Elapsed < TimeSpan.FromSeconds(3))
            { await Task.Delay(20); }
        }
    }
    private static async Task RemoveFixture(string root, bool metadataOnly = false)
    {
        var absolute = PathGuard.Canonical(root);
        if (!PathGuard.Comparer.Equals(Path.GetDirectoryName(absolute), PathGuard.Canonical(Path.GetTempPath())) || !Path.GetFileName(absolute).StartsWith("photoscreener-kill-", StringComparison.Ordinal))
            throw new IOException("Refusing to remove a path outside this test's temporary fixture location.");
        PathGuard.ValidateNoLinks(absolute);
        // Removing optional metadata is fixture preparation for the no-ledger
        // recovery assertion. Apply the same bounded sharing-violation handling
        // as final disposal, without retrying recovery or source assertions.
        var target = metadataOnly ? Path.Combine(absolute, "export", ".photoscreener") : absolute;
        PathGuard.ValidateNoLinks(target);
        var cleanup = Stopwatch.StartNew();
        while (true)
        {
            try { Directory.Delete(target, true); return; }
            // CI can retain a transient source read handle just after process exit.
            // Retry only fixture disposal, never the recovery/source-safety assertions.
            catch (IOException error) when (OperatingSystem.IsWindows() && (error.HResult & 0xffff) is 32 or 33 && cleanup.Elapsed < TimeSpan.FromSeconds(3))
            { await Task.Delay(20); }
        }
    }
}
