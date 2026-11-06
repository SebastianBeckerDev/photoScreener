using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;

namespace PhotoScreener.Desktop.Imaging;
public sealed class ThumbnailDiskCache
{
    private readonly string root;
    private int writes;
    public int SuccessfulWrites => Volatile.Read(ref writes);
    public ThumbnailDiskCache(string? directory = null) => root = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoScreener", "thumbnail-cache-v1");
    private string PathFor(string key) => Path.Combine(root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".png");
    public Bitmap? Read(string key)
    {
        try
        {
            var path = PathFor(key);
            if (!File.Exists(path)) return null;
            using var file = File.OpenRead(path); var bitmap = new Bitmap(file);
            if (bitmap.PixelSize.Width > 160 || bitmap.PixelSize.Height > 160) { bitmap.Dispose(); return null; }
            return bitmap;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
    public void Write(string key, Bitmap bitmap)
    {
        try
        {
            Directory.CreateDirectory(root); var path = PathFor(key); var temporary = path + ".tmp";
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) bitmap.Save(file);
            File.Move(temporary, path, true);
            if (++writes % 128 == 0) Evict();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
    }
    private void Evict()
    {
        var files = new DirectoryInfo(root).EnumerateFiles("*.png").OrderBy(f => f.LastWriteTimeUtc).ToArray();
        var total = files.Sum(f => f.Length);
        foreach (var file in files)
        {
            if (total <= 512L * 1024 * 1024) break;
            total -= file.Length; file.Delete();
        }
    }
}
