using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace PhotoScreener.Desktop.Imaging;
public static class ScaledDecoder
{
    // PNG decoding can need a native-size buffer. Serialize allocations above 64 MiB.
    private static readonly SemaphoreSlim LargeDecode = new(1);
    public static Bitmap Decode(string path, int longestSide, CancellationToken token)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        using var stream = new SKManagedStream(input);
        using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException("Unsupported or damaged JPEG/PNG.");
        var original = codec.Info;
        if ((long)original.Width * original.Height > 60_000_000) throw new InvalidDataException("Image exceeds the 60 MP decode limit.");
        var ratio = Math.Min(1f, (float)longestSide / Math.Max(original.Width, original.Height));
        var size = codec.GetScaledDimensions(ratio);
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
            using var oriented = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(oriented))
            {
                canvas.Scale((float)width / (swapped ? size.Height : size.Width), (float)height / (swapped ? size.Width : size.Height));
                canvas.SetMatrix(SKMatrix.Concat(canvas.TotalMatrix, Orientation(codec.EncodedOrigin, size.Width, size.Height)));
                using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = false };
                canvas.DrawBitmap(decoded, 0, 0, paint);
            }
            token.ThrowIfCancellationRequested();
            var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using var framebuffer = bitmap.Lock();
            var bytes = new byte[width * 4];
            for (var y = 0; y < height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(oriented.GetPixels() + y * oriented.RowBytes, bytes, 0, bytes.Length);
                System.Runtime.InteropServices.Marshal.Copy(bytes, 0, framebuffer.Address + y * framebuffer.RowBytes, bytes.Length);
            }
            return bitmap;
        }
        finally { if (large) LargeDecode.Release(); }
    }
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
