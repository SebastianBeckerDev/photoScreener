using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;

namespace PhotoScreener.Desktop.Imaging;

public static class PreviewHistogram
{
    public static int[]? Read(Bitmap bitmap)
    {
        if (bitmap is not WriteableBitmap writable) return null;
        using var frame = writable.Lock();
        var bins = new int[256]; var width = bitmap.PixelSize.Width; var height = bitmap.PixelSize.Height;
        var row = new byte[width * 4]; var stepX = Math.Max(1, width / 512); var stepY = Math.Max(1, height / 512);
        for (var y = 0; y < height; y += stepY)
        {
            Marshal.Copy(frame.Address + y * frame.RowBytes, row, 0, row.Length);
            for (var x = 0; x < width; x += stepX)
            {
                var pixel = x * 4; if (row[pixel + 3] == 0) continue;
                var luminance = (54 * row[pixel + 2] + 183 * row[pixel + 1] + 19 * row[pixel]) >> 8;
                bins[luminance]++;
            }
        }
        return bins;
    }
}
