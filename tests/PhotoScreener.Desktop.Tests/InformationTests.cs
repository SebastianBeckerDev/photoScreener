using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PhotoScreener.Desktop;
using PhotoScreener.TestFixtures;
using SkiaSharp;
using Xunit;

namespace PhotoScreener.Desktop.Tests;
public sealed class InformationTests
{
    [AvaloniaFact]
    public async Task InformationAndHistogramFollowTheCurrentPhotoAndMissingFieldsClear()
    {
        var root = Path.Combine(Path.GetTempPath(), "photoscreener-info-window-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        using (var bitmap = new SKBitmap(64, 48))
        {
            bitmap.Erase(SKColors.Coral); using var encoded = bitmap.Encode(SKEncodedImageFormat.Jpeg, 95);
            File.WriteAllBytes(Path.Combine(root, "a.JPG"), PhotoExifFixture.Jpeg(encoded.ToArray()));
            bitmap.Erase(SKColors.Blue); using var blue = bitmap.Encode(SKEncodedImageFormat.Jpeg, 95);
            File.WriteAllBytes(Path.Combine(root, "b.JPG"), blue.ToArray());
        }
        var window = new MainWindow(); window.Show();
        async Task Wait(Func<bool> condition)
        {
            for (var i = 0; i < 300 && !condition(); i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            Assert.True(condition());
        }
        try
        {
            await window.OpenFolder(root);
            var strip = window.FindControl<ListBox>("Filmstrip")!;
            strip.SelectedItem = strip.Items.Cast<PhotoRow>().Single(row => row.Name == "a.JPG");
            await Wait(() => window.FindControl<TextBlock>("IsoValue")!.Text == "400");
            Assert.Equal("2024-05-06 12:34:56 +02:00", window.FindControl<TextBlock>("CaptureTimeValue")!.Text);
            Assert.Contains("1/250", window.FindControl<TextBlock>("ExposureValue")!.Text);
            Assert.Contains("47.500000, 8.250000", window.FindControl<TextBlock>("LocationValue")!.Text);
            var histogram = window.FindControl<HistogramView>("Histogram")!;
            await Wait(() => histogram.Values?.Sum() > 0);
            var first = histogram.Values!.ToArray();
            strip.SelectedItem = strip.Items.Cast<PhotoRow>().Single(row => row.Name == "b.JPG");
            await Wait(() => window.FindControl<TextBlock>("InfoNotice")!.Text == "No embedded capture metadata");
            Assert.Equal("Unknown", window.FindControl<TextBlock>("IsoValue")!.Text);
            Assert.Equal("Unknown", window.FindControl<TextBlock>("CaptureTimeValue")!.Text);
            Assert.Equal("Unknown", window.FindControl<TextBlock>("LocationValue")!.Text);
            await Wait(() => histogram.Values?.Sum() > 0);
            var second = histogram.Values!.ToArray(); Assert.NotEqual(first, second);
            var peak = Enumerable.Range(0, 256).MaxBy(i => second[i]);
            Assert.InRange(peak, 17, 20); Assert.Equal(64 * 48, second.Sum());
            window.FindControl<Button>("InfoToggleButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(window.FindControl<Control>("InfoPanel")!.IsVisible);
            Assert.True(window.FindControl<Control>("ReviewSurface")!.IsFocused);
        }
        finally
        {
            window.Close(); await Wait(() => !window.IsVisible); Directory.Delete(root, true);
        }
    }
}
