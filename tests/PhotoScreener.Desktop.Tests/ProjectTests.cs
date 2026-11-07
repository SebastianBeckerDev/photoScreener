using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PhotoScreener.Desktop;
using PhotoScreener.Infrastructure;
using SkiaSharp;
using Xunit;
using System.Reflection;

namespace PhotoScreener.Desktop.Tests;
public sealed class ProjectTests
{
    private static void Image(string path, SKColor color)
    {
        using var bitmap = new SKBitmap(64, 48); bitmap.Erase(color);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Jpeg, 95); File.WriteAllBytes(path, encoded.ToArray());
    }
    private static async Task Wait(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        Assert.True(condition());
    }
    private static async Task EditImage(string path, SKColor color)
    {
        for (var i = 0; ; i++)
        {
            try { Image(path, color); return; }
            catch (IOException) when (i < 200) { await Task.Delay(10); }
        }
    }
    private static void Click(MainWindow window, string name) => window.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    [AvaloniaFact]
    public async Task ReplacedSourceRevisionRetainsTheOldFailureAndAcceptsANewRequest()
    {
        var root = Path.Combine(Path.GetTempPath(), "photoscreener-version-work-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "a.JPG"); Image(path, SKColors.Coral);
        var window = new MainWindow(); window.Show();
        using var verifying = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        try
        {
            await window.OpenFolder(root);
            var strip = window.FindControl<ListBox>("Filmstrip")!;
            var before = Assert.IsType<PhotoRow>(strip.SelectedItem);
            var worker = Assert.IsType<MutationCoordinator>(typeof(MainWindow).GetField("mutations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
            worker.Phase = phase => { if (phase == "verify") { verifying.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new IOException("Test release timed out."); } };
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            await Wait(() => verifying.IsSet);
            await EditImage(path, SKColors.Blue); var sourceBytes = File.ReadAllBytes(path);
            await window.OpenFolder(root, true);
            var changed = Assert.IsType<PhotoRow>(strip.SelectedItem); Assert.NotSame(before, changed);
            release.Set();
            var work = window.FindControl<ListBox>("WorkList")!;
            await Wait(() => before.Status.StartsWith("Error", StringComparison.Ordinal));
            Assert.Contains(before, work.Items.Cast<PhotoRow>());
            Assert.DoesNotContain("Copying", changed.Status);
            Assert.StartsWith("0 picked / 1", window.FindControl<TextBlock>("Counts")!.Text);
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            await Wait(() => changed.Picked == true && !changed.Intent.Pending);
            Assert.Empty(work.Items); Assert.Equal(sourceBytes, File.ReadAllBytes(path));
        }
        finally
        {
            release.Set(); Click(window, "CancelButton"); window.Close(); await Wait(() => !window.IsVisible);
            Directory.Delete(root, true);
        }
    }
    [AvaloniaFact]
    public async Task RescanReplacesRevisionsAndShowsMissingOriginalExportsWithoutDeletingThem()
    {
        var root = Path.Combine(Path.GetTempPath(), "photoscreener-project-refresh-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "photos"); Directory.CreateDirectory(folder);
        var a = Path.Combine(folder, "a.JPG"); var b = Path.Combine(folder, "b.JPG");
        Image(a, SKColors.Coral); Image(b, SKColors.Coral);
        var binding = new PhotoScreener.Core.ExportBinding(folder, Path.Combine(folder, "export"));
        var store = new ExportStore(new FolderComparer());
        using (var writer = new ExportWriter(binding, [folder]))
            foreach (var photo in new SourceCatalog().Enumerate(folder, default)) store.Pick(photo, binding, () => true, default);
        var preserved = Directory.EnumerateFiles(binding.ExportRoot, "*.JPG").ToDictionary(path => path, File.ReadAllBytes);
        var window = new MainWindow(); window.Show();
        try
        {
            await window.OpenFolder(root); Click(window, "ProjectPickedButton");
            await Wait(() => window.FindControl<TextBlock>("FolderStatus")!.Text?.Contains("scan complete") == true);
            var strip = window.FindControl<ListBox>("Filmstrip")!;
            Assert.Equal(2, strip.Items.Count); Assert.All(strip.Items.Cast<PhotoRow>(), row => Assert.False(row.IsExportOnly));
            await EditImage(a, SKColors.Blue); File.Delete(b);
            Click(window, "ProjectPickedButton");
            await Wait(() => window.FindControl<TextBlock>("FolderStatus")!.Text?.Contains("scan complete") == true);
            Assert.Equal(2, strip.Items.Count); Assert.All(strip.Items.Cast<PhotoRow>(), row => Assert.True(row.IsExportOnly));
            Assert.StartsWith("0 picked / 1", window.FindControl<TextBlock>("Counts")!.Text);
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Assert.Contains("Unmatched export preserved", window.FindControl<TextBlock>("CurrentStatus")!.Text);
            Assert.All(preserved, entry => Assert.Equal(entry.Value, File.ReadAllBytes(entry.Key)));
            window.FindControl<ComboBox>("FilterBox")!.SelectedIndex = 0;
            var current = Assert.IsType<PhotoRow>(Assert.Single(strip.Items));
            Assert.Equal(a, current.Photo.Path); Assert.False(current.IsExportOnly); Assert.False(current.Picked);
        }
        finally
        {
            Click(window, "CancelButton"); window.Close(); await Wait(() => !window.IsVisible);
            Directory.Delete(root, true);
        }
    }
}
