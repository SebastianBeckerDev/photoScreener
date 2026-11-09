using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using PhotoScreener.Desktop;
using PhotoScreener.Infrastructure;
using SkiaSharp;
using Xunit;

namespace PhotoScreener.Desktop.Tests;

public sealed class RawCompanionWindowTests
{
    [AvaloniaFact]
    public async Task JpegIsTheOnlyReviewRowAndSpaceCopiesAndRemovesItsNefCompanion()
    {
        var root = Path.Combine(Path.GetTempPath(), "lumapick-nef-window-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); MainWindow? window = null;
        var jpegPath = Path.Combine(root, "DSC_001.JPG"); var rawPath = Path.Combine(root, "DSC_001.NEF");
        using (var bitmap = new SKBitmap(640, 480))
        {
            bitmap.Erase(SKColors.Coral); using var encoded = bitmap.Encode(SKEncodedImageFormat.Jpeg, 95);
            File.WriteAllBytes(jpegPath, encoded.ToArray());
        }
        var rawBytes = new byte[2 * 1024 * 1024 + 9]; new Random(318).NextBytes(rawBytes); File.WriteAllBytes(rawPath, rawBytes);
        var jpegHash = SHA256.HashData(File.ReadAllBytes(jpegPath));
        try
        {
            window = new MainWindow(); var fullReady = false;
            window.PreviewPublished += (_, full) => { if (full) fullReady = true; };
            window.Show(); await window.OpenFolder(root);
            var strip = window.FindControl<ListBox>("Filmstrip")!;
            await Wait(() => strip.SelectedItem is PhotoRow row && row.Picked == false && fullReady);
            var jpeg = Assert.IsType<PhotoRow>(Assert.Single(strip.Items));
            Assert.Equal("DSC_001.JPG", jpeg.Name);
            var surface = window.FindControl<Control>("ReviewSurface")!; Assert.True(surface.IsFocused);
            var preview = window.FindControl<ZoomImage>("Preview")!.Source;
            void Space()
            {
                surface.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space, Source = surface });
                surface.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Space, Source = surface });
            }
            var export = Path.Combine(root, "export"); var rawExport = Path.Combine(export, LegacyExportName.FromDigest("DSC_001.NEF", MD5.HashData(rawBytes)));
            Space(); await Wait(() => jpeg.Picked == true);
            Assert.Same(jpeg, strip.SelectedItem); Assert.Same(preview, window.FindControl<ZoomImage>("Preview")!.Source);
            Assert.Equal(rawBytes, File.ReadAllBytes(rawExport)); Assert.Single(Directory.EnumerateFiles(export, "*.JPG"));
            Space(); await Wait(() => jpeg.Picked == false);
            Assert.Same(jpeg, strip.SelectedItem); Assert.True(surface.IsFocused); Assert.False(File.Exists(rawExport));
            Assert.Empty(Directory.EnumerateFiles(export, "*.JPG")); Assert.Equal(rawBytes, File.ReadAllBytes(rawPath));
            Assert.Equal(jpegHash, SHA256.HashData(File.ReadAllBytes(jpegPath)));
        }
        finally
        {
            if (window is { IsVisible: true }) { window.Close(); await Wait(() => !window.IsVisible); }
            Directory.Delete(root, true);
        }
    }

    private static async Task Wait(Func<bool> condition)
    {
        for (var i = 0; i < 1000 && !condition(); i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        Assert.True(condition());
    }
}
