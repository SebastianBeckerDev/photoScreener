using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using PhotoScreener.Desktop;
using PhotoScreener.Desktop.Imaging;
using SkiaSharp;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(PhotoScreener.Desktop.Tests.TestApp))]
namespace PhotoScreener.Desktop.Tests;
public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia();
}
public sealed class WindowTests
{
    [AvaloniaFact]
    public async Task WindowKeysPreserveFocusAndSuppressRepeat()
    {
        var root = Path.Combine(Path.GetTempPath(), "photoscreener-window-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using (var bitmap = new SKBitmap(64, 48))
        {
            bitmap.Erase(SKColors.Coral); using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 95);
            File.WriteAllBytes(Path.Combine(root, "a.JPG"), data.ToArray()); File.WriteAllBytes(Path.Combine(root, "b.JPG"), data.ToArray());
        }
        var window = new MainWindow(); window.Show(); await window.OpenFolder(root);
        var surface = window.FindControl<Control>("ReviewSurface")!; surface.Focus();
        var strip = window.FindControl<ListBox>("Filmstrip")!;
        var first = strip.SelectedItem;
        void Down(Key key) => surface.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = surface });
        void Up(Key key) => surface.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key, Source = surface });
        Down(Key.Space); var once = window.FindControl<TextBlock>("CurrentStatus")!.Text;
        Down(Key.Space); Assert.Equal(once, window.FindControl<TextBlock>("CurrentStatus")!.Text);
        Assert.Same(first, strip.SelectedItem); Up(Key.Space); Down(Key.Space); Up(Key.Space);
        Assert.NotEqual(once, window.FindControl<TextBlock>("CurrentStatus")!.Text);
        Down(Key.Right); Assert.NotSame(first, strip.SelectedItem);
        Down(Key.Left); Assert.Same(first, strip.SelectedItem);
        window.Close();
        for (var i = 0; i < 100 && window.IsVisible; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        Assert.False(window.IsVisible); Directory.Delete(root, true);
    }
    [AvaloniaTheory]
    [InlineData(1, false)] [InlineData(2, false)] [InlineData(3, false)] [InlineData(4, false)]
    [InlineData(5, true)] [InlineData(6, true)] [InlineData(7, true)] [InlineData(8, true)]
    public void ExifAllOrientationsAtBothStages(int orientation, bool swapped)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".JPG");
        using var original = new SKBitmap(400, 200); original.Erase(SKColors.Coral);
        using var encoded = original.Encode(SKEncodedImageFormat.Jpeg, 95);
        // APP1 little-endian TIFF orientation; actual decoder must consume it, not just a matrix helper.
        byte[] exif = [0xff, 0xe1, 0, 34, 69, 120, 105, 102, 0, 0, 73, 73, 42, 0, 8, 0, 0, 0, 1, 0,
            18, 1, 3, 0, 1, 0, 0, 0, (byte)orientation, 0, 0, 0, 0, 0, 0, 0];
        var bytes = encoded.ToArray(); File.WriteAllBytes(path, [.. bytes[..2], .. exif, .. bytes[2..]]);
        try
        {
            using var low = ScaledDecoder.Decode(path, 100, default); using var full = ScaledDecoder.Decode(path, 400, default);
            Assert.Equal(swapped ? new PixelSize(50, 100) : new PixelSize(100, 50), low.PixelSize);
            Assert.Equal(swapped ? new PixelSize(200, 400) : new PixelSize(400, 200), full.PixelSize);
            var matrix = ScaledDecoder.Orientation((SKEncodedOrigin)orientation, 400, 200);
            var corners = matrix.MapPoints([new(0, 0), new(400, 0), new(0, 200), new(400, 200)]);
            Assert.All(corners, p => { Assert.InRange(p.X, 0, swapped ? 200 : 400); Assert.InRange(p.Y, 0, swapped ? 400 : 200); });
        }
        finally { File.Delete(path); }
    }
}
