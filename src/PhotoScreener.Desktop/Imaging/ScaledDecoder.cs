using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace PhotoScreener.Desktop.Imaging;
public static class ScaledDecoder
{
    // PNG decoding can need a native-size buffer. Serialize allocations above 64 MiB.
    private static readonly SemaphoreSlim LargeDecode = new(1);
    private const int MaximumEncodedBytes = 40 * 1024 * 1024;
    private static readonly SemaphoreSlim EncodedSlots = new(2);
    private static readonly ConcurrentBag<SKData> EncodedBuffers = [];
    public static Bitmap Decode(string path, int longestSide, CancellationToken token, bool quickPreview = false)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        using var stream = new SKManagedStream(input);
        using var headerCodec = SKCodec.Create(stream) ?? throw new InvalidDataException("Unsupported or damaged JPEG/PNG.");
        // JPEG entropy decoding performs many small stream reads. A bounded
        // native encoded buffer avoids managed callbacks throughout that work.
        // Two reusable buffers reserve at most 80 MiB, avoiding large-allocation
        // fragmentation during sustained navigation. Native codecs return each
        // buffer only when their borrowed data reference has been released.
        // PNG and larger files keep the existing streamed allocation policy.
        using var encoded = headerCodec.EncodedFormat == SKEncodedImageFormat.Jpeg && input.Length <= MaximumEncodedBytes ? ReadEncoded(input, token) : null;
        using var memoryCodec = encoded != null ? SKCodec.Create(encoded) ?? throw new InvalidDataException("JPEG memory decode could not be initialized.") : null;
        var codec = memoryCodec ?? headerCodec;
        var original = codec.Info;
        if ((long)original.Width * original.Height > 60_000_000) throw new InvalidDataException("Image exceeds the 60 MP decode limit.");
        var ratio = Math.Min(1f, (float)longestSide / Math.Max(original.Width, original.Height));
        // The first JPEG view uses the codec's lowest native DCT resolution.
        // This avoids decoding a larger fraction just to downscale a brief quick
        // preview. Full refinement still decodes for its requested output size.
        var decodeRatio = quickPreview && codec.EncodedFormat == SKEncodedImageFormat.Jpeg ? Math.Min(ratio, .125f) : ratio;
        var size = codec.GetScaledDimensions(decodeRatio);
        var large = (long)size.Width * size.Height * 4 > 64 * 1024 * 1024;
        if (large) LargeDecode.Wait(token);
        try
        {
            token.ThrowIfCancellationRequested();
            using var decoded = new SKBitmap(new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            var result = codec.GetPixels(decoded.Info, decoded.GetPixels());
            if (result != SKCodecResult.Success) throw new InvalidDataException($"Image decode failed: {result}.");
            token.ThrowIfCancellationRequested();
            var swapped = (int)codec.EncodedOrigin >= 5;
            var width = Math.Max(1, (int)Math.Round((swapped ? original.Height : original.Width) * ratio));
            var height = Math.Max(1, (int)Math.Round((swapped ? original.Width : original.Height) * ratio));
            var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            try
            {
                using var framebuffer = bitmap.Lock();
                using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), framebuffer.Address, framebuffer.RowBytes)
                    ?? throw new InvalidDataException("Preview surface allocation failed.");
                var canvas = surface.Canvas;
                canvas.Scale((float)width / (swapped ? size.Height : size.Width), (float)height / (swapped ? size.Width : size.Height));
                canvas.SetMatrix(SKMatrix.Concat(canvas.TotalMatrix, Orientation(codec.EncodedOrigin, size.Width, size.Height)));
                using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = false };
                canvas.DrawBitmap(decoded, 0, 0, paint);
                canvas.Flush(); token.ThrowIfCancellationRequested();
                return bitmap;
            }
            catch { bitmap.Dispose(); throw; }
        }
        finally { if (large) LargeDecode.Release(); }
    }
    private static SKData ReadEncoded(FileStream input, CancellationToken token)
    {
        EncodedSlots.Wait(token);
        SKData? buffer = null; byte[]? transfer = null; var borrowed = false;
        try
        {
            buffer = EncodedBuffers.TryTake(out var reusable) ? reusable : SKData.Create(MaximumEncodedBytes);
            if (buffer == null) throw new IOException("JPEG encoded buffer could not be allocated.");
            transfer = ArrayPool<byte>.Shared.Rent(65536);
            var observedLength = input.Length;
            if (observedLength <= 0 || observedLength > MaximumEncodedBytes) throw new IOException("JPEG size changed before its encoded buffer was filled.");
            input.Position = 0; var length = (int)observedLength; var offset = 0;
            while (offset < length)
            {
                token.ThrowIfCancellationRequested();
                var read = input.Read(transfer, 0, Math.Min(transfer.Length, length - offset));
                if (read == 0) throw new IOException("JPEG ended before its encoded buffer was filled.");
                Marshal.Copy(transfer, 0, IntPtr.Add(buffer.Data, offset), read); offset += read;
            }
            var data = SKData.Create(buffer.Data, length, ReleaseEncoded, buffer) ?? throw new IOException("JPEG encoded buffer could not be borrowed.");
            borrowed = true; return data;
        }
        finally
        {
            if (transfer != null) ArrayPool<byte>.Shared.Return(transfer);
            if (!borrowed) { if (buffer != null) EncodedBuffers.Add(buffer); EncodedSlots.Release(); }
        }
    }
    private static void ReleaseEncoded(IntPtr address, object context)
    { EncodedBuffers.Add((SKData)context); EncodedSlots.Release(); }
    public static SKMatrix Orientation(SKEncodedOrigin origin, int w, int h) => origin switch
    {
        SKEncodedOrigin.TopRight => Matrix(-1, 0, w, 0, 1, 0),
        SKEncodedOrigin.BottomRight => Matrix(-1, 0, w, 0, -1, h),
        SKEncodedOrigin.BottomLeft => Matrix(1, 0, 0, 0, -1, h),
        SKEncodedOrigin.LeftTop => Matrix(0, 1, 0, 1, 0, 0),
        SKEncodedOrigin.RightTop => Matrix(0, -1, h, 1, 0, 0),
        SKEncodedOrigin.RightBottom => Matrix(0, -1, h, -1, 0, w),
        SKEncodedOrigin.LeftBottom => Matrix(0, 1, 0, -1, 0, w),
        _ => SKMatrix.CreateIdentity()
    };
    private static SKMatrix Matrix(float a, float b, float c, float d, float e, float f) => new()
    { ScaleX = a, SkewX = b, TransX = c, SkewY = d, ScaleY = e, TransY = f, Persp2 = 1 };
}
