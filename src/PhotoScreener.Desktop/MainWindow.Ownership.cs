using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using PhotoScreener.Infrastructure;

namespace PhotoScreener.Desktop;
public partial class MainWindow
{
    private async Task ResolveOwnership(PhotoRow row)
    {
        try
        {
            var knownBindings = folders.Keys.Select(bindings.Resolve).ToArray();
            var candidates = await Task.Run(() =>
            {
                var index = comparer.Index(row.Binding, default);
                if (index.Error != null) throw new IOException(index.Error);
                var sources = AssociationSources.Read(row.Photo, index, knownBindings, default);
                var digest = comparer.Hash(row.Photo.Path, row.Photo.Revision, default);
                return index.Candidates.Values.SelectMany(v => v).DistinctBy(e => e.Path)
                    .Where(e => LegacyExportName.Candidate(row.Name, e.Name) && e.Revision.Length == row.Photo.Revision.Length && comparer.Hash(e.Path, e.Revision, default) == digest)
                    .Select(e => (Entry: e, Names: sources.Where(p => p.Revision.Length == e.Revision.Length && LegacyExportName.Candidate(p.Name, e.Name) && comparer.Hash(p.Path, p.Revision, default) == digest).Select(p => p.Name).Distinct().ToArray()))
                    .Where(c => c.Names.Length > 1).ToArray();
            });
            foreach (var candidate in candidates)
            {
                var dialog = new Window { Title = "Choose export ownership", Width = 560, Height = 310, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                var choices = new ListBox { ItemsSource = candidate.Names, Height = 120 };
                var choose = new Button { Content = "Use selected original name", IsEnabled = false };
                ToolTip.SetTip(choose, "Associate this export with the selected original filename for this session so its pick state can be checked safely.");
                choices.SelectionChanged += (_, _) => choose.IsEnabled = choices.SelectedItem is string;
                choose.Click += (_, _) => dialog.Close(choices.SelectedItem as string);
                var cancel = new Button { Content = "Keep unresolved" }; cancel.Click += (_, _) => dialog.Close(null);
                ToolTip.SetTip(cancel, "Leave the export unchanged and return to reviewing without choosing an owner.");
                dialog.Content = new StackPanel { Margin = new Thickness(20), Spacing = 12, Children =
                {
                    new TextBlock { Text = $"{candidate.Entry.Name} matches identical bytes under different original names. Select its owner for this session.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    choices, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { cancel, choose } }
                } };
                var name = await dialog.ShowDialog<string?>(this); if (name == null) return;
                if (await Task.Run(() => PathGuard.Revision(candidate.Entry.Path)) != candidate.Entry.Revision) throw new IOException("Export changed while ownership was being chosen. Refresh and try again.");
                comparer.SetOwner(candidate.Entry.Path, name);
            }
            if (row.Intent.Pending) RequestMutation(row);
            else if (folders.TryGetValue(row.Photo.Folder, out var collection)) BeginComparison(collection, ImageContext);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { Text("FolderStatus", "Ownership unresolved · " + e.Message); }
    }
}
