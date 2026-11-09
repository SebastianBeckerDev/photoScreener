using System.Buffers.Binary;
using SkiaSharp;

namespace PhotoScreener.Desktop.Imaging;

// The pinned Skia PNG codec does not expose eXIf orientation. Read only the
// bounded TIFF orientation field; skip encoded image data by seeking, and
// restore the stream position so the existing codec can decode it normally.
internal static class PngOrientation
{
    internal static SKEncodedOrigin? Read(Stream input, CancellationToken token)
    {
        const int maximumExif = 64 * 1024, maximumChunks = 4096;
        var position = input.Position;
        try
        {
            input.Position = 0;
            Span<byte> header = stackalloc byte[8];
            Span<byte> crcBytes = stackalloc byte[4];
            ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
            if (!ReadExact(input, header, token) || !header.SequenceEqual(signature)) return null;
            for (var chunk = 0; chunk < maximumChunks; chunk++)
            {
                token.ThrowIfCancellationRequested();
                if (!ReadExact(input, header, token)) return null;
                var length = BinaryPrimitives.ReadUInt32BigEndian(header);
                if (length > input.Length - input.Position - 4) return null;
                if (header[4..].SequenceEqual("IEND"u8)) return null;
                if (header[4..].SequenceEqual("eXIf"u8) && length is >= 8 and <= maximumExif)
                {
                    var payload = new byte[(int)length];
                    if (!ReadExact(input, payload, token)) return null;
                    if (!ReadExact(input, crcBytes, token)) return null;
                    uint crc = uint.MaxValue;
                    foreach (var value in header[4..]) crc = UpdateCrc(crc, value);
                    foreach (var value in payload) crc = UpdateCrc(crc, value);
                    if (~crc == BinaryPrimitives.ReadUInt32BigEndian(crcBytes)) return ReadTiff(payload);
                }
                else input.Seek((long)length + 4, SeekOrigin.Current);
            }
            return null;
        }
        finally { input.Position = position; }
    }

    private static SKEncodedOrigin? ReadTiff(ReadOnlySpan<byte> tiff)
    {
        var little = tiff[..2].SequenceEqual("II"u8);
        if (!little && !tiff[..2].SequenceEqual("MM"u8)) return null;
        if (UInt16(tiff[2..], little) != 42) return null;
        var offset = little ? BinaryPrimitives.ReadUInt32LittleEndian(tiff[4..]) : BinaryPrimitives.ReadUInt32BigEndian(tiff[4..]);
        if (offset < 8 || offset > tiff.Length - 2) return null;
        var ifd = tiff[(int)offset..]; var count = UInt16(ifd, little);
        if ((long)count * 12 + 6 > ifd.Length) return null;
        for (var i = 0; i < count; i++)
        {
            var field = ifd.Slice(2 + i * 12, 12);
            if (UInt16(field, little) != 0x0112) continue;
            var values = little ? BinaryPrimitives.ReadUInt32LittleEndian(field[4..]) : BinaryPrimitives.ReadUInt32BigEndian(field[4..]);
            if (UInt16(field[2..], little) != 3 || values != 1) return null;
            var orientation = UInt16(field[8..], little);
            return orientation is >= 1 and <= 8 ? (SKEncodedOrigin)orientation : null;
        }
        return null;
    }

    private static ushort UInt16(ReadOnlySpan<byte> bytes, bool little) => little
        ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes);
    private static uint UpdateCrc(uint crc, byte value)
    { crc ^= value; for (var i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u); return crc; }
    private static bool ReadExact(Stream input, Span<byte> bytes, CancellationToken token)
    {
        while (!bytes.IsEmpty)
        { token.ThrowIfCancellationRequested(); var read = input.Read(bytes); if (read == 0) return false; bytes = bytes[read..]; }
        return true;
    }
}
