using System.Runtime.InteropServices;
using PhotoScreener.Core;
using PhotoScreener.Infrastructure;
using Xunit;

namespace PhotoScreener.Tests;
public sealed class AliasTests
{
    [Fact] public void HardLinkedExportCannotModifyOrDeleteSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "photoscreener-alias-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "a.JPG"); File.WriteAllText(path, "abc"); var photo = new SourceCatalog().Enumerate(root, default).Single();
            var binding = new ExportBinding(root, Path.Combine(root, "export")); var comparer = new FolderComparer(); var store = new ExportStore(comparer);
            using var owner = new ExportWriter(binding, [root]); var alias = Path.Combine(binding.ExportRoot, "a.kAFQ.JPG");
            if (OperatingSystem.IsWindows()) Assert.True(CreateHardLink(alias, path, IntPtr.Zero));
            else Assert.Equal(0, Link(path, alias));
            Assert.Null(comparer.Compare(photo, comparer.Index(binding, default), [photo], default).Picked);
            Assert.Throws<IOException>(() => store.Pick(photo, binding, () => true, default));
            Assert.Equal("abc", File.ReadAllText(path)); Assert.Equal("abc", File.ReadAllText(alias));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact] public void WindowsPinnedDirectoriesRejectRenamesDuringOwnedWork()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "photoscreener-pin-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "a.JPG"), "abc"); var binding = new ExportBinding(root, Path.Combine(root, "export"));
            using var owner = new ExportWriter(binding, [root]);
            Assert.Throws<IOException>(() => Directory.Move(binding.ExportRoot, binding.ExportRoot + ".substituted"));
            Assert.Throws<IOException>(() => Directory.Move(root, root + ".substituted"));
        }
        finally { Directory.Delete(root, true); }
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLink(string alias, string original, IntPtr security);
    [DllImport("libc", EntryPoint = "link", SetLastError = true)] private static extern int Link(string original, string alias);
}
