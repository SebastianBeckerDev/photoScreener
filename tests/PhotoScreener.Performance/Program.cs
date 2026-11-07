using SkiaSharp;
using System.Text.Json;
using System.Security.Cryptography;
using PhotoScreener.TestFixtures;

if (args.Length < 1) { Console.WriteLine("Usage: PhotoScreener.Performance <corpus-directory> [count=100] [width=4000] [height=3000] [--exif] [--folders N] [--export-samples N]"); return; }
var directory = Path.GetFullPath(args[0]); Directory.CreateDirectory(directory);
var count = args.Length > 1 ? int.Parse(args[1]) : 100;
var width = args.Length > 2 ? int.Parse(args[2]) : 4000;
var height = args.Length > 3 ? int.Parse(args[3]) : 3000;
var withExif = args.Contains("--exif");
var folderCount = OptionInt("--folders", 1); var exportSamples = OptionInt("--export-samples", 0);
if (count < 1 || width < 1 || height < 1 || folderCount < 1 || folderCount > count || exportSamples < 0) throw new ArgumentException("Counts and dimensions must be positive; export samples may be zero.");
var folders = Enumerable.Range(0, folderCount).Select(i => folderCount == 1 ? directory : Path.Combine(directory, $"folder-{i:D5}")).ToArray();
foreach (var folder in folders) Directory.CreateDirectory(folder);
using var image = new SKBitmap(width, height);
using var canvas = new SKCanvas(image);
var random = new Random(813);
using var paint = new SKPaint();
for (var y = 0; y < height; y += 8)
    for (var x = 0; x < width; x += 8)
    { paint.Color = new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)); canvas.DrawRect(x, y, 8, 8, paint); }
for (var i = 0; i < count; i++)
{
    paint.Color = SKColors.White; paint.TextSize = 160;
    canvas.DrawText($"Photo Screener diagnostic {i:D6}", 50, 200, paint);
    using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
    var bytes = withExif ? PhotoExifFixture.Jpeg(data.ToArray()) : data.ToArray();
    var folder = folders[i % folderCount]; var name = $"diagnostic-{i:D6}.JPG";
    File.WriteAllBytes(Path.Combine(folder, name), bytes);
    if (i / folderCount < exportSamples)
    {
        var export = Path.Combine(folder, "export"); Directory.CreateDirectory(export);
        var suffix = Convert.ToBase64String(MD5.HashData(bytes))[..4].Replace('+', '-').Replace('/', '_');
        File.WriteAllBytes(Path.Combine(export, Path.GetFileNameWithoutExtension(name) + "." + suffix + ".JPG"), bytes);
    }
}
File.WriteAllText(Path.Combine(directory, "corpus.json"), JsonSerializer.Serialize(new { kind = "synthetic diagnostic, not representative photographic evidence", count, width, height, folders = folderCount, exportSamplesPerFolder = exportSamples, seed = 813, embeddedExifFixture = withExif }));
Console.WriteLine($"Generated {count} {width}×{height} synthetic JPEGs across {folderCount} folders in {directory}");
int OptionInt(string option, int fallback) { var index = Array.IndexOf(args, option); return index >= 0 ? int.Parse(args[index + 1]) : fallback; }
