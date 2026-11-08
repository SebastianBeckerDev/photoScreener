using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PhotoScreener.Core;
using PhotoScreener.Infrastructure;
using PhotoScreener.Desktop;
using PhotoScreener.Desktop.Imaging;
using SkiaSharp;
using Xunit;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;

[assembly: AvaloniaTestApplication(typeof(PhotoScreener.Desktop.Tests.TestApp))]
namespace PhotoScreener.Desktop.Tests;
public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia();
}
public sealed class WindowTests
{
    [AvaloniaFact]
    public async Task StartupRestoresLastAvailableFolderAndKeepsMissingDrivePreference()
    {
        var root = Path.Combine(Path.GetTempPath(), "photoscreener-startup-" + Guid.NewGuid().ToString("N"));
        var photos = Path.Combine(root, "photos"); Directory.CreateDirectory(photos);
        var store = new SessionSettingsStore(Path.Combine(root, "session.json"));
        using (var bitmap = new SKBitmap(64, 48))
        {
            bitmap.Erase(SKColors.Coral); using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 95);
            File.WriteAllBytes(Path.Combine(photos, "a.JPG"), data.ToArray());
            File.WriteAllBytes(Path.Combine(photos, "b.JPG"), data.ToArray());
        }
        store.RememberFolder(photos);
        async Task CloseWindow(MainWindow window)
        {
            window.Close();
            for (var i = 0; i < 200 && window.IsVisible; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            Assert.False(window.IsVisible);
        }
        try
        {
            var window = new MainWindow([], store); window.Show();
            try
            {
                var strip = window.FindControl<ListBox>("Filmstrip")!;
                for (var i = 0; i < 200 && strip.Items.Count < 2; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
                Assert.Equal(2, strip.Items.Count);
                Assert.True(window.FindControl<Control>("ReviewSurface")!.IsFocused);
                var first = strip.SelectedItem;
                window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
                Assert.NotSame(first, strip.SelectedItem);
            }
            finally { await CloseWindow(window); }
            // An unplugged SSD must not become an empty successful source or erase
            // the saved path, so reconnecting it works on the next launch.
            Directory.Delete(photos, true);
            var missing = new MainWindow([], store); missing.Show();
            try
            {
                var status = missing.FindControl<TextBlock>("FolderStatus")!;
                for (var i = 0; i < 200 && status.Text?.Contains("unavailable") != true; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
                Assert.Contains("Last folder unavailable", status.Text);
                Assert.Empty(missing.FindControl<ListBox>("Filmstrip")!.Items);
                Assert.Equal(photos, store.ReadLastFolder());
                var alternate = Path.Combine(root, "alternate"); Directory.CreateDirectory(alternate);
                File.WriteAllText(Path.Combine(alternate, "c.JPG"), "invalid image bytes");
                await missing.OpenFolder(alternate);
                Assert.Single(missing.FindControl<ListBox>("Filmstrip")!.Items);
                Assert.True(missing.FindControl<Control>("ReviewSurface")!.IsFocused);
            }
            finally { await CloseWindow(missing); }
            Assert.Equal(Path.Combine(root, "alternate"), new SessionSettingsStore(Path.Combine(root, "session.json")).ReadLastFolder());
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task OpeningAColdOrCachedFolderRestoresKeyboardNavigation()
    {
        var root = Path.Combine(Path.GetTempPath(), "photoscreener-folder-focus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using (var bitmap = new SKBitmap(64, 48))
        {
            bitmap.Erase(SKColors.Coral);
            using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 95);
            File.WriteAllBytes(Path.Combine(root, "a.JPG"), data.ToArray());
            File.WriteAllBytes(Path.Combine(root, "b.JPG"), data.ToArray());
        }
        var window = new MainWindow();
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var surface = window.FindControl<Control>("ReviewSurface")!;
            var strip = window.FindControl<ListBox>("Filmstrip")!;
            Assert.True(surface.IsFocused);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                // The toolbar owns focus and All is already selected, just as when
                // reopening a folder without changing the filter.
                window.FindControl<Button>("OpenButton")!.Focus();
                Assert.False(surface.IsFocused);
                await window.OpenFolder(root); Dispatcher.UIThread.RunJobs();
                Assert.True(surface.IsFocused);
                var before = strip.SelectedItem;
                window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
                Assert.NotSame(before, strip.SelectedItem);
                window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
                Assert.Same(before, strip.SelectedItem);
            }
        }
        finally
        {
            window.Close();
            for (var i = 0; i < 200 && window.IsVisible; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public async Task TenThousandRowsRealizeOnlyABoundedViewport()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".JPG");
        using (var bitmap = new SKBitmap(64, 48)) { bitmap.Erase(SKColors.Coral); using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 95); File.WriteAllBytes(path, data.ToArray()); }
        var window = new MainWindow(); window.Show();
        var strip = window.FindControl<ListBox>("Filmstrip")!;
        var revision = PhotoScreener.Infrastructure.PathGuard.Revision(path);
        strip.ItemsSource = Enumerable.Range(0, 10000).Select(i => new PhotoRow(new Photo(path, Path.GetDirectoryName(path)!, i + ".JPG", revision), new(Path.GetDirectoryName(path)!, Path.Combine(Path.GetDirectoryName(path)!, "export")))).ToArray();
        for (var i = 0; i < 10; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(20); }
        var realized = strip.GetVisualDescendants().Count(c => c is ThumbnailView);
        Assert.InRange(realized, 1, 40);
        strip.ScrollIntoView(((PhotoRow[])strip.ItemsSource)[9000]);
        for (var i = 0; i < 10; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(20); }
        Assert.InRange(strip.GetVisualDescendants().Count(c => c is ThumbnailView), 1, 40);
        for (var i = 0; i < 100 && strip.GetVisualDescendants().OfType<ThumbnailView>().Any(t => t.Source == null); i++)
        { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        Assert.All(strip.GetVisualDescendants().OfType<ThumbnailView>(), t => Assert.NotNull(t.Source));
        window.Close(); for (var i = 0; i < 100 && window.IsVisible; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        File.Delete(path);
    }
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
        var surface = window.FindControl<Control>("ReviewSurface")!;
        var strip = window.FindControl<ListBox>("Filmstrip")!;
        for (var i = 0; i < 100 && strip.GetVisualDescendants().OfType<ThumbnailView>().Count(t => t.Source != null) < 2; i++)
        { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        Assert.Equal(2, strip.GetVisualDescendants().OfType<ThumbnailView>().Count(t => t.Source != null));
        Assert.True(surface.IsFocused);
        var first = strip.SelectedItem;
        void Down(Key key) => surface.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = surface });
        void Up(Key key) => surface.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key, Source = surface });
        Down(Key.Space); var once = window.FindControl<TextBlock>("CurrentStatus")!.Text;
        Down(Key.Space); Assert.Equal(once, window.FindControl<TextBlock>("CurrentStatus")!.Text);
        Assert.Same(first, strip.SelectedItem); Up(Key.Space); Down(Key.Space); Up(Key.Space);
        Assert.NotEqual(once, window.FindControl<TextBlock>("CurrentStatus")!.Text);
        Down(Key.Right); Assert.NotSame(first, strip.SelectedItem);
        Down(Key.Left); Assert.Same(first, strip.SelectedItem);
        Down(Key.Right); var second = strip.SelectedItem; Down(Key.Space); Up(Key.Space);
        for (var i = 0; i < 120; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        Assert.Same(second, strip.SelectedItem); // Creating the default export must not reset review focus.
        window.Close();
        for (var i = 0; i < 100 && window.IsVisible; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        Assert.False(window.IsVisible); Directory.Delete(root, true);
    }
    [AvaloniaFact]
    public async Task SavedPickedFilterAndCursorRestoreAfterAssociationChecks()
    {
        var root = Path.Combine(Path.GetTempPath(), "photoscreener-resume-window-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            using (var bitmap = new SKBitmap(64, 48))
            {
                bitmap.Erase(SKColors.Coral); using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 95);
                File.WriteAllBytes(Path.Combine(root, "a.JPG"), data.ToArray()); File.WriteAllBytes(Path.Combine(root, "b.JPG"), data.ToArray());
            }
            var photo = new SourceCatalog().Enumerate(root, default).Single(p => p.Name == "b.JPG");
            var binding = new ExportBinding(root, Path.Combine(root, "export")); var comparer = new FolderComparer();
            using (var owner = new ExportWriter(binding, [root])) new ExportStore(comparer).Pick(photo, binding, () => true, default);
            new ReviewMetadataStore().Write(binding, new(photo.Name, photo.Revision, ReviewFilter.Picked, []), [root]);
            var window = new MainWindow(); window.Show(); await window.OpenFolder(root);
            for (var i = 0; i < 150 && window.FindControl<ComboBox>("FilterBox")!.SelectedIndex != 1; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            var strip = window.FindControl<ListBox>("Filmstrip")!;
            Assert.Equal(1, window.FindControl<ComboBox>("FilterBox")!.SelectedIndex);
            Assert.Equal("b.JPG", Assert.IsType<PhotoRow>(strip.SelectedItem).Name);
            Assert.Single(strip.Items);
            window.Close(); for (var i = 0; i < 100 && window.IsVisible; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            Assert.False(window.IsVisible);
        }
        finally { Directory.Delete(root, true); }
    }
    [AvaloniaFact]
    public async Task ProjectPicksUseEachFoldersTargetAndSpaceKeepsTheChosenPhoto()
    {
        var root = Path.Combine(Path.GetTempPath(), "photoscreener-project-window-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            foreach (var child in new[] { "one", "two" })
            {
                var folder = Path.Combine(root, child); Directory.CreateDirectory(folder); WriteImage(folder, "a.JPG", 64, 48);
                var photo = new SourceCatalog().Enumerate(folder, default).Single(); var binding = new ExportBinding(folder, Path.Combine(folder, "export"));
                var comparer = new FolderComparer(); using var owner = new ExportWriter(binding, [folder]); new ExportStore(comparer).Pick(photo, binding, () => true, default);
            }
            var window = new MainWindow(); window.Show(); await window.OpenFolder(root);
            window.FindControl<Button>("ProjectPickedButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var strip = window.FindControl<ListBox>("Filmstrip")!;
            for (var i = 0; i < 150 && strip.Items.Count < 2; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            Assert.Equal(2, strip.Items.Count); Assert.Equal(1, window.FindControl<ComboBox>("FilterBox")!.SelectedIndex);
            Assert.Equal(2, strip.Items.Cast<PhotoRow>().Select(r => r.Binding.ExportRoot).Distinct().Count());
            var chosen = Assert.IsType<PhotoRow>(strip.SelectedItem); var surface = window.FindControl<Control>("ReviewSurface")!; surface.Focus();
            surface.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space, Source = surface });
            surface.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Space, Source = surface });
            for (var i = 0; i < 150 && chosen.Picked != false; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            Assert.Same(chosen, strip.SelectedItem); Assert.Equal(0, window.FindControl<ComboBox>("FilterBox")!.SelectedIndex); Assert.False(chosen.Picked);
            window.Close(); for (var i = 0; i < 100 && window.IsVisible; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            Assert.False(window.IsVisible);
        }
        finally { Directory.Delete(root, true); }
    }
    [AvaloniaFact]
    public async Task BrokenFocusedImageDoesNotBlockOtherVisibleTiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "photoscreener-broken-window-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "a.JPG"), "not an image"); WriteImage(root, "b.JPG", 64, 48);
            var window = new MainWindow(); window.Show(); await window.OpenFolder(root);
            var strip = window.FindControl<ListBox>("Filmstrip")!; strip.SelectedItem = strip.Items.Cast<PhotoRow>().Single(r => r.Name == "a.JPG");
            for (var i = 0; i < 150 && !strip.GetVisualDescendants().OfType<ThumbnailView>().Any(t => t.DataContext is PhotoRow { Name: "b.JPG" } && t.Source != null); i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            Assert.Contains(strip.GetVisualDescendants().OfType<ThumbnailView>(), t => t.DataContext is PhotoRow { Name: "b.JPG" } && t.Source != null);
            Assert.NotEqual("Loading…", window.FindControl<TextBlock>("PreviewMessage")!.Text);
            Assert.Equal("a.JPG", window.FindControl<TextBlock>("CurrentName")!.Text);
            window.Close(); for (var i = 0; i < 100 && window.IsVisible; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            Assert.False(window.IsVisible);
        }
        finally { Directory.Delete(root, true); }
    }
    [AvaloniaFact]
    public async Task LateDecodeFromPreviousFolderCannotReplaceCurrentPhoto()
    {
        var root = Path.Combine(Path.GetTempPath(), "photoscreener-stale-window-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var slow = Path.Combine(root, "slow"); var next = Path.Combine(root, "next"); Directory.CreateDirectory(slow); Directory.CreateDirectory(next);
            WriteImage(slow, "portrait.png", 2000, 4000); WriteImage(next, "landscape.JPG", 64, 32);
            var window = new MainWindow(); var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.PreviewPublished += (row, full) => { if (row.Name == "landscape.JPG" && full) ready.TrySetResult(); };
            window.Show(); await window.OpenFolder(slow); await window.OpenFolder(next);
            for (var i = 0; i < 200 && !ready.Task.IsCompleted; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            Assert.True(ready.Task.IsCompleted);
            await Task.Delay(100); Dispatcher.UIThread.RunJobs();
            Assert.Equal("landscape.JPG", window.FindControl<TextBlock>("CurrentName")!.Text);
            Assert.Equal(new PixelSize(64, 32), Assert.IsAssignableFrom<Bitmap>(window.FindControl<ZoomImage>("Preview")!.Source).PixelSize);
            window.Close(); for (var i = 0; i < 100 && window.IsVisible; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            Assert.False(window.IsVisible);
        }
        finally { Directory.Delete(root, true); }
    }
    private static void WriteImage(string folder, string name, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height); bitmap.Erase(SKColors.Coral);
        using var data = bitmap.Encode(Path.GetExtension(name) == ".png" ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg, 95);
        File.WriteAllBytes(Path.Combine(folder, name), data.ToArray());
    }
    [AvaloniaTheory]
    [InlineData(1, false)] [InlineData(2, false)] [InlineData(3, false)] [InlineData(4, false)]
    [InlineData(5, true)] [InlineData(6, true)] [InlineData(7, true)] [InlineData(8, true)]
    public void ExifAllOrientationsAtBothStages(int orientation, bool swapped)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".JPG");
        using var original = new SKBitmap(400, 200);
        var colors = new[] { SKColors.Red, SKColors.Lime, SKColors.Blue, SKColors.Yellow };
        using (var canvas = new SKCanvas(original))
        for (var q = 0; q < 4; q++) { using var paint = new SKPaint { Color = colors[q] }; canvas.DrawRect(new SKRect(q % 2 * 200, q / 2 * 100, q % 2 * 200 + 200, q / 2 * 100 + 100), paint); }
        using var encoded = original.Encode(SKEncodedImageFormat.Jpeg, 95);
        // APP1 little-endian TIFF orientation; actual decoder must consume it, not just a matrix helper.
        byte[] exif = [0xff, 0xe1, 0, 34, 69, 120, 105, 102, 0, 0, 73, 73, 42, 0, 8, 0, 0, 0, 1, 0,
            18, 1, 3, 0, 1, 0, 0, 0, (byte)orientation, 0, 0, 0, 0, 0, 0, 0];
        var bytes = encoded.ToArray(); File.WriteAllBytes(path, [.. bytes[..2], .. exif, .. bytes[2..]]);
        try
        {
            using var low = ScaledDecoder.Decode(path, 100, default, quickPreview: true); using var full = ScaledDecoder.Decode(path, 400, default);
            Assert.Equal(swapped ? new PixelSize(50, 100) : new PixelSize(100, 50), low.PixelSize);
            Assert.Equal(swapped ? new PixelSize(200, 400) : new PixelSize(400, 200), full.PixelSize);
            int[][] quadrants = [[0,1,2,3], [1,0,3,2], [3,2,1,0], [2,3,0,1], [0,2,1,3], [2,0,3,1], [3,1,2,0], [1,3,0,2]];
            foreach (var decoded in new[] { low, full })
            {
                using var pixels = ((WriteableBitmap)decoded).Lock();
                for (var q = 0; q < 4; q++)
                {
                    var x = (q % 2 * 2 + 1) * decoded.PixelSize.Width / 4;
                    var y = (q / 2 * 2 + 1) * decoded.PixelSize.Height / 4;
                    var value = (uint)Marshal.ReadInt32(pixels.Address, y * pixels.RowBytes + x * 4);
                    var expected = colors[quadrants[orientation - 1][q]];
                    Assert.InRange(Math.Abs((int)((value >> 16) & 255) - expected.Red), 0, 12);
                    Assert.InRange(Math.Abs((int)((value >> 8) & 255) - expected.Green), 0, 12);
                    Assert.InRange(Math.Abs((int)(value & 255) - expected.Blue), 0, 12);
                }
            }
            var matrix = ScaledDecoder.Orientation((SKEncodedOrigin)orientation, 400, 200);
            var corners = matrix.MapPoints([new(0, 0), new(400, 0), new(0, 200), new(400, 200)]);
            Assert.All(corners, p => { Assert.InRange(p.X, 0, swapped ? 200 : 400); Assert.InRange(p.Y, 0, swapped ? 400 : 200); });
        }
        finally { File.Delete(path); }
    }
    [AvaloniaFact]
    public void CorruptThumbnailCacheFallsBackToOriginalImage()
    {
        var root = Path.Combine(Path.GetTempPath(), "photoscreener-corrupt-cache-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            const string key = "corrupt-tile";
            var name = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key))) + ".png";
            File.WriteAllText(Path.Combine(root, name), "not a PNG");
            Assert.Null(new ThumbnailDiskCache(root).Read(key));
        }
        finally { Directory.Delete(root, true); }
    }
}
