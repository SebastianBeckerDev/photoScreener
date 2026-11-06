using SkiaSharp;
using System.Text.Json;

if (args.Length < 1) { Console.WriteLine("Usage: PhotoScreener.Performance <corpus-directory> [count=100] [width=4000] [height=3000]"); return; }
var directory = Path.GetFullPath(args[0]); Directory.CreateDirectory(directory);
var count = args.Length > 1 ? int.Parse(args[1]) : 100;
var width = args.Length > 2 ? int.Parse(args[2]) : 4000;
var height = args.Length > 3 ? int.Parse(args[3]) : 3000;
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
    using var file = File.Create(Path.Combine(directory, $"diagnostic-{i:D6}.JPG")); data.SaveTo(file);
}
File.WriteAllText(Path.Combine(directory, "corpus.json"), JsonSerializer.Serialize(new { kind = "synthetic diagnostic, not representative photographic evidence", count, width, height, seed = 813 }));
Console.WriteLine($"Generated {count} {width}×{height} synthetic JPEGs in {directory}");
