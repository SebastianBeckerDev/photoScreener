using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PhotoScreener.Infrastructure;
public static partial class PlatformFileSystem
{
    public static string DirectoryIdentity(string path)
    {
        PathGuard.ValidateNoLinks(path);
        if (OperatingSystem.IsWindows())
        {
            using var handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            return Identity(handle, out _);
        }
        using var directory = OpenDirectory(path); return Identity(directory, out _);
    }
    public static IDisposable CreateDirectoryPinned(string path)
    {
        var canonical = PathGuard.Canonical(path);
        if (OperatingSystem.IsMacOS())
        {
            if (activeExport != null && !PathGuard.Within(canonical, activeExport.Root)) throw new IOException("Directory creation escapes its captured export root.");
            using var directory = OpenDirectory(canonical, true);
            return PinDirectories(canonical);
        }
        var leases = new List<IDisposable>();
        try
        {
            var missing = new Stack<string>(); var current = canonical;
            while (!Directory.Exists(current)) { missing.Push(current); current = Path.GetDirectoryName(current) ?? throw new IOException("Storage root unavailable."); }
            leases.Add(PinDirectories(current));
            while (missing.TryPop(out var next)) { PathGuard.ValidateNoLinks(next); Directory.CreateDirectory(next); leases.Add(PinDirectories(next)); }
            return new DisposableGroup(leases);
        }
        catch { foreach (var lease in leases) lease.Dispose(); throw; }
    }
    private sealed class DisposableGroup(List<IDisposable> leases) : IDisposable
    { public void Dispose() { foreach (var lease in leases) lease.Dispose(); } }
    private static SafeFileHandle OpenDirectory(string path, bool create = false)
        => activeExport != null && PathGuard.Within(path, activeExport.Root) ? activeExport.OpenChildDirectory(path, create) : OpenDirectoryAbsolute(path, create);
    private static SafeFileHandle OpenDirectoryAbsolute(string path, bool create = false)
    {
        var canonical = PathGuard.Canonical(path);
        var descriptor = Open("/", 0x100100);
        if (descriptor < 0) throw new IOException("Cannot open storage root.");
        var directory = new SafeFileHandle((IntPtr)descriptor, true);
        try
        {
            foreach (var component in canonical.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var next = OpenAt(directory.DangerousGetHandle().ToInt32(), component, 0x100100, 0);
                if (next < 0 && create && Marshal.GetLastWin32Error() == 2)
                {
                    if (MakeDirectoryAt(directory.DangerousGetHandle().ToInt32(), component, 448) != 0 && Marshal.GetLastWin32Error() != 17) throw new IOException("Cannot create export directory.");
                    next = OpenAt(directory.DangerousGetHandle().ToInt32(), component, 0x100100, 0);
                }
                if (next < 0) throw new IOException("Directory unavailable or replaced with a link.");
                directory.Dispose(); directory = new SafeFileHandle((IntPtr)next, true);
            }
            return directory;
        }
        catch { directory.Dispose(); throw; }
    }
    public static FileStream OpenOwned(string path, FileMode mode, FileAccess access, FileShare sharing, int bufferSize = 4096)
    {
        PathGuard.ValidateNoLinks(path);
        if (OperatingSystem.IsWindows()) return new FileStream(path, mode, access, sharing, bufferSize);
        if (access != FileAccess.Read && activeExport != null && !PathGuard.Within(path, activeExport.Root)) throw new IOException("Writable file escapes its captured export root.");
        using var parent = OpenDirectory(Path.GetDirectoryName(PathGuard.Canonical(path))!);
        var flags = 0x100 | (access == FileAccess.Read ? 0 : access == FileAccess.Write ? 1 : 2);
        if (mode == FileMode.CreateNew) flags |= 0x200 | 0x800;
        else if (mode == FileMode.OpenOrCreate) flags |= 0x200;
        else if (mode != FileMode.Open) throw new ArgumentException("Only no-clobber/open modes are supported.", nameof(mode));
        var descriptor = OpenAt(parent.DangerousGetHandle().ToInt32(), Path.GetFileName(path), flags, 384);
        if (descriptor < 0) throw new IOException("Cannot open file without following links.", new Win32Exception(Marshal.GetLastWin32Error()));
        return new FileStream(new SafeFileHandle((IntPtr)descriptor, true), access, bufferSize, false);
    }
    public static void PublishPinned(FileStream staged, string part, string final)
        => RenamePinned(staged, part, final, false);
    public static void ReplaceOwnedMetadata(FileStream staged, string temporary, string final)
    {
        if (Path.GetFileName(final) != "review.json" || Path.GetFileName(Path.GetDirectoryName(final)) != ".photoscreener" ||
            !PathGuard.Comparer.Equals(Path.GetDirectoryName(temporary), Path.GetDirectoryName(final))) throw new IOException("Metadata replacement is restricted to its captured review file.");
        PathGuard.ValidateNoLinks(final);
        for (var attempt = 0; ; attempt++)
        {
            try { RenamePinned(staged, temporary, final, true); return; }
            catch (IOException e) when (attempt < 3 && e.InnerException is Win32Exception { NativeErrorCode: 5 or 32 or 33 })
            { Thread.Sleep(5 * (attempt + 1)); }
        }
    }
    private static void RenamePinned(FileStream staged, string part, string final, bool replace)
    {
        if (OperatingSystem.IsWindows())
        {
            var name = System.Text.Encoding.Unicode.GetBytes(Path.GetFullPath(final));
            var offset = IntPtr.Size == 8 ? 20 : 12;
            var buffer = Marshal.AllocHGlobal(offset + name.Length + 2);
            try
            {
                for (var i = 0; i < offset + name.Length + 2; i++) Marshal.WriteByte(buffer, i, 0);
                if (replace) Marshal.WriteByte(buffer, 0, 1);
                Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 16 : 8, name.Length);
                Marshal.Copy(name, 0, buffer + offset, name.Length);
                if (!SetRenameInformationByHandle(staged.SafeFileHandle, 3, buffer, (uint)(offset + name.Length + 2)))
                    throw new IOException("Atomic no-clobber publication failed.", new Win32Exception(Marshal.GetLastWin32Error()));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        else
        {
            using var source = OpenDirectory(Path.GetDirectoryName(part)!);
            using var destination = OpenDirectory(Path.GetDirectoryName(final)!);
            using var current = OpenReadCandidate(part);
            if (Identity(current.SafeFileHandle, out _) != Identity(staged.SafeFileHandle, out _)) throw new IOException("Staging was substituted before publication.");
            if (RenameExclusive(source.DangerousGetHandle().ToInt32(), Path.GetFileName(part), destination.DangerousGetHandle().ToInt32(), Path.GetFileName(final), replace ? 0u : 4u) != 0)
                throw new IOException("Atomic no-clobber publication failed.", new Win32Exception(Marshal.GetLastWin32Error()));
        }
    }
    public static IDisposable BackgroundWork() => new BackgroundScope();
    private sealed class BackgroundScope : IDisposable
    {
        [ThreadStatic] private static int depth;
        private readonly bool changed;
        public BackgroundScope()
        {
            if (++depth > 1) return;
            if (OperatingSystem.IsWindows()) changed = SetThreadPriority(GetCurrentThread(), 0x10000);
            else if (OperatingSystem.IsMacOS()) changed = SetQos(0x09, 0) == 0;
        }
        public void Dispose()
        {
            if (--depth > 0) return;
            if (!changed) return;
            if (OperatingSystem.IsWindows()) SetThreadPriority(GetCurrentThread(), 0x20000);
            else if (OperatingSystem.IsMacOS()) SetQos(0x15, 0);
        }
    }
    public static IDisposable PinDirectories(params string[] paths)
    {
        var handles = new List<SafeFileHandle>();
        var opened = new HashSet<string>(PathGuard.Comparer);
        try
        {
            foreach (var path in paths.Distinct(PathGuard.Comparer))
            {
                PathGuard.ValidateNoLinks(path);
                var current = Directory.Exists(path) ? PathGuard.Canonical(path) : Path.GetDirectoryName(PathGuard.Canonical(path));
                while (current != null)
                {
                    if (Directory.Exists(current) && opened.Add(current))
                    {
                        if (OperatingSystem.IsWindows())
                        {
                            var handle = CreateFile(current, 0, 1, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                            if (!GetFileInformationByHandle(handle, out var info) || (info.Attributes & 0x400) != 0) { handle.Dispose(); throw new IOException("Directory was replaced with a link."); }
                            handles.Add(handle);
                        }
                        else if (OperatingSystem.IsMacOS())
                        {
                            handles.Add(OpenDirectory(current));
                        }
                    }
                    current = Path.GetDirectoryName(current);
                }
                PathGuard.ValidateNoLinks(path);
            }
            return new HandleGroup(handles);
        }
        catch { foreach (var handle in handles) handle.Dispose(); throw; }
    }
    private sealed class HandleGroup(List<SafeFileHandle> handles) : IDisposable
    { public void Dispose() { foreach (var handle in handles) handle.Dispose(); } }
    public static FileStream OpenDeleteCandidate(string path)
        => OpenCandidate(path, true);
    public static FileStream OpenReadCandidate(string path)
        => OpenCandidate(path, false);
    private static FileStream OpenCandidate(string path, bool deletion)
    {
        PathGuard.ValidateNoLinks(path);
        if (OperatingSystem.IsWindows())
        {
            var handle = CreateFile(path, deletion ? 0x80010000u : 0x80000000u, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!GetFileInformationByHandle(handle, out var info) || (info.Attributes & 0x410) != 0 || info.Links != 1)
            { handle.Dispose(); throw new IOException("Deletion requires a regular, unaliased export file."); }
            return new FileStream(handle, FileAccess.Read, 1024 * 1024, false);
        }
        var file = OpenOwned(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024);
        Identity(file.SafeFileHandle, out var links);
        if (links != 1) { file.Dispose(); throw new IOException("Deletion requires an unaliased export file."); }
        return file;
    }
    public static void DeletePinned(FileStream stream, string path, string identity)
    {
        if (Identity(stream.SafeFileHandle, out var links) != identity || links != 1) throw new IOException("Export identity changed before deletion.");
        if (OperatingSystem.IsWindows())
        {
            var disposition = 1;
            if (!SetFileInformationByHandle(stream.SafeFileHandle, 4, ref disposition, 4)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        else
        {
            using var parent = OpenDirectory(Path.GetDirectoryName(path)!);
            // Move to a fresh private name first. A substituted candidate is preserved and restored;
            // only the exact verified inode can be unlinked. Never delete through an absolute path.
            var name = Path.GetFileName(path); var quarantine = $".photoscreener-delete-{Guid.NewGuid():N}.part";
            var descriptor = parent.DangerousGetHandle().ToInt32();
            if (RenameExclusive(descriptor, name, descriptor, quarantine, 4) != 0) throw new IOException("Cannot isolate export for verified deletion.");
            var held = OpenAt(descriptor, quarantine, 0x100, 0);
            if (held < 0) throw new IOException("Isolated export could not be verified; preserved as " + quarantine);
            using var isolated = new SafeFileHandle((IntPtr)held, true);
            if (Identity(isolated, out var isolatedLinks) != identity || isolatedLinks != 1)
            {
                var restored = RenameExclusive(descriptor, quarantine, descriptor, name, 4) == 0;
                throw new IOException(restored ? "Substituted export was preserved." : "Substituted export preserved as " + quarantine);
            }
            if (UnlinkAt(descriptor, quarantine, 0) != 0) throw new IOException("Verified deletion failed; preserved as " + quarantine);
        }
    }
    public static string Identity(string path)
    {
        using var stream = OpenOwned(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Identity(stream.SafeFileHandle, out _);
    }
    public static string Identity(SafeFileHandle handle, out uint links)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            links = info.Links;
            return $"win:{info.Volume}:{info.IndexHigh}:{info.IndexLow}";
        }
        if (OperatingSystem.IsMacOS())
        {
            if (FStat(handle.DangerousGetHandle().ToInt32(), out var stat) != 0) throw new IOException("Unable to read macOS file identity.");
            links = stat.Links;
            return $"mac:{stat.Device}:{stat.Inode}:{stat.BirthSeconds}:{stat.BirthNanoseconds}";
        }
        throw new PlatformNotSupportedException("Photo Screener supports Windows and macOS.");
    }
    public static string? ChangeToken(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!GetBasicInformationByHandle(handle, 0, out var info, 40))
            {
                var error = Marshal.GetLastWin32Error();
                if (error is 1 or 50 or 87) return null; // Unsupported filesystem: never reuse content caches.
                throw new Win32Exception(error);
            }
            return info.Changed == 0 ? null : $"win-change:{info.Changed}";
        }
        if (OperatingSystem.IsMacOS())
        {
            if (FStat(handle.DangerousGetHandle().ToInt32(), out var stat) != 0) throw new IOException("Unable to read macOS file change time.");
            return stat.ChangedSeconds == 0 ? null : $"mac-change:{stat.ChangedSeconds}:{stat.ChangedNanoseconds}";
        }
        throw new PlatformNotSupportedException();
    }
    [StructLayout(LayoutKind.Sequential)] private struct WinBasicInfo
    { public long Created, Accessed, Written, Changed; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct WinInfo
    {
        public uint Attributes; public uint CreatedLow, CreatedHigh, AccessedLow, AccessedHigh, WrittenLow, WrittenHigh;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [StructLayout(LayoutKind.Explicit, Size = 144)] private struct MacStat
    {
        [FieldOffset(0)] public int Device;
        [FieldOffset(6)] public ushort Links;
        [FieldOffset(8)] public ulong Inode;
        [FieldOffset(64)] public long ChangedSeconds;
        [FieldOffset(72)] public long ChangedNanoseconds;
        [FieldOffset(80)] public long BirthSeconds;
        [FieldOffset(88)] public long BirthNanoseconds;
    }
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle handle, out WinInfo info);
    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetBasicInformationByHandle(SafeFileHandle handle, int kind, out WinBasicInfo info, uint size);
    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)] private static partial int FStat(int descriptor, out MacStat stat);
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle handle, int kind, ref int information, uint size);
    [LibraryImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetRenameInformationByHandle(SafeFileHandle handle, int kind, IntPtr information, uint size);
    [LibraryImport("libc", EntryPoint = "renameatx_np", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int RenameExclusive(int sourceDirectory, string source, int destinationDirectory, string destination, uint flags);
    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Open(string path, int flags);
    [LibraryImport("libc", EntryPoint = "openat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenAtFixed(int directory, string path, int flags, uint mode);
    // Darwin ARM64 passes variadic arguments on the stack. openat's mode is
    // variadic, so a fixed four-argument P/Invoke puts it in the wrong register.
    // Exhaust x0..x7 before the mode argument to match the native stack ABI.
    [LibraryImport("libc", EntryPoint = "openat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenAtArm64(int directory, string path, int flags, long unused3, long unused4, long unused5, long unused6, long unused7, uint mode);
    private static int OpenAt(int directory, string path, int flags, uint mode)
        => OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? OpenAtArm64(directory, path, flags, 0, 0, 0, 0, 0, mode)
            : OpenAtFixed(directory, path, flags, mode);
    [LibraryImport("libc", EntryPoint = "mkdirat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int MakeDirectoryAt(int directory, string path, uint mode);
    [LibraryImport("libc", EntryPoint = "unlinkat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int UnlinkAt(int directory, string path, int flags);
    [LibraryImport("libc", EntryPoint = "flock", SetLastError = true)] private static partial int Flock(int descriptor, int operation);
    [LibraryImport("kernel32.dll")] private static partial IntPtr GetCurrentThread();
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetThreadPriority(IntPtr thread, int priority);
    [LibraryImport("libc", EntryPoint = "pthread_set_qos_class_self_np")] private static partial int SetQos(uint qos, int relativePriority);
    public static void ClaimOwnership(FileStream stream)
    {
        var identity = Identity(stream.SafeFileHandle, out var links);
        if (links != 1) throw new IOException("Writer lock is an unsafe file alias.");
        if (OperatingSystem.IsMacOS() && Flock(stream.SafeFileHandle.DangerousGetHandle().ToInt32(), 6) != 0)
            throw new IOException("Another Photo Screener instance owns this export target.");
    }
}
