using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using PhotoScreener.Desktop.Imaging;
using SkiaSharp;
using Xunit;

namespace PhotoScreener.Desktop.Tests;

public sealed class PngPreviewTests
{
    [AvaloniaFact]
    public async Task BigEndianPngExifAfterImageDataIsAppliedWithoutReadingThePixelPayloadAgain()
    {
        var path = Path.Combine(Path.GetTempPath(), "lumapick-png-" + Guid.NewGuid().ToString("N") + ".PNG");
        using var original = new SKBitmap(400, 200); original.Erase(SKColors.Coral);
        using var encoded = original.Encode(SKEncodedImageFormat.Png, 100); var png = encoded.ToArray();
        byte[] tiff = [77, 77, 0, 42, 0, 0, 0, 8, 0, 1, 1, 18, 0, 3, 0, 0, 0, 1, 0, 6, 0, 0, 0, 0, 0, 0];
        byte[] withExif = [.. png[..^12], .. Chunk("eXIf"u8, tiff), .. png[^12..]];
        File.WriteAllBytes(path, withExif);
        try
        {
            using var counted = new CountingStream(withExif); counted.Position = 17;
            Assert.Equal(SKEncodedOrigin.RightTop, PngOrientation.Read(counted, default));
            Assert.Equal(17, counted.Position);
            Assert.True(counted.BytesRead < 128, $"Metadata inspection read {counted.BytesRead} encoded bytes.");
            using var low = await Task.Run(() => ScaledDecoder.Decode(path, 100, default, quickPreview: true));
            using var full = await Task.Run(() => ScaledDecoder.Decode(path, 400, default));
            Assert.Equal(new PixelSize(50, 100), low.PixelSize); Assert.Equal(new PixelSize(200, 400), full.PixelSize);
            AssertPixel(low, 25, 50, SKColors.Coral, 1); AssertPixel(full, 100, 200, SKColors.Coral, 1);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("offset")] [InlineData("count")] [InlineData("orientation")] [InlineData("crc")] [InlineData("oversized")]
    public void InvalidPngOrientationIsOptionalAndInspectionRemainsBounded(string fault)
    {
        byte[] tiff = [73, 73, 42, 0, 8, 0, 0, 0, 1, 0, 18, 1, 3, 0, 1, 0, 0, 0, 6, 0, 0, 0, 0, 0, 0, 0];
        if (fault == "offset") BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), uint.MaxValue);
        if (fault == "count") BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(8), ushort.MaxValue);
        if (fault == "orientation") tiff[18] = 9;
        if (fault == "oversized") tiff = new byte[64 * 1024 + 1];
        var exif = Chunk("eXIf"u8, tiff); if (fault == "crc") exif[^1] ^= 1;
        byte[] bytes = [137, 80, 78, 71, 13, 10, 26, 10, .. exif, .. Chunk("IEND"u8, [])];
        using var input = new CountingStream(bytes); input.Position = 3;
        Assert.Null(PngOrientation.Read(input, default)); Assert.Equal(3, input.Position);
        Assert.InRange(input.BytesRead, 8, 64);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => PngOrientation.Read(input, cancelled.Token)); Assert.Equal(3, input.Position);
    }

    [AvaloniaTheory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public async Task PngExifOrientationPreservesCompositionAtBothPreviewStages(int orientation)
    {
        var path = Path.Combine(Path.GetTempPath(), "lumapick-png-" + Guid.NewGuid().ToString("N") + ".PNG");
        var colors = new[] { SKColors.Red, SKColors.Lime, SKColors.Blue, SKColors.Yellow };
        using var original = new SKBitmap(400, 200);
        using (var canvas = new SKCanvas(original))
            for (var q = 0; q < 4; q++)
            {
                using var paint = new SKPaint { Color = colors[q] };
                canvas.DrawRect(new SKRect(q % 2 * 200, q / 2 * 100, q % 2 * 200 + 200, q / 2 * 100 + 100), paint);
            }
        using var encoded = original.Encode(SKEncodedImageFormat.Png, 100);
        var png = encoded.ToArray();
        // PNG eXIf carries raw TIFF, without the JPEG APP1 Exif prefix.
        byte[] tiff = [73, 73, 42, 0, 8, 0, 0, 0, 1, 0, 18, 1, 3, 0, 1, 0, 0, 0, (byte)orientation, 0, 0, 0, 0, 0, 0, 0];
        File.WriteAllBytes(path, [.. png[..33], .. Chunk("eXIf"u8, tiff), .. png[33..]]);
        var before = SHA256.HashData(File.ReadAllBytes(path));
        try
        {
            using var low = await Task.Run(() => ScaledDecoder.Decode(path, 100, default, quickPreview: true));
            using var full = await Task.Run(() => ScaledDecoder.Decode(path, 400, default));
            var swapped = orientation >= 5;
            Assert.Equal(swapped ? new PixelSize(50, 100) : new PixelSize(100, 50), low.PixelSize);
            Assert.Equal(swapped ? new PixelSize(200, 400) : new PixelSize(400, 200), full.PixelSize);
            int[][] quadrants = [[0, 1, 2, 3], [1, 0, 3, 2], [3, 2, 1, 0], [2, 3, 0, 1],
                [0, 2, 1, 3], [2, 0, 3, 1], [3, 1, 2, 0], [1, 3, 0, 2]];
            foreach (var bitmap in new[] { low, full })
                for (var q = 0; q < 4; q++)
                    AssertPixel(bitmap, (q % 2 * 2 + 1) * bitmap.PixelSize.Width / 4,
                        (q / 2 * 2 + 1) * bitmap.PixelSize.Height / 4, colors[quadrants[orientation - 1][q]], 1);
            Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
        }
        finally { File.Delete(path); }
    }

    [AvaloniaFact]
    public async Task TwelveMegapixelPngKeepsTransparencyAndDetailWithConcurrentQuickAndFullDecodes()
    {
        var path = Path.Combine(Path.GetTempPath(), "lumapick-png-" + Guid.NewGuid().ToString("N") + ".png");
        var colors = new[] { SKColors.Red, new SKColor(0, 255, 0, 128), SKColors.Blue, SKColors.Transparent };
        using (var original = new SKBitmap(new SKImageInfo(4000, 3000, SKColorType.Bgra8888, SKAlphaType.Premul)))
        {
            using var canvas = new SKCanvas(original); canvas.Clear(SKColors.Transparent);
            for (var q = 0; q < 4; q++)
            {
                using var paint = new SKPaint { Color = colors[q], BlendMode = SKBlendMode.Src };
                canvas.DrawRect(new SKRect(q % 2 * 2000, q / 2 * 1500, q % 2 * 2000 + 2000, q / 2 * 1500 + 1500), paint);
            }
            using var encoded = original.Encode(SKEncodedImageFormat.Png, 100); File.WriteAllBytes(path, encoded.ToArray());
        }
        var before = SHA256.HashData(File.ReadAllBytes(path));
        Bitmap[] outputs = [];
        try
        {
            outputs = await Task.WhenAll(Task.Run(() => ScaledDecoder.Decode(path, 800, default, quickPreview: true)),
                Task.Run(() => ScaledDecoder.Decode(path, 2000, default))).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(new PixelSize(800, 600), outputs[0].PixelSize);
            Assert.Equal(new PixelSize(2000, 1500), outputs[1].PixelSize);
            foreach (var bitmap in outputs)
                for (var q = 0; q < 4; q++)
                    AssertPixel(bitmap, (q % 2 * 2 + 1) * bitmap.PixelSize.Width / 4,
                        (q / 2 * 2 + 1) * bitmap.PixelSize.Height / 4, colors[q], 1);
            Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
        }
        finally { foreach (var bitmap in outputs) bitmap.Dispose(); File.Delete(path); }
    }

    private static void AssertPixel(Bitmap bitmap, int x, int y, SKColor color, int tolerance)
    {
        using var pixels = ((WriteableBitmap)bitmap).Lock();
        var value = unchecked((uint)Marshal.ReadInt32(pixels.Address, y * pixels.RowBytes + x * 4));
        // Avalonia BGRA storage is premultiplied; compare its actual alpha and channels.
        Assert.InRange(Math.Abs((int)(value >> 24) - color.Alpha), 0, tolerance);
        Assert.InRange(Math.Abs((int)((value >> 16) & 255) - color.Red * color.Alpha / 255), 0, tolerance);
        Assert.InRange(Math.Abs((int)((value >> 8) & 255) - color.Green * color.Alpha / 255), 0, tolerance);
        Assert.InRange(Math.Abs((int)(value & 255) - color.Blue * color.Alpha / 255), 0, tolerance);
    }

    private static byte[] Chunk(ReadOnlySpan<byte> type, ReadOnlySpan<byte> payload)
    {
        var chunk = new byte[payload.Length + 12]; BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)payload.Length);
        type.CopyTo(chunk.AsSpan(4)); payload.CopyTo(chunk.AsSpan(8));
        uint crc = uint.MaxValue;
        foreach (var value in chunk.AsSpan(4, payload.Length + 4))
        { crc ^= value; for (var i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u); }
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(payload.Length + 8), ~crc); return chunk;
    }

    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int BytesRead { get; private set; }
        public override int Read(Span<byte> buffer) { var read = base.Read(buffer); BytesRead += read; return read; }
    }
}
