using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using PhotoScreener.Core;
using PhotoScreener.Desktop;
using PhotoScreener.Infrastructure;
using SkiaSharp;
using Xunit;

namespace PhotoScreener.Desktop.Tests;

public sealed class MetadataRecoveryUiTests
{
    private static async Task Wait(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        Assert.True(condition());
    }

    [AvaloniaTheory]
    [InlineData("missing")] [InlineData("corrupt")] [InlineData("saved")]
    public async Task HistoryCertaintyIsExplicitWithoutChangingRecoveredPicksOrRepeatingFooterPaths(string history)
    {
        var root = Path.Combine(Path.GetTempPath(), "lumapick-history-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var window = new MainWindow();
        try
        {
            using var bitmap = new SKBitmap(64, 48); bitmap.Erase(SKColors.Coral);
            using var encoded = bitmap.Encode(SKEncodedImageFormat.Jpeg, 95);
            foreach (var name in new[] { "a.JPG", "b.JPG" }) File.WriteAllBytes(Path.Combine(root, name), encoded.ToArray());
            var photo = new SourceCatalog().Enumerate(root, default).Single(p => p.Name == "b.JPG");
            var binding = new ExportBinding(root, Path.Combine(root, "export"));
            using (var owner = new ExportWriter(binding, [root])) new ExportStore(new FolderComparer()).Pick(photo, binding, () => true, default);
            if (history == "saved")
                new ReviewMetadataStore().Write(binding, new(photo.Name, photo.Revision, ReviewFilter.All, [ReviewMetadataStore.RevisionKey(photo)]), [root]);
            else if (history == "corrupt")
                File.WriteAllText(Path.Combine(binding.ExportRoot, ".photoscreener", "review.json"), "broken optional history");

            window.Show(); await window.OpenFolder(root);
            var strip = window.FindControl<ListBox>("Filmstrip")!;
            var source = window.FindControl<Button>("SourceLink")!;
            var status = window.FindControl<TextBlock>("FolderStatus")!;
            await Wait(() => strip.Items.Count == 2 && strip.Items.Cast<PhotoRow>().All(row => row.Picked.HasValue));
            var expected = history == "saved" ? "1 known assessed revision" : "Review history: Unknown";
            await Wait(() => ToolTip.GetTip(source)?.ToString()?.Contains(expected) == true &&
                (history == "corrupt" ? status.IsVisible && status.Text?.Contains("Unknown") == true : !status.IsVisible));
            Assert.Equal("b.JPG", Assert.Single(strip.Items.Cast<PhotoRow>(), row => row.Picked == true).Name);
            Assert.Contains(root, ToolTip.GetTip(source)?.ToString());
            Assert.DoesNotContain(root, status.Text ?? "");
            if (history == "corrupt") { Assert.True(status.IsVisible); Assert.Contains("Unknown", status.Text); }
            else Assert.False(status.IsVisible);

            // Switching navigation roles rebuilds links; it must not erase the
            // history certainty or reinterpret the authoritative export copy.
            window.FindControl<TabStrip>("DirectoryRole")!.SelectedIndex = 1;
            window.FindControl<TabStrip>("DirectoryRole")!.SelectedIndex = 0;
            Assert.Contains(expected, ToolTip.GetTip(source)?.ToString());
            Assert.Single(strip.Items.Cast<PhotoRow>(), row => row.Picked == true);
            Assert.Single(Directory.EnumerateFiles(binding.ExportRoot, "*.JPG"));
            Assert.Equal(encoded.ToArray(), File.ReadAllBytes(Path.Combine(root, "b.JPG")));
        }
        finally
        {
            window.Close(); await Wait(() => !window.IsVisible); Directory.Delete(root, true);
        }
    }
}
