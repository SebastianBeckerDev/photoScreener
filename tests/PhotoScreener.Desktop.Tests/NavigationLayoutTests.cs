using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PhotoScreener.Desktop;
using PhotoScreener.Infrastructure;
using SkiaSharp;
using Xunit;

namespace PhotoScreener.Desktop.Tests;

public sealed class NavigationLayoutTests
{
    private static async Task Wait(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        Assert.True(condition());
    }
    private static void Click(MainWindow window, string name) => window.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static string Fixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "lumapick-navigation-" + Guid.NewGuid().ToString("N"));
        using var bitmap = new SKBitmap(400, 300); bitmap.Erase(SKColors.Coral);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Jpeg, 95);
        foreach (var name in new[] { "one", "two" })
        {
            var folder = Path.Combine(root, name); Directory.CreateDirectory(Path.Combine(folder, "child"));
            File.WriteAllBytes(Path.Combine(folder, "a.JPG"), encoded.ToArray()); File.WriteAllBytes(Path.Combine(folder, "b.JPG"), encoded.ToArray());
        }
        return root;
    }
    [AvaloniaFact]
    public async Task TreeArrowKeysBrowseBranchesAndEnterOpensTheSelectedSource()
    {
        var root = Fixture(); var window = new MainWindow(); window.Show();
        try
        {
            await window.OpenFolder(Path.Combine(root, "one"));
            var tree = window.FindControl<TreeView>("DirectoryTree")!;
            var parent = ((IEnumerable<TreeViewItem>)tree.ItemsSource!).OfType<MainWindow.DirectoryItem>().Single();
            await Wait(() => parent.ChildrenLoaded && parent.Template != null);
            var second = parent.Children.OfType<MainWindow.DirectoryItem>().Single(item => item.Path == Path.Combine(root, "two"));
            await Wait(() => second.Template != null && second.Bounds.Height > 0);
            tree.SelectedItem = second; Assert.True(second.Focus());
            window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            await Wait(() => second.ChildrenLoaded);
            var strip = window.FindControl<ListBox>("Filmstrip")!;
            Assert.Equal(Path.Combine(root, "one"), ((PhotoRow)strip.SelectedItem!).Photo.Folder);
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            await Wait(() => strip.SelectedItem is PhotoRow row && row.Photo.Folder == Path.Combine(root, "two"));
            Assert.True(window.FindControl<Control>("ReviewSurface")!.IsFocused);
        }
        finally { window.Close(); await Wait(() => !window.IsVisible); Directory.Delete(root, true); }
    }
    [AvaloniaFact]
    public async Task TreeSelectionChangesSourceAndCollapsedStripKeepsLinksFilterAndPickFeedback()
    {
        var root = Fixture(); var window = new MainWindow(); window.Show();
        try
        {
            await window.OpenFolder(Path.Combine(root, "one"));
            var tree = window.FindControl<TreeView>("DirectoryTree")!;
            var parent = ((IEnumerable<TreeViewItem>)tree.ItemsSource!).OfType<MainWindow.DirectoryItem>().Single();
            await Wait(() => parent.ChildrenLoaded);
            await Wait(() => parent.Template != null); // A derived tree container must retain its visible Avalonia template.
            var second = parent.Children.OfType<MainWindow.DirectoryItem>().Single(item => item.Path == Path.Combine(root, "two"));
            Assert.False(second.ChildrenLoaded); // Descendants have not been scanned eagerly.
            var point = second.TranslatePoint(new Avalonia.Point(second.Bounds.Width / 2, 14), window)!.Value;
            window.MouseDown(point, MouseButton.Left, RawInputModifiers.None); window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
            var strip = window.FindControl<ListBox>("Filmstrip")!;
            await Wait(() => strip.SelectedItem is PhotoRow row && row.Photo.Folder == Path.Combine(root, "two"));
            Click(window, "StripToggleButton"); Assert.False(strip.IsVisible);
            Assert.True(window.FindControl<Control>("StripFooter")!.IsVisible);
            Assert.True(window.FindControl<Control>("SourceLink")!.IsVisible); Assert.True(window.FindControl<Control>("ExportLink")!.IsVisible);
            Assert.True(window.FindControl<Control>("FilterBox")!.IsVisible); Assert.True(window.FindControl<Control>("CurrentPhotoBadge")!.IsVisible);
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Assert.NotEqual("Checking", window.FindControl<TextBlock>("CurrentStatus")!.Text);
            var first = strip.SelectedItem;
            window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            Assert.NotSame(first, strip.SelectedItem);
            Click(window, "FoldersToggleButton"); Click(window, "InfoToggleButton");
            Assert.False(window.FindControl<Control>("FoldersPanel")!.IsVisible); Assert.False(window.FindControl<Control>("InfoPanel")!.IsVisible);
            Assert.True(window.FindControl<Control>("FoldersToggleButton")!.IsVisible); Assert.True(window.FindControl<Control>("InfoToggleButton")!.IsVisible);
            Assert.True(window.FindControl<Control>("ReviewSurface")!.IsFocused);
        }
        finally { window.Close(); await Wait(() => !window.IsVisible); Directory.Delete(root, true); }
    }
    [AvaloniaFact]
    public async Task ExportCandidateDoesNotChangeBindingOrCreateDirectoriesUntilExplicitApply()
    {
        var root = Fixture(); var window = new MainWindow(); window.Show();
        try
        {
            var source = Path.Combine(root, "one"); var target = Path.Combine(root, "target"); Directory.CreateDirectory(target);
            await window.OpenFolder(source);
            var strip = window.FindControl<ListBox>("Filmstrip")!;
            Assert.False(Directory.Exists(Path.Combine(source, "export")));
            window.FindControl<TabStrip>("DirectoryRole")!.SelectedIndex = 1;
            await window.SelectDirectory(target);
            Assert.Equal(Path.Combine(source, "export"), ((PhotoRow)strip.SelectedItem!).Binding.ExportRoot);
            Assert.Empty(Directory.EnumerateFileSystemEntries(target)); Assert.False(Directory.Exists(Path.Combine(source, "export")));
            Click(window, "TargetButton");
            await Wait(() => strip.SelectedItem is PhotoRow row && row.Binding.ExportRoot == target);
            Assert.Empty(Directory.EnumerateFileSystemEntries(target));
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            await Wait(() => Directory.EnumerateFiles(target, "*.JPG").Any());
            Assert.False(Directory.Exists(Path.Combine(source, "export")));
            Assert.Equal(2, Directory.EnumerateFiles(source, "*.JPG").Count());
            var earlier = Path.Combine(root, "earlier-target"); var latest = Path.Combine(root, "latest-target");
            Directory.CreateDirectory(earlier); Directory.CreateDirectory(latest);
            await Task.WhenAll(window.ApplyExportTarget(earlier), window.ApplyExportTarget(latest));
            Assert.Equal(latest, ((PhotoRow)strip.SelectedItem!).Binding.ExportRoot);
            Assert.Empty(Directory.EnumerateFileSystemEntries(earlier)); Assert.Empty(Directory.EnumerateFileSystemEntries(latest));
            Assert.Single(Directory.EnumerateFiles(target, "*.JPG")); // Changing the binding preserves completed copies in the previous target.
        }
        finally { window.Close(); await Wait(() => !window.IsVisible); Directory.Delete(root, true); }
    }
    [AvaloniaFact]
    public async Task PanelPreferencesAndOfflineRecentsSurviveRestartWithoutLosingReviewFocus()
    {
        var root = Fixture(); var settings = new SessionSettingsStore(Path.Combine(root, "session.json"));
        var offline = Path.Combine(root, "disconnected", "photos"); var source = Path.Combine(root, "one");
        settings.RememberFolder(offline); settings.RememberFolder(source);
        var window = new MainWindow([], settings); window.Show();
        try
        {
            await Wait(() => window.FindControl<ListBox>("Filmstrip")!.SelectedItem != null);
            var recents = window.FindControl<ComboBox>("RecentFolders")!;
            await Wait(() => recents.ItemsSource is IEnumerable<MainWindow.RecentDirectory> entries && entries.Any(e => e.Path == offline && !e.Available));
            recents.SelectedItem = ((IEnumerable<MainWindow.RecentDirectory>)recents.ItemsSource!).Single(e => e.Path == offline);
            Assert.Equal(source, ((PhotoRow)window.FindControl<ListBox>("Filmstrip")!.SelectedItem!).Photo.Folder);
            Assert.Contains("unavailable", window.FindControl<TextBlock>("FolderStatus")!.Text);
            var panel = window.FindControl<Control>("FoldersPanel")!;
            var beforeWidth = panel.Width;
            var grip = window.FindControl<Control>("FoldersResizeThumb")!;
            var start = grip.TranslatePoint(new Avalonia.Point(2, grip.Bounds.Height / 2), window)!.Value;
            window.MouseDown(start, MouseButton.Left, RawInputModifiers.None);
            window.MouseMove(start + new Avalonia.Point(24, 0), RawInputModifiers.LeftMouseButton);
            window.MouseUp(start + new Avalonia.Point(24, 0), MouseButton.Left, RawInputModifiers.None);
            Assert.True(panel.Width > beforeWidth);
            var savedWidth = panel.Width;
            Click(window, "FoldersToggleButton"); Click(window, "InfoToggleButton"); Click(window, "StripToggleButton");
            window.Close(); await Wait(() => !window.IsVisible);
            window = new MainWindow([], new SessionSettingsStore(Path.Combine(root, "session.json"))); window.Show();
            await Wait(() => window.FindControl<ListBox>("Filmstrip")!.SelectedItem != null);
            Assert.False(window.FindControl<Control>("FoldersPanel")!.IsVisible); Assert.False(window.FindControl<Control>("InfoPanel")!.IsVisible);
            Assert.False(window.FindControl<Control>("Filmstrip")!.IsVisible); Assert.True(window.FindControl<Control>("StripFooter")!.IsVisible);
            Assert.Equal(savedWidth, window.FindControl<Control>("FoldersPanel")!.Width);
            Assert.Equal(source, settings.ReadLastFolder()); Assert.Contains(offline, settings.ReadPreferences().RecentSources);
            Assert.True(window.FindControl<Control>("ReviewSurface")!.IsFocused);
        }
        finally { window.Close(); await Wait(() => !window.IsVisible); Directory.Delete(root, true); }
    }
}
