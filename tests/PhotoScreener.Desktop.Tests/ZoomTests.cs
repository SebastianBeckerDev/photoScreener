using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PhotoScreener.Desktop;
using SkiaSharp;
using Xunit;

namespace PhotoScreener.Desktop.Tests;
public sealed class ZoomTests
{
    [AvaloniaFact]
    public async Task ClickLoadsDetailAtPhysicalPixelScaleKeepsPointerAnchorAndSecondClickFits()
    {
        var root = Path.Combine(Path.GetTempPath(), "lumapick-click-zoom-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        using (var pixels = new SKBitmap(4000, 3000))
        {
            pixels.Erase(SKColors.Coral); using var encoded = pixels.Encode(SKEncodedImageFormat.Jpeg, 95);
            File.WriteAllBytes(Path.Combine(root, "a.JPG"), encoded.ToArray());
        }
        var window = new MainWindow { Width = 1000, Height = 750 }; window.Show();
        async Task Wait(Func<bool> condition)
        {
            for (var i = 0; i < 500 && !condition(); i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            Assert.True(condition());
        }
        try
        {
            var full = false; window.PreviewPublished += (_, detailed) => full |= detailed;
            await window.OpenFolder(root); await Wait(() => full);
            var image = window.FindControl<ZoomImage>("Preview")!;
            var anchor = new Point(image.Bounds.Width / 2 + 12, image.Bounds.Height / 2 + 8);
            var before = image.ImageRectangle;
            var u = (anchor.X - before.X) / before.Width; var v = (anchor.Y - before.Y) / before.Height;
            var windowPoint = image.TranslatePoint(anchor, window)!.Value;
            window.MouseDown(windowPoint, MouseButton.Left, RawInputModifiers.None); window.MouseUp(windowPoint, MouseButton.Left, RawInputModifiers.None);
            Assert.NotNull(image.Source);
            await Wait(() => image.Zoom > 1 && window.FindControl<TextBlock>("ZoomStatus")!.Text != "Loading 100% preview…");
            var bitmap = Assert.IsAssignableFrom<Bitmap>(image.Source);
            Assert.Equal(bitmap.PixelSize.Width / window.RenderScaling, image.ImageRectangle.Width, 4);
            Assert.Equal(anchor.X, image.ImageRectangle.X + u * image.ImageRectangle.Width, 4);
            Assert.Equal(anchor.Y, image.ImageRectangle.Y + v * image.ImageRectangle.Height, 4);
            window.MouseDown(windowPoint, MouseButton.Left, RawInputModifiers.None); window.MouseUp(windowPoint, MouseButton.Left, RawInputModifiers.None);
            Assert.Equal(1, image.Zoom); Assert.Equal("Fit", window.FindControl<TextBlock>("ZoomStatus")!.Text);
            Assert.True(window.FindControl<Control>("ReviewSurface")!.IsFocused);
        }
        finally
        {
            window.Close(); await Wait(() => !window.IsVisible); Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public void DetailScaleAccountsForRetinaAndDraggingDoesNotToggleFit()
    {
        using var pixels = new SKBitmap(1600, 1200); pixels.Erase(SKColors.Coral);
        using var encoded = pixels.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new MemoryStream(encoded.ToArray()); using var bitmap = new Bitmap(stream);
        var image = new ZoomImage { Source = bitmap, Width = 400, Height = 300 };
        var window = new Window { Content = image, Width = 400, Height = 300 }; window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(); image.ShowDetailAt(new(200, 150), 2);
            Assert.Equal(2, image.Zoom); Assert.Equal(800, image.ImageRectangle.Width);
            window.MouseDown(new(200, 150), MouseButton.Left, RawInputModifiers.None);
            window.MouseMove(new(230, 170), RawInputModifiers.LeftMouseButton);
            window.MouseUp(new(230, 170), MouseButton.Left, RawInputModifiers.None);
            Assert.Equal(2, image.Zoom);
        }
        finally { image.Source = null; window.Close(); }
    }
    [AvaloniaFact]
    public void WheelKeepsTheImagePointUnderThePointerAndPanStaysWithinTheImage()
    {
        using var pixels = new SKBitmap(400, 300); pixels.Erase(SKColors.Coral);
        using var encoded = pixels.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new MemoryStream(encoded.ToArray()); using var bitmap = new Bitmap(stream);
        var image = new ZoomImage { Source = bitmap, Width = 800, Height = 600 };
        var window = new Window { Content = image, Width = 800, Height = 600 }; window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var anchor = new Point(300, 250); var before = image.ImageRectangle;
            var u = (anchor.X - before.X) / before.Width; var v = (anchor.Y - before.Y) / before.Height;
            window.MouseWheel(anchor, new Vector(0, 4), RawInputModifiers.None);
            var after = image.ImageRectangle;
            Assert.True(image.Zoom > 1);
            Assert.Equal(anchor.X, after.X + u * after.Width, 6);
            Assert.Equal(anchor.Y, after.Y + v * after.Height, 6);
            window.MouseDown(anchor, MouseButton.Left, RawInputModifiers.None);
            window.MouseMove(new Point(4000, 3000), RawInputModifiers.LeftMouseButton);
            window.MouseUp(new Point(4000, 3000), MouseButton.Left, RawInputModifiers.None);
            var panned = image.ImageRectangle;
            Assert.NotEqual(after, panned);
            Assert.True(panned.Left <= 0 && panned.Top <= 0);
            Assert.True(panned.Right >= image.Bounds.Width && panned.Bottom >= image.Bounds.Height);
            image.ResetView(); Assert.Equal(1, image.Zoom); Assert.Equal(before, image.ImageRectangle);
        }
        finally { image.Source = null; window.Close(); }
    }

    [AvaloniaFact]
    public async Task ZoomRefinesThePreviewAndNavigationReturnsToFitWithReviewFocus()
    {
        var root = Path.Combine(Path.GetTempPath(), "photoscreener-zoom-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        using (var pixels = new SKBitmap(4000, 3000))
        {
            pixels.Erase(SKColors.Coral); using var encoded = pixels.Encode(SKEncodedImageFormat.Jpeg, 95);
            File.WriteAllBytes(Path.Combine(root, "a.JPG"), encoded.ToArray());
            File.WriteAllBytes(Path.Combine(root, "b.JPG"), encoded.ToArray());
        }
        var window = new MainWindow { Width = 900, Height = 700 }; window.Show();
        try
        {
            var fullCount = 0; window.PreviewPublished += (_, full) => { if (full) fullCount++; };
            await window.OpenFolder(root);
            for (var i = 0; i < 200 && fullCount == 0; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            Assert.True(fullCount > 0);
            var image = window.FindControl<ZoomImage>("Preview")!;
            var beforeSize = Assert.IsAssignableFrom<Bitmap>(image.Source).PixelSize.Width;
            var previous = fullCount;
            var anchor = new Point(image.Bounds.Width * .6, image.Bounds.Height * .6);
            var windowPoint = image.TranslatePoint(anchor, window)!.Value;
            window.MouseWheel(windowPoint, new Vector(0, 5), RawInputModifiers.None);
            Assert.True(image.Zoom > 1);
            Assert.NotNull(image.Source); // Keep the visible image during refinement.
            for (var i = 0; i < 200 && fullCount == previous; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            Assert.True(fullCount > previous);
            var afterSize = Assert.IsAssignableFrom<Bitmap>(image.Source).PixelSize.Width;
            Assert.True(afterSize > beforeSize); Assert.InRange(afterSize, 1024, 3840);
            Assert.True(window.FindControl<Control>("ReviewSurface")!.IsFocused);
            var strip = window.FindControl<ListBox>("Filmstrip")!; var first = strip.SelectedItem;
            window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            Assert.NotSame(first, strip.SelectedItem); Assert.Equal(1, image.Zoom);
            Assert.Equal("Fit", window.FindControl<TextBlock>("ZoomStatus")!.Text);
        }
        finally
        {
            window.Close();
            for (var i = 0; i < 200 && window.IsVisible; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            Directory.Delete(root, true);
        }
    }
}
