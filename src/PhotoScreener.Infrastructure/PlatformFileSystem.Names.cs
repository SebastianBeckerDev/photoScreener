using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PhotoScreener.Infrastructure;

public static partial class PlatformFileSystem
{
    // Read the actual mounted filesystem policy. macOS is not uniformly
    // case-insensitive; APFS and case-sensitive APFS can be used together.
    public static bool CaseSensitiveNames(string directory)
    {
        if (!OperatingSystem.IsMacOS()) return false; // Windows ambiguity is rejected by its binding guard.
        using var held = OpenDirectory(directory);
        var result = FPathConf(held.DangerousGetHandle().ToInt32(), 11); // Darwin _PC_CASE_SENSITIVE
        if (result is not (0 or 1)) throw new IOException("Cannot establish filesystem filename rules.", new Win32Exception(Marshal.GetLastWin32Error()));
        return result == 1;
    }

    // APFS treats canonically equivalent Unicode spellings as the same name,
    // including on its case-sensitive profile. Windows keeps its existing rules.
    public static string AssociationName(string name) => OperatingSystem.IsMacOS() ? name.Normalize(NormalizationForm.FormC) : name;

    internal static unsafe void RequireStoredName(SafeFileHandle held, string expected)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var attributes = new MacNameAttributes { BitmapCount = 5, Common = 1 }; // ATTR_CMN_NAME
        Span<byte> buffer = stackalloc byte[4096];
        buffer.Clear();
        fixed (byte* bytes = buffer)
            if (FGetAttrList(held.DangerousGetHandle().ToInt32(), ref attributes, bytes, (nuint)buffer.Length, 0) != 0)
                throw new IOException("Cannot verify the stored destination filename.", new Win32Exception(Marshal.GetLastWin32Error()));
        var returned = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        var offset = (long)BinaryPrimitives.ReadInt32LittleEndian(buffer[4..]) + 4; // Relative to attrreference.
        var length = BinaryPrimitives.ReadUInt32LittleEndian(buffer[8..]);
        if (returned < 12 || returned > buffer.Length || offset < 12 || length < 2 || offset + length > returned || buffer[(int)(offset + length - 1)] != 0)
            throw new IOException("Invalid native destination filename attributes.");
        string stored;
        try { stored = new UTF8Encoding(false, true).GetString(buffer.Slice((int)offset, (int)length - 1)); }
        catch (DecoderFallbackException e) { throw new IOException("Invalid native destination filename encoding.", e); }
        if (!string.Equals(AssociationName(stored), AssociationName(expected), StringComparison.Ordinal))
            throw new IOException("Legacy filename collision: the export filesystem cannot distinguish the original filename case. No file was overwritten.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MacNameAttributes
    {
        public ushort BitmapCount, Reserved;
        public uint Common, Volume, Directory, File, Fork;
    }
    [LibraryImport("libc", EntryPoint = "fpathconf", SetLastError = true)]
    private static partial long FPathConf(int descriptor, int name);
    // Unlike fcntl, fgetattrlist has a fixed signature on Darwin ARM64.
    [LibraryImport("libc", EntryPoint = "fgetattrlist", SetLastError = true)]
    private static unsafe partial int FGetAttrList(int descriptor, ref MacNameAttributes attributes, byte* buffer, nuint size, nuint options);
}
