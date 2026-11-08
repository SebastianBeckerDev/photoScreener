using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System.Collections.ObjectModel;
using PhotoScreener.Desktop;
using PhotoScreener.Desktop.Imaging;
using SkiaSharp;
using Xunit;

namespace PhotoScreener.Desktop.Tests;
public sealed class RetainedPreviewTests
{
    private static void WriteImage(string root, string name, SKColor color)
    {
        using var bitmap = new SKBitmap(640, 480); bitmap.Erase(color);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Jpeg, 95); File.WriteAllBytes(Path.Combine(root, name), encoded.ToArray());
    }
    private static async Task Wait(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        Assert.True(condition());
    }
    private static async Task Close(MainWindow window)
    { window.Close(); await Wait(() => !window.IsVisible); }
    private static void ArrangeFixtureOrder(MainWindow window)
    {
        // Directory enumeration order differs between NTFS and APFS. This test
        // exercises preview transitions through a known visible sequence, not sorting.
        var strip = window.FindControl<ListBox>("Filmstrip")!;
        var rows = Assert.IsType<ObservableCollection<PhotoRow>>(strip.ItemsSource);
        var ordered = rows.OrderBy(row => row.Name, StringComparer.Ordinal).ToArray();
        for (var index = 0; index < ordered.Length; index++) rows.Move(rows.IndexOf(ordered[index]), index);
        strip.SelectedItem = null; strip.SelectedItem = ordered[0];
    }

    [AvaloniaFact]
    public async Task NavigationRetainsOldImageMarksItsIdentityAndSkipsBlockedNextPhoto()
    {
        var root = Path.Combine(Path.GetTempPath(), "lumapick-retained-preview-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        using var release = new ManualResetEventSlim(); MainWindow? window = null;
        var images = new PreviewScheduler(Path.Combine(root, "cache"), (path, side, token, quick) =>
        {
            if (Path.GetFileName(path) == "b.JPG" && side >= 800) release.Wait(token);
            return ScaledDecoder.Decode(path, side, token, quick);
        });
        try
        {
            WriteImage(root, "a.JPG", SKColors.Coral); WriteImage(root, "b.JPG", SKColors.Blue); WriteImage(root, "c.JPG", SKColors.SeaGreen);
            window = new MainWindow([], null, images) { Width = 1700, Height = 950 };
            var firstFull = false; var thirdFull = false;
            window.PreviewPublished += (row, full) => { if (row.Name == "a.JPG" && full) firstFull = true; if (row.Name == "c.JPG" && full) thirdFull = true; };
            window.Show(); await window.OpenFolder(root); firstFull = false; ArrangeFixtureOrder(window); await Wait(() => firstFull);
            var image = window.FindControl<ZoomImage>("Preview")!; var previous = image.Source;
            window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("b.JPG", window.FindControl<TextBlock>("CurrentName")!.Text);
            Assert.Same(previous, image.Source);
            Assert.True(window.FindControl<Border>("PreviewPending")!.IsVisible);
            Assert.Contains("b.JPG", window.FindControl<TextBlock>("PendingPreviewTarget")!.Text);
            Assert.Contains("a.JPG", window.FindControl<TextBlock>("PendingPreviewShown")!.Text);
            Assert.Equal("", window.FindControl<TextBlock>("PreviewMessage")!.Text);
            Assert.InRange(image.Opacity, .5, .9); Assert.False(image.IsHitTestVisible);
            if (Environment.GetEnvironmentVariable("LUMAPICK_LOADING_PREVIEW") is { Length: > 0 } preview)
            {
                using var render = new RenderTargetBitmap(new PixelSize(1700, 950), new Vector(96, 96));
                render.Render(window); render.Save(preview);
            }
            window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            await Wait(() => thirdFull); // Does not require releasing b.JPG: its cancelled work must yield.
            Assert.Equal("c.JPG", window.FindControl<TextBlock>("CurrentName")!.Text);
            Assert.NotSame(previous, image.Source); Assert.NotNull(image.Source);
            Assert.False(window.FindControl<Border>("PreviewPending")!.IsVisible);
            Assert.Equal(1, image.Opacity); Assert.True(image.IsHitTestVisible);
        }
        finally
        {
            release.Set();
            if (window != null) await Close(window); else await images.DisposeAsync();
            Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public async Task FailedNextPreviewKeepsPreviousImageWithTruthfulErrorAndCanNavigateBack()
    {
        var root = Path.Combine(Path.GetTempPath(), "lumapick-retained-error-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        MainWindow? window = null;
        try
        {
            WriteImage(root, "a.JPG", SKColors.Coral); File.WriteAllText(Path.Combine(root, "b.JPG"), "damaged image");
            window = new MainWindow(); var firstFull = false;
            window.PreviewPublished += (row, full) => { if (row.Name == "a.JPG" && full) firstFull = true; };
            window.Show(); await window.OpenFolder(root); firstFull = false; ArrangeFixtureOrder(window); await Wait(() => firstFull);
            var image = window.FindControl<ZoomImage>("Preview")!; var previous = image.Source;
            window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            await Wait(() => window.FindControl<TextBlock>("PendingPreviewError")!.IsVisible);
            Assert.Same(previous, image.Source); Assert.True(window.FindControl<Border>("PreviewPending")!.IsVisible);
            Assert.Contains("Preview unavailable: b.JPG", window.FindControl<TextBlock>("PendingPreviewTarget")!.Text);
            Assert.Contains("a.JPG", window.FindControl<TextBlock>("PendingPreviewShown")!.Text);
            Assert.False(window.FindControl<ProgressBar>("PendingPreviewProgress")!.IsVisible);
            window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
            Assert.Equal("a.JPG", window.FindControl<TextBlock>("CurrentName")!.Text);
            Assert.NotNull(image.Source); Assert.False(window.FindControl<Border>("PreviewPending")!.IsVisible); Assert.Equal(1, image.Opacity);
        }
        finally { if (window != null) await Close(window); Directory.Delete(root, true); }
    }
}
