using System.Diagnostics;
using PhotoScreener.Core;
using PhotoScreener.Infrastructure;
using Xunit;

namespace PhotoScreener.Tests;
public sealed class InterruptionTests
{
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
            using (var owner = new ExportWriter(binding, [root])) store.CleanupOwnedStaging(binding);
            Assert.Equal("keep", File.ReadAllText(unrelated));
            Assert.Empty(Directory.EnumerateFiles(staging, "ps-v1-*.part"));
            Directory.Delete(Path.Combine(binding.ExportRoot, ".photoscreener"), true);
            var observed = comparer.Compare(photo, comparer.Index(binding, default), [photo], default);
            Assert.Equal(expectedPicked, observed.Picked); Assert.Equal("abc", File.ReadAllText(path));
        }
        finally { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); } await RemoveFixture(root); }
    }
    private static async Task RemoveFixture(string root)
    {
        var absolute = PathGuard.Canonical(root);
        if (!PathGuard.Comparer.Equals(Path.GetDirectoryName(absolute), PathGuard.Canonical(Path.GetTempPath())) || !Path.GetFileName(absolute).StartsWith("photoscreener-kill-", StringComparison.Ordinal))
            throw new IOException("Refusing to remove a path outside this test's temporary fixture location.");
        PathGuard.ValidateNoLinks(absolute);
        var cleanup = Stopwatch.StartNew();
        while (true)
        {
            try { Directory.Delete(absolute, true); return; }
            // CI can retain a transient source read handle just after process exit.
            // Retry only fixture disposal, never the recovery/source-safety assertions.
            catch (IOException error) when (OperatingSystem.IsWindows() && (error.HResult & 0xffff) is 32 or 33 && cleanup.Elapsed < TimeSpan.FromSeconds(3))
            { await Task.Delay(20); }
        }
    }
}
