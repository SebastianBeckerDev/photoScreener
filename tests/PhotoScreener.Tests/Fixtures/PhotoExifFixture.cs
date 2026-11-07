using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace PhotoScreener.TestFixtures;

public static class PhotoExifFixture
{
    public static byte[] Tiff(bool southWest = false)
    {
        const uint exifOffset = 62, gpsOffset = 152;
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
        writer.Write(new byte[230]);
        uint Blob(byte[] bytes) { var offset = (uint)stream.Position; writer.Write(bytes); return offset; }
        uint Text(string value) => Blob(Encoding.ASCII.GetBytes(value + '\0'));
        uint Rational(params uint[] values) { var offset = (uint)stream.Position; foreach (var value in values) writer.Write(value); return offset; }
        var make = Text("TestCam"); var model = Text("Model X"); var date = Text("2024:05:06 12:34:56");
        var zone = Text("+02:00"); var lens = Text("Lens 50mm");
        var exposure = Rational(1, 250); var aperture = Rational(28, 10); var focal = Rational(50, 1);
        var latitude = Rational(47, 1, 30, 1, 0, 1); var longitude = Rational(8, 1, 15, 1, 0, 1); var altitude = Rational(500, 1);
        stream.Position = 0; writer.Write((byte)'I'); writer.Write((byte)'I'); writer.Write((ushort)42); writer.Write((uint)8);
        void Ifd(uint offset, params (ushort Tag, ushort Type, uint Count, uint Value)[] entries)
        {
            stream.Position = offset; writer.Write((ushort)entries.Length);
            foreach (var entry in entries.OrderBy(e => e.Tag)) { writer.Write(entry.Tag); writer.Write(entry.Type); writer.Write(entry.Count); writer.Write(entry.Value); }
            writer.Write(0u);
        }
        Ifd(8, (0x010F, 2, 8, make), (0x0110, 2, 8, model), (0x8769, 4, 1, exifOffset), (0x8825, 4, 1, gpsOffset));
        Ifd(exifOffset, (0x829A, 5, 1, exposure), (0x829D, 5, 1, aperture), (0x8827, 3, 1, 400), (0x9003, 2, 20, date),
            (0x9011, 2, 7, zone), (0x920A, 5, 1, focal), (0xA434, 2, 10, lens));
        Ifd(gpsOffset, (1, 2, 2, southWest ? (uint)'S' : 'N'), (2, 5, 3, latitude), (3, 2, 2, southWest ? (uint)'W' : 'E'),
            (4, 5, 3, longitude), (5, 1, 1, 0), (6, 5, 1, altitude));
        return stream.ToArray();
    }
    public static byte[] Jpeg(byte[] jpeg, bool southWest = false)
    {
        var tiff = Tiff(southWest); var length = tiff.Length + 8;
        using var stream = new MemoryStream(); stream.Write(jpeg.AsSpan(0, 2));
        stream.Write([0xff, 0xe1, (byte)(length >> 8), (byte)length]); stream.Write("Exif\0\0"u8); stream.Write(tiff); stream.Write(jpeg.AsSpan(2));
        return stream.ToArray();
    }
    public static byte[] Png(bool withExif = true, bool southWest = false)
    {
        using var stream = new MemoryStream(); stream.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        void Chunk(string type, byte[] bytes)
        {
            Span<byte> number = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(number, (uint)bytes.Length); stream.Write(number);
            var name = Encoding.ASCII.GetBytes(type); stream.Write(name); stream.Write(bytes);
            uint crc = uint.MaxValue;
            foreach (var value in name.Concat(bytes)) { crc ^= value; for (var i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u); }
            BinaryPrimitives.WriteUInt32BigEndian(number, ~crc); stream.Write(number);
        }
        var header = new byte[13]; BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), 1); BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), 1); header[8] = 8; header[9] = 2;
        Chunk("IHDR", header);
        using var pixels = new MemoryStream(); using (var compressed = new ZLibStream(pixels, CompressionLevel.Fastest, true)) compressed.Write([0, 255, 0, 0]);
        Chunk("IDAT", pixels.ToArray()); if (withExif) Chunk("eXIf", Tiff(southWest)); Chunk("IEND", []);
        return stream.ToArray();
    }
}
