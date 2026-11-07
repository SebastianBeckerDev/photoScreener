using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PhotoScreener.Desktop;
using SkiaSharp;
using Xunit;

namespace PhotoScreener.Desktop.Tests;
public sealed class StripTests
{
    [AvaloniaFact]
    public async Task SelectedThumbnailIsCenteredWithScrollClampedAtBothEnds()
    {
        var root = Path.Combine(Path.GetTempPath(), "photoscreener-centered-strip-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        using (var bitmap = new SKBitmap(64, 48))
        {
            bitmap.Erase(SKColors.Coral); using var encoded = bitmap.Encode(SKEncodedImageFormat.Jpeg, 95); var bytes = encoded.ToArray();
            for (var i = 0; i < 400; i++) File.WriteAllBytes(Path.Combine(root, $"{i:000}.JPG"), bytes);
        }
        var window = new MainWindow(); window.Show();
        async Task Wait(Func<bool> condition)
        {
            for (var i = 0; i < 300 && !condition(); i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            Assert.True(condition());
        }
        try
        {
            await window.OpenFolder(root); Dispatcher.UIThread.RunJobs();
            var strip = window.FindControl<ListBox>("Filmstrip")!;
            var viewer = strip.GetVisualDescendants().OfType<ScrollViewer>().First();
            await Wait(() => viewer.Offset.X < 1 && strip.ContainerFromItem(strip.SelectedItem!) != null);
            strip.SelectedIndex = 200;
            bool Centered()
            {
                var container = strip.ContainerFromItem(strip.SelectedItem!);
                var point = container?.TranslatePoint(new Point(container.Bounds.Width / 2, 0), viewer);
                return point.HasValue && Math.Abs(point.Value.X - viewer.Viewport.Width / 2) < 3;
            }
            await Wait(Centered);
            Assert.InRange(strip.GetVisualDescendants().OfType<ThumbnailView>().Count(), 1, 40);
            strip.SelectedIndex = 399;
            await Wait(() => Math.Abs(viewer.Offset.X - Math.Max(0, viewer.Extent.Width - viewer.Viewport.Width)) < 1);
            var last = strip.ContainerFromItem(strip.SelectedItem!)!;
            Assert.True(last.TranslatePoint(new Point(last.Bounds.Width / 2, 0), viewer)!.Value.X > viewer.Viewport.Width - last.Bounds.Width);
            strip.SelectedIndex = 0;
            await Wait(() => viewer.Offset.X < 1);
            var first = strip.ContainerFromItem(strip.SelectedItem!)!;
            Assert.True(first.TranslatePoint(new Point(first.Bounds.Width / 2, 0), viewer)!.Value.X < first.Bounds.Width);
            Assert.True(window.FindControl<Control>("ReviewSurface")!.IsFocused);
        }
        finally
        {
            window.Close(); await Wait(() => !window.IsVisible); Directory.Delete(root, true);
        }
    }
}
