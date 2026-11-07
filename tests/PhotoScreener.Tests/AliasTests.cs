using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PhotoScreener.Core;
using PhotoScreener.Infrastructure;
using Xunit;

namespace PhotoScreener.Tests;
public sealed class AliasTests
{
    [Fact] public async Task CaseSensitiveWindowsNamesCannotBeMergedOrMutatedAsOnePhoto()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "photoscreener-case-sensitive-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            using (var directory = CreateFile(root, 0x100, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero))
            {
                Assert.False(directory.IsInvalid);
                uint flags = 1;
                Assert.True(SetCaseSensitive(directory, 23, ref flags, sizeof(uint)), "Cannot enable the NTFS case-sensitive fixture: " + Marshal.GetLastWin32Error());
            }
            var lower = Path.Combine(root, "a.JPG"); var upper = Path.Combine(root, "A.JPG");
            File.WriteAllText(lower, "abc"); File.WriteAllText(upper, "xyz");
            var binding = new ExportBinding(root, Path.Combine(root, "export")); Directory.CreateDirectory(binding.ExportRoot);
            var copy = Path.Combine(binding.ExportRoot, "a.kAFQ.JPG"); File.WriteAllText(copy, "abc");
            Assert.Throws<IOException>(() => new SourceCatalog().Enumerate(root, default).ToArray());
            var photo = new Photo(lower, root, "a.JPG", PathGuard.Revision(lower));
            await using var coordinator = new MutationCoordinator(new()); coordinator.RegisterPhoto(photo, binding);
            MutationResult? result = null; coordinator.Completed += value => result = value;
            coordinator.Submit(new(photo, binding, false, false, 1));
            await coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(result?.Error); Assert.Equal("abc", File.ReadAllText(copy));
            Assert.Equal("abc", File.ReadAllText(lower)); Assert.Equal("xyz", File.ReadAllText(upper));
            var first = Path.Combine(root, "one"); var second = Path.Combine(root, "One");
            Directory.CreateDirectory(first); Directory.CreateDirectory(second);
            var shared = Path.Combine(root, "shared");
            Assert.Throws<IOException>(() => BindingIdentityGuard.Validate(new(first, shared), [new(second, shared)]));
            var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var failures = new List<LibraryBatch>();
            await using var library = new LibraryWorker(new(), new());
            library.Submit(new(root, false, batch =>
            {
                if (batch.Error != null) failures.Add(batch);
                if (batch.Complete) complete.TrySetResult();
                return Task.CompletedTask;
            }, default, []));
            await complete.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains(failures, batch => batch.Error!.Contains("differ only by case"));
        }
        finally { Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowsExportPathAliasesAreRejectedWithoutRemovingAnyCopy(bool extendedPath)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "photoscreener-target-alias-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var first = Path.Combine(root, "one"); var second = Path.Combine(root, "two");
            Directory.CreateDirectory(first); Directory.CreateDirectory(second);
            File.WriteAllText(Path.Combine(first, "a.JPG"), "abc"); File.WriteAllText(Path.Combine(second, "a.JPG"), "abc");
            var target = Path.Combine(root, "shared"); Directory.CreateDirectory(target);
            var copy = Path.Combine(target, "a.kAFQ.JPG"); File.WriteAllText(copy, "abc");
            var binding = new ExportBinding(first, target); var alias = new ExportBinding(second, extendedPath ? "\\\\?\\" + target : target + ".");
            Assert.True(Directory.Exists(alias.ExportRoot));
            Assert.Throws<IOException>(() => BindingIdentityGuard.Validate(alias, [binding]));
            var photo = new SourceCatalog().Enumerate(second, default).Single();
            await using var coordinator = new MutationCoordinator(new());
            coordinator.RegisterBinding(binding); coordinator.RegisterPhoto(photo, alias);
            MutationResult? result = null; coordinator.Completed += value => result = value;
            coordinator.Submit(new(photo, alias, false, false, 1));
            await coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(result?.Error); Assert.Equal("abc", File.ReadAllText(copy));
            Assert.Equal("abc", File.ReadAllText(photo.Path));
            Assert.False(Directory.Exists(Path.Combine(target, ".photoscreener")));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact] public void CaseVariantBindingsRespectTheActualDirectoriesAndSessionKeys()
    {
        var root = Path.Combine(Path.GetTempPath(), "photoscreener-case-binding-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var first = Path.Combine(root, "one"); var second = Path.Combine(root, "two");
            Directory.CreateDirectory(first); Directory.CreateDirectory(second);
            var target = Path.Combine(root, "shared"); var alternate = Path.Combine(root, "Shared");
            Directory.CreateDirectory(target); Directory.CreateDirectory(alternate);
            File.WriteAllText(Path.Combine(target, "identity.txt"), "first");
            var sameDirectory = File.Exists(Path.Combine(alternate, "identity.txt"));
            var binding = new ExportBinding(first, target); var other = new ExportBinding(second, alternate);
            if (sameDirectory && !OperatingSystem.IsWindows() || !sameDirectory && OperatingSystem.IsWindows())
                Assert.Throws<IOException>(() => BindingIdentityGuard.Validate(other, [binding]));
            else BindingIdentityGuard.Validate(other, [binding]);
            Assert.Equal("first", File.ReadAllText(Path.Combine(target, "identity.txt")));
        }
        finally { Directory.Delete(root, true); }
    }
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
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint attributes, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetCaseSensitive(SafeFileHandle directory, int informationClass, ref uint flags, uint size);
}
