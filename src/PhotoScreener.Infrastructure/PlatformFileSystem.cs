using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PhotoScreener.Infrastructure;
public static partial class PlatformFileSystem
{
    public static string Identity(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
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
            return $"mac:{stat.Device}:{stat.Inode}";
        }
        throw new PlatformNotSupportedException("Photo Screener supports Windows and macOS.");
    }
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
    }
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle handle, out WinInfo info);
    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)] private static partial int FStat(int descriptor, out MacStat stat);
}
